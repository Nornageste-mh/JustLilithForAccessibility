#Requires -Version 5.1
<#
    平台层自检 —— 证明 L1（platform\）**没有任何游戏类型依赖**。

    做法：把 platform\*.cs 与 contract\A11yHost.cs 拷进一个临时工程编译，
    引用 UnityEngine / TMPro / BepInEx，**故意不引用 Assembly-CSharp**。

        编得过  ⇒ 平台层确实只依赖引擎与 BepInEx，没有偷偷引用游戏类型。

    这就是《无障碍补丁流水线》§8 出口检查清单里
    「共享层无任何游戏类型引用」那一条的**机械验证**，而不是靠人眼看。

    同时它还会断言 platform\ 里没有任何 Plugin. 残留 ——
    有残留说明接口没有全部改指 A11yHost，逐作拷贝时会编译失败。

    用法：
        .\tools\compile_check.ps1
        .\tools\compile_check.ps1 -GameDir "D:\Steam\steamapps\common\The NOexistenceN of Lilith"
        .\tools\compile_check.ps1 -Keep          # 保留临时工程，便于看编译错误

    注意（逐作仓库）：本脚本属于 **A11yFramework 仓库**。逐作骨架里平台层已经和
    逐作代码合并在 mod\src\<Plugin>\ 同一个工程里编译，没有 platform\ 与 contract\，
    所以在本仓库跑它会自己报「找不到 platform\」并 Fail —— 那是正确行为，不是坏了。
    逐作仓库的编译检查请用 mod\build.ps1 的第 4/5 步。

    注意：本脚本必须保存为 UTF-8 **带 BOM** —— Windows PowerShell 5.1
    读无 BOM 的脚本会按 GBK 解码，中文注释变乱码并直接语法错误（流水线 G5）。
#>
[CmdletBinding()]
param(
    # 借一个已安装的游戏来取 UnityEngine / TMPro / BepInEx 的引用。
    # 平台层不引用任何游戏类型，所以用哪个游戏都可以。
    # 默认读环境变量 A11Y_GAMEDIR；也可以用 -GameDir 显式指定。
    [string]$GameDir = "$env:A11Y_GAMEDIR",
    [string]$BepInExCore = "",
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$platform = Join-Path $root "platform"
$contract = Join-Path $root "contract"
$work = Join-Path $root ".compilecheck"
$src = Join-Path $work "src"
$NL = [Environment]::NewLine

function Fail($msg) { Write-Host ""; Write-Host "FAIL  $msg" -ForegroundColor Red; exit 1 }
function Ok($msg)   { Write-Host "  ok  $msg" -ForegroundColor Green }

Write-Host ""
Write-Host "=== A11yFramework 平台层自检 ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1/4 静态断言
Write-Host ""
Write-Host "[1/4] 静态断言：Plugin. 引用 / namespace / partial / **重读缓冲区是唯一出口**" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $platform)) { Fail "找不到 platform\（请在 A11yFramework 仓库根目录下运行）" }

# 只看**注释之外**的代码。源文件里保留了「原来引用的是 Plugin.*」这类说明性注释，
# 人眼看是正常的，不该被机械检查判成违规。
$bad = @()
foreach ($f in Get-ChildItem $platform -Filter *.cs) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
        $code = $line
        $i = $code.IndexOf('//')
        if ($i -ge 0) { $code = $code.Substring(0, $i) }
        if ($code -match 'Plugin\.') { $n++ }
    }
    if ($n -gt 0) { $bad += ("{0} 里 {1} 处" -f $f.Name, $n) }
}
if ($bad.Count -gt 0) {
    Fail ("platform\ 的代码里还有 Plugin. 引用: " + ($bad -join "; ") + " —— 平台层只允许引用 A11yHost.*")
}
Ok "platform\*.cs 无 Plugin. 引用"

