@echo off
rem Parse the whole block before uninstall.ps1 removes this launcher.
(
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
    if errorlevel 1 (
        echo Uninstallation failed. Close CurseTorrent, including its tray icon, and try again.
        pause
        exit /b 1
    )
    pause
)
