@echo off
REM One-shot installer for the test laptop. Run from repo root as Administrator.
dotnet test VictusControl.sln -c Release || exit /b 1
dotnet publish src/VictusControl.Cli -c Release -o dist || exit /b 1
dotnet publish src/VictusControl.App -c Release -o dist || exit /b 1
cd dist
.\VictusControl.exe --system
echo.
echo Tray: run VictusControlTray.exe, tick "Start with Windows" in its menu.
