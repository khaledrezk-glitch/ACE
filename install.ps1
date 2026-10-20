#Requires -Version 5.1
<#
.SYNOPSIS
  Makes this PC ready to control Revit 2024 and 2025 from Claude (Desktop or Claude Code) via MCP.

.DESCRIPTION
  1. Installs prerequisites if missing (.NET 8 SDK, Node.js LTS) using winget.
  2. Builds the ACE Revit add-in and registers it with each installed Revit (2024 and/or 2025; per-user, no admin needed).
     Revit 2024 gets the .NET Framework 4.8 build, which does not run C# scripts yet; all other tools work.
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
    [switch]$SkipAddin,  # only (re)install the MCP server + Claude registration; leaves Revit alone
    [string[]]$RevitYear # default: every supported Revit installed on this PC (2024, 2025)
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Silent) { $NonInteractive = $true }
# A release package ships a prebuilt add-in (bin\addin) and bundled Node dependencies, so team PCs
# need neither the .NET SDK nor internet access to npm. A source checkout builds on the machine.
# Prebuilt add-ins per Revit year: bin\addin\2024 (.NET Framework 4.8) and bin\addin\2025 (.NET 8). Packages before 1.9
# had only bin\addin (2025).
$Frameworks = @{ '2024' = 'net48'; '2025' = 'net8.0-windows' }
function Get-PrebuiltDir($year) {
    $d = Join-Path $Root "bin\addin\$year"
    if (Test-Path (Join-Path $d 'AceRevitMcp.dll')) { return $d }
    if ($year -eq '2025' -and (Test-Path (Join-Path $Root 'bin\addin\AceRevitMcp.dll'))) { return (Join-Path $Root 'bin\addin') }
    return $null
}
$Prebuilt = [bool](Get-PrebuiltDir '2025') -or [bool](Get-PrebuiltDir '2024')
$BundledNodeModules = Test-Path (Join-Path $Root 'mcp-server\node_modules\@modelcontextprotocol')
$PackageVersion = (Get-Content (Join-Path $Root 'mcp-server\package.json') -Raw | ConvertFrom-Json).version
if ($RevitYear) {
    $RevitYears = @($RevitYear | Where-Object { $Frameworks.ContainsKey($_) })
    if (-not $RevitYears) { throw "Supported Revit versions: 2024, 2025 (given: $($RevitYear -join ', '))." }
} else {
    $RevitYears = @('2024', '2025' | Where-Object { Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $_\Revit.exe") })
    if (-not $RevitYears) { $RevitYears = @('2025') }
}
$RevitLabel = $RevitYears -join ' and '

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
# Optional local code signing (trust-ace.ps1): signs ACE's own DLLs; any problem warns and the install carries on unsigned.
function Sign-AceDlls($dir) {
    try {
        $settingsFile = Join-Path $env:APPDATA 'ACE-RevitMCP\signing.json'
        if (-not (Test-Path $settingsFile)) { return }
        $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (-not $s.enabled -or -not $s.thumbprint) { return }
        $cert = Get-Item ("Cert:\CurrentUser\My\" + $s.thumbprint) -ErrorAction SilentlyContinue
        if (-not $cert -or -not $cert.HasPrivateKey) { Warn "Signing is on, but its certificate is not in CurrentUser\My: installed unsigned (run trust-ace.ps1 again)."; return }
        # Only ACE's own DLLs (the add-in and the script compiler), never third-party ones.
        $dlls = @(Get-ChildItem $dir -Recurse -File -Filter 'AceRevitMcp*.dll')
        if (-not $dlls.Count) { return }
        $ts = 'http://timestamp.digicert.com'
        $tsOk = $false
        try { $null = Invoke-WebRequest -Uri $ts -Method Head -UseBasicParsing -TimeoutSec 5; $tsOk = $true } catch { $tsOk = $false }
        if (-not $tsOk) { Warn "Timestamp server not reachable: signed without a timestamp (the signature stays valid while the certificate is)." }
        foreach ($f in $dlls) {
            $sig = $null
            try {
                if ($tsOk) { $sig = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $ts -ErrorAction Stop }
            } catch { $sig = $null }
            if (-not $sig -or $sig.Status -ne 'Valid') {
                try { $sig = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop } catch { Warn "Could not sign $($f.Name): $($_.Exception.Message)" }
            }
            $check = Get-AuthenticodeSignature -FilePath $f.FullName
            $rel = $f.FullName.Substring($dir.Length).TrimStart('\')
            if ($check.Status -eq 'Valid') { Ok "Signed $rel (Valid)" } else { Warn "$rel is $($check.Status) after signing; Revit will ask about it as an unsigned add-in." }
        }
    } catch {
        Warn "Signing skipped ($($_.Exception.Message)); the add-in is installed unsigned."
    }
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

Write-Host "ACE Revit MCP $PackageVersion installer (Revit $RevitLabel)" -ForegroundColor White
Write-Host $(if ($Prebuilt) { "    prebuilt package" } else { "    source checkout (the add-in is built on this PC)" })
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# ------------------------------------------------------------------------------------------------
if (-not $SkipAddin) {
Step "Checking Revit $RevitLabel"
# Revit may stay open: the ACE loader (the only DLL in the .addin manifest) loads each ACE build from its own folder
# (payload\<version>-<hash>), so an install never replaces a file Revit has in use. Only a new loader needs a restart.
$RevitProcs = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
$RunningYears = @()
foreach ($p in $RevitProcs) {
    $exe = $null
    try { $exe = $p.Path } catch { $exe = $null }
    if ($exe -and ($exe -match 'Revit (\d{4})')) { $RunningYears += $Matches[1] } else { $RunningYears += $RevitYears }
}
$RunningYears = @($RunningYears | Select-Object -Unique)
if ($RevitProcs.Count) { Write-Host "    Revit $($RunningYears -join ' and ') is running: that's fine, ACE is installed next to the copy in use." }
foreach ($year in $RevitYears) {
    $revitExe = Join-Path $env:ProgramFiles "Autodesk\Revit $year\Revit.exe"
    if (Test-Path $revitExe) { Ok "Revit $year found$(if ($year -eq '2024') { ' (C# scripts are not available on 2024 yet; all other tools work)' })" }
    else { Warn "Revit $year was not found at $revitExe. Continuing; the add-in will load once Revit $year is installed." }
}
foreach ($year in $RevitYears) {
    if ($Prebuilt -and -not (Get-PrebuiltDir $year)) { throw "This package has no add-in for Revit $year. Use a package built with ACE 1.9 or later." }
}

}

# ------------------------------------------------------------------------------------------------
if (-not $SkipAddin -and -not $Prebuilt) {
Step "Checking .NET 8 SDK (used to build the add-in)"
# Find a dotnet.exe that really has an SDK 8+. "dotnet" on PATH can be an older or x86 host without
# SDKs (or PATH is stale right after winget installs the SDK), so check every standard location.
function Find-DotnetSdk {
    $candidates = @()
    $candidates += @(Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    if ($env:USERPROFILE)  { $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe') }
    foreach ($exe in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        if (-not (Test-Path $exe)) { continue }
        $sdks = Invoke-Quiet { & $exe --list-sdks }
        if ($sdks -match '^(8|9|10)\.') { return $exe }
    }
    return $null
}
$Dotnet = Find-DotnetSdk
if (-not $Dotnet) {
    Winget-Install 'Microsoft.DotNet.SDK.8' '.NET 8 SDK'
    $Dotnet = Find-DotnetSdk
}
if (-not $Dotnet) {
    $seen = @(Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }) -join ', '
    throw ".NET 8 SDK is still not available (dotnet found at: $(if ($seen) { $seen } else { 'none' })). Close this window, open a NEW PowerShell and re-run; or install it from https://dotnet.microsoft.com/download/dotnet/8.0; or use the prebuilt team package, which needs no SDK."
}
# Quiet first-run experience: no welcome banner and no ASP.NET dev certificate (not needed for a Revit add-in).
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
# Make sure child processes (MSBuild) use the same SDK host.
$env:Path = (Split-Path $Dotnet) + ';' + $env:Path
Ok ".NET SDK $((& $Dotnet --version).Trim()) at $Dotnet"

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
$template = Join-Path $Root 'revit-addin\AceRevitMcp.addin.template'
if (-not (Test-Path $template)) { $template = Join-Path $Root 'bin\AceRevitMcp.addin.template' }
# Layout per Revit year in %APPDATA%\Autodesk\Revit\Addins\<year>\AceRevitMcp:
#   AceRevitMcp.Loader.dll       the ACE loader, named in AceRevitMcp.addin; replaced only when its version changes
#   current.json                 { version, path, sha256, previous } : the build the loader loads, and the fallback
#   payload\<version>-<hash>\    one folder per ACE build (AceRevitMcp.dll, Compiler\, Revit 2024 dependencies)
function Get-BuildHash($dir) {
    $all = (Get-ChildItem $dir -Recurse -File -Filter 'AceRevitMcp*.dll' | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ''
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return (([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($all)))) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Test-SigningOn {
    $f = Join-Path $env:APPDATA 'ACE-RevitMCP\signing.json'
    if (-not (Test-Path $f)) { return $false }
    try { $s = Get-Content $f -Raw | ConvertFrom-Json; return [bool]($s.enabled -and $s.thumbprint) } catch { return $false }
}
$RestartYears = @()
$ReloadYears = @()
foreach ($year in $RevitYears) {
    $AddinRoot    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $InstallDir   = Join-Path $AddinRoot 'AceRevitMcp'
    $ManifestFile = Join-Path $AddinRoot 'AceRevitMcp.addin'
    $PayloadRoot  = Join-Path $InstallDir 'payload'
    $CurrentFile  = Join-Path $InstallDir 'current.json'
    $LoaderDll    = Join-Path $InstallDir 'AceRevitMcp.Loader.dll'
    $running = $RunningYears -contains $year
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $stage = Join-Path $PayloadRoot ".staging-$tag"
    $loaderStage = Join-Path $InstallDir ".loader-$tag"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    New-Item -ItemType Directory -Force -Path $loaderStage | Out-Null
    try {
        if ($Prebuilt) {
            $src = Get-PrebuiltDir $year
            Get-ChildItem $src | Where-Object { $_.Name -notin '2024', '2025', 'loader' } | Copy-Item -Destination $stage -Recurse -Force
            $loaderSrc = Join-Path $src 'loader'
            if (-not (Test-Path (Join-Path $loaderSrc 'AceRevitMcp.Loader.dll'))) { throw "This package has no ACE loader for Revit $year (bin\addin\$year\loader). Use a package built with ACE 1.14 or later." }
            Copy-Item (Join-Path $loaderSrc '*') -Destination $loaderStage -Recurse -Force
        } else {
            & $Dotnet publish (Join-Path $Root 'revit-addin\AceRevitMcp\AceRevitMcp.csproj') -c Release -f $Frameworks[$year] -o $stage --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw "Building the add-in for Revit $year failed (see errors above)." }
            if ($year -ne '2024') {   # the script compiler runs on .NET 8 only (Revit 2025)
                & $Dotnet publish (Join-Path $Root 'revit-addin\AceRevitMcp.Compiler\AceRevitMcp.Compiler.csproj') -c Release -o (Join-Path $stage 'Compiler') --nologo -v quiet
                if ($LASTEXITCODE -ne 0) { throw "Building the script compiler failed (see errors above)." }
            }
            & $Dotnet publish (Join-Path $Root 'revit-addin\AceRevitMcp.Loader\AceRevitMcp.Loader.csproj') -c Release -f $Frameworks[$year] -o $loaderStage --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw "Building the ACE loader for Revit $year failed (see errors above)." }
        }
        Get-ChildItem $stage, $loaderStage -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

        # The payload: signed first (signing changes the file), then hashed, then moved into its own folder.
        Sign-AceDlls $stage
        $payloadDll = Join-Path $stage 'AceRevitMcp.dll'
        $sha = (Get-FileHash $payloadDll -Algorithm SHA256).Hash.ToLowerInvariant()
        $ver = (((Get-Item $payloadDll).VersionInfo.FileVersion).Split('.')[0..2]) -join '.'
        $folder = "$ver-" + (Get-BuildHash $stage).Substring(0, 8)
        $dest = Join-Path $PayloadRoot $folder
        if (Test-Path (Join-Path $dest 'AceRevitMcp.dll')) {
            # The identical build is there already (it may be the one Revit runs): keep it.
            $sha = (Get-FileHash (Join-Path $dest 'AceRevitMcp.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        } else {
            if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }   # an incomplete earlier copy
            Move-Item $stage $dest
        }

        $old = $null
        if (Test-Path $CurrentFile) { try { $old = Get-Content $CurrentFile -Raw | ConvertFrom-Json } catch { $old = $null } }
        $switch = $true
        if (Test-SigningOn) {
            $bad = @(Get-ChildItem $dest -Recurse -File -Filter 'AceRevitMcp*.dll' | Where-Object { (Get-AuthenticodeSignature -FilePath $_.FullName).Status -ne 'Valid' })
            if ($bad.Count -and $old -and $old.path) {
                Warn "Signing is on but $($bad[0].Name) is not validly signed: Revit $year keeps ACE $($old.version) (the loader would refuse this build)."
                $switch = $false
            }
        }
        if ($switch) {
            $entry = [ordered]@{ version = $ver; path = "payload\$folder"; sha256 = $sha }
            if ($old -and $old.path -and $old.path -ne $entry.path) { $entry.previous = [ordered]@{ version = $old.version; path = $old.path; sha256 = $old.sha256 } }
            elseif ($old -and $old.previous) { $entry.previous = $old.previous }
            $tmp = "$CurrentFile.tmp"
            Write-Utf8NoBom $tmp ([PSCustomObject]$entry | ConvertTo-Json -Depth 4)
            Move-Item $tmp $CurrentFile -Force
            Ok "ACE $ver for Revit $year installed to $dest"
        }

        # The loader: replaced only when its version differs (or signing is on and the installed one is not signed).
        $newLoader = (Get-Item (Join-Path $loaderStage 'AceRevitMcp.Loader.dll')).VersionInfo.FileVersion
        $oldLoader = $null
        if (Test-Path $LoaderDll) { $oldLoader = (Get-Item $LoaderDll).VersionInfo.FileVersion }
        $replace = $oldLoader -ne $newLoader
        if (-not $replace -and (Test-SigningOn) -and (Get-AuthenticodeSignature -FilePath $LoaderDll).Status -ne 'Valid') { $replace = $true }
        $loaderNew = $false
        if ($replace) {
            Sign-AceDlls $loaderStage
            try {
                Get-ChildItem $loaderStage -File | Copy-Item -Destination $InstallDir -Force -ErrorAction Stop
                $loaderNew = $true
                Ok "ACE loader $newLoader installed$(if ($oldLoader) { " (was $oldLoader)" })"
            } catch {
                Warn "The ACE loader $oldLoader is in use by Revit $year and was not replaced by $newLoader. Close Revit $year and run the installer again."
                $RestartYears += $year
            }
        }

        $manifest = (Get-Content $template -Raw).Replace('{{ASSEMBLY_PATH}}', $LoaderDll)
        $oldManifest = $null
        if (Test-Path $ManifestFile) { $oldManifest = Get-Content $ManifestFile -Raw }
        $wasDirect = $oldManifest -and ($oldManifest -notmatch 'AceRevitMcp\.Loader\.LoaderApp')
        if ($oldManifest -ne $manifest) { Write-Utf8NoBom $ManifestFile $manifest }

        if ($running) {
            if ($loaderNew -or $wasDirect -or -not $oldManifest) { $RestartYears += $year } elseif ($switch) { $ReloadYears += $year }
        }

        # With Revit closed: the files of the layout before the loader (AceRevitMcp.dll and its folders directly in
        # AceRevitMcp\) go, and of the builds only the current and previous stay. While Revit runs nothing is removed (it
        # may be running any of them).
        if (-not $running) {
            Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -notin 'payload', 'current.json' -and $_.Name -notlike 'AceRevitMcp.Loader.*' -and $_.Name -notlike '.loader-*' } |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }
        if (-not $running -and $switch) {
            $keep = @($folder)
            if ($entry.previous) { $keep += Split-Path ([string]$entry.previous.path) -Leaf }
            Get-ChildItem $PayloadRoot -Directory -Force | Where-Object { $keep -notcontains $_.Name -and $_.Name -notlike '.staging-*' } |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }
    } finally {
        Remove-Item $stage, $loaderStage -Recurse -Force -ErrorAction SilentlyContinue
        Get-ChildItem $PayloadRoot -Directory -Force -Filter '.staging-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$RestartYears = @($RestartYears | Select-Object -Unique)
if ($RestartYears.Count) {
    Warn "Restart Revit $($RestartYears -join ' and ') once to load ACE: the ACE loader is new or changed. Later updates load without a restart."
} elseif ($ReloadYears.Count) {
    Ok "No Revit restart needed. Ask Claude to reload ACE (reload_addin), or close all models in Revit: ACE then loads the new build by itself."
}

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

# Company branding (logo, colours) for the ribbon icons and the Companion panel.
$brandSrc = Join-Path $Root 'branding'
if (Test-Path $brandSrc) {
    $brandDst = Join-Path $CfgDir 'branding'
    New-Item -ItemType Directory -Force -Path $brandDst | Out-Null
    Copy-Item (Join-Path $brandSrc '*') -Destination $brandDst -Recurse -Force
    Ok "Branding installed to $brandDst"
}

# ------------------------------------------------------------------------------------------------
Step "Installing the MCP server"
$McpDir = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\mcp-server'
if (Test-Path $McpDir) { Remove-Item $McpDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $McpDir | Out-Null
foreach ($item in 'index.js', 'instructions.md', 'package.json', 'package-lock.json', 'lib', 'guides', 'scripts', 'lessons', 'bep') {
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
if (-not $SkipClaudeDesktop) {
    Step "Registering 'ace-revit' with Claude (Desktop chats + Claude Code sessions)"
    # A running Claude Desktop can rewrite its config from memory when it quits, dropping our entry,
    # so ask the user to quit it first. (Non-interactive installs register anyway; doctor -Fix repairs.)
    if (Get-Process -Name 'Claude' -ErrorAction SilentlyContinue) {
        if ($NonInteractive) {
            Warn "Claude Desktop is running. If the Revit tools don't appear after restarting it, run 'Check and fix ACE Revit'."
        } else {
            Warn "Claude Desktop is running. Quit it now: right-click the Claude icon near the clock > Quit."
            Read-Host "    Press Enter once Claude is closed"
        }
    }
    $IndexJs = Join-Path $McpDir 'index.js'
    & $NodeExe (Join-Path $McpDir 'lib\register.js') add --node $NodeExe --index $IndexJs | ForEach-Object { Write-Host "    $_" }
    if ($LASTEXITCODE -ne 0) { Warn "Some Claude configs could not be updated (see above). Run 'Check and fix ACE Revit' after closing Claude." }
    if (-not $SkipClaudeCode -and (Has claude)) {
        Invoke-Quiet { claude mcp remove ace-revit --scope user } | Out-Null
        Invoke-Quiet { claude mcp add ace-revit --scope user -- $NodeExe $IndexJs } | Out-Null
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
    foreach ($dir in 'bin', 'docs', 'branding') { if (Test-Path (Join-Path $Root $dir)) { Copy-Item (Join-Path $Root $dir) -Destination $PkgDir -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path (Join-Path $PkgDir 'mcp-server') | Out-Null
    Get-ChildItem (Join-Path $Root 'mcp-server') | Where-Object { $_.Name -ne 'test' } | Copy-Item -Destination (Join-Path $PkgDir 'mcp-server') -Recurse -Force
    if (-not $Prebuilt) {
        New-Item -ItemType Directory -Force -Path (Join-Path $PkgDir 'revit-addin') | Out-Null
        Copy-Item (Join-Path $Root 'revit-addin\AceRevitMcp.addin.template') -Destination (Join-Path $PkgDir 'revit-addin') -Force
        foreach ($proj in 'AceRevitMcp', 'AceRevitMcp.Compiler', 'AceRevitMcp.Loader') {
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
Write-Host "  1. Start Revit ($RevitLabel; one at a time with ACE). If it asks about the unsigned add-in 'ACE Revit MCP', click 'Always Load'."
Write-Host "  2. Open a model. The 'ACE' ribbon tab > 'MCP Status' shows the connection."
Write-Host "  3. (Re)start Claude Desktop, then ask e.g.: 'Give me an overview of the open Revit model.'"
Write-Host "  Problems? Start menu > ACE Revit MCP > 'Check and fix ACE Revit', or 'Report a problem'."
