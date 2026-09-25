#Requires -Version 7.6
# Установщик Doctor. Требует pwsh 7.6+: он стоит и на стенде, и у монтажёра (владелец, 25.09.2026).
# Ставит, обновляет поверх и удаляет. Права администратора берутся один раз, здесь; дальше всё работает без них.
#   Install-Doctor.ps1               установить из папки рядом со скриптом или обновить поверх
#   Install-Doctor.ps1 -Uninstall    удалить задачи и программу; журналы — по вопросу
[CmdletBinding()]
param(
    [switch] $Uninstall,
    # Учётная запись монтажёра. По умолчанию — тот, кто сейчас сидит за консолью.
    [string] $User = '',
    # Ответ на вопрос о журналах при удалении без вопроса: 'yes' — удалить, 'no' — оставить.
    [ValidateSet('', 'yes', 'no')] [string] $RemoveData = '',
    [switch] $NoPause
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $arguments += '-Uninstall' }
    if ($User) { $arguments += @('-User', "`"$User`"") }
    if ($RemoveData) { $arguments += @('-RemoveData', $RemoveData) }
    if ($NoPause) { $arguments += '-NoPause' }
    Start-Process (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList $arguments -Wait
    exit
}

$Root = Join-Path $env:ProgramFiles 'PsDoctor'
$MachineData = Join-Path $env:ProgramData 'PsDoctor'
$SettingsFile = Join-Path $MachineData 'settings.json'
$TaskPath = '\PsDoctor\'
$WatchdogTask = 'watchdog'
$EtwTask = 'etw'
$Observer = Join-Path $Root 'Observer\PsDoctor.Observer.exe'
$App = Join-Path $Root 'App\PsDoctor.App.exe'
$ShortcutFolder = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'PsDoctor'
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Doctor.lnk'

function Finish([int] $code) {
    if (-not $NoPause) { Read-Host 'Нажмите Enter, чтобы закрыть окно' | Out-Null }
    exit $code
}

function Resolve-TargetUser {
    if ($User) { return $User }
    # Установщик может работать под другой учётной записью администратора; задачи ставятся тому, кто за консолью.
    $console = (Get-CimInstance Win32_ComputerSystem).UserName
    if ($console) { return $console }
    return "$env:USERDOMAIN\$env:USERNAME"
}

function Get-UserLocalAppData([string] $account) {
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
    $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
    $profilePath = (Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath
    return Join-Path $profilePath 'AppData\Local'
}

# Останавливаются задачи и процессы только из каталога установки, по пути: под тем же именем на стенде
# работают наблюдатель конвейера и его помощник ETW из другого каталога. ProShow, запущенный наблюдателем,
# остаётся жить: его задание не закрывает процессы вместе с наблюдателем.
function Stop-Doctor {
    foreach ($name in @($WatchdogTask, $EtwTask)) {
        $task = Get-ScheduledTask -TaskPath $TaskPath -TaskName $name -ErrorAction SilentlyContinue
        if ($task) { Stop-ScheduledTask -InputObject $task }
    }
    $prefix = $Root.TrimEnd('\') + '\'
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in $processes) {
        Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Remove-Tasks {
    foreach ($name in @($WatchdogTask, $EtwTask)) {
        $task = Get-ScheduledTask -TaskPath $TaskPath -TaskName $name -ErrorAction SilentlyContinue
        if ($task) { Unregister-ScheduledTask -InputObject $task -Confirm:$false }
    }
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { $service.GetFolder('\').DeleteFolder('PsDoctor', 0) } catch { }
}

function New-Shortcut([string] $path, [string] $target) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = Split-Path $target
    $shortcut.IconLocation = "$target,0"
    $shortcut.Save()
}

try {
    $account = Resolve-TargetUser

    if ($Uninstall) {
        Write-Output "Удаление Doctor для $account"
        Stop-Doctor
        Remove-Tasks
        Remove-Item -LiteralPath $ShortcutFolder -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $DesktopShortcut -Force -ErrorAction SilentlyContinue
        # Скрипт может лежать в удаляемом каталоге: PowerShell прочёл его целиком, файл не держится.
        Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $Root) { Write-Warning "Каталог $Root удалён не полностью: что-то его держит." }

        $userData = Join-Path (Get-UserLocalAppData $account) 'PsDoctor'
        $answer = $RemoveData
        if (-not $answer) {
            $reply = Read-Host "Удалить журналы наблюдения, ключ и настройки ($userData, $MachineData)? [д/Н]"
            $answer = $reply -match '^(д|y)' ? 'yes' : 'no'
        }
        if ($answer -eq 'yes') {
            Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $MachineData -Recurse -Force -ErrorAction SilentlyContinue
            Write-Output 'Журналы, ключ и настройки удалены.'
        } else {
            Write-Output "Журналы, ключ и настройки оставлены: $userData, $MachineData"
        }
        Write-Output 'Doctor удалён.'
        Finish 0
    }

    $source = $PSScriptRoot
    $manifestFile = Join-Path $source 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "рядом с установщиком нет manifest.json: $source" }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.product -ne 'PsDoctor' -or $manifest.runtime -ne 'win-x64') {
        throw 'неподдерживаемый пакет'
    }
    if ($source.TrimEnd('\') -eq $Root.TrimEnd('\')) { throw 'установщик запущен из каталога установки; запустите его из пакета' }

    $installed = Join-Path $Root 'manifest.json'
    $previous = $null
    if (Test-Path -LiteralPath $installed) {
        $previous = (Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json).revision
    }
    if ($previous) { Write-Output "Обновление Doctor $previous -> $($manifest.revision) для $account" }
    else { Write-Output "Установка Doctor $($manifest.revision) для $account" }

    Stop-Doctor
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    foreach ($program in @($manifest.programs)) {
        if ($program -notmatch '^[A-Za-z]+$') { throw "недопустимое имя программы: $program" }
        $from = Join-Path $source $program
        if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "в пакете нет программы $program" }
        # Зеркало только внутри каталога программы: лишние файлы прежней версии уходят, соседнее не трогается.
        robocopy $from (Join-Path $Root $program) /MIR /R:3 /W:1 /NP /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "ошибка копирования ${program}: код $LASTEXITCODE" }
    }
    foreach ($file in @('Install-Doctor.ps1', 'Удалить.cmd', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $Root -Force
    }
    Copy-Item -LiteralPath $manifestFile -Destination $installed -Force

    # Настройки пишутся один раз: при обновлении их правка инженером сохраняется.
    New-Item -ItemType Directory -Force -Path $MachineData | Out-Null
    if (-not (Test-Path -LiteralPath $SettingsFile)) {
        @'
{
  // Где слушает наблюдатель. Для инженера из локальной сети: "0.0.0.0:8100", allowRemote: true
  // и правило брандмауэра на этот порт только для его адреса. Ключ в сети едет открытым текстом.
  "listen": "127.0.0.1:8100",
  "allowRemote": false,
  // Сторож опрашивает наблюдатель раз в pollSeconds; misses опросов подряд без ответа за timeoutSeconds — перезапуск.
  "watchdog": { "pollSeconds": 10, "timeoutSeconds": 5, "misses": 3 },
  // Обычный сеанс живёт days дней; все сеансы вместе — не больше megabytes. Сеансы мастера и с эпизодом — markedDays.
  "retention": { "days": 30, "megabytes": 2048, "markedDays": 180 }
}
'@ | Set-Content -LiteralPath $SettingsFile -Encoding UTF8
    }

    # Две задачи при входе монтажёра. Сторож с обычными правами сам запускает наблюдатель и перезапускает его.
    # Помощник ETW — с наивысшими: права берутся здесь, один раз, и окна повышения во время сеанса нет.
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
    $watchdogAction = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$Observer`" --watchdog"
    $etwAction = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$Observer`" --etw-helper"
    $limited = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
    $highest = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskPath $TaskPath -TaskName $WatchdogTask -Action $watchdogAction -Trigger $trigger `
        -Principal $limited -Settings $settings -Force | Out-Null
    Register-ScheduledTask -TaskPath $TaskPath -TaskName $EtwTask -Action $etwAction -Trigger $trigger `
        -Principal $highest -Settings $settings -Force | Out-Null

    New-Item -ItemType Directory -Force -Path $ShortcutFolder | Out-Null
    New-Shortcut (Join-Path $ShortcutFolder 'Doctor.lnk') $App
    New-Shortcut $DesktopShortcut $App

    # Монтажёр уже вошёл — задачи запускаются сейчас, не дожидаясь следующего входа.
    Start-ScheduledTask -TaskPath $TaskPath -TaskName $EtwTask
    Start-ScheduledTask -TaskPath $TaskPath -TaskName $WatchdogTask

    Write-Output "Doctor $($manifest.revision) установлен в $Root"
    Write-Output "Настройки: $SettingsFile"
    Finish 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    Finish 1
}
