# Self-sign dist\LunoLauncher.exe to silence the local "unknown publisher" / SmartScreen prompt.
#
# Notes:
#   · The certificate is installed into the CURRENT USER stores (no admin needed), so
#     it is trusted only on THIS machine.
#   · Other people downloading the exe will still see a SmartScreen warning. Removing
#     that for everyone needs a real code signing certificate (Azure Trusted Signing or
#     a purchased OV certificate) - out of scope for this script.
#   · Re-run after every rebuild: signing changes the file hash.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\sign.ps1 [-Exe <path to exe>]

param(
    [string]$Exe = (Join-Path $PSScriptRoot 'dist\LunoLauncher.exe'),
    [string]$Subject = 'CN=LunoLauncher Dev',
    [string]$CerPath = (Join-Path $PSScriptRoot 'build\internal-codesign.cer')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "Cannot find $Exe - run build.ps1 first." }

# 1. Reuse or create a code-signing certificate in the current user store
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating self-signed code signing certificate: $Subject"
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName 'LunoLauncher self-signed (this machine only)' `
        -KeyUsage DigitalSignature `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
} else {
    Write-Host "Using existing certificate: $($cert.Thumbprint)"
}

# 2. Export the public certificate
New-Item -ItemType Directory -Force (Split-Path $CerPath) | Out-Null
Export-Certificate -Cert $cert -FilePath $CerPath -Force | Out-Null
Write-Host "Exported certificate: $CerPath"

# 3. Trust it for the current user (Root for chain, TrustedPublisher for SmartScreen)
foreach ($store in @('Root', 'TrustedPublisher')) {
    $target = "Cert:\CurrentUser\$store"
    $exists = Get-ChildItem $target -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if ($exists) { Write-Host "$store already has this certificate"; continue }
    try {
        Import-Certificate -FilePath $CerPath -CertStoreLocation $target -ErrorAction Stop | Out-Null
        Write-Host "Added to CurrentUser\$store"
    } catch {
        Write-Host "Failed to add to ${store}: $($_.Exception.Message)"
    }
}

# 4. Clear the "downloaded from the internet" mark, if any
try { Unblock-File -Path $Exe; Write-Host "Cleared the internet mark on the exe" } catch { }

# 5. Sign
Write-Host "Signing $Exe ..."
$result = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
if ($result.Status -ne 'Valid') {
    Write-Host "Signature status: $($result.Status) - $($result.StatusMessage)"
} else {
    Write-Host "Signed OK"
}

# 6. Verify
$check = Get-AuthenticodeSignature $Exe
Write-Host ""
Write-Host "Status : $($check.Status)"
Write-Host "Signer : $($check.SignerCertificate.Subject)"
Write-Host "SHA256 : $((Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower())"
Write-Host ""
Write-Host "Reminder: a self-signed certificate is trusted only on this machine."
