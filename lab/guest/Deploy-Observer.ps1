# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Развернуть наблюдатель на стенде: остановить прежний, положить сборку из папки обмена,
# открыть порт только хосту и запустить задачами планировщика в сеансе пользователя.
# Вызывается lab/observer.sh по SSH от администратора; ключ к этому моменту уже лежит в $Root.
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
$etwTask = 'psdoctor-etw'
$exe = Join-Path $Root 'bin\PsDoctor.Observer.exe'
$key = Join-Path $Root 'observer.key'
$data = Join-Path $Root 'sessions'
$port = [int]($Listen -split ':')[-1]

if (-not (Test-Path $key)) { throw "нет ключа $key" }

foreach ($name in @($task, $etwTask)) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $name }
}
# Остановка задачи гасит conhost, но не обязательно дочерний процесс: ждём освобождения exe.
# Только процессы своего каталога: под тем же именем работают сторож и помощник ETW установленного Doctor.
$binPrefix = (Join-Path $Root 'bin').TrimEnd('\') + '\'
function Get-LabObserver {
    @(Get-Process PsDoctor.Observer -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($binPrefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}
Get-LabObserver | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }

robocopy (Join-Path $Exchange 'bin') (Join-Path $Root 'bin') /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy сборки: код $LASTEXITCODE" }

$rule = 'psdoctor-observer'
Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $port `
    -RemoteAddress $HostAddress -Profile Any -Action Allow | Out-Null
# Окно «разрешить доступ» при первом прослушивании мешало бы фактам о диалогах программы.
Set-NetFirewallProfile -All -NotifyOnListen False

# ETW держит отдельный повышенный процесс; HTTP и работа с окнами остаются неповышенными.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$exe`" --listen $Listen --allow-remote --key-file `"$key`" --data `"$data`""
$etwAction = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$exe`" --etw-helper --data `"$data`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User 'user'
$principal = New-ScheduledTaskPrincipal -UserId 'user' -LogonType Interactive -RunLevel Limited
$etwPrincipal = New-ScheduledTaskPrincipal -UserId 'user' -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Register-ScheduledTask -TaskName $etwTask -Action $etwAction -Trigger $trigger -Principal $etwPrincipal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $etwTask
Start-ScheduledTask -TaskName $task

$clock = [Diagnostics.Stopwatch]::StartNew()
while ((Get-LabObserver).Count -lt 2) {
    if ($clock.Elapsed.TotalSeconds -gt 20) { throw 'наблюдатель и ETW-помощник не запустились за 20 с' }
    Start-Sleep -Milliseconds 200
}
$process = Get-LabObserver
"наблюдатель и ETW-помощник запущены: pid $($process.Id -join ', '), сеанс $($process.SessionId -join ', ')"