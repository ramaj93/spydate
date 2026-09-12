# The whole managed debugging flow through the window: open a method's decompiled C#, set a
# breakpoint by clicking the gutter, run to it, and step. One photograph per step.
#
#   powershell -File tools\ui\step-csharp.ps1 -Open src\Spydate.Mcp\bin\Debug\net10.0\spydate-mcp.dll `
#              -Namespace Spydate.Mcp -Type McpOptions -Member "Parse*"
#
# The binary needs a host configured in the window (Debug > ⋮ > Run configuration) if it is a DLL,
# which every .NET assembly with an entry point is.

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [Parameter(Mandatory = $true)][string]$Namespace,
    [Parameter(Mandatory = $true)][string]$Type,
    [Parameter(Mandatory = $true)][string]$Member,
    [string]$Out = "$env:TEMP\spydate-step",
    [int]$Steps = 3,
    [int]$GutterX = 382,
    [int]$GutterY = 260)

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$ui = Start-Spydate -Open (Resolve-Path $Open)
try {
    foreach ($step in @('Namespaces', $Namespace, $Type)) { [void](Expand-Tree $ui $step) }
    [void](Open-TreeItem $ui $Member)

    $r = New-Object SpydateWin+R
    [void][SpydateWin]::GetWindowRect($ui.Handle, [ref]$r)
    [void][SpydateWin]::SetForegroundWindow($ui.Handle)
    Start-Sleep -Milliseconds 500

    # The margin, beside whichever statement is drawn at this height.
    [SpydateWin]::Click(($r.L + $GutterX), ($r.T + $GutterY))
    Start-Sleep -Seconds 2
    Save-Shot $ui "$Out-breakpoint.png"

    Select-Pane $ui Debug
    Invoke-Menu $ui Debug "Run under the debugger"
    Confirm-Dialog $ui
    Start-Sleep -Seconds 6

    # Held before anything ran, so one continue takes it to the breakpoint.
    Invoke-Menu $ui Debug "Continue"
    Start-Sleep -Seconds 8
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
