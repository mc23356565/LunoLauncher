# 编译 + 生成签名更新清单（维护者用）
# 用法：
#   .\publish.ps1 -Version 0.1.1 -Key D:\keys\private-key.xml -Pub D:\keys\public-key.xml `
#                 -Urls "https://github.com/mc23356565/LunoLauncher/releases/download/v0.1.1/LunoLauncher.exe","https://<国内镜像>/LunoLauncher.exe" `
#                 [-Notes release\notes.md] [-Config release\config.json]
# 之后：把 dist\LunoLauncher.exe 上传到上面每个地址，把 release\update.json 提交到仓库（以及所有镜像）。
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Key,
    [Parameter(Mandatory)] [string]$Pub,
    [Parameter(Mandatory)] [string[]]$Urls,
    [string]$Notes,
    [string]$Config
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = "$root\src\OpenLauncher\OpenLauncher.csproj"

# 1. 写入版本号
$xml = Get-Content $csproj -Raw -Encoding UTF8
$xml = [regex]::Replace($xml, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $xml, (New-Object Text.UTF8Encoding $false))

# 2. 编译启动器和发布工具
& "$root\build.ps1"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
& $dotnet build "$root\tools\Release\OpenLauncher.Release.csproj" -c Release -nologo -v q | Out-Null
$tool = "$root\tools\Release\bin\Release\ol-release.exe"

# 3. 签名清单
$toolArgs = @('publish', '--exe', "$root\dist\LunoLauncher.exe", '--version', $Version, '--key', $Key, '--pub', $Pub, '--out', "$root\release\update.json")
foreach ($u in $Urls) { $toolArgs += @('--url', $u) }
if ($Notes) { $toolArgs += @('--notes', $Notes) }
if ($Config) { $toolArgs += @('--config', $Config) }
& $tool @toolArgs
if ($LASTEXITCODE -ne 0) { throw '签名失败' }
& $tool verify --in "$root\release\update.json" --pub $Pub --exe "$root\dist\LunoLauncher.exe"
