# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Выполнить скрипт в сеансе пользователя гостевой системы стенда и вернуть его вывод.
#   lab\Invoke-LabUi.ps1 -Script lab\guest\Probe-Windows.ps1 -Parameters @{ ProcessName = 'proshow' }
#
# SSH попадает в нулевой сеанс и окон программы не видит, поэтому скрипт выполняет задача
# планировщика lab-ui: вход «только когда пользователь вошёл», без повышения, консоль без окна.
# Задача и обёртка ставятся при первом вызове после отката — в снимке их нет.
param(
    [Parameter(Mandatory = $true)] [string] $Script,
    [hashtable] $Parameters = @{},
    [int] $TimeoutSec = 600,
    [string] $LabHost = '192.168.56.5',
    [string] $Key = "$env:USERPROFILE\.ssh\lab_ed25519"
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ssh = @('-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-i', $Key)
$target = "user@$LabHost"

function Invoke-Guest([string] $body) {
    $full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; `$ProgressPreference='SilentlyContinue'`n$body"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($full))
    ssh @ssh $target "powershell -NoProfile -NonInteractive -EncodedCommand $enc"
}

Invoke-Guest 'New-Item -ItemType Directory -Force C:\lab\ui | Out-Null'
if ($LASTEXITCODE) { throw "гостевая система не отвечает по SSH (код $LASTEXITCODE)" }

# Windows PowerShell 5.1 в гостевой системе читает .ps1 без BOM как ANSI, и кириллица в скрипте
# превращается в мусор, поэтому скрипты уходят туда в UTF-8 с BOM.
$stage = Join-Path ([IO.Path]::GetTempPath()) "lab-ui-$PID"
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    # Уходит вся папка guest (обёртка и общие помощники) и выбранный скрипт под именем job.ps1.
    $bom = [Text.UTF8Encoding]::new($true)
    foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'guest') -File) {
        if ($file.Extension -eq '.ps1') { [IO.File]::WriteAllText("$stage\$($file.Name)", [IO.File]::ReadAllText($file.FullName), $bom) }
        else { Copy-Item $file.FullName $stage }
    }
    [IO.File]::WriteAllText("$stage\job.ps1", [IO.File]::ReadAllText((Resolve-Path $Script)), $bom)
    [IO.File]::WriteAllText("$stage\job.json", ($Parameters | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
    scp @ssh -q @((Get-ChildItem $stage).FullName) "${target}:C:/lab/ui/"
    if ($LASTEXITCODE) { throw "scp не прошёл (код $LASTEXITCODE)" }
}
finally { Remove-Item $stage -Recurse -ErrorAction SilentlyContinue }

Invoke-Guest @"
if (-not (Get-ScheduledTask -TaskName lab-ui -ErrorAction SilentlyContinue)) {
    `$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument '--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\lab\ui\run.ps1'
    `$principal = New-ScheduledTaskPrincipal -UserId 'user' -LogonType Interactive -RunLevel Limited
    `$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4)
    Register-ScheduledTask -TaskName lab-ui -Action `$action -Principal `$principal -Settings `$settings | Out-Null
}
Remove-Item C:\lab\ui\out.txt, C:\lab\ui\exit.txt -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName lab-ui
`$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path C:\lab\ui\exit.txt) -and `$clock.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Milliseconds 300 }
if (Test-Path C:\lab\ui\out.txt) { Get-Content C:\lab\ui\out.txt -Encoding UTF8 }
if (-not (Test-Path C:\lab\ui\exit.txt)) { Stop-ScheduledTask -TaskName lab-ui; Write-Output "таймаут $TimeoutSec с"; exit 124 }
exit [int](Get-Content C:\lab\ui\exit.txt)
"@
exit $LASTEXITCODE
