@echo off
rem Double-click to install ACE Revit MCP (Claude <-> Revit 2025).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
