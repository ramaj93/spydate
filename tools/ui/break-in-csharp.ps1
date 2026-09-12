# Opens a method's decompiled C#, clicks the breakpoint gutter beside a statement, and photographs
# the result.
#
# What it is checking is that the C# view is addressed at all: a dot only appears if the line the
# click landed on carried an address, which only happens if the decompiler's IL mapping reached it.
#
#   powershell -File tools\ui\break-in-csharp.ps1 -Open src\Spydate.Core\bin\Debug\net10.0\Spydate.Core.dll `
#              -Namespace Spydate.Core.Text -Type AddressText -Member "ParseHex*"

param(
    [Parameter(Mandatory = $true)][string]$Open,
    [Parameter(Mandatory = $true)][string]$Namespace,
    [Parameter(Mandatory = $true)][string]$Type,
    [Parameter(Mandatory = $true)][string]$Member,
    [string]$Out = "$env:TEMP\spydate-csharp.png",
    [int]$GutterX = 382,
    [int]$GutterY = 495)

Import-Module (Join-Path $PSScriptRoot "Spydate.Ui.psm1") -Force -DisableNameChecking

$ui = Start-Spydate -Open (Resolve-Path $Open)
try {
    # The tree is virtualised: each level has to be expanded before the next one exists.
    foreach ($step in @('Namespaces', $Namespace, $Type)) { [void](Expand-Tree $ui $step) }
    [void](Open-TreeItem $ui $Member)

    # The document opens in C#, which is the default language, so nothing has to be switched.
    $r = New-Object SpydateWin+R
    [void][SpydateWin]::GetWindowRect($ui.Handle, [ref]$r)
    [void][SpydateWin]::SetForegroundWindow($ui.Handle)
    Start-Sleep -Milliseconds 500

    # The margin sits left of the line numbers. The line this lands on is whichever statement is
    # drawn there, and the point is only whether a dot appears.
    [SpydateWin]::Click(($r.L + $GutterX), ($r.T + $GutterY))
    Start-Sleep -Seconds 2

    Save-Shot $ui $Out
}
finally {
    Stop-Spydate $ui
}
