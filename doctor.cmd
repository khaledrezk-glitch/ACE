@echo off
rem ACE Revit MCP doctor. Examples:  doctor.cmd   |   doctor.cmd -Fix   |   doctor.cmd -Report
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0doctor.ps1" %*
echo.
pause
