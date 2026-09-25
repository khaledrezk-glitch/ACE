#Requires -Version 5.1
<#
.SYNOPSIS
  Makes this PC ready to control Revit 2025 from Claude (Desktop or Claude Code) via MCP.

.DESCRIPTION
  1. Installs prerequisites if missing (.NET 8 SDK, Node.js LTS) using winget.
  2. Builds the ACE Revit add-in and registers it with Revit 2025 (per-user, no admin needed).
  3. Installs the MCP server to %LOCALAPPDATA%\ACE-RevitMCP\mcp-server.
  4. Creates the shared config (%APPDATA%\ACE-RevitMCP\config.json: port + secret token).
  5. Registers the "ace-revit" MCP server in Claude Desktop and, if installed, Claude Code.

  Safe to re-run: it upgrades in place.

.EXAMPLE
  Double-click install.cmd, or:
  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [int]$Port = 48884,
    [switch]$SkipClaudeDesktop,
    [switch]$SkipClaudeCode,
    [switch]$NonInteractive,
    [switch]$Silent,     # for IT / mass deployment: no prompts (same as -NonInteractive)
    [switch]$SkipAddin   # only (re)install the MCP server + Claude registration; leaves Revit alone
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Silent) { $NonInteractive = $true }
# A release package ships a prebuilt add-in (bin\addin) and bundled Node dependencies, so team PCs
# need neither the .NET SDK nor internet access to npm. A source checkout builds on the machine.
$Prebuilt = Test-Path (Join-Path $Root 'bin\addin\AceRevitMcp.dll')
$BundledNodeModules = Test-Path (Join-Path $Root 'mcp-server\node_modules\@modelcontextprotocol')
$PackageVersion = (Get-Content (Join-Path $Root 'mcp-server\package.json') -Raw | ConvertFrom-Json).version
$RevitYear = '2025'

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    !!  $m" -ForegroundColor Yellow }
function Has($c)  { [bool](Get-Command $c -ErrorAction SilentlyContinue) }
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
# Runs a native command whose stderr we want to ignore. In Windows PowerShell 5.1, redirecting
# native stderr while $ErrorActionPreference = 'Stop' turns any stderr line into a terminating error.
function Invoke-Quiet([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>$null } finally { $ErrorActionPreference = $old }
}
function Write-Utf8NoBom($path, $content) {
    [IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($false)))
}
function Winget-Install($id, $label) {
    if (-not (Has winget)) {
        throw "winget is not available to install $label. Install it manually (or 'App Installer' from the Microsoft Store) and re-run."
    }
    Write-Host "    installing $label with winget (this can take a few minutes)..."
    winget install --id $id -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
    Refresh-Path
}

# Windows PowerShell 5.1 otherwise serialises some arrays as {"value":[...],"Count":n}.
Remove-TypeData System.Array -ErrorAction SilentlyContinue

Write-Host "ACE Revit MCP $PackageVersion installer (Revit $RevitYear)" -ForegroundColor White
Write-Host $(if ($Prebuilt) { "    prebuilt package" } else { "    source checkout (the add-in is built on this PC)" })
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# ------------------------------------------------------------------------------------------------
if (-not $SkipAddin) {
Step "Checking Revit $RevitYear"
$existingDll = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\AceRevitMcp\AceRevitMcp.dll"
if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    if (Test-Path $existingDll) {
        # Upgrade: Revit has the old add-in loaded and its files are locked.
        while (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
            if ($NonInteractive) { throw "Revit is running and has the current add-in loaded. Close Revit, then re-run the installer." }
            Warn "Revit is running. Save your work and close Revit so the add-in files can be updated."
            Read-Host "    Press Enter once Revit is closed"
        }
    } else {
        Warn "Revit is running. That's fine for a first install - restart Revit afterwards to load the add-in."
    }
}
$revitExe = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitYear\Revit.exe"
if (Test-Path $revitExe) { Ok "Revit $RevitYear found" }
else { Warn "Revit $RevitYear was not found at $revitExe. Continuing; the add-in will load once Revit $RevitYear is installed." }

}

