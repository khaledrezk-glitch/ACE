#Requires -Version 5.1
# Checks that Revit (with the ACE add-in) is reachable the same way the MCP server reaches it.
$ErrorActionPreference = 'Stop'
$cfgFile = Join-Path $env:APPDATA 'ACE-RevitMCP\config.json'
if (-not (Test-Path $cfgFile)) { Write-Host "No $cfgFile - run install.ps1 first." -ForegroundColor Red; exit 1 }
$cfg = Get-Content $cfgFile -Raw | ConvertFrom-Json
$base = "http://localhost:$($cfg.port)"
try {
    $health = Invoke-RestMethod "$base/health" -TimeoutSec 5
    Write-Host "Add-in is listening: Revit $($health.revitVersion)" -ForegroundColor Green
} catch {
    Write-Host "Revit is not reachable at $base. Is Revit 2025 open? Check the ACE tab > MCP Status." -ForegroundColor Red
    exit 1
}
$body = @{ command = 'ping'; args = @{} } | ConvertTo-Json
$r = Invoke-RestMethod "$base/command" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Ace-Token' = $cfg.token } -TimeoutSec 30
if ($r.ok) {
    Write-Host "Command round-trip OK. Active document: $($r.result.activeDocument)" -ForegroundColor Green
} else {
    Write-Host "Bridge answered with an error: $($r.error)" -ForegroundColor Yellow
}
if (Test-Path (Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\mcp-server\index.js')) { Write-Host "MCP server is installed." -ForegroundColor Green }
else { Write-Host "MCP server is NOT installed - run install.ps1." -ForegroundColor Red }
