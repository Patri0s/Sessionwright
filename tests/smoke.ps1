<#
.SYNOPSIS
    Starts Sessionwright on made-up data, opens its main windows and every Settings section through UI Automation, sets
    a master password and locks it, runs a script against a machine that is not there, and
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
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
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

# An element of that type and name (wildcards allowed) in any window of the app: a prompt, a menu, an editor.
function Find-Anywhere($Type, [string] $Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($Ae::ControlTypeProperty, $Type)
    foreach ($window in Get-AppWindows) {
        $hit = $window.FindAll($Scope::Descendants, $condition) | Where-Object { $_.Current.Name -like $Name } | Select-Object -First 1
        if ($hit) { return $hit }
    }
}

function Invoke-Anywhere($Type, [string] $Name, [int] $Seconds = 30) {
    (Wait-For { Find-Anywhere $Type $Name } "'$Name'" $Seconds).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}

function Set-Text([string] $Name, [string] $Text) {
    (Wait-For { Find-Anywhere $ControlType::Edit $Name } "the '$Name' box").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
}

# A key for the list's selected row, as from the keyboard: F2 edits, the menu key opens its menu.
function Send-Key([int] $Key) {
    $handle = [IntPtr](Get-MainWindow).Current.NativeWindowHandle
    [void][SmokeNative]::PostMessage($handle, 0x100, [IntPtr]$Key, [IntPtr]0)
    [void][SmokeNative]::PostMessage($handle, 0x101, [IntPtr]$Key, [IntPtr]0)
    Start-Sleep -Milliseconds 600
}

