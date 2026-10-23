<#
.SYNOPSIS
    The end-to-end scenario, run inside Windows Sandbox by its LogonCommand (see Start-Scenario.ps1).
    Every check is written to C:\Results\summary.txt as PASS or FAIL, with the raw evidence next to it.
#>
param(
    [string] $Variant = 'default',
    [string] $QbtVersion = '5.2.3'
)

$ErrorActionPreference = 'Continue'
$Results = 'C:\Results'
$Scenario = 'C:\Scenario'
$Tool = Join-Path $Scenario 'QbtAutoUpdate.exe'
$Installed = 'C:\Program Files\QbtAutoUpdate\QbtAutoUpdate.exe'
$Qbt = 'C:\Program Files\qBittorrent\qbittorrent.exe'
$Data = 'C:\ProgramData\QbtAutoUpdate'
$PublicShortcut = 'C:\Users\Public\Desktop\qBittorrent.lnk'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$QbtLog = Join-Path $env:LOCALAPPDATA 'qBittorrent\logs\qbittorrent.log'
$Summary = New-Object System.Collections.Generic.List[string]

Start-Transcript -Path (Join-Path $Results 'transcript.txt') | Out-Null

function Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    $line = '{0}  {1}{2}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { " - $Detail" } else { '' })
    $Summary.Add($line)
    Write-Host $line
}
function Note([string] $Text) { $Summary.Add("      $Text"); Write-Host $Text }
function Save([string] $Name, $Content) { $Content | Out-File -FilePath (Join-Path $Results $Name) -Encoding utf8 }
function QbtVersionNow { if (Test-Path $Qbt) { (Get-Item $Qbt).VersionInfo.FileVersionRaw.ToString() } else { 'missing' } }
function Tool([string] $Exe, [string[]] $Arguments, [string] $OutName) {
    $output = & $Exe @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    Save $OutName ("exit code $code`r`n$output")
    return $code
}
function AsUser([System.Management.Automation.PSCredential] $Credential, [string] $File, [string[]] $Arguments) {
    try {
        # A working directory the other account can open: Start-Process defaults to the caller's.
        $p = Start-Process $File -ArgumentList $Arguments -Credential $Credential -WorkingDirectory 'C:\Windows\System32' `
            -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
        return $p.ExitCode
    }
    catch {
        Note "Could not run $File as $($Credential.UserName): $_"
        return -1
    }
}
function State { if (Test-Path "$Data\state.json") { Get-Content "$Data\state.json" -Raw | ConvertFrom-Json } }

# ---- UI Automation, for the windows of the "ui" variant (TaskDialogs titled "QbtAutoUpdate")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
function Get-DialogText($Window) {
    if (-not $Window) { return '' }
    try { ($Window.FindAll($Tree::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ' } catch { '' }
}
function Dump($Window) {
    if (-not $Window) { return 'no window' }
    try { ($Window.FindAll($Tree::Descendants, $All) | ForEach-Object { '{0} [{1}] {2}' -f $_.Current.ControlType.ProgrammaticName, $_.Current.AutomationId, $_.Current.Name }) -join "`r`n" } catch { "$_" }
}
function Wait-Dialog([string] $Pattern, [int] $Seconds) {
    $byTitle = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'QbtAutoUpdate')
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            foreach ($window in $AE::RootElement.FindAll($Tree::Children, $byTitle)) {
                if ((Get-DialogText $window) -match $Pattern) { return $window }
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
    public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, System.UIntPtr.Zero); mouse_event(4, 0, 0, 0, System.UIntPtr.Zero); }
}
'@
# TaskDialog controls are exposed as "CommandButton_<id>", "CommandLink_<id>" and "RadioButton_<id>",
# with the button ids given to the dialog.
function Press($Window, [string] $ControlId) {
    if (-not $Window) { return $false }
    $control = $null
    foreach ($automationId in @("CommandButton_$ControlId", "CommandLink_$ControlId", "RadioButton_$ControlId", $ControlId)) {
        $control = $Window.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $automationId)))
        if ($control) { break }
    }
    if (-not $control) { Note "No control $ControlId in the window"; return $false }
    $pattern = $null
    if ($control.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) { $pattern.Invoke(); return $true }
    if ($control.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) { $pattern.Select(); return $true }
    # DirectUI panes without patterns: click their centre.
    $box = $control.Current.BoundingRectangle
    [Mouse]::Click([int]($box.X + $box.Width / 2), [int]($box.Y + $box.Height / 2))
    Start-Sleep -Milliseconds 300
    return $true
}
function TaskInfo([string] $Name) { schtasks.exe /query /tn "\QbtAutoUpdate\$Name" /v /fo list 2>&1 | Out-String }

# ---------------------------------------------------------------- environment
Save 'environment.txt' @(
    [Environment]::OSVersion.VersionString
    "Variant: $Variant, starting version: $QbtVersion"
    (whoami.exe /all | Out-String)
    (reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' /v EnableLUA 2>&1 | Out-String)
)
for ($i = 0; $i -lt 30; $i++) {
    try { [System.Net.Dns]::GetHostEntry('www.qbittorrent.org') | Out-Null; break } catch { Start-Sleep 2 }
}

# ---------------------------------------------------------------- 1. qBittorrent, configured the hard way
$installer = if ($Variant -eq 'lt20') { "qbittorrent_${QbtVersion}_lt20_x64_setup.exe" } else { "qbittorrent_${QbtVersion}_x64_setup.exe" }
$p = Start-Process (Join-Path $Scenario $installer) -ArgumentList '/S' -Wait -PassThru
Check "qBittorrent $QbtVersion ($Variant) installed" ($p.ExitCode -eq 0 -and (QbtVersionNow) -like "$QbtVersion*") "exit $($p.ExitCode), file $(QbtVersionNow)"

$config = Join-Path $env:APPDATA 'qBittorrent'
New-Item -ItemType Directory -Force -Path $config | Out-Null
@'
[LegalNotice]
Accepted=true

[Preferences]
General\CloseToTray=true
General\CloseToTrayNotified=true
General\MinimizeToTray=true
General\MinimizeToTrayNotified=true
General\ExitConfirm=true
Advanced\updateCheck=false

[GUI]
StartUpWindowState=Minimized

[BitTorrent]
Session\GlobalDLSpeedLimit=300
Session\GlobalUPSpeedLimit=30
'@ | Set-Content -Path (Join-Path $config 'qBittorrent.ini') -Encoding Ascii

$runValue = "`"$Qbt`" `"--profile=`" `"--configuration=`""
Set-ItemProperty -Path $RunKey -Name 'qBittorrent' -Value $runValue
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($PublicShortcut)
$link.TargetPath = $Qbt
$link.WorkingDirectory = 'C:\Program Files\qBittorrent'
$link.Save()

$downloads = Join-Path $env:USERPROFILE 'Downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$torrent = Join-Path $downloads 'big-buck-bunny.torrent'
try { Invoke-WebRequest 'https://webtorrent.io/torrents/big-buck-bunny.torrent' -OutFile $torrent -UseBasicParsing } catch { Note "No test torrent: $_" }
$arguments = @('--skip-dialog=true', "`"--save-path=$downloads`"")
if (Test-Path $torrent) { $arguments += "`"$torrent`"" }
Start-Process $Qbt -ArgumentList $arguments
Start-Sleep 40
$before = Get-Process qbittorrent -ErrorAction SilentlyContinue | Select-Object -First 1
Check 'qBittorrent running (started minimized to tray, with a torrent)' ($null -ne $before) "PID $($before.Id)"
Save 'qbt-commandline-before.txt' ((Get-CimInstance Win32_Process -Filter "Name='qbittorrent.exe'").CommandLine)
$resumeBefore = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'qBittorrent\BT_backup') -Filter *.fastresume -ErrorAction SilentlyContinue)
Note "Resume files before: $($resumeBefore.Count)"

Tool $Tool @('status') 'status-before.txt' | Out-Null

# ---------------------------------------------------------------- 2. setup
if ($Variant -eq 'ui') {
    # Double-click equivalent: started by Explorer, so no console is attached and the windows open.
    # Buttons are pressed by control id, which does not depend on the Windows display language.
    Start-Process explorer.exe -ArgumentList "`"$Tool`""
    $w = Wait-Dialog 'Keep qBittorrent up to date' 60
    Save 'ui-setup-window.txt' (Dump $w)
    Check 'setup window shown on double-click' ($null -ne $w) (Get-DialogText $w)
    Press $w '102' | Out-Null   # "Ask me first with a notification": the first check must install nothing
    Press $w '200' | Out-Null   # Install
    $w = Wait-Dialog 'QbtAutoUpdate is ready' 120
    Save 'ui-setup-result.txt' (Dump $w)
    Check 'setup window reports success' ($null -ne $w) (Get-DialogText $w)
    Press $w '1' | Out-Null     # OK
    Start-Sleep 5
    Check 'setup window closed' (-not (Get-Process QbtAutoUpdate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Tool }))
    Check 'notify mode saved' ((Get-Content "$Data\config.json" -Raw) -match '"mode": "notify"')
}
else {
    $code = Tool $Tool @('setup', '--quiet', '--mode', 'auto') 'setup.txt'
    Check 'setup --quiet --mode auto' ($code -eq 0) "exit $code"
}
Check 'exe copied to Program Files' (Test-Path $Installed)
Check 'Check and Apply tasks registered' ((schtasks.exe /query /tn '\QbtAutoUpdate\Check' 2>$null) -and (schtasks.exe /query /tn '\QbtAutoUpdate\Apply' 2>$null))
Check 'Installed apps entry' ($null -ne (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QbtAutoUpdate' -ErrorAction SilentlyContinue))
Check 'Start menu shortcut' (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\QbtAutoUpdate.lnk")
Save 'acl.txt' $(icacls.exe $Data; icacls.exe "$Data\inbox"; icacls.exe "$Data\work"; icacls.exe 'C:\Program Files\QbtAutoUpdate')
Save 'task-check.xml' (schtasks.exe /query /tn '\QbtAutoUpdate\Check' /xml | Out-String)
Save 'task-apply.xml' (schtasks.exe /query /tn '\QbtAutoUpdate\Apply' /xml | Out-String)

# ---------------------------------------------------------------- 3. the update
if ($Variant -eq 'ui') {
    # Notify mode: the first check installs nothing. The update is done from the settings window.
    Start-Sleep 20
    Check 'first check in notify mode installed nothing' ((QbtVersionNow) -like "$QbtVersion*") "file $(QbtVersionNow)"
    Start-Process explorer.exe -ArgumentList "`"$Installed`""
    $w = Wait-Dialog 'Check for updates now' 60
    Save 'ui-settings-window.txt' (Dump $w)
    Check 'settings window shown when installed' ($null -ne $w) (Get-DialogText $w)
    Press $w '301' | Out-Null   # Check for updates now
    $w = Wait-Dialog 'is available' 120
    Save 'ui-update-offer.txt' (Dump $w)
    Check 'update offered in a window' ($null -ne $w) (Get-DialogText $w)
    Press $w '6' | Out-Null     # Update now
    # The result's main instruction: "qBittorrent 5.3.0 is installed" (not the setup text "... (libtorrent 1.2) is installed in").
    $w = Wait-Dialog '(qBittorrent \d[\d.]* is installed|did not complete)' 900
    Save 'ui-update-result.txt' (Dump $w)
    Check 'update done from the settings window' ((Get-DialogText $w) -match 'qBittorrent \d[\d.]* is installed') (Get-DialogText $w)
    Press $w '1' | Out-Null     # OK
    $w = Wait-Dialog 'Check for updates now' 30
    Check 'back to the settings window' ($null -ne $w)
    Press $w '8' | Out-Null     # Close
    Start-Sleep 20
}
else {
    $deadline = (Get-Date).AddMinutes(15)
    $kicked = $false
    do {
        Start-Sleep 5
        $state = State
        if (-not $kicked -and (Get-Date) -gt $deadline.AddMinutes(-13) -and -not $state) {
            Note 'No activity after 2 minutes: starting the Check task by hand.'
            schtasks.exe /run /tn '\QbtAutoUpdate\Check' | Out-Null
            $kicked = $true
        }
    } until (($state -and $state.lastAttempt -and $state.lastAttempt.result -ne 'installing') -or (Get-Date) -gt $deadline)
    Start-Sleep 20
}

$state = State
Save 'state.json' ($state | ConvertTo-Json -Depth 5)
Save 'task-info-after.txt' ((TaskInfo 'Check') + (TaskInfo 'Apply'))
Check 'apply answered' ($null -ne $state.lastAttempt) "$($state.lastAttempt.result): $($state.lastAttempt.message)"
Check 'qBittorrent updated' ((QbtVersionNow) -notlike "$QbtVersion*" -and (QbtVersionNow) -ne 'missing') "file $(QbtVersionNow)"

Tool $Installed @('status') 'status-after.txt' | Out-Null
$statusAfter = Get-Content (Join-Path $Results 'status-after.txt') -Raw
$expected = if ($Variant -eq 'lt20') { 'libtorrent 2.x' } else { 'libtorrent 1.2' }
Check "variant kept ($expected)" ($statusAfter -match [regex]::Escape("Variant        $expected"))

$after = Get-Process qbittorrent -ErrorAction SilentlyContinue | Select-Object -First 1
Check 'qBittorrent started again' ($null -ne $after -and $after.Id -ne $before.Id) "PID $($after.Id)"
$commandAfter = (Get-CimInstance Win32_Process -Filter "Name='qbittorrent.exe'").CommandLine
Save 'qbt-commandline-after.txt' $commandAfter
Check 'restarted without the .torrent argument' ($commandAfter -and $commandAfter -notmatch '\.torrent')
Start-Sleep 30
Check 'qBittorrent still running 50 s after the task ended' ($null -ne (Get-Process qbittorrent -ErrorAction SilentlyContinue))

# UTF-8 without BOM: Windows PowerShell 5.1 would read it as ANSI and garble the accents.
$log = if (Test-Path $QbtLog) { Get-Content $QbtLog -Raw -Encoding UTF8 } else { '' }
# qBittorrent logs in the Windows display language (the sandbox copies the host's): English or French here.
Check 'clean shutdown in qBittorrent log' ($log -match 'ready to exit|pr.t . quitter')
Check 'resume data saved at shutdown' ($log -match 'Saving resume data completed|donn.es de reprise compl.t.e')
if (Test-Path $torrent) {
    Check 'torrent restored by the new version' ($log -match '(Restored torrent|Torrent restaur.e?)\. Torrent')
}
$resumeAfter = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'qBittorrent\BT_backup') -Filter *.fastresume -ErrorAction SilentlyContinue)
Check 'torrents kept' ($resumeAfter.Count -eq $resumeBefore.Count) "$($resumeBefore.Count) -> $($resumeAfter.Count)"
Check 'Public desktop shortcut kept' (Test-Path $PublicShortcut)
Check 'start with Windows kept' ((Get-ItemProperty $RunKey -ErrorAction SilentlyContinue).qBittorrent -eq $runValue)
Check 'settings backed up' (@(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'QbtAutoUpdate\backups') -Recurse -Filter qBittorrent.ini -ErrorAction SilentlyContinue).Count -ge 1)

# ---------------------------------------------------------------- 4. a standard user
$user = 'qbtstd'
# At most 14 characters: net.exe asks an interactive question above that.
$password = 'Qa1!' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$created = net.exe user $user $password /add 2>&1 | Out-String
Check 'standard account created' ($LASTEXITCODE -eq 0) $created.Trim()
$credential = New-Object System.Management.Automation.PSCredential(".\$user", (ConvertTo-SecureString $password -AsPlainText -Force))
Check 'standard account is not an administrator' (-not ((net.exe localgroup Administrators 2>&1 | Out-String) -match "\b$user\b"))

$code = AsUser $credential 'cmd.exe' @('/c', "echo x> $Data\work\probe.txt")
Check 'standard user cannot write work\' (-not (Test-Path "$Data\work\probe.txt")) "cmd exit $code"
$code = AsUser $credential 'cmd.exe' @('/c', "echo x> $Data\config.json")
Check 'standard user cannot change config.json' ((Get-Content "$Data\config.json" -Raw) -notmatch '^x')

# A tampered installer, dropped and triggered by the standard user.
$id = [guid]::NewGuid().ToString('N')
Copy-Item (Join-Path $Scenario $installer) 'C:\Users\Public\tampered.exe'
$stream = [System.IO.File]::Open('C:\Users\Public\tampered.exe', 'Append')
$stream.WriteByte(0)
$stream.Close()
Copy-Item (Join-Path $Scenario "$installer.asc") 'C:\Users\Public\tampered.exe.asc'
$name = "${id}__qbittorrent_9.9.9_x64_setup.exe"
AsUser $credential 'cmd.exe' @('/c', "copy /y C:\Users\Public\tampered.exe $Data\inbox\$name && copy /y C:\Users\Public\tampered.exe.asc $Data\inbox\$name.asc") | Out-Null
Check 'standard user can drop files in inbox\' (Test-Path "$Data\inbox\$name")
$code = AsUser $credential 'schtasks.exe' @('/run', '/tn', '\QbtAutoUpdate\Apply')
Check 'standard user can start the Apply task' ($code -eq 0) "schtasks exit $code"
Start-Sleep 30
$state = State
Check 'tampered installer refused' ($state.lastAttempt.requestId -eq $id -and $state.lastAttempt.result -eq 'refused') "$($state.lastAttempt.result): $($state.lastAttempt.message)"
Check 'inbox emptied' (@(Get-ChildItem "$Data\inbox").Count -eq 0)

# ---------------------------------------------------------------- 5. uninstall
New-Item -ItemType Directory -Force -Path (Join-Path $Results 'logs') | Out-Null
Copy-Item "$Data\logs" (Join-Path $Results 'logs\machine') -Recurse -ErrorAction SilentlyContinue
Copy-Item (Join-Path $env:LOCALAPPDATA 'QbtAutoUpdate\logs') (Join-Path $Results 'logs\user') -Recurse -ErrorAction SilentlyContinue
Copy-Item $QbtLog (Join-Path $Results 'logs\qbittorrent.log') -ErrorAction SilentlyContinue

$code = Tool $Installed @('uninstall', '--quiet') 'uninstall.txt'
Check 'uninstall --quiet' ($code -eq 0) "exit $code"
Check 'no task left' (-not (schtasks.exe /query /tn '\QbtAutoUpdate\Check' 2>$null))
Check 'Program Files folder removed' (-not (Test-Path 'C:\Program Files\QbtAutoUpdate'))
Check 'ProgramData folder removed' (-not (Test-Path $Data))
Check 'Installed apps entry removed' ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QbtAutoUpdate' -ErrorAction SilentlyContinue))
Check 'Start menu shortcut removed' (-not (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\QbtAutoUpdate.lnk"))
Check 'qBittorrent itself untouched' (Test-Path $Qbt)

# ---------------------------------------------------------------- done
$failed = @($Summary | Where-Object { $_ -like 'FAIL*' }).Count
$Summary.Insert(0, "QbtAutoUpdate sandbox scenario - variant $Variant from $QbtVersion - $(Get-Date -Format s) - $failed failure(s)")
Save 'summary.txt' $Summary
Stop-Transcript | Out-Null
'done' | Out-File (Join-Path $Results 'DONE') -Encoding ascii
shutdown.exe /s /t 20
