@echo off
rem Double-click: asks for admin (UAC), then runs install.ps1 in a window that stays open so you can read the result.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0install.ps1\"'"
