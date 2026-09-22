# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Установить последний релиз Doctor из общей папки. Лишние файлы установки не удаляются.
[CmdletBinding()]
param(
    [string] $Exchange = '\\VBoxSvr\exchange\psdoctor',
    [string] $Root = 'C:\lab\psdoctor',
    [string] $ObserverUrl = '',
    [string] $ObserverKeyFile = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'
$currentFile = Join-Path $Exchange 'current.txt'
if (-not (Test-Path -LiteralPath $currentFile -PathType Leaf)) { throw "нет указателя релиза: $currentFile" }
$relativeRelease = (Get-Content -LiteralPath $currentFile -Raw).Trim() -replace '/', '\'
if ([string]::IsNullOrWhiteSpace($relativeRelease) -or $relativeRelease -notmatch '^releases\\[^\\/]+$') {
    throw "недопустимый указатель релиза: $relativeRelease"
}
$release = Join-Path $Exchange $relativeRelease
$manifestFile = Join-Path $release 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "нет манифеста релиза: $manifestFile" }
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.runtime -ne 'win-x64') { throw 'неподдерживаемый манифест релиза' }

foreach ($name in @('PsDoctor.Observer', 'PsDoctor.Workbench', 'PsDoctor.App')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(3000)) { $_.Kill(); $_.WaitForExit(5000) }
    }
}
New-Item -ItemType Directory -Force -Path $Root | Out-Null
foreach ($project in @($manifest.projects)) {
    if ($project -notmatch '^[A-Za-z0-9.]+$') { throw "недопустимое имя программы: $project" }
    $source = Join-Path $release $project
    $target = Join-Path $Root $project
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "в релизе нет программы $project" }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    robocopy $source $target /E /COPY:DAT /DCOPY:DAT /R:3 /W:1 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "ошибка копирования ${project}: код $LASTEXITCODE" }
}
Copy-Item -LiteralPath $manifestFile -Destination (Join-Path $Root 'manifest.json') -Force
"$($manifest.revision) $($manifest.createdUtc)" | Set-Content -LiteralPath (Join-Path $Root 'installed.txt') -Encoding UTF8

# Постоянные точки входа не зависят от номера релиза и не требуют PATH.
@("@echo off", '"%~dp0PsDoctor.Cli\psdoctor.exe" %*') |
    Set-Content -LiteralPath (Join-Path $Root 'Run-Cli.cmd') -Encoding ASCII
@("@echo off", 'set "PSDOCTOR_INSTALL_ROOT=%~dp0"', 'if exist "%~dp0observer.url" set /p PSDOCTOR_OBSERVER_URL=<"%~dp0observer.url"', 'if exist "%~dp0observer.key" set "PSDOCTOR_OBSERVER_KEY_FILE=%~dp0observer.key"', '"%~dp0PsDoctor.App\PsDoctor.App.exe" %*') |
    Set-Content -LiteralPath (Join-Path $Root 'Run-App.cmd') -Encoding ASCII
@("@echo off", 'set "PSDOCTOR_INSTALL_ROOT=%~dp0"', 'if exist "%~dp0observer.url" set /p PSDOCTOR_OBSERVER_URL=<"%~dp0observer.url"', 'if exist "%~dp0observer.key" set "PSDOCTOR_OBSERVER_KEY_FILE=%~dp0observer.key"', '"%~dp0PsDoctor.Workbench\PsDoctor.Workbench.exe" %*') |
    Set-Content -LiteralPath (Join-Path $Root 'Run-Workbench.cmd') -Encoding ASCII
@("@echo off", 'if not exist "%~dp0observer.key" (echo Нет observer.key в каталоге установки. Запустите Deploy-Observer.ps1. & exit /b 2)', '"%~dp0PsDoctor.Observer\PsDoctor.Observer.exe" --listen 0.0.0.0:8100 --allow-remote --key-file "%~dp0observer.key" --data "%~dp0sessions" %*') |
    Set-Content -LiteralPath (Join-Path $Root 'Run-Observer.cmd') -Encoding ASCII

if ([string]::IsNullOrWhiteSpace($ObserverKeyFile)) {
    $ObserverKeyFile = Join-Path $Root 'observer.key'
}
[Environment]::SetEnvironmentVariable('PSDOCTOR_INSTALL_ROOT', $Root, 'User')
if (-not [string]::IsNullOrWhiteSpace($ObserverUrl)) {
    [Environment]::SetEnvironmentVariable('PSDOCTOR_OBSERVER_URL', $ObserverUrl, 'User')
    $ObserverUrl | Set-Content -LiteralPath (Join-Path $Root 'observer.url') -Encoding ASCII
}
if (Test-Path -LiteralPath $ObserverKeyFile -PathType Leaf) {
    [Environment]::SetEnvironmentVariable('PSDOCTOR_OBSERVER_KEY_FILE', $ObserverKeyFile, 'User')
}

function New-DoctorShortcut([string] $LinkPath, [string] $TargetPath, [string] $Arguments = '') {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($LinkPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $Root
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Save()
}

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PsDoctor'
$desktop = [Environment]::GetFolderPath('Desktop')
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
New-DoctorShortcut (Join-Path $startMenu 'Doctor.lnk') 'cmd.exe' "/d /c call `"$Root\Run-App.cmd`""
New-DoctorShortcut (Join-Path $startMenu 'Workbench.lnk') 'cmd.exe' "/d /c call `"$Root\Run-Workbench.cmd`""
New-DoctorShortcut (Join-Path $startMenu 'CLI.lnk') 'cmd.exe' "/d /k `"$Root\Run-Cli.cmd`""
New-DoctorShortcut (Join-Path $desktop 'Doctor.lnk') 'cmd.exe' "/d /c call `"$Root\Run-App.cmd`""
New-DoctorShortcut (Join-Path $desktop 'Workbench.lnk') 'cmd.exe' "/d /c call `"$Root\Run-Workbench.cmd`""

@"
PsDoctor — установленный релиз

Каталог: $Root
Версия: $($manifest.revision)

Запуск:
  Doctor.lnk       окно доктора
  Workbench.lnk    пульт наблюдателя
  Run-Cli.cmd      CLI; пример: Run-Cli.cmd "C:\путь\проект.psh"
  Run-Observer.cmd ручной запуск Observer, если рядом есть observer.key

Правильный запуск Observer для стенда:
  lab/observer.sh --no-tests на хосте — он доставляет Observer и поднимает задачи Windows.
"@ | Set-Content -LiteralPath (Join-Path $Root 'README-START.txt') -Encoding UTF8

Write-Output "Установлен релиз $($manifest.revision) ($($manifest.createdUtc)) в $Root"
Write-Output "Ярлыки: $startMenu и $desktop"
