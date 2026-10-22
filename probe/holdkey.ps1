# SendInput chord with configurable hold, x64-correct INPUT layout.
param(
    [string]$Spec = 'ctrl+alt+shift+equal',
    [int]$HoldMs = 350,
    [int]$MainHoldMs = 120,
    [int]$GapMs = 40
)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HK {
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct KI {
        [FieldOffset(0)] public ushort wVk;
        [FieldOffset(2)] public ushort wScan;
        [FieldOffset(4)] public uint dwFlags;
        [FieldOffset(8)] public uint time;
        [FieldOffset(16)] public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KI ki;
    }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] p, int cb);
    public static bool Down(ushort vk) {
        INPUT[] a = new INPUT[1];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.dwFlags = 0;
        return SendInput(1, a, 40) == 1;
    }
    public static bool Up(ushort vk) {
        INPUT[] a = new INPUT[1];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.dwFlags = 2;
        return SendInput(1, a, 40) == 1;
    }
}
'@
$VK = @{ ctrl=0x11; control=0x11; lctrl=0xA2; rctrl=0xA3; alt=0x12; lalt=0xA4; ralt=0xA5;
         shift=0x10; lshift=0xA0; rshift=0xA1; win=0x5B
         equal=0xBB; plus=0xBB; minus=0xBD; tab=0x09; return=0x0D; space=0x20; escape=0x1B
         up=0x26; down=0x28; left=0x25; right=0x27; pageup=0x21; pagedown=0x22; home=0x24; end=0x23
         f1=0x70; f2=0x71; f3=0x72; f4=0x73; f5=0x74; f6=0x75; f7=0x76; f8=0x77; f9=0x78; f10=0x79; f11=0x7A; f12=0x7B }
$parts = $Spec.ToLower().Split('+')
$main = $parts[-1]
$mods = @()
for ($i = 0; $i -lt $parts.Length - 1; $i++) { $mods += [int]$VK[$parts[$i]] }
$mainVk = [int]$VK[$main]
foreach ($m in $mods) { [void][HK]::Down([uint16]$m) }
Start-Sleep -Milliseconds $HoldMs
# 主键盘加号需要 Shift 参与：若主键是 '=' 或 'plus'，额外按住 shift
$needShift = ($main -eq 'equal' -or $main -eq 'plus')
$shiftDown = $false
if ($needShift -and -not ($mods -contains 0x10) -and -not ($mods -contains 0xA0) -and -not ($mods -contains 0xA1)) {
    [void][HK]::Down(0x10); $shiftDown = $true; Start-Sleep -Milliseconds 60
}
[void][HK]::Down([uint16]$mainVk)
Start-Sleep -Milliseconds $MainHoldMs
[void][HK]::Up([uint16]$mainVk)
if ($shiftDown) { [void][HK]::Up(0x10) }
Start-Sleep -Milliseconds $GapMs
for ($i = $mods.Length - 1; $i -ge 0; $i--) { [void][HK]::Up([uint16]$mods[$i]) }
"sent $Spec ok"
