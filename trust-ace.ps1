#Requires -Version 5.1
<#
.SYNOPSIS
  Optional: stop Revit's "Unsigned Add-In" prompt for ACE on this PC by signing the ACE DLLs with a local certificate.

.DESCRIPTION
  Run once per user (no administrator rights needed: everything goes into the CurrentUser stores).
  1. Creates a code-signing certificate "CN=ACE RevitMCP (local, <COMPUTERNAME>)" in Cert:\CurrentUser\My (RSA 3072,
     SHA256, valid 5 years). Its private key is NOT exportable: it never leaves this PC. An existing one is reused.
  2. Adds the certificate's public part to CurrentUser\TrustedPublisher and CurrentUser\Root, so Windows and Revit
     trust DLLs signed with it. Adding to Root shows a Windows security prompt; answer Yes.
  3. Saves the thumbprint to %APPDATA%\ACE-RevitMCP\signing.json. install.ps1 then signs the ACE DLLs (only ACE's own,
     never third-party DLLs) after each install or update.

  -Remove undoes all of it: the certificate leaves all three stores (removing it from Root shows a Windows prompt
  again) and signing is turned off. The next install.ps1 run installs unsigned DLLs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\trust-ace.ps1
  powershell -ExecutionPolicy Bypass -File .\trust-ace.ps1 -Remove
#>
param(
    [switch]$Remove,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$Subject  = "CN=ACE RevitMCP (local, $env:COMPUTERNAME)"
$DataDir  = Join-Path $env:APPDATA 'ACE-RevitMCP'
$Settings = Join-Path $DataDir 'signing.json'

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    !!  $m" -ForegroundColor Yellow }

function Open-Store($name) {
    $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $s.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    return $s
}
function Find-InStore($name, $thumb) {
    $s = Open-Store $name
    try { return @($s.Certificates | Where-Object { $_.Thumbprint -eq $thumb }) } finally { $s.Close() }
}
function Add-PublicPart($name, $cert) {
    # Only the public part goes into the trust stores; the private key stays in CurrentUser\My.
    $pub = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, $cert.RawData)
    $s = Open-Store $name
    try { $s.Add($pub) } finally { $s.Close() }
}
function Remove-FromStore($name, $thumb) {
    $s = Open-Store $name
    try {
        $found = @($s.Certificates | Where-Object { $_.Thumbprint -eq $thumb })
        foreach ($c in $found) { $s.Remove($c) }
        return $found.Count
    } finally { $s.Close() }
}
function Save-Settings($obj) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $json = $obj | ConvertTo-Json
    [IO.File]::WriteAllText($Settings, $json, (New-Object System.Text.UTF8Encoding($false)))
}
function Read-Settings {
    if (-not (Test-Path $Settings)) { return $null }
    try { return Get-Content $Settings -Raw | ConvertFrom-Json } catch { return $null }
}
function Our-Certs {
    # Ours: the subject this script uses, with a private key, for code signing.
    return @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey })
}

# ------------------------------------------------------------------------------------------------
if ($Remove) {
    Step "Removing the ACE signing certificate and turning signing off"
    $thumbs = @()
    $saved = Read-Settings
    if ($saved -and $saved.thumbprint) { $thumbs += [string]$saved.thumbprint }
    foreach ($c in (Our-Certs)) { if ($thumbs -notcontains $c.Thumbprint) { $thumbs += $c.Thumbprint } }
    if (-not $thumbs.Count) { Ok "No ACE signing certificate found: nothing to remove." }
    foreach ($t in $thumbs) {
        foreach ($store in 'TrustedPublisher', 'Root') {
            if ($store -eq 'Root' -and (Find-InStore 'Root' $t).Count) { Write-Host "    Windows will now ask to delete the certificate from the trusted root store: answer Yes." -ForegroundColor Gray }
            try { $n = Remove-FromStore $store $t; if ($n) { Ok "Removed $t from CurrentUser\$store" } }
            catch { Warn "Could not remove $t from CurrentUser\$store ($($_.Exception.Message)). Remove it in certmgr.msc." }
        }
        $mine = "Cert:\CurrentUser\My\$t"
        if (Test-Path $mine) {
            try { Remove-Item $mine -DeleteKey -ErrorAction Stop; Ok "Removed $t and its private key from CurrentUser\My" }
            catch { Warn "Could not remove $t from CurrentUser\My ($($_.Exception.Message)). Remove it in certmgr.msc." }
        }
    }
    Save-Settings ([ordered]@{ enabled = $false; thumbprint = $null; subject = $Subject; removed = (Get-Date).ToString('s') })
    Ok "Signing is off ($Settings). The next install.ps1 run installs the ACE DLLs unsigned; Revit asks about them again."
    exit 0
}

# ------------------------------------------------------------------------------------------------
Step "What this does (once, for $env:USERNAME on $env:COMPUTERNAME)"
Write-Host "    - creates (or reuses) a code-signing certificate '$Subject' in your personal store;"
Write-Host "      its private key cannot be exported: it never leaves this PC."
Write-Host "    - trusts it for you only: TrustedPublisher and Root (current user, no administrator rights)."
Write-Host "      Adding it to Root makes Windows show a security warning about installing a certificate: answer Yes."
Write-Host "    - install.ps1 then signs ACE's own DLLs with it; Revit's dialog offers 'Always Load' once for this publisher."
Write-Host "    - undo any time: trust-ace.ps1 -Remove"
if (-not $NonInteractive) {
    $answer = Read-Host "`nContinue? (Y/N)"
    if ($answer -notmatch '^(y|yes)$') { Write-Host "Nothing changed."; exit 0 }
}

Step "Code-signing certificate"
$cert = Our-Certs | Where-Object { $_.NotAfter -gt (Get-Date).AddDays(30) } | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    Ok "Reusing $($cert.Thumbprint) (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))"
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'ACE RevitMCP local code signing'
    Ok "Created $($cert.Thumbprint) (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')); private key not exportable)"
}

Step "Trusting it for $env:USERNAME"
foreach ($store in 'TrustedPublisher', 'Root') {
    if ((Find-InStore $store $cert.Thumbprint).Count) { Ok "Already in CurrentUser\$store"; continue }
    if ($store -eq 'Root') { Write-Host "    Windows will now show a security warning about installing this certificate: answer Yes." -ForegroundColor Gray }
    try { Add-PublicPart $store $cert; Ok "Added to CurrentUser\$store" }
    catch { Warn "Not added to CurrentUser\$store ($($_.Exception.Message)). Signed DLLs will not be trusted until it is; run this script again." }
}

Save-Settings ([ordered]@{ enabled = $true; thumbprint = $cert.Thumbprint; subject = $Subject; notAfter = $cert.NotAfter.ToString('s'); created = (Get-Date).ToString('s') })
Ok "Saved the thumbprint to $Settings"

Write-Host "`nDone. Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "Next: run install.ps1 (or doctor.cmd -Fix) so the ACE DLLs are signed; Revit then asks once per publisher: choose 'Always Load'."
Write-Host "Undo: powershell -ExecutionPolicy Bypass -File .\trust-ace.ps1 -Remove"
