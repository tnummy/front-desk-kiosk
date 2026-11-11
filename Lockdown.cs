using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace FrontDeskKiosk;

/// <summary>
/// Everything that stops people leaving the kiosk:
///  - a low-level keyboard hook that swallows Win key, Alt+Tab, Alt+F4, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc
///    and turns Ctrl+Shift+F12 into the "admin" hotkey
///  - per-user Windows policies (these work on Windows Home) that remove Task Manager, Lock,
///    Sign out and Change password from the Ctrl+Alt+Del screen
///  - keeps the screen from sleeping
/// Stop() undoes all of it, putting back any policy values that were there before.
/// </summary>
static class Lockdown
{
    public static event Action? AdminHotkey;

    static bool active;
    static IntPtr hook = IntPtr.Zero;
    static readonly HookProc hookProc = KeyboardProc; // field keeps the delegate alive for the GC

    const string SystemPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    static readonly (string Key, string Name)[] Policies =
    {
        (SystemPolicies, "DisableTaskMgr"),
        (SystemPolicies, "DisableLockWorkstation"),
        (SystemPolicies, "DisableChangePassword"),
        (ExplorerPolicies, "NoLogoff"),
    };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "FrontDeskKiosk";

    public static void Start()
    {
        if (active) return;
        active = true;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Write("Keyboard hook failed, error " + Marshal.GetLastWin32Error());
        SetPolicies(true);
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        Log.Write("Lockdown on");
    }

    public static void Stop()
    {
        if (!active) return;
        active = false;
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        SetPolicies(false);
        SetThreadExecutionState(ES_CONTINUOUS);
        Log.Write("Lockdown off");
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("Could not change auto-start: " + ex.Message);
        }
    }

    /// <summary>Puts a "Front Desk Kiosk" shortcut on the desktop (used to turn the kiosk back on after exiting).</summary>
    public static void CreateDesktopShortcut()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(Path.Combine(desktop, "Front Desk Kiosk.lnk"));
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Start the front desk kiosk";
            link.Save();
            Log.Write("Desktop shortcut created");
        }
        catch (Exception ex)
        {
            Log.Write("Could not create desktop shortcut: " + ex.Message);
        }
    }

    // Remembers what the policies were before the kiosk touched them, so exiting puts back
    // anything an IT department (or the owner) had already set instead of just deleting it.
    // Written once per lockdown session and only removed on a clean exit, so a crash-restart
    // never mistakes the kiosk's own values for the originals.
    static string BackupPath => Path.Combine(KioskConfig.Dir, "policy-backup.json");

    static void SetPolicies(bool on)
    {
        if (on) BackupPolicies();
        var backup = on ? null : LoadBackup();

        foreach (var (keyPath, name) in Policies)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                if (on)
                    key.SetValue(name, 1, RegistryValueKind.DWord);
                else if (backup != null && backup.TryGetValue(BackupId(keyPath, name), out var original) && original is int value)
                    key.SetValue(name, value, RegistryValueKind.DWord);
                else
                    key.DeleteValue(name, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Write($"Could not {(on ? "set" : "restore")} policy {name}: {ex.Message}");
            }
        }

        if (!on)
        {
            try { File.Delete(BackupPath); } catch { }
        }
    }

    static string BackupId(string keyPath, string name) => keyPath + "\\" + name;

    static void BackupPolicies()
    {
        if (File.Exists(BackupPath)) return; // still the same lockdown session (e.g. after a crash)
        var backup = new Dictionary<string, int?>();
        foreach (var (keyPath, name) in Policies)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                backup[BackupId(keyPath, name)] = key?.GetValue(name) is int value ? value : null;
            }
            catch
            {
                backup[BackupId(keyPath, name)] = null;
            }
        }
        try
        {
            Directory.CreateDirectory(KioskConfig.Dir);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }
        catch (Exception ex)
        {
            Log.Write("Could not save policy backup: " + ex.Message);
        }
    }

    static Dictionary<string, int?>? LoadBackup()
    {
        try
        {
            return File.Exists(BackupPath)
                ? JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(BackupPath))
                : null;
        }
        catch (Exception ex)
        {
            Log.Write("Could not read policy backup: " + ex.Message);
            return null;
        }
    }

    static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && active)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            uint vk = k.vkCode;

            if (vk == VK_F12 && ctrl && shift)
            {
                // Keep this fast: the handler only posts a message to the UI thread.
                if (down) AdminHotkey?.Invoke();
                return (IntPtr)1;
            }
            if (vk == VK_LWIN || vk == VK_RWIN) return (IntPtr)1;
            if (alt && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4 || vk == VK_SPACE)) return (IntPtr)1;
            if (ctrl && vk == VK_ESCAPE) return (IntPtr)1; // Ctrl+Esc (Start) and Ctrl+Shift+Esc (Task Manager)
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---- Win32 ----
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint LLKHF_ALTDOWN = 0x20;
    const uint VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_F4 = 0x73, VK_F12 = 0x7B, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
}