# ------------------------------------------------------------------------------------------------
if (-not $SkipAddin -and -not $Prebuilt) {
Step "Checking .NET 8 SDK (used to build the add-in)"
$hasSdk = $false
if (Has dotnet) { $hasSdk = [bool]((Invoke-Quiet { dotnet --list-sdks }) -match '^(8|9|10)\.') }
if (-not $hasSdk) { Winget-Install 'Microsoft.DotNet.SDK.8' '.NET 8 SDK' }
if (-not (Has dotnet) -or -not ((Invoke-Quiet { dotnet --list-sdks }) -match '^(8|9|10)\.')) { throw ".NET 8 SDK is still not available. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run." }
Ok ".NET SDK $((& dotnet --version).Trim())"

}

Step "Checking Node.js 18+ (runs the MCP server)"
$nodeOk = $false
if (Has node) { $nodeOk = [int]((& node --version).TrimStart('v').Split('.')[0]) -ge 18 }
if (-not $nodeOk) { Winget-Install 'OpenJS.NodeJS.LTS' 'Node.js LTS' }
if (-not (Has node)) { throw "Node.js is still not available. Install it from https://nodejs.org and re-run." }
$NodeExe = (Get-Command node).Source
Ok "Node.js $((& node --version).Trim()) at $NodeExe"

# ------------------------------------------------------------------------------------------------
if (-not $SkipAddin) {
Step "Building and installing the Revit add-in"
$AddinRoot  = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
$InstallDir = Join-Path $AddinRoot 'AceRevitMcp'
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

if ($Prebuilt) {
    Copy-Item (Join-Path $Root 'bin\addin\*') -Destination $InstallDir -Recurse -Force
} else {
    & dotnet publish (Join-Path $Root 'revit-addin\AceRevitMcp\AceRevitMcp.csproj') -c Release -o $InstallDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building the add-in failed (see errors above)." }
    & dotnet publish (Join-Path $Root 'revit-addin\AceRevitMcp.Compiler\AceRevitMcp.Compiler.csproj') -c Release -o (Join-Path $InstallDir 'Compiler') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building the script compiler failed (see errors above)." }
}
Get-ChildItem $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$template = Join-Path $Root 'revit-addin\AceRevitMcp.addin.template'
if (-not (Test-Path $template)) { $template = Join-Path $Root 'bin\AceRevitMcp.addin.template' }
$manifest = (Get-Content $template -Raw).Replace('{{ASSEMBLY_PATH}}', (Join-Path $InstallDir 'AceRevitMcp.dll'))
Write-Utf8NoBom (Join-Path $AddinRoot 'AceRevitMcp.addin') $manifest
Ok "Add-in installed to $InstallDir"

}

# ------------------------------------------------------------------------------------------------
Step "Creating shared configuration"
$CfgDir  = Join-Path $env:APPDATA 'ACE-RevitMCP'
$CfgFile = Join-Path $CfgDir 'config.json'
New-Item -ItemType Directory -Force -Path (Join-Path $CfgDir 'scripts') | Out-Null
$cfg = $null
if (Test-Path $CfgFile) { try { $cfg = Get-Content $CfgFile -Raw | ConvertFrom-Json } catch { $cfg = $null } }
if (-not $cfg -or -not $cfg.token) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    $cfg = [PSCustomObject]@{ port = $Port; token = $token }
} elseif ($PSBoundParameters.ContainsKey('Port')) {
    $cfg.port = $Port
}
# Team settings (shared script library, shared report folder) from team.json in the package.
$teamFile = Join-Path $Root 'team.json'
if (Test-Path $teamFile) {
    $team = Get-Content $teamFile -Raw | ConvertFrom-Json
    foreach ($key in 'teamName', 'teamScriptsDir', 'teamReportsDir') {
        $value = $team.$key
        if ($value) {
            $value = [Environment]::ExpandEnvironmentVariables([string]$value)
            $cfg | Add-Member -NotePropertyName $key -NotePropertyValue $value -Force
        }
    }
    Ok "Team settings applied from team.json ($($team.teamName))"
}
Write-Utf8NoBom $CfgFile ($cfg | ConvertTo-Json)
Ok "Config at $CfgFile (port $($cfg.port))"

