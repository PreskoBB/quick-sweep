@echo off
rem Double-click: asks for admin (UAC), then runs install.ps1 in a window that stays open so you can read the result.
rem The elevated PowerShell is started from its own system folder (not looked up through PATH), and install.ps1 itself ignores
rem the user's environment variables (ProgramFiles, PATH, ...) for everything it does with administrator rights.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0install.ps1\"'"
