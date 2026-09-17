# Прогнать тесты на стенде из исходников в папке обмена. Сети у стенда нет, поэтому пакеты
# берутся только из локального источника, который lab/observer.sh собрал на хосте.
#
#   Test-Stand.ps1            — здесь и сейчас: по SSH это нулевой сеанс
#   Test-Stand.ps1 -Session   — то же задачей планировщика в сеансе пользователя (окна видны);
#                               вызывающий ждёт её и получает вывод и код
#
# Оба пути собирают один каталог, поэтому выполняются по очереди, а не одновременно.
param(
    [switch] $Session,
    [string] $Log,
    [string] $Exchange = '\\VBoxSvr\exchange\observer',
    [string] $Root = 'C:\lab\observer',
    [int] $TimeoutSec = 1200
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if ($Session) {
    $task = 'psdoctor-test'
    # Вывод ложится в папку обмена: хост видит его и без SSH.
    $dir = Join-Path $Exchange 'session'
    $out = Join-Path $dir 'out.txt'
    $exit = Join-Path $dir 'exit.txt'
    New-Item -ItemType Directory -Force $dir | Out-Null
    Remove-Item $out, $exit -ErrorAction SilentlyContinue
    Copy-Item $PSCommandPath (Join-Path $Root 'Test-Stand.ps1') -Force

    $argument = "--headless pwsh.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Root\Test-Stand.ps1`" -Log `"$out`" -Exchange `"$Exchange`" -Root `"$Root`""
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
}

function Invoke-Tests {
    "сеанс $((Get-Process -Id $PID).SessionId), $env:USERNAME"
    robocopy (Join-Path $Exchange 'src') (Join-Path $Root 'src') /MIR /XD bin obj /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy исходников: код $LASTEXITCODE" }
    robocopy (Join-Path $Exchange 'packages') (Join-Path $Root 'packages') /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy пакетов: код $LASTEXITCODE" }

    $failed = 0
    foreach ($project in Get-ChildItem (Join-Path $Root 'src\tests') -Filter *.csproj -Recurse) {
        dotnet restore $project.FullName --source (Join-Path $Root 'packages') --verbosity quiet
        if ($LASTEXITCODE) { "restore не прошёл: $($project.Name)"; $failed++; continue }
        dotnet test $project.FullName --no-restore --disable-build-servers --verbosity quiet
        if ($LASTEXITCODE) { $failed++ }
    }
    "проектов с ошибкой: $failed"
    return $failed
}

if (-not $Log) {
    Invoke-Tests | Tee-Object -Variable lines
    exit [int]($lines | Select-Object -Last 1)
}

# Внутри задачи: весь вывод в файл, код — последним, по его появлению ждущий понимает, что всё.
$code = 0
try {
    Invoke-Tests *>&1 | Tee-Object -Variable lines | Out-File $Log -Encoding utf8 -Width 400
    $code = [int]($lines | Select-Object -Last 1)
}
catch {
    $_ | Out-String | Out-File $Log -Encoding utf8 -Append
    $code = 1
}
Set-Content (Join-Path (Split-Path $Log) 'exit.txt') $code
