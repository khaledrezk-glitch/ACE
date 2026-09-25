#Requires -Version 5.1
<#
.SYNOPSIS
  ACE Revit MCP doctor: checks every part of the setup, explains problems, fixes them, writes reports.

.EXAMPLE
  doctor.cmd                          # check everything, print results
  doctor.cmd -Fix                     # check, then repair what can be repaired automatically
  doctor.cmd -Report                  # check + write a Markdown health report
  doctor.cmd -Report -Note "Claude can't see my model since this morning"   # issue report
#>
param(
    [switch]$Fix,
    [switch]$Report,
    [string]$Note = '',
    [ValidateSet('health', 'issue', 'improvement')][string]$Kind = ''
)

$ErrorActionPreference = 'Continue'
Remove-TypeData System.Array -ErrorAction SilentlyContinue
$RevitYear = '2025'
$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppData   = $env:APPDATA
$DataDir   = Join-Path $AppData 'ACE-RevitMCP'
$CfgFile   = Join-Path $DataDir 'config.json'
$AddinRoot = Join-Path $AppData "Autodesk\Revit\Addins\$RevitYear"
$AddinDir  = Join-Path $AddinRoot 'AceRevitMcp'
$Manifest  = Join-Path $AddinRoot 'AceRevitMcp.addin'
$McpDir    = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\mcp-server'
$InfoFile  = Join-Path $env:LOCALAPPDATA 'ACE-RevitMCP\install-info.json'

$results = New-Object System.Collections.ArrayList
$fixes   = New-Object System.Collections.ArrayList   # scriptblocks / markers to run with -Fix

function Add-Result($status, $name, $detail, $fixText, $fixKey) {
    [void]$results.Add([PSCustomObject]@{ Status = $status; Name = $name; Detail = $detail; Fix = $fixText })
    if ($fixKey -and -not ($fixes -contains $fixKey)) { [void]$fixes.Add($fixKey) }
    $color = @{ OK = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; INFO = 'Gray' }[$status]
    Write-Host ("  {0,-4}  {1}: {2}" -f $status, $name, $detail) -ForegroundColor $color
    if ($fixText -and $status -ne 'OK') { Write-Host "        -> $fixText" -ForegroundColor DarkGray }
}

function Source-Dir {
    # Where the install package lives (needed to repair/reinstall).
    if (Test-Path (Join-Path $Here 'install.ps1')) { return $Here }
    if (Test-Path $InfoFile) {
        try { $s = (Get-Content $InfoFile -Raw | ConvertFrom-Json).source; if ($s -and (Test-Path (Join-Path $s 'install.ps1'))) { return $s } } catch { }
    }
    return $null
}

Write-Host "ACE Revit MCP doctor" -ForegroundColor White
Write-Host "Checking $([Environment]::MachineName) as $([Environment]::UserName)`n"

# --- System -------------------------------------------------------------------------------------
$os = [Environment]::OSVersion.Version
Add-Result 'INFO' 'Windows' ("{0} (PowerShell {1})" -f $os, $PSVersionTable.PSVersion)

$revitExe = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitYear\Revit.exe"
if (Test-Path $revitExe) {
    Add-Result 'OK' "Revit $RevitYear" ((Get-Item $revitExe).VersionInfo.ProductVersion)
} else {
    Add-Result 'FAIL' "Revit $RevitYear" "Not found at $revitExe" "Install Revit $RevitYear (the add-in only supports 2025)."
}

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    $nv = (& node --version).Trim()
    if ([int]($nv.TrimStart('v').Split('.')[0]) -ge 18) { Add-Result 'OK' 'Node.js' "$nv at $($node.Source)" }
    else { Add-Result 'FAIL' 'Node.js' "$nv is too old (need 18+)" 'Install Node.js LTS.' 'node' }
} else {
    Add-Result 'FAIL' 'Node.js' 'Not installed' 'Install Node.js LTS (winget install OpenJS.NodeJS.LTS).' 'node'
}

