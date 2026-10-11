# Changelog

## 1.0.2

- **Installer permission check fixed.** The check that the folder Quick Sweep is installed into is administrators-only skipped every permission entry that is only inherited by files and folders created later. Now any inheritable entry that gives a non-administrator the right to write, delete or change permissions makes the folder untrusted (only the CREATOR OWNER and CREATOR GROUP placeholders are excused, and only when inherit-only). Generic write and all-access rights count too.
- **Explicit permissions after the copy.** The install folder is given its own protected permissions (inheritance removed; SYSTEM and Administrators full control, Users read and execute) before the files are copied in, every copied file is reset to inherit them, and every file and folder is checked afterwards, as is the folder once more right before the logon task is registered. The data folder gets the same verification; if something inside it is not administrators-only, it is renamed aside (never deleted) and a fresh folder is used.
- **The installer and uninstaller load their PowerShell modules by full path** from the Windows PowerShell system folder, and run themselves again in Windows PowerShell 5.1 when they are started from PowerShell 7. A PowerShell 7 window used to hand its own module path to the 5.1 process, which then could not load `Microsoft.PowerShell.Security`. This is also what broke two of the installer-script tests on the 1.0.1 workflow run.
- The app's own check of its data folder now also recognises inherited entries stored with generic rights.
- Added a canary test that the standard-user CI job really runs without administrator rights, and tests for the cases above (inherited entries, a PowerShell-7-style module path, the permission check of a whole tree).

## 1.0.1 (tagged, never released: its workflow run failed; everything in it is in 1.0.2)

- **The installer and uninstaller no longer trust environment variables.** Locations come from Windows' known folders, `PATH` and `PSModulePath` are reset to the system folders, winget is only run from its own package folder, the install folder must be administrators-only before anything is copied, and the logon task is read back after registration to check that it points at the program below the real `Program Files`. The batch file starts PowerShell from the system folder by absolute path.
- **Protected folders.** `?` now stands for one character. A relative path, a bare folder name, a network path, a `..` or an unknown `%variable%` is ignored and flagged inline in Settings (a line that is valid but matches no folder right now gets a note).
- **Personal folders** (Documents, Desktop, Pictures, Videos, Music, Downloads, OneDrive) are looked up through the known-folder API for the interactive user, so a folder moved to another drive is protected where it really is.
- **CI.** Read-only workflow token; a separate release job that only uploads; a best-effort job that runs the tests marked "non-admin" as a freshly created standard user.

## 1.0.0

First public release.
