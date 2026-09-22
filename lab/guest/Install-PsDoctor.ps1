# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Установить последний релиз Doctor из общей папки. Лишние файлы установки не удаляются.
[CmdletBinding()]
param(
    [string] $Exchange = '\\VBoxSvr\exchange\psdoctor',
    [string] $Root = 'C:\lab\psdoctor'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$PSStyle.OutputRendering = 'PlainText'
$currentFile = Join-Path $Exchange 'current.txt'
if (-not (Test-Path -LiteralPath $currentFile -PathType Leaf)) { throw "нет указателя релиза: $currentFile" }
$relativeRelease = (Get-Content -LiteralPath $currentFile -Raw).Trim()
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
Write-Output "Установлен релиз $($manifest.revision) ($($manifest.createdUtc)) в $Root"