# --- Revit add-in -------------------------------------------------------------------------------
$revitProc = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
$addinOk = (Test-Path $Manifest) -and (Test-Path (Join-Path $AddinDir 'AceRevitMcp.dll')) -and (Test-Path (Join-Path $AddinDir 'Compiler\AceRevitMcp.Compiler.dll'))
if ($addinOk) {
    $ver = (Get-Item (Join-Path $AddinDir 'AceRevitMcp.dll')).VersionInfo.FileVersion
    $manifestText = Get-Content $Manifest -Raw
    if ($manifestText -notmatch [regex]::Escape((Join-Path $AddinDir 'AceRevitMcp.dll'))) {
        Add-Result 'FAIL' 'Add-in manifest' "Points to the wrong file: $Manifest" 'Reinstall the add-in.' 'addin'
    } else {
        Add-Result 'OK' 'Revit add-in' "v$ver in $AddinDir"
    }
    $blocked = Get-ChildItem $AddinDir -Recurse -File | Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }
    if ($blocked) { Add-Result 'FAIL' 'Blocked files' "$($blocked.Count) add-in file(s) are marked as downloaded from the internet; Revit may refuse to load them." 'Unblock them.' 'unblock' }
} else {
    Add-Result 'FAIL' 'Revit add-in' "Not installed (or incomplete) in $AddinDir" 'Install the add-in (needs Revit closed).' 'addin'
}

$others = Get-ChildItem $AddinRoot -Filter *.addin -ErrorAction SilentlyContinue | Where-Object Name -ne 'AceRevitMcp.addin' | ForEach-Object Name
if ($others) { Add-Result 'INFO' 'Other Revit add-ins' ($others -join ', ') }

if ($revitProc) {
    $loaded = $false
    try { $loaded = [bool]($revitProc.Modules | Where-Object ModuleName -like 'AceRevitMcp*') } catch { }
    if ($loaded) { Add-Result 'OK' 'Revit running' "Add-in loaded (Revit started $($revitProc.StartTime))" }
    elseif ($addinOk) { Add-Result 'WARN' 'Revit running' 'Add-in is installed but NOT loaded in this Revit session.' "Restart Revit; if asked about 'ACE Revit MCP', click 'Always Load'." }
    else { Add-Result 'WARN' 'Revit running' 'Add-in not loaded.' 'Install the add-in, then restart Revit.' }
} else {
    Add-Result 'INFO' 'Revit running' 'Revit is not open (the live connection checks below need it open with a model).'
}

# --- Config & port ------------------------------------------------------------------------------
$cfg = $null
if (Test-Path $CfgFile) { try { $cfg = Get-Content $CfgFile -Raw | ConvertFrom-Json } catch { } }
if ($cfg -and $cfg.token) {
    Add-Result 'OK' 'Config' "$CfgFile (port $($cfg.port))"
    if ($cfg.teamScriptsDir) {
        if (Test-Path $cfg.teamScriptsDir) { Add-Result 'OK' 'Team scripts' $cfg.teamScriptsDir }
        else { Add-Result 'WARN' 'Team scripts' "$($cfg.teamScriptsDir) not reachable" 'Connect to the network/OneDrive folder or fix teamScriptsDir in config.json.' }
    }
    $listener = Get-NetTCPConnection -LocalPort $cfg.port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        if ($owner -and $owner.ProcessName -ne 'Revit' -and $owner.ProcessName -ne 'System') {
            Add-Result 'FAIL' 'Port' "Port $($cfg.port) is used by '$($owner.ProcessName)', not Revit." 'Move ACE to a free port.' 'port'
        }
    }
} else {
    Add-Result 'FAIL' 'Config' "$CfgFile missing or invalid" 'Recreate it (then restart Revit).' 'config'
}

# --- MCP server & Claude ------------------------------------------------------------------------
$index = Join-Path $McpDir 'index.js'
if ((Test-Path $index) -and (Test-Path (Join-Path $McpDir 'node_modules\@modelcontextprotocol'))) {
    $mv = (Get-Content (Join-Path $McpDir 'package.json') -Raw | ConvertFrom-Json).version
    Add-Result 'OK' 'MCP server' "v$mv in $McpDir"
    $src = Source-Dir
    if ($src -and (Test-Path (Join-Path $src 'mcp-server\package.json'))) {
        $sv = (Get-Content (Join-Path $src 'mcp-server\package.json') -Raw | ConvertFrom-Json).version
        if ($sv -ne $mv) { Add-Result 'WARN' 'MCP server version' "Installed $mv, package has $sv" 'Update the MCP server.' 'mcp' }
    }
} else {
    Add-Result 'FAIL' 'MCP server' "Not installed (or dependencies missing) in $McpDir" 'Install the MCP server.' 'mcp'
}

