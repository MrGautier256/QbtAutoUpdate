<#
.SYNOPSIS
    Runs the QbtAutoUpdate end-to-end scenario in Windows Sandbox (test tooling, not part of the product).

.DESCRIPTION
    Publishes QbtAutoUpdate.exe, fetches the official qBittorrent installers of the starting version,
    writes a .wsb file and starts Windows Sandbox. Inside, Run-Scenario.ps1 installs qBittorrent,
    configures it the hard way (minimized to tray, close to tray, confirm on exit, start with Windows,
    Public desktop shortcut, an active torrent), sets QbtAutoUpdate up in auto mode, waits for the update,
    checks the result, rolls back, checks that the rolled-back version is held back, switches to notify
    mode and drives the notification and its buttons, repeats key checks as a standard user, uninstalls,
    writes summary.txt to the results folder and shuts the sandbox down.

.EXAMPLE
    .\sandbox\Start-Scenario.ps1 -Variant default
    .\sandbox\Start-Scenario.ps1 -Variant lt20
#>
[CmdletBinding()]
param(
    # default / lt20: setup --quiet in auto mode, update by the scheduled task.
    # ui: setup through the double-click window in notify mode, update from the settings window.
    [ValidateSet('default', 'lt20', 'ui')]
    [string] $Variant = 'default',
    [string] $QbtVersion = '5.2.3',
    [string] $WorkDir = (Join-Path $env:TEMP 'QbtAutoUpdate-Sandbox')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$scenario = Join-Path $WorkDir 'scenario'
$results = Join-Path $WorkDir ("results-{0}-{1:yyyyMMdd-HHmmss}" -f $Variant, (Get-Date))
New-Item -ItemType Directory -Force -Path $scenario, $results | Out-Null

Write-Host "Publishing QbtAutoUpdate (win-x64)..."
dotnet publish (Join-Path $repo 'src\QbtAutoUpdate') -c Release -r win-x64 -o $scenario --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$installer = if ($Variant -eq 'lt20') { "qbittorrent_${QbtVersion}_lt20_x64_setup.exe" } else { "qbittorrent_${QbtVersion}_x64_setup.exe" }
foreach ($file in @($installer, "$installer.asc")) {
    $target = Join-Path $scenario $file
    if (-not (Test-Path $target)) {
        Write-Host "Downloading $file..."
        Invoke-WebRequest "https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-$QbtVersion/$file" `
            -OutFile $target -UserAgent 'QbtAutoUpdate-Sandbox (+https://github.com/MrGautier256/QbtAutoUpdate)'
    }
}
Copy-Item (Join-Path $PSScriptRoot 'Run-Scenario.ps1') $scenario -Force

$wsb = Join-Path $WorkDir "QbtAutoUpdate-$Variant.wsb"
@"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$scenario</HostFolder>
      <SandboxFolder>C:\Scenario</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$results</HostFolder>
      <SandboxFolder>C:\Results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Scenario\Run-Scenario.ps1 -Variant $Variant -QbtVersion $QbtVersion</Command>
  </LogonCommand>
  <MemoryInMB>4096</MemoryInMB>
</Configuration>
"@ | Set-Content -Path $wsb -Encoding UTF8

# Windows Sandbox runs one instance at a time: starting while the previous one is still shutting down
# makes the new one fail before the scenario even starts.
$deadline = (Get-Date).AddMinutes(3)
while (Get-Process | Where-Object { $_.ProcessName -like 'WindowsSandbox*' }) {
    if ((Get-Date) -gt $deadline) { throw 'A Windows Sandbox is still running; close it and try again.' }
    Write-Host 'Waiting for the previous Windows Sandbox to close...'
    Start-Sleep -Seconds 5
}

Write-Host "Starting Windows Sandbox. Results will appear in:"
Write-Host "  $results"
Start-Process $wsb
