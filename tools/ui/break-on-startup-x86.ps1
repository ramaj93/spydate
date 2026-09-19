# Breaks on a startup method of a 32-bit .NET assembly, in the 32-bit build, and says what the
# panel says.
#
# The regression this guards is not subtle: the breakpoint was missed and the debuggee then hung,
# because the first-call catch overwrote the byte it had saved when the assembly was remapped
# during startup (see docs/MIXED-MODE.md, Phase 7). It needs the 32-bit build, because Windows
# will not let a 64-bit debugger read a 32-bit runtime's managed state.
#
#   powershell -File tools\ui\break-on-startup-x86.ps1 -Open path\to\thing.dll
#
# The host to run it under and the breakpoint itself come from what was saved for that binary
# (%LocalAppData%\Spydate\debug.json and the .spydate beside it), which is what makes this a
# check of the analyst's own arrangement rather than a fresh one.

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [string]$Out = "$env:TEMP\spydate-x86-break.png",
    [int]$Settle = 20)

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$ui = Start-Spydate -Open (Resolve-Path $Open) `
    -From "$PSScriptRoot\..\..\src\Spydate.App\bin\x86\Debug\net10.0-windows" `
    -Exe "Spydate-x86.exe"
try {
    Select-Pane $ui Debug
    Invoke-Menu $ui Debug "Start Debugging"
    Confirm-RunDialog $ui

    # The loader break lands first and holds. Let it go, then give the run time to reach the method.
    Start-Sleep -Seconds 3
    Invoke-Menu $ui Debug "Continue"
    Start-Sleep -Seconds $Settle

    Save-Shot $ui $Out
    foreach ($line in Get-PanelText $ui 'Stopped|Running|breakpoint|caught at its first call|loaded at|could not|exited') {
        "PANEL: $line"
    }
}
finally {
    Stop-Spydate $ui
}
