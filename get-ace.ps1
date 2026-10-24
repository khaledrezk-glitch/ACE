#Requires -Version 5.1
# One-line install of ACE Revit MCP from GitHub (no git needed). Run in PowerShell:
#   irm https://raw.githubusercontent.com/khaledrezk-glitch/ACE/claude/brave-carson-vcjduc/get-ace.ps1 | iex
# Downloads the source, then runs install.ps1 (which builds the add-in on this PC; it installs the
# .NET 8 SDK and Node.js with winget if they're missing). For PCs without internet or admin rights,
# use the prebuilt team package (ACE-RevitMCP-<version>.zip) instead.
$ErrorActionPreference = 'Stop'
$branch = if ($env:ACE_BRANCH) { $env:ACE_BRANCH } else { 'claude/brave-carson-vcjduc' }
$zipUrl = "https://codeload.github.com/khaledrezk-glitch/ACE/zip/refs/heads/$branch"
$target = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\source'
$zip = Join-Path $env:TEMP 'ace-revit-mcp-source.zip'

Write-Host "Downloading ACE Revit MCP ($branch)..." -ForegroundColor Cyan
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing

$extract = Join-Path $env:TEMP 'ace-revit-mcp-source'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $extract
$root = Get-ChildItem $extract -Directory | Select-Object -First 1
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
Move-Item $root.FullName $target
Get-ChildItem $target -Recurse -File | Unblock-File

Write-Host "Running the installer..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $target 'install.ps1')
