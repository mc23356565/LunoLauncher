# Build the release exe -> dist\LunoLauncher.exe
# (single file; .NET Framework 4.8 ships with Windows 10/11, nothing to install)
#
# Needs .NET SDK 8 or newer (https://dotnet.microsoft.com/download). No Visual Studio.
# Usage: .\build.ps1            build + self-sign
#        .\build.ps1 -NoSign    build only

param([switch]$NoSign)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { throw 'dotnet not found: install the .NET SDK first.' }

& $dotnet build "$root\src\LunoLauncher\LunoLauncher.csproj" -c Release -nologo
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

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$sig = Get-AuthenticodeSignature $exe.FullName
"Output : $($exe.FullName)"
"Size   : $([math]::Round($exe.Length / 1KB)) KB"
"SHA-256: $hash"
"Signed : $($sig.Status)"
