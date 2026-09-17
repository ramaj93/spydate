# Checks the Debug Program dialog from the window.
#
# Spydate.App has no tests, and what is checked here is a dialog full of bindings — which fail
# silently. A wrong binding path compiles, loads, and shows an empty box; the build cannot tell you.
#
#   powershell -File tools\ui\native-mode-toggle.ps1 -Open src\Spydate.Core\bin\Debug\net10.0\Spydate.Core.dll
#
# The Debug *panel* publishes nothing at all to UI Automation — a dump of the main window found 174
# named elements and not one control from inside it. A dialog is a window of its own, so it may well
# be a different story, and this prints its whole tree rather than assuming either way. Read that
# dump: it is the evidence for what the next version of this script can assert.
#
# Nothing is run under a debugger. The dialog is opened and closed again; Close keeps whatever it
# holds, because every box in it writes itself through as it is edited.

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [string]$Out = "$env:TEMP\spydate-debug-dialog")

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$checks = @()
$failed = $false

function Note([string]$claim, [bool]$ok, [string]$detail) {
    $script:checks += "[{0}] {1}{2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $claim,
        $(if ($detail) { " - $detail" } else { "" })
    if (-not $ok) { $script:failed = $true }
}

$ui = Start-Spydate -Open (Resolve-Path $Open)
try {
    Select-Pane $ui Debug
    Start-Sleep -Milliseconds 600

    $problem = $null
    try {
        Invoke-Menu $ui Debug "Start Debugging"
    }
    catch {
        $problem = $_.Exception.Message
    }

    Note "the Debug menu offers 'Start Debugging...', enabled" ($null -eq $problem) $problem

    Start-Sleep -Milliseconds 1500
    Save-Shot $ui "$Out.png" | Out-Null

    # A dialog is its own top-level window, so it is looked for from the desktop root - the same
    # reason Invoke-Menu searches there for menu popups.
    # Descendants, not Children. An owned modal is not a child of the desktop root in the automation
    # tree, so the obvious search found nothing while the dialog was plainly on the screen — which
    # read as the dialog never opening. Invoke-Menu already searches this way for menu popups, and
    # they are windows of their own for the same reason.
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byName = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Debug Program")

    $dialog = $null
    foreach ($i in 1..15) {
        $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
        if ($null -ne $dialog) { break }
        Start-Sleep -Milliseconds 400
    }

    Note "Start Debugging opens the Debug Program dialog" ($null -ne $dialog) $(
        if ($null -eq $dialog) { "no top-level window named 'Debug Program'" } else { "" })

    if ($null -ne $dialog) {
        $inside = $dialog.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)

        ""
        "---- what the dialog publishes ($($inside.Count) elements) ----"
        foreach ($e in $inside) {
            if ($e.Current.Name) {
                "{0,-16} {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $e.Current.Name
            }
        }

        $named = @($inside | ForEach-Object { $_.Current.Name })
        Note "it carries a Debug engine chooser" ([bool]($named -contains "Debug engine")) ""
        Note "it carries a Break at chooser" ([bool]($named -contains "Break at")) ""
        Note "it carries an Executable box" ([bool]($named -contains "Executable")) ""

        # What the choosers *show* is not assertable here, and the attempt to do it was worse than
        # leaving it out. Both combos once rendered the raw record to the user - "EngineChoice {
        # Native = False, Label = .NET CLR }" - because the themed ComboBox brings its own
        # ItemTemplate and DisplayMemberPath was quietly ignored. The obvious check for that is to
        # read the selected item's name through SelectionPattern. It does not work: GetSelection
        # hands back the item container, whose automation name comes from the bound object's
        # ToString(), so it reports the record either way. It failed on a combo that was rendering
        # perfectly, which is a check that invents faults rather than finding them.
        #
        # So the label is a claim about the picture, like the rest of this panel's appearance. Look
        # at the shot: the engine should read ".NET CLR" and break-at "Create Process", with neither
        # showing a brace.
        "  (what the choosers display is in the screenshot - UIA names them by the bound record)"

        # Closed rather than left up: Stop-Spydate would kill the process anyway, but a modal left
        # open is a modal the next run of this script inherits if anything changes about teardown.
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 500
    }
}
finally {
    Stop-Spydate $ui
}

""
"---- checks ----"
$checks | ForEach-Object { "  $_" }
""
"shot: $Out.png"

if ($failed) { exit 1 }
exit 0
