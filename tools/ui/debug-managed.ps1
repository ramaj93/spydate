# Starts a managed binary under the .NET debugger from the window, and says what the panel says.
#
# The worked example for tools/ui/Spydate.Ui.psm1, and a regression check in its own right: three
# bugs in this exact path were invisible to the test suite, because Spydate.App has no tests.
#
#   powershell -File tools\ui\debug-managed.ps1 -Open src\Spydate.Mcp\bin\Debug\net10.0\spydate-mcp.dll
#
# A managed DLL needs a host to load it; set one in the window first (Debug > ⋮ > Run configuration),
# or this reports the refusal, which is itself the right answer.

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [string]$Out = "$env:TEMP\spydate-debug.png",
    [int]$Settle = 12)

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$ui = Start-Spydate -Open (Resolve-Path $Open)
try {
    Select-Pane $ui Debug
    Invoke-Menu $ui Debug "Run under the debugger"
    Confirm-Dialog $ui
    Start-Sleep -Seconds $Settle

    # The screenshot is the answer here, not a garnish: the Debug panel does not publish its
    # contents to UIA, so its status line ("Held before it ran anything", or why it did not start)
    # can only be read from the picture. Look at it.
    Save-Shot $ui $Out
    foreach ($line in Get-PanelText $ui 'runtime|debugg|Held|Running|breakpoint|exited|stopped|not \.NET|could not') {
        "PANEL: $line"
    }
}
finally {
    Stop-Spydate $ui
}
