<#
.SYNOPSIS
    Starts Sessionwright on made-up data, opens its main windows and every Settings section through UI Automation, and
    fails when a window does not open, an error dialog appears, the log has an error, or the app does not close cleanly.
    Catches what the unit tests cannot see: a window that throws while it opens, an error at startup.
.EXAMPLE
    ./tests/smoke.ps1 -Exe src/Sessionwright/bin/Release/net10.0-windows/Sessionwright.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    # The data file, the app's log and, on failure, pictures of its windows.
    [string] $OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'sessionwright-smoke')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SmokeNative {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

$Ae = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

# Top-level windows of the app and the windows they own: UI Automation lists an owned window (Settings, a dialog over
# the main window) under its owner, not at the top.
function Get-AppWindows {
    $mine = New-Object System.Windows.Automation.PropertyCondition($Ae::ProcessIdProperty, $script:app.Id)
    $window = New-Object System.Windows.Automation.PropertyCondition($Ae::ControlTypeProperty, $ControlType::Window)
    foreach ($top in $Ae::RootElement.FindAll($Scope::Children, $mine)) {
        $top
        $top.FindAll($Scope::Descendants, $window)
    }
}

function Find-Inside($Element, $Type, [string] $Name) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($Ae::ControlTypeProperty, $Type)),
        (New-Object System.Windows.Automation.PropertyCondition($Ae::NameProperty, $Name)))
    $Element.FindFirst($Scope::Descendants, $condition)
}

function Wait-For([scriptblock] $Condition, [string] $What, [int] $Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ($script:app.HasExited) { throw "The app exited (code $($script:app.ExitCode)) while waiting for: $What" }
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out after $Seconds s waiting for: $What"
}

# The main window is the one with the "New connection" button; any other window called Sessionwright is a dialog.
function Get-MainWindow {
    Get-AppWindows | Where-Object { Find-Inside $_ $ControlType::Button 'New connection' } | Select-Object -First 1
}

function Assert-NoDialog([string] $After) {
    $dialogs = @(Get-AppWindows | Where-Object { $_.Current.Name -eq 'Sessionwright' -and -not (Find-Inside $_ $ControlType::Button 'New connection') })
    if ($dialogs.Count -gt 0) {
        $text = ($dialogs[0].FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($Ae::ControlTypeProperty, $ControlType::Text))) |
            ForEach-Object { $_.Current.Name }) -join ' | '
        throw "A dialog appeared after $After`: $text"
    }
}

function Open-Window([string] $Button, [string] $Title) {
    $main = Get-MainWindow
    $element = Find-Inside $main $ControlType::Button $Button
    if (-not $element) { throw "No button '$Button' in the main window" }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { Get-AppWindows | Where-Object { $_.Current.Name -eq $Title } | Select-Object -First 1 } "the '$Title' window"
}

function Close-Window($Window, [string] $Title) {
    $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For { -not (Get-AppWindows | Where-Object { $_.Current.Name -eq $Title }) } "the '$Title' window to close" 10 | Out-Null
}

