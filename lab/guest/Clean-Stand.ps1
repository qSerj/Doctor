# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Уборка стенда перед снимком: что занимает место на диске гостя, есть ли ffmpeg и ImageMagick, и уборка по списку.
#
#   Clean-Stand.ps1                                        — только показать, ничего не меняя
#   Clean-Stand.ps1 -Apply                                 — очистить временные папки пользователя и Windows
#   Clean-Stand.ps1 -Apply -Remove 'C:\lab\a', 'C:\lab\b'  — ещё и эти пути; удаляется только внутри C:\lab
#   Clean-Stand.ps1 -Apply -Tasks 'psdoctor-x'             — снять задачи планировщика из корня, только psdoctor-*
#
# Журналы Doctor (%LOCALAPPDATA%\PsDoctor) — свидетельства опытов: скрипт их показывает и не трогает никогда.
# Запускать при закрытом ProShow. По SSH: pwsh -NoProfile -File \\VBoxSvr\exchange\observer\lab\Clean-Stand.ps1;
# списки через -File приходят одной строкой, поэтому запятая внутри значения тоже разделяет.
param(
    [switch] $Apply,
    [string[]] $Remove = @(),
    [string[]] $Tasks = @()
)
$ErrorActionPreference = 'Stop'
# Без этого индикаторы прогресса приезжают через SSH мусором CLIXML поверх вывода.
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'

$lab = 'C:\lab'
$temps = @($env:TEMP, 'C:\Windows\Temp') | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

function Get-Size([string] $path) {
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($null -eq $sum) { 0 } else { [long] $sum }
}

function Show-Size([string] $path) {
    '{0,10:N0} МБ  {1}' -f ((Get-Size $path) / 1MB), $path
}

function Show-Stand {
    '== свободно на C:'
    $disk = Get-PSDrive C
    '{0:N1} ГБ из {1:N1} ГБ' -f ($disk.Free / 1GB), (($disk.Used + $disk.Free) / 1GB)

    "== $lab"
    Get-ChildItem -LiteralPath $lab -Force -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { Show-Size $_.FullName }

    '== временные папки'
    $temps | ForEach-Object { Show-Size $_ }

    '== прочее, что бывает большим (журналы Doctor не удаляются)'
    foreach ($path in "$env:USERPROFILE\.nuget\packages", "$env:USERPROFILE\Downloads", "$env:LOCALAPPDATA\CrashDumps",
        "$env:LOCALAPPDATA\PsDoctor", 'C:\Windows\SoftwareDistribution\Download') {
        if (Test-Path -LiteralPath $path) { Show-Size $path }
    }

    '== инструменты'
    foreach ($tool in 'ffmpeg', 'ffprobe', 'magick') {
        $found = Get-Command $tool -ErrorAction SilentlyContinue
        '{0}: {1}' -f $tool, $(if ($found) { $found.Source } else { 'нет' })
    }
    'PATH пользователя: ' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

if (-not $Apply) {
    Show-Stand
    return
}

if (Get-Process proshow -ErrorAction SilentlyContinue) {
    throw 'ProShow запущен: закройте его — временные файлы у него открыты'
}
$Remove = @($Remove | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_) })
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
# Списки проверяются целиком до первого изменения: опечатка не должна оставить уборку сделанной наполовину.
foreach ($path in $Remove) {
    if (-not $path.StartsWith($lab + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "удаляется только внутри ${lab}: $path"
    }
    if (-not (Test-Path -LiteralPath $path)) {
        throw "нет такого пути: $path"
    }
}
foreach ($name in $Tasks) {
    if ($name -notlike 'psdoctor-*' -or -not (Get-ScheduledTask -TaskPath '\' -TaskName $name -ErrorAction SilentlyContinue)) {
        throw "нет задачи \$name среди psdoctor-* в корне планировщика"
    }
}

foreach ($name in $Tasks) {
    Stop-ScheduledTask -TaskPath '\' -TaskName $name
    Unregister-ScheduledTask -TaskPath '\' -TaskName $name -Confirm:$false
    "снята задача: \$name"
}
# Процессы из удаляемых каталогов держат свои файлы: задача остановлена, а её процесс под conhost мог остаться жить.
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $exe = $_.ExecutablePath
    $exe -and @($Remove | Where-Object { $exe.StartsWith($_.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
})
foreach ($process in $running) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    'остановлен процесс {0}: {1}' -f $process.ProcessId, $process.ExecutablePath
}
foreach ($process in $running) {
    Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}

foreach ($temp in $temps) {
    $before = Get-Size $temp
    # Занятые файлы остаются: их держат живые процессы.
    Get-ChildItem -LiteralPath $temp -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    'очищено: {0:N0} МБ в {1}' -f (($before - (Get-Size $temp)) / 1MB), $temp
}
foreach ($path in $Remove) {
    Remove-Item -LiteralPath $path -Recurse -Force
    "удалено: $path"
}
''
Show-Stand
