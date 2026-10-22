param(
  [Parameter(Mandatory=$true)][string]$Seq
)
# 慢速注入按键序列，解决"注入过快游戏/全局钩子读不到"的问题。
# 序列格式：以 ; 分隔的步骤，每个步骤是 + 连接的一串键名，例如 "ctrl+alt+shift+equals"
# 支持键名：ctrl alt shift equals tab enter space up down left right pageup pagedown esc
#          数字 0-9 直接写 0..9，字母 a..z
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace W -Name K -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
'@

$map = @{
  'ctrl'=0x11; 'alt'=0x12; 'shift'=0x10; 'win'=0x5B;
  'equals'=0xBB; 'minus'=0xBD; 'plus'=0xBB;
  'tab'=0x09; 'enter'=0x0D; 'return'=0x0D; 'space'=0x20; 'esc'=0x1B; 'escape'=0x1B;
  'up'=0x26; 'down'=0x28; 'left'=0x25; 'right'=0x27;
  'pageup'=0x21; 'pagedown'=0x22; 'home'=0x24; 'end'=0x23;
  'backspace'=0x08; 'delete'=0x2E; 'apps'=0x5D;
  '0'=0x30; '1'=0x31; '2'=0x32; '3'=0x33; '4'=0x34; '5'=0x35; '6'=0x36; '7'=0x37; '8'=0x38; '9'=0x39;
}
1..26 | ForEach-Object { $map[[string][char](96 + $_)] = 0x40 + $_ }

function Vk([string]$k) {
  if ($map.ContainsKey($k)) { return [byte]$map[$k] }
  # 单字符字母/数字
  if ($k.Length -eq 1) {
    $c = $k.ToLower()
    return [byte][int][char]::ToUpperInvariant($c)
  }
  throw "unknown key: $k"
}

$DOWN=0; $UP=2
foreach ($step in ($Seq -split ';')) {
  $step = $step.Trim(); if ($step -eq '') { continue }
  # 步骤内 + 表示组合键；单个词表示单击
  $keys = @($step -split '\+') | Where-Object { $_ -ne '' }
  $vks = @($keys | ForEach-Object { Vk($_.Trim().ToLower()) })
  # 按下（修饰键先按，最后按主键）
  foreach ($vk in $vks) { [W.K]::keybd_event($vk, 0, $DOWN, [UIntPtr]::Zero); Start-Sleep -Milliseconds 90 }
  Start-Sleep -Milliseconds 220
  # 松开（主键先松，修饰键后松）
  [array]::Reverse($vks)
  foreach ($vk in $vks) { [W.K]::keybd_event($vk, 0, $UP, [UIntPtr]::Zero); Start-Sleep -Milliseconds 70 }
  Start-Sleep -Milliseconds 320
  Write-Output ("sent: " + $step)
}
