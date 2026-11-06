# Dot-sourced harness for driving Dance Commander in the real window: . .\scripts\ui-harness.ps1
# How it is used, and what it must never do: .claude/skills/debug-dance-commander/SKILL.md.
# Reading and Invoke go through UI Automation (no foreground needed). Keys need the foreground:
# Send-DcKeys only acts after the user has been idle for $IdleSeconds, confirms the foreground is
# ours, and throws otherwise, so a keystroke can never land in the user's own window.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DcWin {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)Environment.TickCount - i.dwTime; }
}
"@

$IdleSeconds = 20
$LastSendFile = "$env:TEMP\dc-ui-harness-lastsend"
$UIA = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$Cond = [System.Windows.Automation.Condition]::TrueCondition

function Get-DcProcess { Get-Process DanceCommander -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }

function Get-DcWindow {
  $p = Get-DcProcess
  if (-not $p) { throw "Dance Commander is not running" }
  $UIA::FromHandle($p.MainWindowHandle)
}

# Every named element under the window, as "ControlType | Name | AutomationId".
function Get-DcTexts([System.Windows.Automation.AutomationElement]$Root = (Get-DcWindow)) {
  $Root.FindAll($TS::Descendants, $Cond) | ForEach-Object {
    try {
      $c = $_.Current
      if ($c.Name) { "{0} | {1} | {2}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId }
    } catch { }
  }
}

function Find-DcByName([string]$Name, [System.Windows.Automation.AutomationElement]$Root = (Get-DcWindow)) {
  $c = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $Name)
  $Root.FindAll($TS::Descendants, $c)
}

function Invoke-DcButton([string]$Name) {
  $e = Find-DcByName $Name | Select-Object -First 1
  if (-not $e) { throw "No element named '$Name'" }
  $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-DcFocused {
  $f = $UIA::FocusedElement
  "{0} | {1} | {2}" -f $f.Current.ControlType.ProgrammaticName, $f.Current.Name, $f.Current.AutomationId
}

function Enter-DcForeground {
  $p = Get-DcProcess
  if (-not $p) { throw "Dance Commander is not running" }
  $idleMs = [DcWin]::IdleMs()
  # Our own keystrokes count as input too: the last input was ours if nothing came after it.
  $ours = $false
  if (Test-Path $LastSendFile) {
    $sinceOurs = [Environment]::TickCount - [int64](Get-Content $LastSendFile)
    $ours = $sinceOurs -ge 0 -and $sinceOurs -le $idleMs + 250
  }
  if (-not $ours -and $idleMs -lt $IdleSeconds * 1000) { throw "User active $([int]($idleMs/1000)) s ago; not taking the foreground" }
  $h = $p.MainWindowHandle
  if ([DcWin]::GetForegroundWindow() -ne $h) {
    # A bare Alt press lets this process move the foreground (Windows' foreground lock).
    [DcWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [DcWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][DcWin]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 300
  }
  if ([DcWin]::GetForegroundWindow() -ne $h) { throw "Could not take the foreground; aborting" }
}

# SendKeys syntax ("^e", "{ESC}", "{ENTER}"). Checks the foreground before every call.
function Send-DcKeys([string]$Keys, [int]$AfterMs = 400) {
  Enter-DcForeground
  $h = (Get-DcProcess).MainWindowHandle
  if ([DcWin]::GetForegroundWindow() -ne $h) { throw "Foreground moved; aborting" }
  [System.Windows.Forms.SendKeys]::SendWait($Keys)
  Set-Content $LastSendFile ([Environment]::TickCount)
  Start-Sleep -Milliseconds $AfterMs
}

# Literal text (SendKeys escapes applied).
function Send-DcText([string]$Text) {
  $escaped = [regex]::Replace($Text, '[+^%~(){}\[\]]', '{$0}')
  Send-DcKeys $escaped 150
}

function Get-DcLog([int]$Tail = 15) {
  $f = Get-ChildItem "$env:LOCALAPPDATA\DanceCommander\logs\*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
  Get-Content $f.FullName -Tail $Tail | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) } else { $_ } }
}

# The real pointer, so the same idle rule as keys. Moves in small steps, as a hand does: a drag that
# jumps straight to its target skips the rows a real one crosses, which is where drag bugs live.
function Move-DcPointer([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [int]$Steps = 20) {
  for ($i = 1; $i -le $Steps; $i++) {
    [void][DcWin]::SetCursorPos($FromX + [int](($ToX - $FromX) * $i / $Steps), $FromY + [int](($ToY - $FromY) * $i / $Steps))
    Start-Sleep -Milliseconds 40
  }
  Set-Content $LastSendFile ([Environment]::TickCount)
}

function Send-DcDrag([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY) {
  Enter-DcForeground
  [void][DcWin]::SetCursorPos($FromX, $FromY); Start-Sleep -Milliseconds 150
  [DcWin]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  Move-DcPointer $FromX $FromY $ToX $ToY
  Start-Sleep -Milliseconds 400
  [DcWin]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
  Set-Content $LastSendFile ([Environment]::TickCount)
}

# One object per file row: its text, whether it is selected and focused, and where it is on screen.
function Get-DcRows {
  $dc = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
  (Get-DcWindow).FindAll($TS::Descendants, $dc) | ForEach-Object {
    $texts = $_.FindAll($TS::Descendants, $Cond) | ForEach-Object { $_.Current.Name }
    [pscustomobject]@{
      Name = ($texts -join ' ').Trim()
      Rect = $_.Current.BoundingRectangle
      Selected = $(try { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false })
      Focused = $_.Current.HasKeyboardFocus
    }
  }
}
