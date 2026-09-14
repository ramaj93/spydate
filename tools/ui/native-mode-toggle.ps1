# Checks that a .NET target can be told to use the native debugger, from the window.
#
# Spydate.App has no tests, and what is being checked here is a set of bindings — which fail
# silently. A wrong binding path compiles, loads, and shows nothing; the build cannot tell you.
#
#   powershell -File tools\ui\native-mode-toggle.ps1 -Open src\Spydate.Core\bin\Debug\net10.0\Spydate.Core.dll
#
# How this is checked, and why it is not checked the obvious way. The Debug panel publishes nothing
# at all to UI Automation: a dump of the whole window found 174 named elements — the menus, the
# toolbar, the explorer tree, the document tab, the bottom pane tabs and the status bar — and not one
# control from inside the Debug panel. Not the Locals tab, not the run buttons, nothing. So a script
# cannot find the Native checkbox there, and an earlier version of this one reported exactly that as
# a failure of the code, which it was not.
#
# What can be checked is the menu, because menu items are published:
#
#   * "Debug natively" is in the Debug menu       -> the item exists and its bindings resolved
#   * invoking it does not throw "is disabled"    -> CanChooseDebugger is true before a run
#   * the panel's tabs change in the screenshots  -> DebugNatively notified UsesManagedDebugger,
#                                                    and the panes that depend on it re-evaluated
#
# The last of those is read from the pictures, not asserted. Look at them: before should show
# Locals / Call stack / Threads, after should show Threads & registers instead.
#
# Nothing is run under a debugger here. This is about which engine would be chosen, not about
# starting a process — and the assembly used as a target is a DLL, which would need a host anyway.

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [string]$Out = "$env:TEMP\spydate-native-mode")

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
    Start-Sleep -Milliseconds 800
    Save-Shot $ui "$Out-before.png" | Out-Null

    # Invoke-Menu tells the two failures apart for us: a missing item and a disabled one throw
    # different messages, and only one of them would mean the binding is wrong.
    $problem = $null
    try {
        Invoke-Menu $ui Debug "Debug natively"
    }
    catch {
        $problem = $_.Exception.Message
    }

    Note "the Debug menu offers 'Debug natively', enabled before a run" ($null -eq $problem) $problem

    Start-Sleep -Milliseconds 1200
    Save-Shot $ui "$Out-after.png" | Out-Null
}
finally {
    Stop-Spydate $ui
}

""
"---- checks ----"
$checks | ForEach-Object { "  $_" }
""
"shots (compare the Debug panel's tabs in these two):"
$shots = @(Get-ChildItem "$Out*.png" -ErrorAction SilentlyContinue)
if ($shots.Count -eq 0) { "  (none written)" }
else { $shots | ForEach-Object { "  $($_.FullName)" } }

if ($failed) { exit 1 }
exit 0
