<#
  可靠的 SendInput 注入（x64 结构体布局）
  用法：
    powershell -ExecutionPolicy Bypass -File sendinput2.ps1 -Keys 0x28 -HoldMs 900
    powershell -ExecutionPolicy Bypass -File sendinput2.ps1 -Keys 0x11,0x12,0xA0 -HoldMs 300   # ctrl+alt+shift
    powershell -ExecutionPolicy Bypass -File sendinput2.ps1 -Keys 0xBB -HoldMs 200              # 主键盘 +
  说明：Keys 为 Virtual-Key 序列，脚本会先全部按下、HoldMs 后逆序抬起。
#>
param(
  [Parameter(Mandatory=$true)][string[]]$Keys,
  [int]$HoldMs = 200,
  [int]$GapMs = 15
)

Add-Type -Namespace Win32 -Name Kbd -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct INPUT {
  [FieldOffset(0)] public uint type;
  [FieldOffset(8)] public KEYBDINPUT ki;
}
[DllImport("user32.dll", SetLastError=true)]
public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
'@

function New-Input([int]$vk, [bool]$up) {
  $inp = New-Object Win32.Kbd+INPUT
  $inp.type = 1
  $inp.ki.wVk = [uint16]$vk
  $inp.ki.wScan = 0
  $inp.ki.dwFlags = if ($up) { 0x0002 } else { 0x0000 }
  $inp.ki.time = 0
  $inp.ki.dwExtraInfo = [IntPtr]::Zero
  return $inp
}

$vks = @($Keys | ForEach-Object { if ($_ -like '0x*') { [Convert]::ToInt32($_,16) } else { [int]$_ } })
$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Win32.Kbd+INPUT])

$downs = @()
foreach ($vk in $vks) { $downs += (New-Input $vk $false); Start-Sleep -Milliseconds $GapMs }
$n = [Win32.Kbd]::SendInput([uint32]$downs.Count, $downs, $size)
if ($n -ne $downs.Count) { Write-Output "DOWN-FAIL n=$n cbSize=$size err=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"; exit 1 }

Start-Sleep -Milliseconds $HoldMs

$ups = @()
for ($i = $vks.Count - 1; $i -ge 0; $i--) { $ups += (New-Input $vks[$i] $true); Start-Sleep -Milliseconds $GapMs }
$n2 = [Win32.Kbd]::SendInput([uint32]$ups.Count, $ups, $size)
if ($n2 -ne $ups.Count) { Write-Output "UP-FAIL n=$n2 err=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"; exit 1 }

Write-Output ("OK vks=" + (($vks | ForEach-Object { '0x{0:X2}' -f $_ }) -join ',') + " hold=" + $HoldMs + "ms")