function Select-Row([string] $Name) {
    (Wait-For { Find-Anywhere $ControlType::TreeItem $Name } "the row '$Name'").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
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

# A shared list of the team's, read from a file next to the data.
$team = Join-Path $OutDir 'team.json'
'{ "Format": "Sessionwright", "Version": 1, "Groups": [], "Connections": [ { "Id": "b0000000-0000-0000-0000-000000000009", "Name": "Shared server", "Host": "127.0.0.1", "Port": 9 } ] }' |
    Set-Content $team -Encoding UTF8
Remove-Item (Join-Path $OutDir 'shared') -Recurse -ErrorAction SilentlyContinue

# Made-up connections on addresses nothing listens on; host probing off, so the run stays offline. The quick-connect
# key is off: a Sessionwright the developer has running holds it, and the warning about that would fail the run.
@'
{
  "SchemaVersion": 1,
  "Groups": [
    { "Id": "a0000000-0000-0000-0000-000000000001", "Name": "Production", "Color": "#E5534B" },
    { "Id": "a0000000-0000-0000-0000-000000000002", "Name": "Development", "Color": "#2EA043" },
    { "Id": "a0000000-0000-0000-0000-000000000003", "Name": "Team", "SharedList": "__TEAM__" }
  ],
  "Connections": [
    { "Id": "b0000000-0000-0000-0000-000000000001", "Name": "PROD AOS", "Host": "127.0.0.1", "Port": 9, "ParentId": "a0000000-0000-0000-0000-000000000001", "Favorite": true, "FavoriteOrder": 1 },
    { "Id": "b0000000-0000-0000-0000-000000000002", "Name": "DEV 12", "Host": "127.0.0.1", "Port": 9, "ParentId": "a0000000-0000-0000-0000-000000000002", "Notes": "smoke note 4711" },
    { "Id": "b0000000-0000-0000-0000-000000000003", "Name": "Build server", "Host": "127.0.0.1", "Port": 9 }
  ],
  "Credentials": [ { "Id": "c0000000-0000-0000-0000-000000000001", "Name": "Test account", "User": "tester" } ],
  "Settings": { "ProbeHosts": false, "Language": "en", "Theme": "Dark", "NewsSeen": "9999-12-31", "QuickConnectKey": "" }
}
'@.Replace('__TEAM__', $team.Replace('\', '\\')) | Set-Content $data -Encoding UTF8

$env:SESSIONWRIGHT_DATA = $data
$script:app = Start-Process $Exe -PassThru
$passed = $false
try {
    Wait-For { Get-MainWindow } 'the main window' 60 | Out-Null
    Assert-NoDialog 'starting'
    Write-Host 'Main window: open'

    # The palette: opened by its button, it finds a connection and, after ">", lists commands.
    $palette = Open-Window 'Search' 'Search'
    Set-Text 'Search' 'prod'
    Wait-For { Find-Anywhere $ControlType::ListItem 'PROD AOS*' } "'PROD AOS' among the palette's rows" | Out-Null
    Set-Text 'Search' '>sett'
    Wait-For { Find-Anywhere $ControlType::ListItem 'Settings*' } "'Settings' among the palette's commands" | Out-Null
    Close-Window $palette 'Search'
    Assert-NoDialog 'searching'
    Write-Host 'Search: finds a connection'

    Wait-For { Find-Inside (Get-MainWindow) $ControlType::Text 'Shared server' } "'Shared server' from the shared list" | Out-Null
    Assert-NoDialog 'reading the shared list'
    Write-Host 'Shared list: shows under its group'

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

    # The master password: set in Settings with the notes sealed too, then locked. The editor asks for it, and
    # without it shows the notes as locked instead of empty.
    $settings = Open-Window 'Settings (Ctrl+,)' 'Settings'
    (Find-Inside $settings $ControlType::TabItem 'Security').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Anywhere $ControlType::Button 'Set a master password*'
    Set-Text 'Master password' 'smoke test password'
    Set-Text 'Repeat the password' 'smoke test password'
    Invoke-Anywhere $ControlType::Button 'Set'
    (Wait-For { Find-Anywhere $ControlType::CheckBox 'Encrypt the notes too' } 'the master password to be set').GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 800
    Assert-NoDialog 'setting the master password'
    Close-Window $settings 'Settings'
    $saved = Get-Content $data -Raw
    if ($saved -match 'smoke note' -or $saved -notmatch 'SealedNotes') { throw 'The notes were not sealed in the data file' }
    Invoke-Anywhere $ControlType::Button 'Passwords are unlocked. Click to lock them.'
    Wait-For { Find-Anywhere $ControlType::Button 'Passwords are locked.*' } 'the lock to close' | Out-Null
    Select-Row 'DEV 12*'
    Send-Key 0x71
    Wait-For { Find-Anywhere $ControlType::Text 'Enter the master password*' } 'the master password prompt' | Out-Null
    Invoke-Anywhere $ControlType::Button 'Cancel'
    $editor = Wait-For { Get-AppWindows | Where-Object { $_.Current.Name -eq 'Edit connection' } | Select-Object -First 1 } "the 'Edit connection' window"
    (Find-Inside $editor $ControlType::TabItem 'Details').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $notes = Wait-For { Find-Inside $editor $ControlType::Edit 'Notes' } 'the notes box'
    if ($notes.Current.IsEnabled) { throw 'Locked notes can be edited' }
    Close-Window $editor 'Edit connection'
    if ((Get-Content $data -Raw) -notmatch 'SealedNotes') { throw 'The sealed notes were lost' }
    Write-Host 'Master password: set, locked, the editor leaves locked notes alone'

    # A script from a connection's menu: its window opens, runs and ends (reached or not), with no error dialog.
    Select-Row 'Build server*'
    Send-Key 0x5D
    (Wait-For { Find-Anywhere $ControlType::MenuItem 'Scripts' } 'the Scripts menu').GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Anywhere $ControlType::MenuItem 'Free disk space'
    $run = Wait-For { Get-AppWindows | Where-Object { $_.Current.Name -like 'Free disk space*' } | Select-Object -First 1 } 'the script window'
    Wait-For { $again = Find-Inside $run $ControlType::Button 'Run again'; $again -and $again.Current.IsEnabled } 'the script to end' 180 | Out-Null
    Assert-NoDialog 'running a script'
    $run.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For { -not (Get-AppWindows | Where-Object { $_.Current.Name -like 'Free disk space*' }) } 'the script window to close' 10 | Out-Null
    Write-Host 'Script: runs and reports in its window'

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
