using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace FrontDeskKiosk;

/// <summary>
/// Full-screen, always-on-top window holding one browser view. There are no tabs, and
/// pop-ups / "open in new window" links load in the same view instead.
/// </summary>
public sealed class KioskForm : Form
{
    // Taps in this top-left square (5 within 3 seconds) open the admin window, for touchscreens.
    const string CornerTapScript = """
        (() => {
          let taps = [];
          window.addEventListener('pointerdown', e => {
            if (e.clientX > 80 || e.clientY > 80) return;
            const now = Date.now();
            taps = taps.filter(t => now - t < 3000);
            taps.push(now);
            if (taps.length >= 5) {
              taps = [];
              try { window.chrome.webview.postMessage('front-desk-kiosk-admin'); } catch (_) {}
            }
          }, true);
        })();
        """;

    readonly KioskConfig config;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly Label message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 16F),
        Text = "Loading…",
    };
    readonly System.Windows.Forms.Timer keepOnTop = new() { Interval = 2000 };
    readonly System.Windows.Forms.Timer retry = new() { Interval = 15000 };
    readonly List<Form> blockers = new();

    bool dialogOpen, allowClose, webReady, started;

    public KioskForm(KioskConfig config)
    {
        this.config = config;
        Text = "Front Desk Kiosk";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;

        Controls.Add(web);     // on top
        Controls.Add(message); // shows through only if the browser can't start
        web.Visible = false;

        keepOnTop.Tick += (_, _) => KeepOnTop();
        retry.Tick += (_, _) => { retry.Stop(); GoHome(); };
        Lockdown.AdminHotkey += OnAdminHotkey;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CoverOtherScreens();
        Lockdown.Start();
        keepOnTop.Start();
        _ = InitBrowserAsync();
        ShowAdminWindow(atStartup: true);
    }

    void OnAdminHotkey()
    {
        if (IsHandleCreated) BeginInvoke(() => ShowAdminWindow(atStartup: false));
    }

    void ShowAdminWindow(bool atStartup)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        SettingsResult result;
        try
        {
            using var window = new SettingsWindow(config, countdownSeconds: atStartup ? 20 : 60, atStartup);
            window.ShowDialog(this);
            result = window.Result;
        }
        finally
        {
            dialogOpen = false;
        }

        if (result == SettingsResult.Exit)
        {
            ExitKiosk();
            return;
        }

        started = true;
        message.Text = "Loading…";
        GoHome(); // "Back to kiosk" always reloads the home page, which doubles as a "fix a stuck page" button
        Activate();
        if (web.Visible) web.Focus();
    }

    async Task InitBrowserAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(KioskConfig.Dir, "Browser"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;

            var s = core.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+N/T/P/F, F5, etc.
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPasswordAutosaveEnabled = false;

            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true; // never open a second window/tab
                if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
            };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, _) => retry.Stop();
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    Log.Write("Page failed to load: " + e.WebErrorStatus);
                    retry.Stop();
                    retry.Start(); // e.g. Wi-Fi not up yet after boot
                }
            };
            core.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { }
                if (msg == "front-desk-kiosk-admin") BeginInvoke(() => ShowAdminWindow(atStartup: false));
            };
            core.ProcessFailed += (_, e) =>
            {
                Log.Write("Browser process failed: " + e.ProcessFailedKind);
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    Program.Restart("browser exited");
                else
                    BeginInvoke(() => { try { core.Reload(); } catch { GoHome(); } });
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CornerTapScript);

            webReady = true;
            web.Visible = true;
            GoHome();
        }
        catch (Exception ex)
        {
            Log.Write("Browser could not start: " + ex);
            message.Text = "The browser component couldn't start.\n\n" + ex.Message +
                           "\n\nPress Ctrl+Shift+F12 to open the admin window.";
        }
    }

    void GoHome()
    {
        if (!started || !webReady) return; // InitBrowserAsync calls this again once ready
        try { web.CoreWebView2.Navigate(config.Url); }
        catch (Exception ex) { Log.Write("Navigate failed: " + ex.Message); retry.Start(); }
    }

    void KeepOnTop()
    {
        if (dialogOpen || ActiveForm == this) return;
        TopMost = true;
        BringToFront();
        Activate();
    }

    void CoverOtherScreens()
    {
        foreach (var screen in Screen.AllScreens.Where(s => !s.Primary))
        {
            var blocker = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = screen.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
            };
            blocker.Show(this);
            blockers.Add(blocker);
        }
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            Bounds = Screen.PrimaryScreen!.Bounds;
            foreach (var b in blockers) b.Close();
            blockers.Clear();
            CoverOtherScreens();
        });
    }

    void ExitKiosk()
    {
        allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Only the admin "Exit" button (or Windows shutting down) may close the kiosk.
        if (!allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        keepOnTop.Stop();
        retry.Stop();
        Lockdown.AdminHotkey -= OnAdminHotkey;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Lockdown.Stop();
        foreach (var b in blockers) b.Close();
        Log.Write("Kiosk closed (" + e.CloseReason + ")");
        base.OnFormClosed(e);
    }
}