# ------------------------------------------------------------------------------------------------
Step "Installing the MCP server"
$McpDir = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\mcp-server'
if (Test-Path $McpDir) { Remove-Item $McpDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $McpDir | Out-Null
foreach ($item in 'index.js', 'instructions.md', 'package.json', 'package-lock.json', 'lib', 'guides', 'scripts') {
    Copy-Item (Join-Path $Root "mcp-server\$item") -Destination $McpDir -Recurse -Force
}
if ($BundledNodeModules) {
    Copy-Item (Join-Path $Root 'mcp-server\node_modules') -Destination $McpDir -Recurse -Force
} else {
    Push-Location $McpDir
    try {
        & npm.cmd install --omit=dev --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw "npm install failed (see errors above). Behind a proxy? Use the prebuilt release package, which bundles its dependencies." }
    } finally { Pop-Location }
}
& $NodeExe --check (Join-Path $McpDir 'index.js')
if ($LASTEXITCODE -ne 0) { throw "The installed MCP server has a syntax error." }
$IndexJs = Join-Path $McpDir 'index.js'
Ok "MCP server installed to $McpDir"

# ------------------------------------------------------------------------------------------------
function Register-ClaudeDesktop($file) {
    New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
    $conf = $null
    if (Test-Path $file) {
        Copy-Item $file "$file.bak" -Force
        $raw = Get-Content $file -Raw
        if ($raw -and $raw.Trim()) {
            try { $conf = $raw | ConvertFrom-Json }
            catch { Warn "$file is not valid JSON; left unchanged (backup: $file.bak). Add the server manually - see README."; return }
        }
    }
    if (-not $conf) { $conf = New-Object PSObject }
    if (-not ($conf.PSObject.Properties.Name -contains 'mcpServers') -or -not $conf.mcpServers) {
        $conf | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue (New-Object PSObject) -Force
    }
    $entry = [PSCustomObject]@{ command = $NodeExe; args = @($IndexJs) }
    $conf.mcpServers | Add-Member -NotePropertyName 'ace-revit' -NotePropertyValue $entry -Force
    Write-Utf8NoBom $file ($conf | ConvertTo-Json -Depth 32)
    Ok "Registered in Claude Desktop: $file"
}

if (-not $SkipClaudeDesktop) {
    Step "Registering with Claude Desktop"
    $targets = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    # Microsoft Store / MSIX installs keep their config in a virtualised folder.
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
        $targets += Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json'
    }
    foreach ($t in $targets) { Register-ClaudeDesktop $t }
    if (Get-Process -Name 'Claude' -ErrorAction SilentlyContinue) {
        Warn "Claude Desktop is running: fully quit it (tray icon > Quit) and reopen it to load the Revit tools."
    }
}

if (-not $SkipClaudeCode) {
    Step "Registering with Claude Code (if installed)"
    if (Has claude) {
        Invoke-Quiet { claude mcp remove ace-revit --scope user } | Out-Null
        & claude mcp add ace-revit --scope user -- $NodeExe $IndexJs | Out-Host
        if ($LASTEXITCODE -eq 0) { Ok "Registered in Claude Code (user scope)" } else { Warn "claude mcp add failed; run it manually (see README)." }
    } else {
        Write-Host "    Claude Code CLI not found - skipped. (Claude Desktop is enough.)"
    }
}