$claudeConfigs = @(Join-Path $AppData 'Claude\claude_desktop_config.json')
Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
    $claudeConfigs += Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json'
}
$anyClaude = $false
foreach ($f in $claudeConfigs) {
    if (-not (Test-Path $f)) { continue }
    $anyClaude = $true
    try {
        $conf = Get-Content $f -Raw | ConvertFrom-Json
        $entry = $null
        if ($conf.mcpServers) { $entry = $conf.mcpServers.PSObject.Properties | Where-Object { $_.Name -ceq 'ace-revit' } | Select-Object -First 1 }
        if (-not $entry) { Add-Result 'FAIL' 'Claude Desktop' "No 'ace-revit' server in $f" 'Register it (then fully quit and reopen Claude Desktop).' 'mcp' }
        elseif (-not (Test-Path ($entry.Value.args | Select-Object -First 1))) { Add-Result 'FAIL' 'Claude Desktop' "'ace-revit' points to a missing file: $($entry.Value.args -join ' ')" 'Re-register it.' 'mcp' }
        elseif (-not (Test-Path $entry.Value.command)) { Add-Result 'FAIL' 'Claude Desktop' "'ace-revit' uses a missing Node.js: $($entry.Value.command)" 'Re-register it.' 'mcp' }
        else { Add-Result 'OK' 'Claude Desktop' "Registered in $f" }
    } catch {
        Add-Result 'FAIL' 'Claude Desktop' "$f is not valid JSON" "Fix or delete the file (a backup may exist next to it), then re-run the installer."
    }
}
if (-not $anyClaude) { Add-Result 'WARN' 'Claude Desktop' 'No Claude Desktop config found (is Claude Desktop installed and opened once?).' 'Install Claude Desktop from claude.ai/download, open it once, then run doctor -Fix.' 'mcp' }
if (Get-Command claude -ErrorAction SilentlyContinue) { Add-Result 'INFO' 'Claude Code CLI' 'Installed (ace-revit registered at user scope by the installer).' }

# --- Live checks through the MCP server's own diagnostics -----------------------------------------
if ($node -and (Test-Path (Join-Path $McpDir 'lib\diagnostics.js'))) {
    $json = & node (Join-Path $McpDir 'lib\diagnostics.js') check --json 2>$null | Out-String
    try {
        $live = $json | ConvertFrom-Json
        foreach ($c in $live | Where-Object { $_.name -in 'Revit connection', 'Revit session', 'Version match', 'Open model', 'C# compiler', 'Last 7 days', 'Script library' }) {
            Add-Result $c.status.ToUpper() $c.name $c.detail $c.fix
        }
    } catch { Add-Result 'WARN' 'Live checks' 'Could not run the MCP server diagnostics.' 'Reinstall the MCP server.' 'mcp' }
}

# --- Recent add-in errors -----------------------------------------------------------------------
$log = Join-Path $DataDir 'logs\addin.log'
if (Test-Path $log) {
    $errs = Get-Content $log -Tail 400 | Where-Object { $_ -match '\[ERROR\]' }
    if ($errs) { Add-Result 'INFO' 'Add-in log' "$(@($errs).Count) error line(s) recently; last: $((@($errs)[-1]).Substring(0, [Math]::Min(160, (@($errs)[-1]).Length)))" }
}

$failCount = @($results | Where-Object Status -eq 'FAIL').Count
$warnCount = @($results | Where-Object Status -eq 'WARN').Count
Write-Host ""
if ($failCount -eq 0 -and $warnCount -eq 0) { Write-Host "Everything looks good." -ForegroundColor Green }
else { Write-Host "$failCount problem(s), $warnCount warning(s)." -ForegroundColor Yellow }

