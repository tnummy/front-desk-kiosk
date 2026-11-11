using System.Diagnostics;

namespace FrontDeskKiosk;

static class Program
{
    static readonly DateTime startedAt = DateTime.Now;
    static bool wasRestarted;

    [STAThread]
    static void Main(string[] args)
    {
        wasRestarted = args.Contains("--restarted");

        // Only one copy at a time. After a crash-restart, wait for the old copy to finish exiting.
        using var mutex = new Mutex(false, @"Local\FrontDeskKiosk");
        bool owned;
        try { owned = mutex.WaitOne(wasRestarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => OnCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash(e.ExceptionObject as Exception);

            Log.Write("Starting" + (wasRestarted ? " (after restart)" : ""));
            var config = KioskConfig.Load();
            if (config.IsSetUp) Lockdown.SetAutoStart(config.AutoStart); // keeps the path current if the .exe moved
            Application.Run(new KioskForm(config));
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>Starts a fresh copy of the app and exits this one (lockdown stays on in between).</summary>
    public static void Restart(string reason)
    {
        Log.Write("Restarting: " + reason);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write("Restart failed: " + ex);
        }
        Environment.Exit(1);
    }

    static void OnCrash(Exception? ex)
    {
        Log.Write("Crash: " + ex);
        if (wasRestarted && DateTime.Now - startedAt < TimeSpan.FromSeconds(30))
        {
            // Crashing over and over: switch everything off rather than trap anyone.
            try
            {
                Lockdown.Stop();
                foreach (var f in Application.OpenForms.Cast<Form>().ToList()) f.Hide();
                MessageBox.Show("Front Desk Kiosk keeps crashing, so it has turned itself off.\n\nDetails: "
                                + Path.Combine(KioskConfig.Dir, "kiosk.log"), "Front Desk Kiosk");
            }
            catch { }
            Environment.Exit(1);
        }
        Restart("crash");
    }
}