foreach ($f in Get-ChildItem $platform -Filter *.cs) {
    if (-not (Select-String -LiteralPath $f.FullName -Pattern 'namespace A11yFramework' -Quiet)) {
        Fail ("{0} 的 namespace 不是 A11yFramework —— 平台层被改过了" -f $f.Name)
    }
}
Ok "platform\*.cs 的 namespace 都是 A11yFramework"

if (-not (Select-String -LiteralPath (Join-Path $platform "UiNav.cs") -Pattern 'internal static partial class UiNav' -Quiet)) {
    Fail "UiNav.cs 不是 partial class —— 逐作区无法挂上去"
}
Ok "UiNav 已拆成 partial（引擎 + 逐作区）"

# L1 文件清单必须齐（0.2.0.0 加的这两条）。
# 为什么做成机械断言：assemble_from_sources.py 会**先删掉 platform\ 再按自己的清单重建**，
# new_game.py 也按自己的清单往新作拷 —— 清单漏一个文件，平台层的新能力就被静默删掉，
# 而且 git status 看起来还挺干净（流水线 §1.3 的「平台层单向退化」）。
foreach ($need in @("Speech.cs", "Nvda.cs", "Zdsr.cs", "Sapi.cs", "Hotkey.cs",
                    "UiNav.cs", "KeyEdge.cs", "Repeat.cs")) {
    if (-not (Test-Path -LiteralPath (Join-Path $platform $need))) {
        Fail ("platform\{0} 不在了 —— L1 清单被改过，或被某个只认旧清单的脚本重建过" -f $need)
    }
}
Ok "L1 文件清单齐全（含 Zdsr.cs / Hotkey.cs）"

if (-not (Select-String -LiteralPath (Join-Path $platform "Speech.cs") -Pattern 'CycleAvailable' -Quiet)) {
    Fail "Speech.cs 里没有 CycleAvailable —— 运行中切换后端的能力被删掉了"
}
if (-not (Select-String -LiteralPath (Join-Path $platform "Zdsr.cs") -Pattern 'GetSpeakState' -Quiet)) {
    Fail "Zdsr.cs 里没有 GetSpeakState —— 争渡后端的可用性判据被改掉了（判据必须是 3/4）"
}
if (-not (Select-String -LiteralPath (Join-Path $platform "Hotkey.cs") -Pattern 'IsAllDigits' -Quiet)) {
    Fail "Hotkey.cs 里没有纯数字防护（流水线 G3：TryParse(""0"") 会成功但得到 KeyCode.None）"
}
Ok "争渡后端 / 切换后端 / 按键解析三个能力都在"

# 重读缓冲区只允许一个出口：platform 下只有 Repeat.cs 能调 Speech.Speak。
# 这个坑踩过两次（《钟塔》0.1.0.1「选项播报绕过了缓冲区」/《妹控计划》0.1.0.0 同款），
# 所以做成机械断言，而不是再写一遍复盘。
$lint = Join-Path $PSScriptRoot "lint_repeat.ps1"
if (Test-Path -LiteralPath $lint) {
    & $lint -Path $platform -AllowFile "Repeat.cs"
    if ($LASTEXITCODE -ne 0) { Fail "平台层里有绕过重读缓冲区的播报 —— 逐作层将听不到它" }
} else {
    Write-Host "  （找不到 lint_repeat.ps1，跳过重读出口检查）" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 2/4 找引用
Write-Host ""
Write-Host "[2/4] 解析引用（GameDir = $GameDir）" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $GameDir)) { Fail "找不到游戏目录: $GameDir（用 -GameDir 指定）" }

$managed = Get-ChildItem -LiteralPath $GameDir -Directory -Filter "*_Data" |
           ForEach-Object { Join-Path $_.FullName "Managed" } |
           Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $managed) { Fail "在 $GameDir 下找不到 *_Data\Managed" }
Ok "Managed: $managed"

