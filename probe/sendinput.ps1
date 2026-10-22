param([Parameter(Mandatory=$true)][string]$Seq)
# SendInput 版按键注入（真实硬件路径），可被全局 GetAsyncKeyState / 游戏读取。
Add-Type -Namespace SI -Name K -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(4)] public KEYBDINPUT ki; }
[DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
'@
$map = @{ 'ctrl'=0x11;'alt'=0x12;'shift'=0x10;'equals'=0xBB;'tab'=0x09;'enter'=0x0D;'space'=0x20;'esc'=0x1B;
  'up'=0x26;'down'=0x28;'left'=0x25;'right'=0x27;'pageup'=0x21;'pagedown'=0x22;'home'=0x24;'end'=0x23;
  'backspace'=0x08;'delete'=0x2E;'minus'=0xBD;'oemplus'=0xBB;'numpad0'=0x60;'0'=0x30;'1'=0x31;'2'=0x32;'3'=0x33;'4'=0x34;'5'=0x35;'6'=0x36;'7'=0x37;'8'=0x38;'9'=0x39 }
1..26 | ForEach-Object { $map[[string][char](96+$_)] = 0x40+$_ }
function Vk([string]$k){ $k=$k.Trim().ToLower(); if($map.ContainsKey($k)){return [ushort]$map[$k]}; throw "unknown key: $k" }
function Make([int]$vk,[bool]$up){
  $i = New-Object SI.K+INPUT
  $i.type = 1
  $i.ki.wVk = [ushort]$vk
  $i.ki.dwFlags = if($up){2}else{0}
  return $i
}
foreach($step in ($Seq -split ';')) {
  $step=$step.Trim(); if($step -eq ''){continue}
  $keys = @($step -split '\+') | Where-Object { $_ -ne '' } | ForEach-Object { Vk($_) }
  $arr = @()
  foreach($vk in $keys){ $arr += (Make $vk $false); Start-Sleep -Milliseconds 60 }
  Start-Sleep -Milliseconds 180
  for($j=$keys.Count-1; $j -ge 0; $j--){ $arr += (Make $keys[$j] $true); Start-Sleep -Milliseconds 60 }
  $n=[SI.K]::SendInput([uint32]$arr.Count, $arr, [System.Runtime.InteropServices.Marshal]::SizeOf([type][SI.K+INPUT]))
  Write-Output ("step '$step' sent inputs=$n err=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())")
  Start-Sleep -Milliseconds 320
}
