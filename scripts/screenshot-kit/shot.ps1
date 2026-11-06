# Dot-source after ui-harness.ps1: . .\scripts\ui-harness.ps1; . .\scripts\screenshot-kit\shot.ps1
# PrintWindow gives white for this window, so a shot is copied from the screen: the window has to be
# in front and unobstructed, which Enter-DcForeground (and its idle rule) sees to.
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DcShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
"@
# Physical pixels, so a shot at 150% scaling isn't cut to two thirds of the window.
[void][DcShot]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))

# The window as drawn, without the invisible resize border GetWindowRect includes.
function Get-DcBounds([IntPtr] $Handle = (Get-DcProcess).MainWindowHandle) {
  $r = New-Object DcShot+RECT
  [void][DcShot]::DwmGetWindowAttribute($Handle, 9, [ref]$r, 16)
  [System.Drawing.Rectangle]::FromLTRB($r.Left, $r.Top, $r.Right, $r.Bottom)
}

# Saves the window (or -Crop x,y,w,h within it) to the site's screenshots folder. -HoldKey holds a
# key (0x11 = Ctrl) down while the capture is taken, for the legend's layers.
function Save-DcShot([string] $Name, [int[]] $Crop, [int] $HoldKey = 0, [int] $SettleMs = 600, [string] $Folder = "$PSScriptRoot\..\..\site\screenshots") {
  Enter-DcForeground
  if ($HoldKey) { [DcWin]::keybd_event([byte]$HoldKey, 0, 0, [UIntPtr]::Zero) }
  try {
    Start-Sleep -Milliseconds $SettleMs
    $b = Get-DcBounds
    if ($Crop) { $b = [System.Drawing.Rectangle]::new($b.X + $Crop[0], $b.Y + $Crop[1], $Crop[2], $Crop[3]) }
    $bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  } finally {
    if ($HoldKey) { [DcWin]::keybd_event([byte]$HoldKey, 0, 2, [UIntPtr]::Zero) }
    Set-Content $LastSendFile ([Environment]::TickCount)
  }
  # A shot already on the site is the user's own unless they say otherwise: never replace it.
  $path = Join-Path (Resolve-Path $Folder) "$Name.png"
  if (Test-Path $path) { $path = $path -replace '\.png$', "-$(Get-Date -Format HHmmss).png" }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  $path
}
