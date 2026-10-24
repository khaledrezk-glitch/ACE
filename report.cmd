@echo off
rem Creates an issue report for the ACE tool maintainers.
echo ACE Revit MCP - create an issue report
echo.
set /p ACE_NOTE=Describe the problem in one or two sentences, then press Enter: 
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0doctor.ps1" -Report -Kind issue -Note "%ACE_NOTE%"
echo.
pause
