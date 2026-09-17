# Выполнить скрипт из папки обмена в сеансе пользователя задачей планировщика и дождаться его.
# По SSH сеанс нулевой и окон не видит; опытам с окнами программы нужен сеанс 1.
#
#   Invoke-InSession.ps1 -Script Probe-BackgroundClick.ps1 -Arguments '-Runs 1'
#
# Вывод ложится в папку обмена (<Exchange>\session\out.txt), код — последним в exit.txt;
# вызывающий получает и то и другое.
param(
    [Parameter(Mandatory = $true)] [string] $Script,
    [string] $Arguments = '',
    [string] $Exchange = '\\VBoxSvr\exchange\observer',
    [string] $Root = 'C:\lab\probe',
    [int] $TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'

$task = 'psdoctor-probe'
$dir = Join-Path $Exchange 'session'
$out = Join-Path $dir 'out.txt'
$exit = Join-Path $dir 'exit.txt'
New-Item -ItemType Directory -Force $dir, $Root | Out-Null
Remove-Item $out, $exit -ErrorAction SilentlyContinue
# Скрипты копируются локально: задача не должна зависеть от того, что папка обмена подключена в её сеансе.
Copy-Item (Join-Path $PSScriptRoot '*.ps1') $Root -Force

$wrapper = Join-Path $Root 'In-Session.ps1'
@"
`$code = 0
try {
    & '$Root\$Script' $Arguments *>&1 | Out-File '$out' -Encoding utf8 -Width 400
    if (`$LASTEXITCODE) { `$code = `$LASTEXITCODE }
}
catch {
    `$_ | Out-String | Out-File '$out' -Encoding utf8 -Append
    `$code = 1
}
Set-Content '$exit' `$code
"@ | Set-Content $wrapper -Encoding utf8

$argument = "--headless pwsh.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$wrapper`""
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $argument
$principal = New-ScheduledTaskPrincipal -UserId 'user' -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSec)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task

$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $exit) -and $clock.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Milliseconds 500 }
if (Test-Path $out) { Get-Content $out -Encoding UTF8 }
if (-not (Test-Path $exit)) { Stop-ScheduledTask -TaskName $task; "таймаут $TimeoutSec с"; exit 124 }
exit [int](Get-Content $exit)
