#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the team release package: dist\ACE-RevitMCP-<version>.zip
  The package contains the PREBUILT add-in and bundled Node dependencies, so team PCs need
  neither the .NET SDK nor npm access. Requires (on the build machine only): .NET 8+ SDK, Node.js 18+.
  Put a team.json next to this script (copy team.example.json) to bake team settings into the package.
#>
param([string]$Out = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$version = (Get-Content (Join-Path $Root (Join-Path 'mcp-server' 'package.json')) -Raw | ConvertFrom-Json).version
$name = "ACE-RevitMCP-$version"
$stage = Join-Path $Out $name
$npm = if ($env:OS -eq 'Windows_NT') { 'npm.cmd' } else { 'npm' }

Write-Host "Building $name" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$addinOut = Join-Path (Join-Path $stage 'bin') 'addin'
& dotnet publish (Join-Path $Root (Join-Path 'revit-addin' (Join-Path 'AceRevitMcp' 'AceRevitMcp.csproj'))) -c Release -o $addinOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Add-in build failed' }
& dotnet publish (Join-Path $Root (Join-Path 'revit-addin' (Join-Path 'AceRevitMcp.Compiler' 'AceRevitMcp.Compiler.csproj'))) -c Release -o (Join-Path $addinOut 'Compiler') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Compiler build failed' }
Get-ChildItem $addinOut -Recurse -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Root (Join-Path 'revit-addin' 'AceRevitMcp.addin.template')) -Destination (Join-Path $stage 'bin')

$mcp = Join-Path $stage 'mcp-server'
New-Item -ItemType Directory -Force -Path $mcp | Out-Null
foreach ($item in 'index.js', 'instructions.md', 'package.json', 'package-lock.json', 'lib', 'guides', 'scripts') {
    Copy-Item (Join-Path (Join-Path $Root 'mcp-server') $item) -Destination $mcp -Recurse -Force
}
Push-Location $mcp
try {
    & $npm ci --omit=dev --no-audit --no-fund --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
} finally { Pop-Location }

foreach ($f in 'install.ps1', 'install.cmd', 'doctor.ps1', 'doctor.cmd', 'report.cmd', 'uninstall.ps1', 'test-connection.ps1',
               'README.md', 'USER-GUIDE.md', 'AGENT-GUIDE.md', 'REQUIREMENTS.md', 'TEAM-DEPLOYMENT.md', 'FIRST-TEST.md', 'CHANGELOG.md',
               'team.example.json', 'team.json') {
    $p = Join-Path $Root $f
    if (Test-Path $p) { Copy-Item $p -Destination $stage }
}

$zip = Join-Path $Out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$size = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Package ready: $zip ($size MB)" -ForegroundColor Green
Write-Host "Share it with the team: unzip, then double-click install.cmd."