# --- Fix ----------------------------------------------------------------------------------------
if ($Fix -and $fixes.Count -gt 0) {
    Write-Host "`nFixing..." -ForegroundColor Cyan
    $src = Source-Dir
    foreach ($k in $fixes) {
        switch ($k) {
            'unblock' { Get-ChildItem $AddinDir -Recurse -File | Unblock-File; Write-Host "  unblocked add-in files" }
            'node' {
                if (Get-Command winget -ErrorAction SilentlyContinue) {
                    winget install --id OpenJS.NodeJS.LTS -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
                    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
                } else { Write-Host "  winget missing: install Node.js LTS from https://nodejs.org" -ForegroundColor Yellow }
            }
            'config' {
                New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
                $bytes = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
                $token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
                $new = [PSCustomObject]@{ port = 48884; token = $token }
                [IO.File]::WriteAllText($CfgFile, ($new | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
                Write-Host "  recreated $CfgFile (restart Revit to use it)"
            }
            'port' {
                $port = 48885
                while (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { $port++ }
                $cfg.port = $port
                [IO.File]::WriteAllText($CfgFile, ($cfg | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
                Write-Host "  moved ACE to port $port (restart Revit to use it)"
            }
        }
    }
    if ($fixes -contains 'addin' -or $fixes -contains 'mcp') {
        if (-not $src) {
            Write-Host "  The install package was not found. Open the ACE-RevitMCP package folder and run install.cmd." -ForegroundColor Yellow
        } elseif ($fixes -contains 'addin' -and (Get-Process -Name Revit -ErrorAction SilentlyContinue)) {
            Write-Host "  The add-in needs reinstalling but Revit is open. Save your work, close Revit, and run doctor -Fix again." -ForegroundColor Yellow
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'install.ps1') -NonInteractive -SkipAddin
        } elseif ($fixes -contains 'addin') {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'install.ps1') -NonInteractive
        } else {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'install.ps1') -NonInteractive -SkipAddin
        }
    }
    Write-Host "`nDone. Re-run doctor to confirm, and fully quit + reopen Claude Desktop if it was registered again." -ForegroundColor Green
} elseif ($Fix) {
    Write-Host "Nothing to fix automatically."
}

# --- Report -------------------------------------------------------------------------------------
if ($Report -or $Note -or $Kind) {
    if (-not $Kind) { $Kind = $(if ($Note) { 'issue' } else { 'health' }) }
    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine('## Doctor (Windows checks)'); [void]$md.AppendLine('')
    [void]$md.AppendLine('| Status | Check | Result | Fix |'); [void]$md.AppendLine('|---|---|---|---|')
    foreach ($r in $results) { [void]$md.AppendLine(("| {0} | {1} | {2} | {3} |" -f $r.Status, $r.Name, ($r.Detail -replace '\|', '/'), ($r.Fix -replace '\|', '/'))) }
    $extra = Join-Path $env:TEMP 'ace-doctor.md'
    [IO.File]::WriteAllText($extra, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    if ($node -and (Test-Path (Join-Path $McpDir 'lib\diagnostics.js'))) {
        $out = & node (Join-Path $McpDir 'lib\diagnostics.js') report --kind $Kind --note $Note --extra $extra
        $file = @($out)[0]
        Write-Host "`nReport written to:`n  $file" -ForegroundColor Green
        if (@($out).Count -gt 1) { Write-Host "Also copied to the team folder:`n  $(@($out)[1])" -ForegroundColor Green }
        Write-Host "Send this file to the ACE tool maintainers (or attach it in Claude Code with: 'Work on this ACE Revit MCP report')."
        try { Start-Process explorer.exe "/select,`"$file`"" } catch { }
    } else {
        $file = Join-Path $DataDir ("reports\doctor-$([Environment]::MachineName)-{0:yyyy-MM-dd-HHmmss}.md" -f (Get-Date))
        New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
        [IO.File]::WriteAllText($file, "# ACE Revit MCP - Doctor report`n`n$Note`n`n" + $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "`nReport written to:`n  $file (MCP server missing, so only Windows checks are included)" -ForegroundColor Yellow
    }
}

if ($failCount -gt 0) { exit 1 } else { exit 0 }
