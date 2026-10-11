param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'artifacts\win-x64'),
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$taskExecutable = Join-Path $PublishDirectory 'Yokote.WeeklyTasks.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) {
    $PublishDirectory = $PSScriptRoot
    $taskExecutable = Join-Path $PublishDirectory 'Yokote.WeeklyTasks.exe'
}
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw '配布フォルダのYokote.WeeklyTasks.exeが見つかりません。先にpublishしてください。' }
$taskInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\WeeklyTasks'
New-Item -ItemType Directory -Path $taskInstallDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $PublishDirectory -Force | Where-Object { $_.Name -notin @('install.ps1','README.md','LICENSE') } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskInstallDirectory -Recurse -Force
}
$taskInstalledExecutable = Join-Path $taskInstallDirectory 'Yokote.WeeklyTasks.exe'
$taskShell = New-Object -ComObject WScript.Shell
$taskDesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) '1週間のタスク.lnk'
$taskShortcut = $taskShell.CreateShortcut($taskDesktopLink)
$taskShortcut.TargetPath = $taskInstalledExecutable
$taskShortcut.WorkingDirectory = $taskInstallDirectory
$taskShortcut.Description = '今日から7日間のタスクを表示'
$taskShortcut.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShortcut) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null
Write-Output ('導入先: ' + $taskInstallDirectory)
Write-Output ('ショートカット: ' + $taskDesktopLink)
if (-not $NoLaunch) { Start-Process -FilePath $taskInstalledExecutable }
