param([string]$Out = "D:\DSHWorkSpace\justlilith_a11y\probe\shot.png")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<IntPtr> OfPid(int want) {
    var res = new List<IntPtr>();
    EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (pid == want && IsWindowVisible(h)) res.Add(h); return true; }, IntPtr.Zero);
    return res;
  }
  public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
}
"@
$p = Get-Process -Name Lilith -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "no Lilith process"; exit 1 }
$best = [IntPtr]::Zero; $bestArea = 0; $bestTitle = ""
foreach ($h in [W]::OfPid($p.Id)) {
  $t = [W]::Title($h)
  if ($t -like "*BepInEx*") { continue }
  $r = New-Object W+RECT
  [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $a = ($r.R - $r.L) * ($r.B - $r.T)
  if ($a -gt $bestArea) { $bestArea = $a; $best = $h; $bestTitle = $t }
}
if ($best -eq [IntPtr]::Zero) { Write-Output "no non-console Lilith window"; exit 1 }
[W]::ShowWindow($best, 9) | Out-Null
[W]::SetForegroundWindow($best) | Out-Null
Start-Sleep -Milliseconds 500
$r = New-Object W+RECT
[W]::GetWindowRect($best, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
if ($w -le 0 -or $hh -le 0) { Write-Output "bad rect $w x $hh"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved {0} {1}x{2} title='{3}'" -f $Out, $w, $hh, $bestTitle)
