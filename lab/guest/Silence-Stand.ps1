# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Тишина на стенде: без обновлений, обслуживания, индексатора и проверок Защитника.
# Запускается владельцем через SSH от администратора, перед снимком «База + руки + тишина»:
#   Get-Content lab\guest\Silence-Stand.ps1 -Raw | C:\Projects\DoctorLab\tools\wps.ps1
#
# Зачем: фоновая установка обновлений и проверка каждого читаемого файла искажают ровно то,
# что меряет наблюдатель, — время загрузки и чтение файлов. Обновлять стенд бессмысленно:
# откат к снимку возвращает его в прошлое, и через месяц всё повторяется. Машина изолирована:
# host-only сеть, кабель NAT по умолчанию выдернут.
$ErrorActionPreference = 'Continue'
function Step($name, [scriptblock] $body) { try { & $body; "ок     $name" } catch { "ОШИБКА $name — $($_.Exception.Message)" } }
function RegSet($path, $name, $value) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    New-ItemProperty -Path $path -Name $name -Value $value -PropertyType DWord -Force -ErrorAction Stop | Out-Null
}

Step 'политика: не обновлять автоматически' { RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' NoAutoUpdate 1; RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' AUOptions 1 }
Step 'политика: не ходить в Центр обновления' { RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' DoNotConnectToWindowsUpdateInternetLocations 1; RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' DisableWindowsUpdateAccess 1 }
Step 'политика: оптимизация доставки только локально' { RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' DODownloadMode 0 }
Step 'политика: магазин не качает сам' { RegSet 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' AutoDownload 2 }
Step 'автоматическое обслуживание выключено' { RegSet 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' MaintenanceDisabled 1 }
Step 'окно «разрешить обнаруживать ваш ПК» для новых сетей не показывать' { if (-not (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff')) { New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff' -Force | Out-Null } }

foreach ($svc in 'wuauserv', 'UsoSvc', 'WaaSMedicSvc', 'DoSvc', 'WSearch', 'edgeupdate', 'edgeupdatem', 'MapsBroker') {
    Step "служба $svc" {
        if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) { return }
        Stop-Service $svc -Force -ErrorAction SilentlyContinue
        try { Set-Service $svc -StartupType Disabled -ErrorAction Stop }
        catch { Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -Name Start -Value 4 -ErrorAction Stop }
    }
}

$folders = '\Microsoft\Windows\WindowsUpdate\', '\Microsoft\Windows\UpdateOrchestrator\', '\Microsoft\Windows\WaaSMedic\',
    '\Microsoft\Windows\InstallService\', '\Microsoft\Windows\Windows Defender\', '\Microsoft\Windows\Maintenance\',
    '\Microsoft\Windows\Defrag\', '\Microsoft\Windows\Application Experience\', '\Microsoft\Windows\Customer Experience Improvement Program\'
foreach ($folder in $folders) {
    foreach ($task in Get-ScheduledTask -TaskPath $folder -ErrorAction SilentlyContinue) {
        try { $task | Disable-ScheduledTask -ErrorAction Stop | Out-Null; "ок     задание $folder$($task.TaskName)" }
        catch { "отказ  задание $folder$($task.TaskName) — $($_.Exception.Message.Trim())" }
    }
}
Get-ScheduledTask -TaskName 'MicrosoftEdgeUpdate*' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_ | Disable-ScheduledTask -ErrorAction Stop | Out-Null; "ок     задание $($_.TaskName)" } catch { "отказ  задание $($_.TaskName)" }
}

$mp = Get-MpComputerStatus
"Защитник до: реальное время=$($mp.RealTimeProtectionEnabled), защита от изменений=$($mp.IsTamperProtected)"
Step 'Защитник: исключения путей и процессов' { Add-MpPreference -ExclusionPath 'C:\lab', 'C:\Program Files (x86)\Photodex', 'C:\Users\user\AppData\Local\Temp' -ExclusionProcess 'proshow.exe', 'fvideo.exe', 'device-enc.dll', 'device-encp.dll' -ErrorAction Stop }
Step 'Защитник: без облака, отправки образцов и плановых проверок' { Set-MpPreference -MAPSReporting 0 -SubmitSamplesConsent 2 -ScanScheduleDay 8 -DisableCatchupFullScan $true -DisableCatchupQuickScan $true -ErrorAction Stop }
Step 'Защитник: реальное время выключить' { Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction Stop }
Start-Sleep -Seconds 2
"Защитник после: реальное время=$((Get-MpComputerStatus).RealTimeProtectionEnabled) (при защите от изменений выключение не держится — тогда остаются исключения)"
