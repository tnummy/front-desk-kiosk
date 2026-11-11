# Front Desk Kiosk

A small Windows program that locks the front-desk PC to one web page. It shows the page fullscreen with no tabs and no address bar, and stays on top of everything else. Leaving it takes a password.

The page is shown with Microsoft's WebView2, the Chromium engine that Chrome is built on. It comes with Windows 11, so there's nothing else to install.

## What it blocks

| Way out | What happens |
|---|---|
| Windows key, Start menu, taskbar | Blocked / covered |
| Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc | Blocked |
| Ctrl+N, Ctrl+T, Ctrl+P, F5, right-click menu, dev tools | Turned off |
| Links that open a new tab or window | Open in the same view instead |
| Downloads | Cancelled |
| Ctrl+Shift+Esc (Task Manager) | Blocked |
| Ctrl+Alt+Del | Can't be blocked by any program, but **Task Manager, Lock, Sign out and Change password are removed** from that screen. Only *Switch user*, the power button and *Cancel* remain. |
| Wi-Fi not ready at boot / page fails to load | Retries every 15 seconds |
| Program crashes | Restarts itself |
| Screen sleep | Kept awake while the kiosk is running |

TeamViewer keeps working. It runs as a background service, so unattended access connects as usual.

## Installing on the PC

1. Copy `FrontDeskKiosk.exe` somewhere permanent, e.g. `C:\FrontDeskKiosk\FrontDeskKiosk.exe`. Don't run it from Downloads or a USB stick, because auto-start remembers this location.
2. Double-click it. Windows will probably warn **"Windows protected your PC"** because the program isn't from a known publisher. Click **More info → Run anyway**.
3. **First-time setup** window:
   - Paste the web address.
   - Choose an admin password (type it twice).
   - Press **Save and start kiosk**.
4. A **recovery code** appears. **Save it in your password manager.** This is Plan A if you forget the password.
5. Optional: right-click the exe → *Show more options* → *Send to* → *Desktop (create shortcut)*. That gives you an easy way to turn the kiosk back on after you've exited it.

The kiosk now starts automatically every time Windows signs in.

### Make the PC sign in by itself (optional)

So that the kiosk is up after a power cut or Windows update without anyone typing a PIN:

1. **Settings → Accounts → Sign-in options**: turn **off** "For improved security, only allow Windows Hello sign-in…".
2. Press **Win+R**, type `netplwiz`, press Enter.
3. Untick **"Users must enter a user name and password to use this computer"**, press OK, and enter the Windows account password.

(If that checkbox doesn't appear, Microsoft's free **Autologon** tool from Sysinternals does the same thing.)

### TeamViewer

Make sure TeamViewer is set up for **unattended access** (assigned to your TeamViewer account, "Start TeamViewer with Windows" on). Test a remote connection **before** relying on the kiosk.

## Daily use

**To open the admin window:** press **Ctrl+Shift+F12**, or tap the **top-left corner of the screen 5 times** quickly (for a touchscreen).

In the admin window:
- **Back to kiosk** needs no password. It also reloads the home page, so staff can use it to fix a stuck page.
- **Unlock** with the password to:
  - change the **web address**
  - change the **password** (the current password is needed first)
  - turn auto-start on/off
  - make a new recovery code
  - **Exit to Windows**
- If nobody touches it, the window closes and the kiosk resumes by itself after 60–90 seconds.

**When the kiosk starts**, the same window appears with a 20-second countdown, then the kiosk starts automatically. That gives you a chance to unlock and change settings at boot.

**To turn it back on after exiting:** double-click the desktop shortcut, or just restart the PC.

All of this also works through TeamViewer. Send Ctrl+Shift+F12 from the TeamViewer toolbar or keyboard.

## Plan B: forgot the password

Try these in order:

1. **Recovery code.** Type it into the *Admin password* box and press Unlock (dashes and capital letters don't matter). Then set a new password.

2. **TeamViewer file transfer (no password needed).**
   1. Connect with TeamViewer → **Files & Extras → File transfer**.
   2. On the PC side, go to `C:\Users\<windows user>\AppData\Local\FrontDeskKiosk\` and **delete `settings.json`**.
   3. In the remote session, press **Ctrl+Alt+Del** (TeamViewer toolbar → Actions), click the **power icon** (bottom right) → **Restart**.
   4. The kiosk comes back up in **first-time setup**, where you choose a new password and paste the web address again.

   Logins saved by the website (the `Browser` folder) are kept.

3. **At the PC, no remote access.** The kiosk doesn't start in Safe Mode:
   1. Press **Ctrl+Alt+Del**, then hold **Shift** while clicking the **power icon → Restart**.
   2. Go to **Troubleshoot → Advanced options → Startup Settings → Restart**, then press **4** for Safe Mode.
   3. Sign in. Safe Mode needs the account's actual **password**. A PIN doesn't work there; for a Microsoft account it's the Microsoft account password.
   4. Delete `C:\Users\<windows user>\AppData\Local\FrontDeskKiosk\settings.json` and restart normally.

   If Shift+Restart doesn't bring up the blue menu: turn the PC off with the power button while Windows is loading, twice in a row. The third boot opens the same recovery menu.

## Removing it

Unlock → untick auto-start → **Exit to Windows**. Then delete the exe and the folder `C:\Users\<windows user>\AppData\Local\FrontDeskKiosk\`.

If the program was ever force-killed and Task Manager / Lock / Sign out are still missing on the normal desktop: start the kiosk once and exit it properly, which removes those restrictions.

## Files it uses

`C:\Users\<windows user>\AppData\Local\FrontDeskKiosk\`
- `settings.json`: web address, auto-start setting, **hashed** password and recovery code (never stored in readable form)
- `Browser\`: the web view's cookies and logins
- `kiosk.log`: troubleshooting log

## Building from source

Needs the .NET 8 SDK (works from macOS too):

```bash
dotnet publish -c Release -o publish
```

The output is a single self-contained `publish/FrontDeskKiosk.exe` (~70 MB). The PC doesn't need .NET installed.
