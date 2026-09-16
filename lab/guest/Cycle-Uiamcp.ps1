# Критерий Л1 без модели: открыть проект, закрыть диалоги по правилам, дождаться и прочитать
# заголовок, закрыть программу — Runs раз подряд. Все действия с окнами — через CLI
# mcp-server-uiautomation (uiamcp) по описанию элемента; Win32 здесь только наблюдает курсор.
# Строка JSON на прогон.
param(
    [Parameter(Mandatory = $true)] [string] $Project,
    [int] $Runs = 10,
    # окно диалога → аргументы поиска кнопки внутри процесса
    [object] $Rules = @{ 'Old Show format detected.' = @('--automation-id', '2'); 'Message' = @('--name', 'Ok to All') },
    [string] $Program = 'C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe',
    [string] $Uia = 'C:\lab\tools\uiamcp\uiamcp.exe',
    [string[]] $DialogClasses = @('#32770', 'AGDSDocParent'),
    [int] $LoadTimeoutSec = 120,
    [int] $ExitTimeoutSec = 30
)
. "$PSScriptRoot\Win32.ps1"
$ErrorActionPreference = 'Stop'

$ruleTable = @{}
if ($Rules -is [hashtable]) { $ruleTable = $Rules } else { $Rules.PSObject.Properties | ForEach-Object { $ruleTable[$_.Name] = @($_.Value) } }
$file = Split-Path $Project -Leaf
$cache = [IO.Path]::ChangeExtension($Project, '.pxc')
$pristine = "$cache.orig"      # кэш до первого прогона: ProShow переписывает .pxc при закрытии

function Get-Cursor { $p = New-Object Lab.Win32+POINT; [void][Lab.Win32]::GetCursorPos([ref]$p); "$($p.X),$($p.Y)" }

function Invoke-Uia([string[]] $Arguments) {
    # Windows PowerShell 5.1 при Stop делает исключение из любой строки stderr внешней программы
    $ErrorActionPreference = 'Continue'
    $out = & $Uia @Arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}

# Окна процесса. Диалог в дереве UI Automation висит под своим владельцем — главным окном, —
# поэтому ищется по всему поддереву, а от внутренних окон программы (превью и прочие)
# отличается классом.
function Get-Windows([int] $ProcessId) {
    $r = Invoke-Uia @('find', '--process-id', "$ProcessId", '--control-type', '50032', '--scope', 'descendants', '--max-results', '50')
    if ($r.Code -ne 0) { return @() }
    try { ($r.Text | ConvertFrom-Json) | ForEach-Object { $_ } | Where-Object { $_.name } | ForEach-Object { [pscustomobject]@{ Name = $_.name; Class = $_.className } } } catch { @() }
}

if (-not (Test-Path $pristine) -and (Test-Path $cache)) { Copy-Item $cache $pristine }
Get-Process proshow -ErrorAction SilentlyContinue | Stop-Process -Force

$passed = 0
for ($run = 1; $run -le $Runs; $run++) {
    if (Test-Path $pristine) { Copy-Item $pristine $cache -Force }
    $events = New-Object System.Collections.Generic.List[object]
    $result = [ordered]@{ run = $run; ok = $false }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process $Program -ArgumentList "`"$Project`"" -PassThru

    $title = $null
    while (-not $title -and $clock.Elapsed.TotalSeconds -lt $LoadTimeoutSec -and -not $proc.HasExited) {
        $windows = @(Get-Windows $proc.Id)
        $open = @($windows | Where-Object { $DialogClasses -contains $_.Class })
        $unknown = @($open | Where-Object { -not $ruleTable.ContainsKey($_.Name) } | ForEach-Object { "$($_.Name) [$($_.Class)]" })
        if ($unknown) { $result.unknown = $unknown; break }
        if ($open) {
            $name = $open[0].Name
            $before = Get-Cursor
            $r = Invoke-Uia (@('action', 'invoke', '--process-id', "$($proc.Id)", '--control-type', '50000') + $ruleTable[$name] + @('--scope', 'descendants'))
            $events.Add([ordered]@{ t = [math]::Round($clock.Elapsed.TotalSeconds, 1); window = $name; code = $r.Code; cursor_moved = ($before -ne (Get-Cursor)) })
            Start-Sleep -Milliseconds 700   # дать диалогу закрыться, чтобы не нажать его второй раз
            continue
        }
        $title = $windows | Where-Object { $_.Name -like "ProShow Producer - * - $file" } | ForEach-Object { $_.Name } | Select-Object -First 1
        if (-not $title) { Start-Sleep -Milliseconds 500 }
    }
    $result.dialogs = $events
    if ($title) {
        $result.title = $title
        $result.t_loaded = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        $before = Get-Cursor
        # код возврата close ненадёжен: окно исчезает во время вызова и CLI отвечает 0x80040201
        [void](Invoke-Uia @('action', 'close', '--process-id', "$($proc.Id)", '--name', $title, '--scope', 'children'))
        $result.close_cursor_moved = ($before -ne (Get-Cursor))
        $exited = $proc.WaitForExit($ExitTimeoutSec * 1000)
        $result.t_exit = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        $result.ok = $exited -and -not ($events | Where-Object { $_.code -ne 0 -or $_.cursor_moved }) -and -not $result.close_cursor_moved
        if (-not $exited) { $result.error = 'не завершился после close' }
    }
    elseif ($proc.HasExited) { $result.error = "процесс завершился сам, код $($proc.ExitCode)" }
    elseif (-not $result.unknown) { $result.error = "заголовок с $file не дождались за $LoadTimeoutSec с" }

    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Seconds 1 }
    if ($result.ok) { $passed++ }
    [pscustomobject]$result | ConvertTo-Json -Compress -Depth 5
}
[pscustomobject]@{ runs = $Runs; passed = $passed } | ConvertTo-Json -Compress
exit $(if ($passed -eq $Runs) { 0 } else { 1 })