function Save-Pictures {
    foreach ($w in Get-AppWindows) {
        try {
            $handle = [IntPtr]$w.Current.NativeWindowHandle
            $r = New-Object SmokeNative+RECT
            [void][SmokeNative]::GetWindowRect($handle, [ref]$r)
            if ($r.R - $r.L -lt 50 -or $r.B - $r.T -lt 50) { continue }
            $bitmap = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $dc = $graphics.GetHdc()
            [void][SmokeNative]::PrintWindow($handle, $dc, 2)
            $graphics.ReleaseHdc($dc); $graphics.Dispose()
            $name = ($w.Current.Name -replace '[^\w-]', '_')
            $bitmap.Save((Join-Path $OutDir "window-$name-$($handle.ToInt64()).png")); $bitmap.Dispose()
        }
        catch { Write-Warning "Could not picture a window: $_" }
    }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item (Join-Path $OutDir 'window-*.png') -ErrorAction SilentlyContinue
$data = Join-Path $OutDir 'data.json'
$log = Join-Path $OutDir 'sessionwright.log'
Remove-Item $log, "$data.lock" -ErrorAction SilentlyContinue

# Made-up connections on addresses nothing listens on; host probing off, so the run stays offline.
@'
{
  "SchemaVersion": 1,
  "Groups": [
    { "Id": "a0000000-0000-0000-0000-000000000001", "Name": "Production", "Color": "#E5534B" },
    { "Id": "a0000000-0000-0000-0000-000000000002", "Name": "Development", "Color": "#2EA043" }
  ],
  "Connections": [
    { "Id": "b0000000-0000-0000-0000-000000000001", "Name": "PROD AOS", "Host": "127.0.0.1", "Port": 9, "ParentId": "a0000000-0000-0000-0000-000000000001", "Favorite": true, "FavoriteOrder": 1 },
    { "Id": "b0000000-0000-0000-0000-000000000002", "Name": "DEV 12", "Host": "127.0.0.1", "Port": 9, "ParentId": "a0000000-0000-0000-0000-000000000002" },
    { "Id": "b0000000-0000-0000-0000-000000000003", "Name": "Build server", "Host": "127.0.0.1", "Port": 9 }
  ],
  "Credentials": [ { "Id": "c0000000-0000-0000-0000-000000000001", "Name": "Test account", "User": "tester" } ],
  "Settings": { "ProbeHosts": false, "Language": "en", "Theme": "Dark", "NewsSeen": "9999-12-31" }
}
'@ | Set-Content $data -Encoding UTF8

$env:SESSIONWRIGHT_DATA = $data
$script:app = Start-Process $Exe -PassThru
$passed = $false
try {
    Wait-For { Get-MainWindow } 'the main window' 60 | Out-Null
    Assert-NoDialog 'starting'
    Write-Host 'Main window: open'

    $search = Wait-For { (Get-MainWindow).FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($Ae::AutomationIdProperty, 'SearchBox'))) } 'the search box'
    $value = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('prod')
    Wait-For { Find-Inside (Get-MainWindow) $ControlType::Text 'PROD AOS' } "'PROD AOS' among the search results" | Out-Null
    $value.SetValue('')
    Assert-NoDialog 'searching'
    Write-Host 'Search: finds a connection'

    $settings = Open-Window 'Settings (Ctrl+,)' 'Settings'
    $tabs = @($settings.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($Ae::ControlTypeProperty, $ControlType::TabItem))))
    if ($tabs.Count -lt 2) { throw "Settings shows $($tabs.Count) sections" }
    foreach ($tab in $tabs) {
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 500
        Assert-NoDialog "opening Settings > $($tab.Current.Name)"
    }
    Close-Window $settings 'Settings'
    Write-Host "Settings: $($tabs.Count) sections open"

    foreach ($step in @(
            @{ Button = 'Keyboard shortcuts (F1)'; Title = 'Keyboard shortcuts' },
            @{ Button = 'New connection'; Title = 'New connection' },
            @{ Button = 'Credentials'; Title = 'Credentials' },
            @{ Button = 'Azure machines (Ctrl+M)'; Title = 'Azure machines' })) {
        $window = Open-Window $step.Button $step.Title
        Start-Sleep -Milliseconds 500
        Assert-NoDialog "opening $($step.Title)"
        Close-Window $window $step.Title
        Write-Host "$($step.Title): opens and closes"
    }

    Assert-NoDialog 'closing the windows'
    (Get-MainWindow).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $script:app.WaitForExit(20000)) { throw 'The app did not exit within 20 s of closing its main window' }
    if ($script:app.ExitCode -ne 0) { throw "The app exited with code $($script:app.ExitCode)" }
    Write-Host 'Closing: the app exits'

    $errors = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match '\sERR\s' })
    if ($errors.Count -gt 0) { throw "The log has errors:`n$($errors -join "`n")" }
    $passed = $true
    Write-Host 'Smoke test passed' -ForegroundColor Green
}
finally {
    if (-not $passed) {
        if (-not $script:app.HasExited) { Save-Pictures }
        Write-Host "Failed. Windows, data and log are in $OutDir" -ForegroundColor Red
    }
    if (-not $script:app.HasExited) { Stop-Process -Id $script:app.Id -Force }
}
