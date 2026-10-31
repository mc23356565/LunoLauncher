# 重建 lib\cubiomes.dll（x64）。cubiomes 是 MIT 许可，见 NOTICE。
# 前提：MinGW-w64 的 gcc（只把 gcc.exe 放进去不够，cc1/as 加载不到依赖会静默失败）
#   → 把 mingw64\bin 加进 PATH，或用 -Gcc 指定，或设 $env:LUNO_GCC
# 提示：MSVC 编不了，finders.c 用了 C99 变长数组。
param(
    [string]$Gcc = $env:LUNO_GCC,
    [string]$Out = (Join-Path $PSScriptRoot '..\..\src\LunoLauncher\lib\cubiomes.dll')
)
$ErrorActionPreference = 'Stop'
# 没给路径就在 PATH 里找 gcc（不在仓库里写死本机目录）
if (-not $Gcc) {
    $cmd = Get-Command gcc -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $Gcc = $cmd.Source }
}
if (-not $Gcc -or -not (Test-Path $Gcc)) {
    throw '找不到 gcc：请把 MinGW-w64 的 bin 目录加进 PATH，或用 -Gcc 指定 gcc.exe 的完整路径。'
}
$env:PATH = ((Split-Path $Gcc -Parent) + ';' + $env:PATH)
$work = Join-Path $PSScriptRoot 'build'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

# 源码来自 https://github.com/xpple/cubiomes（唯一支持 26.x 的分支）
if (-not (Test-Path (Join-Path $work 'CMakeLists.txt'))) {
    git clone --depth 1 https://github.com/xpple/cubiomes $work
}
Copy-Item (Join-Path $PSScriptRoot 'luno_api.c') $work -Force

Push-Location $work
try {
    # 按 CMakeLists.txt 的清单编译（features/ 和 loot/ 里的文件不能漏，否则链接缺符号）
    $srcs = Select-String -Path 'CMakeLists.txt' -Pattern '(?:^|\s)([A-Za-z0-9_/]+\.c)\s*$' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $objs = @()
    $i = 0
    foreach ($s in $srcs) { $i++; $o = "obj_$i.o"; & $Gcc -c -O2 -fwrapv -w -I. -Ifeatures -Iloot -Itables $s -o $o; if ($LASTEXITCODE -ne 0) { throw "编译失败: $s" }; $objs += $o }
    & $Gcc -c -O2 -fwrapv -w -I. -Ifeatures luno_api.c -o luno_api.o
    if ($LASTEXITCODE -ne 0) { throw '封装编译失败' }
    $objs += 'luno_api.o'
    & $Gcc -shared -O2 -fwrapv -static-libgcc -static -o cubiomes.dll $objs
    if ($LASTEXITCODE -ne 0) { throw '链接失败' }
    Copy-Item cubiomes.dll $Out -Force
    "已输出 $Out（$([math]::Round((Get-Item $Out).Length/1KB)) KB）"
} finally { Pop-Location }
