# Driving the Spydate window from a script.
#
# Spydate.App has no tests and never will (docs/DECISIONS.md), so the only way to know the window
# works is to run it. This module is the accumulated answer to "how do I do that", because the
# obstacles are not obvious and every one of them cost an hour the first time:
#
#   * A live Spydate holds a lock on the repo's build output, so a copy is run from a scratch folder.
#   * The explorer tree is virtualised: unrealised nodes are not in the automation tree at all, so
#     parents must be expanded before children can be found, with a wait between.
#   * The bottom pane's tabs realise their content only when selected, and Select() through UIA is
#     not enough for some of them - a real click is.
#   * The window exposes no ComboBox to UIA. Click it and use {DOWN} then {ENTER}; without the
#     {ENTER} the list stays open and the next click dismisses it and reverts the selection.
#   * PrintWindow draws the window on its own and so misses every dialog, popup and dropdown, which
#     are exactly the things worth photographing. Capture from the screen instead.
#   * Non-ASCII in a menu header (an ellipsis, say) will not survive a script file read as ANSI, so
#     match names by prefix rather than exactly.
#   * A modal dialog is invisible to UIA - there is no button to find. Answer it with a keystroke,
#     after waiting for a #32770 to take the foreground, and photograph only once it is gone:
#     Save-Shot fronts the main window and would push the dialog behind it.
#
# Usage:
#   Import-Module .\tools\ui\Spydate.Ui.psm1
#   $ui = Start-Spydate -Open C:\path\to\thing.dll
#   Select-Pane $ui Debug
#   Invoke-Menu $ui Debug "Run under the debugger"
#   Confirm-Dialog $ui
#   Save-Shot $ui out.png
#   Get-PanelText $ui 'runtime|Held|breakpoint'
#   Stop-Spydate $ui

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SpydateWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
  }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  /// <summary>The class of whatever has the keyboard. "#32770" is every Win32 dialog.</summary>
  public static string ForegroundClass() {
    var name = new System.Text.StringBuilder(256);
    GetClassName(GetForegroundWindow(), name, 256);
    return name.ToString();
  }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
  public static IntPtr Top(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        R r; GetWindowRect(h, out r);
        if (r.Rr - r.L > 500) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$script:A = [System.Windows.Automation.AutomationElement]
$script:Scope = [System.Windows.Automation.TreeScope]::Descendants

function Start-Spydate {
    <#
    .SYNOPSIS Runs a copy of the built app and waits for its window.
    .PARAMETER Open  A binary to open on the command line.
    .PARAMETER From  The build output to copy. Defaults to the debug build.
    #>
    param(
        [string]$Open,
        [string]$From = "$PSScriptRoot\..\..\src\Spydate.App\bin\Debug\net10.0-windows",
        [string]$Copy = "$env:TEMP\spydate-ui",
        [int]$Settle = 10)

    [void][SpydateWin]::SetProcessDPIAware()

    # A copy, always. A running instance holds the repo's build output open, and the next build
    # fails with a file lock that looks like a compiler problem.
    New-Item -ItemType Directory -Force -Path $Copy | Out-Null
    Copy-Item "$From\*" $Copy -Recurse -Force

    $arguments = if ($Open) { @("`"$Open`"") } else { @() }
    $app = Start-Process (Join-Path $Copy "Spydate.exe") -ArgumentList $arguments -PassThru

    $window = [IntPtr]::Zero
    foreach ($i in 1..80) {
        Start-Sleep -Milliseconds 500
        $window = [SpydateWin]::Top($app.Id)
        if ($window -ne [IntPtr]::Zero) { break }
    }
    if ($window -eq [IntPtr]::Zero) { $app.Kill(); throw "Spydate never showed a window" }

    # Opening a binary analyses it, and nothing is in the automation tree until that finishes.
    Start-Sleep -Seconds $Settle
    [void][SpydateWin]::SetForegroundWindow($window)

    return [pscustomobject]@{
        Process = $app
        Handle  = $window
        Root    = $script:A::FromHandle($window)
    }
}

function Stop-Spydate { param($Ui) $Ui.Process.Kill() }

function Find-Element {
    <#
    .SYNOPSIS One element by control type and the start of its name. Prefix, because a name with
              an ellipsis or a dash in it will not match exactly across encodings.
    #>
    param($Ui, [string]$Kind, [string]$Name, [int]$Tries = 20)

    foreach ($i in 1..$Tries) {
        $all = $Ui.Root.FindAll($script:Scope,
            [System.Windows.Automation.PropertyCondition]::new(
                $script:A::ControlTypeProperty, [System.Windows.Automation.ControlType]::$Kind))
        foreach ($e in $all) { if ($e.Current.Name -like "$Name*") { return $e } }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Click-Element {
    <#
    .SYNOPSIS Clicks the middle of an element.
              SetForegroundWindow is asked for and then not trusted: Windows ignores it from a
              process that is not already foreground, so the first click of a run sometimes only
              activated the window and did nothing else. SetFocus through UIA is the second attempt
              at the same thing, and callers that can check their own result should.
    #>
    param($Ui, $Element)

    [void][SpydateWin]::SetForegroundWindow($Ui.Handle)
    try { $Ui.Root.SetFocus() } catch { }
    Start-Sleep -Milliseconds 400

    $b = $Element.Current.BoundingRectangle
    [SpydateWin]::Click([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2))
    Start-Sleep -Milliseconds 800
}

function Select-Pane {
    <#
    .SYNOPSIS Shows a bottom-pane tab (Output, Xrefs, Warnings, Debug, Patches, Assistant).
              Clicked rather than selected through UIA: the tab's content is realised lazily and
              SelectionItemPattern.Select() left it unrealised, so its buttons were not in the tree.
    #>
    param($Ui, [string]$Name)

    $tab = Find-Element $Ui TabItem $Name
    if ($null -eq $tab) { throw "no '$Name' tab" }

    # Checked and retried rather than clicked once and hoped. A click that lands while the window is
    # not foreground activates it and nothing else, and a run that then carried on took its
    # screenshot of the wrong pane and called the whole thing a failure.
    foreach ($i in 1..3) {
        Click-Element $Ui $tab
        $selected = $tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        if ($selected) { return }
        Start-Sleep -Milliseconds 600
    }
    throw "'$Name' would not select"
}

function Invoke-Menu {
    <#
    .SYNOPSIS Opens a top-level menu and invokes an item in it. Menu items exist in the automation
              tree only once the menu is open, and the popup is a window of its own - so the item is
              searched for from the desktop root rather than from inside the window.
    #>
    param($Ui, [string]$Menu, [string]$Item)

    $top = Find-Element $Ui MenuItem $Menu
    if ($null -eq $top) { throw "no '$Menu' menu" }

    # Three attempts, each starting by closing whatever is open. Driving the same menu twice in a
    # row is where this goes wrong: the previous popup may still be up, and expanding then toggles
    # it shut instead of open, so the item is looked for in a menu that is not there.
    foreach ($attempt in 1..3) {
        [void][SpydateWin]::SetForegroundWindow($Ui.Handle)
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400

        $top.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 900

        foreach ($i in 1..10) {
            $all = $script:A::RootElement.FindAll($script:Scope,
                [System.Windows.Automation.PropertyCondition]::new(
                    $script:A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
            foreach ($m in $all) {
                if ($m.Current.Name -like "$Item*") {
                    # A disabled item is not a failure to find it, and saying which it was beats
                    # an ElementNotEnabledException from somewhere inside UIA.
                    if (-not $m.Current.IsEnabled) { throw "'$Item' is disabled" }
                    $m.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return
                }
            }
            Start-Sleep -Milliseconds 300
        }
    }
    throw "no '$Item' in the '$Menu' menu"
}

function Confirm-Dialog {
    <#
    .SYNOPSIS Answers a modal dialog by keystroke. A MessageBox takes the foreground and its buttons
              carry access keys, so "y" is Yes - and this works whether or not the dialog publishes
              itself to UIA, which it does not: a MessageBox put up by this window appears in no
              automation query at all, so there is nothing to find and click.

              Waited for by class rather than by clock. A dialog answered a fixed two seconds after
              the menu item was invoked is a race, and losing it is silent and confusing: the
              keystroke lands on the main window, the dialog comes up afterwards and is never
              answered, and the run reads as a debugger that refused to start. #32770 is the
              window class every Win32 dialog has.

              Nothing may touch the foreground between the menu and this. Save-Shot in particular
              fronts the main window, which puts a modal dialog behind it and sends the keystroke
              to the wrong place - photograph after answering, not before.
    #>
    param($Ui, [string]$Key = "y", [int]$Wait = 10)

    foreach ($i in 1..($Wait * 4)) {
        if ([SpydateWin]::ForegroundClass() -eq '#32770') { break }
        Start-Sleep -Milliseconds 250
    }

    [System.Windows.Forms.SendKeys]::SendWait($Key)

    # And waited for again, because the answer is what the caller is really waiting on. A dialog
    # still up here was not answered, and saying so beats letting the next step fail somewhere else.
    foreach ($i in 1..20) {
        if ([SpydateWin]::ForegroundClass() -ne '#32770') { return }
        Start-Sleep -Milliseconds 250
    }

    throw "the dialog did not answer to '$Key'"
}

function Save-Shot {
    <#
    .SYNOPSIS Photographs the window from the screen. Not PrintWindow: that draws the window on its
              own and misses dialogs, popups and dropdowns. The window is fronted first, so whatever
              is over it is meant to be there.
    #>
    param($Ui, [string]$Path)

    [void][SpydateWin]::SetForegroundWindow($Ui.Handle)
    Start-Sleep -Milliseconds 800

    $r = New-Object SpydateWin+R
    [void][SpydateWin]::GetWindowRect($Ui.Handle, [ref]$r)
    $w = $r.Rr - $r.L; $h = $r.B - $r.T

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return "$Path ($w x $h)"
}

function Get-PanelText {
    <#
    .SYNOPSIS Every visible text matching a pattern, so an assertion does not depend on reading
              pixels. Use this for the answer and the screenshot for the evidence.

              Not everything is here. The explorer, the tab strips and the status bar are; the Debug
              panel's own contents are not published to UIA at all, so its status line and its Locals
              grid can only be read from a screenshot. Do not read an empty result as an empty panel.
    #>
    param($Ui, [string]$Pattern = '.')

    $found = @()
    $texts = $Ui.Root.FindAll($script:Scope,
        [System.Windows.Automation.PropertyCondition]::new(
            $script:A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
    foreach ($t in $texts) { if ($t.Current.Name -match $Pattern) { $found += $t.Current.Name } }
    return $found
}

function Expand-Tree {
    <#
    .SYNOPSIS Expands an explorer node by the start of its name. The tree is virtualised, so a child
              does not exist until its parent is expanded - walk down one level at a time.
    #>
    param($Ui, [string]$Name)

    $node = Find-Element $Ui TreeItem $Name
    if ($null -eq $node) { throw "no '$Name' in the explorer" }
    $node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 900
    return $node
}

function Open-TreeItem {
    <#
    .SYNOPSIS Opens an explorer leaf. Tree items offer no Invoke pattern, so it is selected and
              Enter is pressed.
    #>
    param($Ui, [string]$Name)

    $node = Find-Element $Ui TreeItem $Name
    if ($null -eq $node) { throw "no '$Name' in the explorer" }
    $node.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Seconds 3
    return $node
}

Export-ModuleMember -Function Start-Spydate, Stop-Spydate, Find-Element, Click-Element, Select-Pane,
    Invoke-Menu, Confirm-Dialog, Save-Shot, Get-PanelText, Expand-Tree, Open-TreeItem
