# Развернуть наблюдатель на стенде: остановить прежний, положить сборку из папки обмена,
# открыть порт только хосту и запустить задачей планировщика в сеансе пользователя.
# Вызывается lab/observer.sh по SSH от администратора; ключ к этому моменту уже лежит в $Root.
#
# Задача, правило брандмауэра и каталог ставятся при каждом вызове: в снимке их нет, а после
# отката стенд возвращается в состояние без них.
param(
    [string] $Exchange = '\\VBoxSvr\exchange\observer',
    [string] $Root = 'C:\lab\observer',
    [string] $Listen = '192.168.56.5:8100',
    [string] $HostAddress = '192.168.56.1'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'
$task = 'psdoctor-observer'
$exe = Join-Path $Root 'bin\PsDoctor.Observer.exe'
$key = Join-Path $Root 'observer.key'
$port = [int]($Listen -split ':')[-1]

if (-not (Test-Path $key)) { throw "нет ключа $key" }

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $task }
# Остановка задачи гасит conhost, но не обязательно сам наблюдатель: добиваем по имени и ждём,
# иначе robocopy упрётся в занятый exe.
Get-Process PsDoctor.Observer -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }

robocopy (Join-Path $Exchange 'bin') (Join-Path $Root 'bin') /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy сборки: код $LASTEXITCODE" }

$rule = 'psdoctor-observer'
Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $port `
    -RemoteAddress $HostAddress -Profile Any -Action Allow | Out-Null
# Окно «разрешить доступ» при первом прослушивании встало бы на рабочем столе стенда
# и мешало бы фактам о диалогах программы.
Set-NetFirewallProfile -All -NotifyOnListen False

$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$exe`" --listen $Listen --key-file `"$key`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User 'user'
$principal = New-ScheduledTaskPrincipal -UserId 'user' -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task

$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-Process PsDoctor.Observer -ErrorAction SilentlyContinue)) {
    if ($clock.Elapsed.TotalSeconds -gt 20) { throw 'наблюдатель не запустился за 20 с' }
    Start-Sleep -Milliseconds 200
}
$process = Get-Process PsDoctor.Observer
"наблюдатель запущен: pid $($process.Id), сеанс $($process.SessionId)"