if ([string]::IsNullOrEmpty($BepInExCore)) { $BepInExCore = Join-Path $GameDir "BepInEx\core" }
if (-not (Test-Path -LiteralPath (Join-Path $BepInExCore "BepInEx.dll"))) {
    Fail "找不到 $BepInExCore\BepInEx.dll（用 -BepInExCore 指定）"
}
Ok "BepInEx core: $BepInExCore"

$refs = @()
foreach ($n in @("BepInEx.dll", "0Harmony.dll")) {
    $p = Join-Path $BepInExCore $n
    if (Test-Path -LiteralPath $p) { $refs += $p } else { Fail "缺少 $p" }
}
foreach ($n in @("UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UIModule.dll",
                 "UnityEngine.UI.dll", "UnityEngine.InputLegacyModule.dll",
                 "UnityEngine.TextRenderingModule.dll", "Unity.TextMeshPro.dll")) {
    $p = Join-Path $managed $n
    if (Test-Path -LiteralPath $p) { $refs += $p } else { Fail "缺少 $p" }
}
Ok ("引用 {0} 个程序集，**不含 Assembly-CSharp.dll**" -f $refs.Count)

# ---------------------------------------------------------------- 3/4 造临时工程
Write-Host ""
Write-Host "[3/4] 生成临时工程 .compilecheck\" -ForegroundColor Cyan
if (Test-Path -LiteralPath $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $src | Out-Null

foreach ($f in Get-ChildItem $platform -Filter *.cs) { Copy-Item $f.FullName $src }
Copy-Item (Join-Path $contract "A11yHost.cs") $src

# UiNav 的逐作区：这里给一份**最小占位**实现，只是为了让 partial class 编得过。
# 逐作真正的内容在 templates\game\UiNav.Game.cs.template 里。
$stub = @'
// 自检用的最小占位（不是逐作实现）。真东西见 templates\game\UiNav.Game.cs.template
using System;
using System.Collections.Generic;
using TMPro;

namespace A11yFramework
{
    internal static partial class UiNav
    {
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Placeholder", "占位" },
        };

        private static string InputFieldRoleName(TMP_InputField inf) { return ""; }
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path $src "UiNav.Game.cs"), $stub, (New-Object System.Text.UTF8Encoding $false))

$items = ""
foreach ($r in $refs) {
    $items += '    <Reference Include="' + [System.IO.Path]::GetFileNameWithoutExtension($r) + '">' + $NL
    $items += '      <HintPath>' + $r + '</HintPath>' + $NL
    $items += '      <Private>false</Private>' + $NL
    $items += '    </Reference>' + $NL
}
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>A11yFrameworkCompileCheck</AssemblyName>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
    <NoWarn>CS0169;CS0414;CS0649;MSB3277</NoWarn>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <ItemGroup>
$items  </ItemGroup>
</Project>
"@
[System.IO.File]::WriteAllText((Join-Path $work "CompileCheck.csproj"), $csproj, (New-Object System.Text.UTF8Encoding $false))

# ---------------------------------------------------------------- 4/4 编译
Write-Host ""
Write-Host "[4/4] dotnet build（不引用 Assembly-CSharp）" -ForegroundColor Cyan
Push-Location $work
try {
    & dotnet build -c Release -v minimal --nologo 2>&1 | ForEach-Object { Write-Host "      $_" }
    $code = $LASTEXITCODE
} finally { Pop-Location }

if ($code -ne 0) {
    Write-Host ""
    Fail "编译失败 —— 平台层引用了不该引用的东西（常见原因：引用了 Assembly-CSharp 里的游戏类型）。临时工程保留在 .compilecheck\ 供排查。"
}

Write-Host ""
Write-Host "PASS  平台层只依赖 UnityEngine / TMPro / BepInEx，**没有游戏类型依赖**。" -ForegroundColor Green
Write-Host "      注意：这证明的是「编得过」，不是「跑得对」——" -ForegroundColor Yellow
Write-Host "      行为正确性要靠实机跑 docs\templates\测试问卷.md。" -ForegroundColor Yellow
Write-Host ""

if (-not $Keep) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