# ------------------------------------------------------------------------------------------------
Step "Installing support tools (doctor, issue reports, shortcuts)"
$AceLocal = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP'
$PkgDir = Join-Path $AceLocal 'package'
if ((Resolve-Path $Root).Path.TrimEnd('\') -ne $PkgDir) {
    # Keep a private copy of this package so doctor -Fix can always repair, even if the download is deleted.
    if (Test-Path $PkgDir) { Remove-Item $PkgDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PkgDir | Out-Null
    Get-ChildItem $Root -File | Where-Object { $_.Extension -in '.ps1', '.cmd', '.md', '.json' } | Copy-Item -Destination $PkgDir -Force
    foreach ($dir in 'bin', 'docs') { if (Test-Path (Join-Path $Root $dir)) { Copy-Item (Join-Path $Root $dir) -Destination $PkgDir -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path (Join-Path $PkgDir 'mcp-server') | Out-Null
    Get-ChildItem (Join-Path $Root 'mcp-server') | Where-Object { $_.Name -ne 'test' } | Copy-Item -Destination (Join-Path $PkgDir 'mcp-server') -Recurse -Force
    if (-not $Prebuilt) {
        New-Item -ItemType Directory -Force -Path (Join-Path $PkgDir 'revit-addin') | Out-Null
        Copy-Item (Join-Path $Root 'revit-addin\AceRevitMcp.addin.template') -Destination (Join-Path $PkgDir 'revit-addin') -Force
        foreach ($proj in 'AceRevitMcp', 'AceRevitMcp.Compiler') {
            $target = Join-Path $PkgDir "revit-addin\$proj"
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Get-ChildItem (Join-Path $Root "revit-addin\$proj") -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
                $rel = $_.FullName.Substring((Join-Path $Root "revit-addin\$proj").Length)
                $dest = Join-Path $target $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                Copy-Item $_.FullName -Destination $dest -Force
            }
        }
    }
}
$info = [PSCustomObject]@{ version = $PackageVersion; source = $PkgDir; installedFrom = (Resolve-Path $Root).Path; installedAt = (Get-Date).ToString('s'); prebuilt = $Prebuilt }
Write-Utf8NoBom (Join-Path $AceLocal 'install-info.json') ($info | ConvertTo-Json)

try {
    $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ACE Revit MCP'
    New-Item -ItemType Directory -Force -Path $menu | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    function New-Shortcut($name, $target, $arguments, $description) {
        $lnk = $shell.CreateShortcut((Join-Path $menu "$name.lnk"))
        $lnk.TargetPath = $target; $lnk.Arguments = $arguments; $lnk.WorkingDirectory = $PkgDir; $lnk.Description = $description
        $lnk.Save()
    }
    New-Shortcut 'Check and fix ACE Revit' (Join-Path $PkgDir 'doctor.cmd') '-Fix' 'Check every part of the ACE Revit setup and repair problems'
    New-Shortcut 'Report a problem (ACE Revit)' (Join-Path $PkgDir 'report.cmd') '' 'Create an issue report for the ACE tool maintainers'
    if (Test-Path (Join-Path $PkgDir 'USER-GUIDE.md')) { New-Shortcut 'ACE Revit user guide' 'notepad.exe' ('"' + (Join-Path $PkgDir 'USER-GUIDE.md') + '"') 'How to use Claude with Revit' }
    Ok "Start menu: 'ACE Revit MCP' (Check and fix / Report a problem / User guide)"
} catch {
    Warn "Could not create Start menu shortcuts: $($_.Exception.Message)"
}

# ------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "Done! Next steps:" -ForegroundColor Green
Write-Host "  1. Start Revit $RevitYear. If it asks about the unsigned add-in 'ACE Revit MCP', click 'Always Load'."
Write-Host "  2. Open a model. The 'ACE' ribbon tab > 'MCP Status' shows the connection."
Write-Host "  3. (Re)start Claude Desktop, then ask e.g.: 'Give me an overview of the open Revit model.'"
Write-Host "  Problems? Start menu > ACE Revit MCP > 'Check and fix ACE Revit', or 'Report a problem'."
