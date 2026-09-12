# Runs a managed binary, breaks where it starts, and presses Step over a few times, photographing
# the panel after each press.
#
# What it shows is the Debug panel's status line, which says where it stopped as a method and an IL
# offset. Stepping statements moves it further each time than stepping instructions would.
#
#   powershell -File tools\ui\step-statements.ps1 -Open src\Spydate.Mcp\bin\Debug\net10.0\spydate-mcp.dll
#
# A managed DLL needs a host; set one in the window first (Debug > ⋮ > Run configuration).

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [string]$Out = "$env:TEMP\spydate-step",
    [int]$Steps = 4)

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$ui = Start-Spydate -Open (Resolve-Path $Open)
try {
    Select-Pane $ui Debug
    Invoke-Menu $ui Debug "Run under the debugger"
    Confirm-Dialog $ui
    Start-Sleep -Seconds 10
    Save-Shot $ui "$Out-0.png"

    for ($i = 1; $i -le $Steps; $i++) {
        Invoke-Menu $ui Debug "Step over"
        Start-Sleep -Seconds 3
        Save-Shot $ui "$Out-$i.png"
    }
}
finally {
    Stop-Spydate $ui
}
