<#
    打发布 zip。

    用法：
        cd <本文件所在的 mod 目录>
        .\build.ps1
        .\release.ps1

    产物：
        .\dist\JustLilithA11y-<版本>.zip     ← 上传到 GitHub Releases

    发布包 = mod\package\ 的全部内容（BepInEx 6.0.0-be.788 IL2CPP 运行时 + 插件 +
    nvdaControllerClient.dll）+ 两份用户文档 + licenses\。

    本作是 Unity 2021.3.45f2c1 / IL2CPP，所以运行时必须是 BepInEx 6 的 be 版
    （BepInEx 5 只支持 Mono），见 build.ps1 的 $BepInExVersion。

    安装包里**不含安装程序**：补丁是 BepInEx 运行时挂载的，不修改游戏文件，
    安装就是「把 package\ 里的东西拷进游戏根目录」，手写说明足够。

    发布包不进版本库（见 .gitignore）：每次发版都会往历史里塞一个几十 MB
    的二进制，而 git 对 zip 没法做增量，重打包一次就多存一整份。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "D:\Steam\steamapps\common\The NOexistenceN of Lilith"
)

$ErrorActionPreference = "Stop"
$mod = $PSScriptRoot
$pkg = Join-Path $mod "package"
$out = Join-Path $mod "dist"

function Step($msg) { Write-Host "`n>>> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# ------------------------------------------------------------
Step "1/4  编译并组包"
& (Join-Path $mod "build.ps1") -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { Write-Host "build.ps1 失败" -ForegroundColor Red; exit 1 }

$dll = Join-Path $mod "src\JustLilithA11y\bin\Release\JustLilithA11y.dll"
if (-not (Test-Path -LiteralPath $dll)) { Write-Host "找不到 $dll" -ForegroundColor Red; exit 1 }
$ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
Ok "JustLilithA11y.dll 版本 $ver"

# ------------------------------------------------------------
Step "2/4  校验安装包内容"
$need = @(
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    # baselib.dll 不在 BepInEx 6 的发布 zip 里：它是首次启动时由
    # BepInEx.Unity.IL2CPP 生成的引导库（实机验证：装完包首次运行后出现在游戏根目录，
    # 时间戳与首次运行一致）。所以发布包不应包含它，这里也不校验。
    "dotnet",
    # 本作是 IL2CPP：核心库是 BepInEx.Core.dll + BepInEx.Unity.IL2CPP.dll，
    # Il2CppInterop 是 IL2CPP 特有的运行时（BepInEx 5/Mono 没有这两样）。
    "BepInEx\core\BepInEx.Core.dll",
    "BepInEx\core\BepInEx.Unity.IL2CPP.dll",
    "BepInEx\core\Il2CppInterop.Runtime.dll",
    "BepInEx\core\0Harmony.dll",
    "BepInEx\plugins\JustLilithA11y.dll",
    "BepInEx\plugins\nvdaControllerClient.dll",
    "nvdaControllerClient.dll",
    "licenses\THIRD-PARTY-NOTICES.txt"
)
$ok = $true
foreach ($n in $need) {
    $p = Join-Path $pkg $n
    if (Test-Path -LiteralPath $p) { Ok "[ok]   $n" }
    else { Write-Host "    [MISS] $n" -ForegroundColor Red; $ok = $false }
}

# 用户文档：按扩展名收集，避免在这个脚本里写中文文件名
$docs = @(Get-ChildItem $pkg -Filter "*.md" -File)
if ($docs.Count -lt 2) { Write-Host "    [MISS] package\ 里的用户文档不足两份" -ForegroundColor Red; $ok = $false }
foreach ($d in $docs) { Ok "[ok]   $($d.Name)" }

# 不该出现的东西
foreach ($bad in @("BepInEx\config", "BepInEx\plugins\JustLilithA11yProbe.dll")) {
    if (Test-Path -LiteralPath (Join-Path $pkg $bad)) {
        Write-Host "    [不该有] $bad" -ForegroundColor Red; $ok = $false
    }
}
if (-not $ok) { Write-Host "`n安装包校验未通过" -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------
Step "3/4  打 zip"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$zip = Join-Path $out "JustLilithA11y-$ver.zip"

# 不要用 Compress-Archive：Windows PowerShell 5.1 下它生成的条目名用反斜杠，
# Linux/macOS 解压会得到一坨名字里带反斜杠的平铺文件。
# tools\makezip.ps1 手工建条目，分隔符固定为 '/'。
& (Join-Path $mod "tools\makezip.ps1") -Source $pkg -Zip $zip
if (-not (Test-Path -LiteralPath $zip)) { Write-Host "zip 没生成" -ForegroundColor Red; exit 1 }

# 自检：不允许任何条目用反斜杠做分隔符
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
$total = $za.Entries.Count
$badN = @($za.Entries | Where-Object { $_.FullName -like '*\*' }).Count
$za.Dispose()

Ok $zip
Ok ("{0} MB, {1} 个条目" -f [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1), $total)
if ($badN -gt 0) {
    Write-Host "    $badN 个条目用了反斜杠分隔符" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------
Step "4/4  完成"
Write-Host @"

发布：把 $zip 上传到 GitHub Releases（tag 用 v$ver）。
用户拿到后解压，把里面的东西整体拷进游戏根目录即可。

"@ -ForegroundColor Cyan
