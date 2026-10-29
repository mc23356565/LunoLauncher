# Build the release exe -> dist\LunoLauncher.exe
# (single file; .NET Framework 4.8 ships with Windows 10/11, nothing to install)
#
# Needs .NET SDK 8 or newer (https://dotnet.microsoft.com/download). No Visual Studio.
# Usage: .\build.ps1            build + self-sign
#        .\build.ps1 -NoSign    build only

param(
    [switch]$NoSign,
    # Microsoft Client ID baked into the exe. Empty = read clientid.txt in the repo root, if any.
    [string]$MsClientId = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { throw 'dotnet not found: install the .NET SDK first.' }

# Client ID 以 Base64 形式注入，exe 里不会出现明文 GUID（挡自动化采集，不是加密）
$cidFile = Join-Path $root 'clientid.txt'
$cidValue = $MsClientId
if (-not $cidValue -and (Test-Path $cidFile)) { $cidValue = (Get-Content $cidFile -Raw).Trim() }

$buildArgs = @("$root\src\LunoLauncher\LunoLauncher.csproj", '-c', 'Release', '-nologo')
if ($cidValue) {
    $enc = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cidValue))
    $buildArgs += "-p:MsClientIdEncoded=$enc"
}
& $dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

try {
    Copy-Item "$root\src\LunoLauncher\bin\Release\LunoLauncher.exe" $dist -Force
    # The long-path switch is set in code at startup; the .config file is optional.
    Copy-Item "$root\src\LunoLauncher\bin\Release\LunoLauncher.exe.config" $dist -Force
} catch {
    throw "Cannot write to dist - is LunoLauncher still running? Close it and retry. $($_.Exception.Message)"
}

$exe = Get-Item "$dist\LunoLauncher.exe"

if (-not $NoSign -and (Test-Path "$root\sign.ps1")) {
    Write-Host 'Signing with the local self-signed certificate...'
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\sign.ps1" -Exe $exe.FullName | Out-Null
    } catch {
        Write-Host "Sign skipped: $($_.Exception.Message)"
    }
}

if ($cidValue) {
    $from = if ($MsClientId) { 'from -MsClientId' } else { 'from clientid.txt' }
    "ClientID: $($cidValue.Substring(0, [Math]::Min(8, $cidValue.Length)))... ($from, stored as Base64)"
} else {
    "ClientID: NOT injected - this build cannot sign in to Microsoft"
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$sig = Get-AuthenticodeSignature $exe.FullName
"Output : $($exe.FullName)"
"Size   : $([math]::Round($exe.Length / 1KB)) KB"
"SHA-256: $hash"
"Signed : $($sig.Status)"
