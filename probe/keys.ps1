param([string]$Keys = "chord")
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class KI {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(int a, int b, bool f);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static IntPtr Find(int pid){
    IntPtr best=IntPtr.Zero; long area=0;
    EnumWindows((h,l)=>{ int p; GetWindowThreadProcessId(h, out p); if(p!=pid||!IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r); long a=(long)(r.R-r.L)*(r.B-r.T); if(a>area){area=a;best=h;} return true; }, IntPtr.Zero);
    return best;
  }
  public static bool Focus(IntPtr h){
    int tidGame = GetWindowThreadProcessId(h, IntPtr.Zero);
    int tidMe = GetCurrentThreadId();
    AttachThreadInput(tidMe, tidGame, true);
    ShowWindow(h, 9);
    bool ok = SetForegroundWindow(h);
    AttachThreadInput(tidMe, tidGame, false);
    return ok;
  }
  public static void Down(int vk){ keybd_event((byte)vk,0,0,IntPtr.Zero); }
  public static void Up(int vk){ keybd_event((byte)vk,0,2,IntPtr.Zero); }
}
"@
$p = Get-Process -Name Lilith -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "no game"; exit 1 }
$h = [KI]::Find($p.Id)
$ok = [KI]::Focus($h)
Start-Sleep -Milliseconds 400
Write-Output ("focused=" + $ok + " fgTitle=" + [KI]::GetForegroundWindow())
$VK_CTRL = 0x11; $VK_ALT = 0x12
function Chord { [KI]::Down($VK_CTRL); Start-Sleep -Milliseconds 60; [KI]::Down($VK_ALT); Start-Sleep -Milliseconds 450; [KI]::Up($VK_ALT); Start-Sleep -Milliseconds 40; [KI]::Up($VK_CTRL) }
$VK_DOWN=0x28; $VK_UP=0x26; $VK_ENTER=0x0D; $VK_ESC=0x1B; $VK_TAB=0x09; $VK_LEFT=0x25; $VK_RIGHT=0x27
function Tap([int]$vk){ [KI]::Down($vk); Start-Sleep -Milliseconds 60; [KI]::Up($vk); Start-Sleep -Milliseconds 350 }
switch ($Keys) {
  "chord" { Chord }
  "nav"   { Chord; Start-Sleep -Milliseconds 700; Tap $VK_DOWN; Tap $VK_DOWN; Tap $VK_UP }
  "tab"   { Tap $VK_TAB }
  "down"  { Tap $VK_DOWN }
  "up"    { Tap $VK_UP }
  "left"  { Tap $VK_LEFT }
  "right" { Tap $VK_RIGHT }
  "enter" { Tap $VK_ENTER }
  "esc"   { Tap $VK_ESC }
}
Write-Output ("keys sent: " + $Keys)
