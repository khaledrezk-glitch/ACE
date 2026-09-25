#Requires -Version 5.1
# Removes the ACE Revit MCP add-in, MCP server and Claude registrations.
# Your config and saved scripts (%APPDATA%\ACE-RevitMCP) are kept unless you pass -Purge.
param([switch]$Purge)
$ErrorActionPreference = 'Continue'
if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) { Write-Host "Close Revit first." -ForegroundColor Yellow; exit 1 }

$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
Remove-Item (Join-Path $addinRoot 'AceRevitMcp.addin') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $addinRoot 'AceRevitMcp') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ACE Revit MCP') -Recurse -Force -ErrorAction SilentlyContinue

$targets = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
    $targets += Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json'
}
foreach ($file in $targets) {
    if (-not (Test-Path $file)) { continue }
    try {
        $conf = Get-Content $file -Raw | ConvertFrom-Json
        if ($conf.mcpServers -and ($conf.mcpServers.PSObject.Properties.Name -contains 'ace-revit')) {
            $conf.mcpServers.PSObject.Properties.Remove('ace-revit')
            [IO.File]::WriteAllText($file, ($conf | ConvertTo-Json -Depth 32), (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Removed from $file"
        }
    } catch { Write-Host "Could not edit $file : $_" -ForegroundColor Yellow }
}
if (Get-Command claude -ErrorAction SilentlyContinue) { & claude mcp remove ace-revit --scope user 2>$null | Out-Null }
if ($Purge) { Remove-Item (Join-Path $env:APPDATA 'ACE-RevitMCP') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "ACE Revit MCP removed." -ForegroundColor Green
