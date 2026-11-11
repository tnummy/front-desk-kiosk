namespace FrontDeskKiosk;

public enum SettingsResult { Start, Exit }

/// <summary>
/// The window shown when the kiosk starts and when the admin hotkey is pressed.
///  - Anyone can press "Start kiosk" / "Back to kiosk" (or wait for the countdown).
///  - Changing the web address, changing the password, or exiting to Windows
///    needs the current password (or the recovery code).
///  - On the very first run there's no password yet, so it's unlocked and asks for one.
/// </summary>
public sealed class SettingsWindow : Form
{
    const int IdleSeconds = 90; // after any typing/clicking, the countdown is pushed back to this

    readonly KioskConfig cfg;
    readonly bool atStartup;
    bool firstRun;
    bool unlocked;
    int secondsLeft;

    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };

    readonly TextBox urlBox = new() { Width = 560 };
    readonly TextBox passwordBox = new() { Width = 260, UseSystemPasswordChar = true };
    readonly Button unlockButton = new() { Text = "Unlock", AutoSize = true };
    readonly Label newPasswordLabel = Caption("New password");
    readonly TextBox newPassword = new() { Width = 260, UseSystemPasswordChar = true };
    readonly TextBox newPassword2 = new() { Width = 260, UseSystemPasswordChar = true };
    readonly CheckBox autoStart = new() { Text = "Start the kiosk automatically when Windows signs in", AutoSize = true };
    readonly Button saveButton = new() { Text = "Save changes", AutoSize = true };
    readonly Button recoveryButton = new() { Text = "Make a new recovery code…", AutoSize = true };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(3, 10, 3, 3) };
    readonly Label countdown = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
    readonly Button exitButton = new() { Text = "Exit to Windows", AutoSize = true };
    readonly Button startButton = new() { AutoSize = true, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };

    readonly List<Control> lockedOnly = new();
    readonly List<Control> unlockedOnly = new();

    public SettingsResult Result { get; private set; } = SettingsResult.Start;

    public SettingsWindow(KioskConfig cfg, int countdownSeconds, bool atStartup)
    {
        this.cfg = cfg;
        this.atStartup = atStartup;
        firstRun = !cfg.HasPassword;
        unlocked = firstRun;
        secondsLeft = countdownSeconds;

        Text = "Front Desk Kiosk";
        Font = new Font("Segoe UI", 11F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        KeyPreview = true;

        urlBox.Text = cfg.Url;
        autoStart.Checked = cfg.AutoStart;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        int row = 0;
        void Add(Control left, Control right, List<Control>? group = null)
        {
            grid.Controls.Add(left, 0, row);
            grid.Controls.Add(right, 1, row);
            group?.AddRange(new[] { left, right });
            row++;
        }
        void Span(Control c, List<Control>? group = null)
        {
            grid.Controls.Add(c, 0, row);
            grid.SetColumnSpan(c, 2);
            group?.Add(c);
            row++;
        }

        Span(new Label
        {
            Text = firstRun ? "Front Desk Kiosk: first-time setup" : "Front Desk Kiosk",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 12),
        });
        if (firstRun)
            Span(new Label
            {
                Text = "Paste the web address the kiosk should show and choose an admin password. " +
                       "You'll need the password to change settings or exit the kiosk.",
                AutoSize = true,
                MaximumSize = new Size(720, 0),
                Margin = new Padding(3, 0, 3, 12),
            });

        Add(Caption("Web address"), urlBox);
        Add(Caption("Admin password"), Row(passwordBox, unlockButton), lockedOnly);
        Add(newPasswordLabel, newPassword, unlockedOnly);
        Add(Caption("Confirm password"), newPassword2, unlockedOnly);
        Add(Caption(""), autoStart, unlockedOnly);
        Add(Caption(""), Row(saveButton, recoveryButton), unlockedOnly);
        Span(new Label
        {
            Text = "While the kiosk is running, press Ctrl+Shift+F12 (or tap the top-left corner 5 times) to bring this window back.",
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 12, 3, 3),
        });
        Span(status);
        Span(countdown);
        var buttons = Row(exitButton, startButton);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(3, 12, 3, 3);
        Span(buttons);

        Controls.Add(grid);
        AcceptButton = startButton;

        unlockButton.Click += (_, _) => TryUnlock();
        passwordBox.Enter += (_, _) => AcceptButton = unlockButton; // Enter in the password box = Unlock
        passwordBox.Leave += (_, _) => AcceptButton = startButton;
        saveButton.Click += (_, _) => Save();
        recoveryButton.Click += (_, _) => MakeRecoveryCode();
        startButton.Click += (_, _) => StartClicked();
        exitButton.Click += (_, _) => ExitClicked();
        tick.Tick += (_, _) => Tick();
        KeyDown += (_, _) => ResetIdle();
        HookMouse(this);

        UpdateUi();
    }

    static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 16, 3) };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        p.Controls.AddRange(controls);
        return p;
    }

    void HookMouse(Control c)
    {
        c.MouseDown += (_, _) => ResetIdle();
        foreach (Control child in c.Controls) HookMouse(child);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        tick.Start();
        UpdateCountdown();
        if (firstRun) urlBox.Focus();
        else if (atStartup) startButton.Focus();
        else passwordBox.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tick.Stop();
        tick.Dispose();
        base.OnFormClosed(e);
    }

    void UpdateUi()
    {
        foreach (var c in lockedOnly) c.Visible = !unlocked;
        foreach (var c in unlockedOnly) c.Visible = unlocked;
        recoveryButton.Visible = unlocked && !firstRun;
        saveButton.Visible = !firstRun;
        newPasswordLabel.Text = firstRun ? "Choose a password" : "New password";
        urlBox.ReadOnly = !unlocked;
        startButton.Text = firstRun ? "Save and start kiosk" : atStartup ? "Start kiosk" : "Back to kiosk";
        UpdateCountdown();
    }

    void Tick()
    {
        if (!cfg.IsSetUp) return; // nothing to start yet
        secondsLeft--;
        if (secondsLeft <= 0)
        {
            Result = SettingsResult.Start; // unsaved edits are dropped; the saved settings are used
            Close();
            return;
        }
        UpdateCountdown();
    }

    void UpdateCountdown()
    {
        countdown.Visible = cfg.IsSetUp;
        countdown.Text = $"The kiosk {(atStartup ? "starts" : "resumes")} automatically in {secondsLeft} seconds.";
    }

    void ResetIdle()
    {
        if (secondsLeft < IdleSeconds) secondsLeft = IdleSeconds;
        UpdateCountdown();
    }

    void SetStatus(string text, bool ok)
    {
        status.Text = text;
        status.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
    }

    void TryUnlock()
    {
        var input = passwordBox.Text;
        passwordBox.Clear();
        if (cfg.CheckPassword(input))
        {
            SetStatus("Unlocked.", ok: true);
        }
        else if (cfg.CheckRecoveryCode(input))
        {
            SetStatus("Recovery code accepted. Choose a new password below, then press Save changes.", ok: true);
        }
        else
        {
            SetStatus("That password isn't right.", ok: false);
            passwordBox.Focus();
            return;
        }
        unlocked = true;
        AcceptButton = startButton;
        UpdateUi();
        newPassword.Focus();
    }

    bool IsDirty =>
        urlBox.Text.Trim() != cfg.Url || newPassword.Text.Length > 0 || newPassword2.Text.Length > 0 ||
        autoStart.Checked != cfg.AutoStart;

    bool Save()
    {
        var url = urlBox.Text.Trim();
        if (!KioskConfig.IsValidUrl(url))
        {
            SetStatus("Enter the full web address, starting with https://", ok: false);
            urlBox.Focus();
            return false;
        }

        bool changingPassword = newPassword.Text.Length > 0 || newPassword2.Text.Length > 0;
        if (firstRun && !changingPassword)
        {
            SetStatus("Choose an admin password.", ok: false);
            newPassword.Focus();
            return false;
        }
        if (changingPassword)
        {
            if (newPassword.Text.Length < 4)
            {
                SetStatus("The password needs at least 4 characters.", ok: false);
                newPassword.Focus();
                return false;
            }
            if (newPassword.Text != newPassword2.Text)
            {
                SetStatus("The two passwords don't match.", ok: false);
                newPassword2.Clear();
                newPassword2.Focus();
                return false;
            }
        }

        var backup = (cfg.Url, cfg.AutoStart, cfg.PasswordHash, cfg.PasswordSalt, cfg.RecoveryHash, cfg.RecoverySalt);
        cfg.Url = url;
        cfg.AutoStart = autoStart.Checked;
        if (changingPassword) cfg.SetPassword(newPassword.Text);
        string? newCode = cfg.HasRecoveryCode ? null : cfg.NewRecoveryCode();

        try
        {
            cfg.Save();
        }
        catch (Exception ex)
        {
            (cfg.Url, cfg.AutoStart, cfg.PasswordHash, cfg.PasswordSalt, cfg.RecoveryHash, cfg.RecoverySalt) = backup;
            SetStatus("Couldn't save the settings: " + ex.Message, ok: false);
            return false;
        }

        Lockdown.SetAutoStart(cfg.AutoStart);
        newPassword.Clear();
        newPassword2.Clear();
        bool wasFirstRun = firstRun;
        firstRun = false;
        SetStatus(changingPassword && !wasFirstRun ? "Saved. The new password is now active." : "Saved.", ok: true);
        UpdateUi();
        Log.Write(changingPassword ? "Settings saved (password changed)" : "Settings saved");

        if (newCode != null) RecoveryCodeWindow.Show(this, newCode);
        return true;
    }

    void MakeRecoveryCode()
    {
        var answer = MessageBox.Show(this,
            "This replaces your current recovery code. The old one will stop working.\n\nContinue?",
            "New recovery code", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var backup = (cfg.RecoveryHash, cfg.RecoverySalt);
        var code = cfg.NewRecoveryCode();
        try
        {
            cfg.Save();
        }
        catch (Exception ex)
        {
            (cfg.RecoveryHash, cfg.RecoverySalt) = backup;
            SetStatus("Couldn't save the new recovery code: " + ex.Message, ok: false);
            return;
        }
        RecoveryCodeWindow.Show(this, code);
        SetStatus("New recovery code saved.", ok: true);
    }

    void StartClicked()
    {
        if (unlocked && (firstRun || IsDirty) && !Save()) return;
        Result = SettingsResult.Start;
        Close();
    }

    void ExitClicked()
    {
        if (firstRun)
        {
            // Nothing set up yet, so there's nothing to protect.
            Result = SettingsResult.Exit;
            Close();
            return;
        }
        if (!unlocked)
        {
            SetStatus("Enter the admin password and press Unlock first.", ok: false);
            passwordBox.Focus();
            return;
        }
        if (IsDirty && !Save()) return;
        Result = SettingsResult.Exit;
        Close();
    }
}

/// <summary>Shows a newly made recovery code once, big and copyable.</summary>
static class RecoveryCodeWindow
{
    public static void Show(IWin32Window owner, string code)
    {
        using var form = new Form
        {
            Text = "Your recovery code",
            Font = new Font("Segoe UI", 11F),
            AutoScaleMode = AutoScaleMode.Dpi,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            StartPosition = FormStartPosition.CenterParent,
            TopMost = true,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20),
        };
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        layout.Controls.Add(new Label
        {
            Text = "Write this down and keep it somewhere safe (like your password manager). " +
                   "If you ever forget the admin password, type this code into the password box instead.\n\n" +
                   "It won't be shown again.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
        });
        layout.Controls.Add(new TextBox
        {
            Text = code,
            ReadOnly = true,
            Font = new Font("Consolas", 22F, FontStyle.Bold),
            Width = 360,
            TextAlign = HorizontalAlignment.Center,
            Margin = new Padding(3, 16, 3, 16),
        });
        var ok = new Button { Text = "I've saved it", AutoSize = true, DialogResult = DialogResult.OK };
        layout.Controls.Add(ok);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.ShowDialog(owner);
    }
}
