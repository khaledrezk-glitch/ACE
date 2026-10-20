#Requires -Version 5.1
# Removes the ACE Revit MCP add-in, MCP server and Claude registrations.
# Your config and saved scripts (%APPDATA%\ACE-RevitMCP) are kept unless you pass -Purge.
param([switch]$Purge)
$ErrorActionPreference = 'Continue'
if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) { Write-Host "Close Revit first." -ForegroundColor Yellow; exit 1 }

# Deletes a folder tree without following junctions or symbolic links (Remove-Item -Recurse in Windows PowerShell 5.1 can
# delete inside a junction's target): a link is removed as a link, never what it points to.
function Remove-Tree($path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) } else { [IO.File]::Delete($item.FullName) }
        return
    }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force)) { Remove-Tree $child.FullName }
        [IO.Directory]::Delete($item.FullName)
    } else {
        [IO.File]::SetAttributes($item.FullName, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($item.FullName)
    }
}

foreach ($year in '2024', '2025') {
    $addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    Remove-Item (Join-Path $addinRoot 'AceRevitMcp.addin') -Force -ErrorAction SilentlyContinue
    # The whole ACE folder: the ACE loader, current.json, every payload\<version>-<hash> build (and an older flat install).
    try { Remove-Tree (Join-Path $addinRoot 'AceRevitMcp') } catch { }
    if (Test-Path (Join-Path $addinRoot 'AceRevitMcp')) { Write-Host "Could not remove everything in $(Join-Path $addinRoot 'AceRevitMcp') (a file is in use?)." -ForegroundColor Yellow }
}
$reg = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\mcp-server\lib\register.js'
if ((Test-Path $reg) -and (Get-Command node -ErrorAction SilentlyContinue)) { & node $reg remove }
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
