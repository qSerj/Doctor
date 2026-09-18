# Загрузка и рендер проекта БЕЗ наблюдателя (пункт 8 критерия Э4.0): запуск ProShow с файлом шоу, опрос окон
# раз в полсекунды — как у наблюдателя — и те же нажатия через Invoke UI Automation, но без задания, замеров,
# фактов и журнала. Этим и меряется цена наблюдения: разница со сценарием наблюдателя на том же проекте.
#
#   Invoke-InSession.ps1 -Script Measure-TitleLoad.ps1 -Arguments "-ShowFile 'C:\lab\p8\1a.psh' -Render"
#
# Вывод — одна строка JSON: секунды от запуска до имени файла в заголовке, до окна рендера, до диалога об
# окончании рендера и до выхода программы. Проект берётся без диалогов при загрузке; окна без кнопок
# («Please Wait») пропускаются, как и у наблюдателя.
param(
    [Parameter(Mandatory = $true)] [string] $ShowFile,
    [switch] $Render,
    [string] $Program = 'C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe',
    [int] $TitleTimeoutSec = 600,
    [int] $StepTimeoutSec = 120,
    [int] $RenderTimeoutSec = 3600,
    [int] $ExitTimeoutSec = 120,
    [int] $IntervalMs = 500
)
. "$PSScriptRoot\Win32.ps1"
. "$PSScriptRoot\Uia.ps1"
$ErrorActionPreference = 'Stop'

# Те же имена, что у наблюдателя в ProShowWindows.
$КомандаРендера = 9356
$ОкноВывода = 'Video for Web, Devices and Computers'
$ОкноСохранения = 'Save Video File'
$ОкноРендера = 'Rendering Video'
$КлассыДиалогов = @('#32770', 'AGDSDocParent')

function Главное-Окно($Process) {
    Get-ProcessWindows $Process.Id | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text } | Select-Object -First 1
}

# Диалог — видимое окно с владельцем, класса диалога и с кнопками: окно без кнопок диалогом не считается.
function Диалоги($Process) {
    foreach ($w in Get-ProcessWindows $Process.Id | Where-Object { $_.Owner -ne [IntPtr]::Zero -and $КлассыДиалогов -contains $_.Class }) {
        $buttons = @(Get-ChildControls $w.Hwnd | Where-Object Class -eq 'Button')
        if ($buttons) { [pscustomobject]@{ Window = $w; Buttons = $buttons } }
    }
}

# Ждёт окно с таким заголовком, а если названа кнопка — то окно, в котором эта кнопка уже есть: окно программа
# показывает раньше, чем создаёт в нём все кнопки (17.09.2026 прогон сорвался с no-button именно так).
function Ждать-Окно($Process, [string] $Title, [int] $TimeoutSec, [Diagnostics.Stopwatch] $Clock, [string] $Button) {
    $until = $Clock.Elapsed.TotalSeconds + $TimeoutSec
    while ($Clock.Elapsed.TotalSeconds -lt $until -and -not $Process.HasExited) {
        foreach ($окно in Диалоги $Process | Where-Object { $_.Window.Text -eq $Title }) {
            if (-not $Button) { return $окно }
            $кнопка = $окно.Buttons | Where-Object { $_.Text.Replace('&', '').Trim() -eq $Button } | Select-Object -First 1
            if ($кнопка) { return $окно }
        }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return $null
}

if (Get-Process -Name proshow -ErrorAction SilentlyContinue) { throw 'ProShow уже запущен' }
$name = [IO.Path]::GetFileName($ShowFile)
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $Program -ArgumentList "`"$ShowFile`"" -WorkingDirectory ([IO.Path]::GetDirectoryName($ShowFile)) -PassThru
# Хэндл берётся сразу: без него у вышедшего процесса не прочитать код выхода.
$null = $process.Handle

$итог = [ordered]@{ mode = 'script'; title_s = $null; render_start_s = $null; render_done_s = $null; exit_s = $null; code = $null; failed = $null }
$titleAt = $null
$window = [IntPtr]::Zero
while ($clock.Elapsed.TotalSeconds -lt $TitleTimeoutSec -and -not $process.HasExited) {
    $main = Главное-Окно $process
    if ($main -and $main.Text.Contains($name)) { $window = $main.Hwnd; $titleAt = $clock.Elapsed.TotalSeconds; break }
    Start-Sleep -Milliseconds $IntervalMs
}
if ($titleAt) { $итог.title_s = [math]::Round($titleAt, 2) } else { $итог.failed = 'no-title' }

if ($Render -and $titleAt) {
    [void][Lab.Win32]::PostMessage($window, 0x0111, [IntPtr]$КомандаРендера, [IntPtr]::Zero)   # WM_COMMAND
    $вывод = Ждать-Окно $process $ОкноВывода $StepTimeoutSec $clock 'Create'
    if (-not $вывод) { $итог.failed = 'no-output-window' }
    else {
        $create = $вывод.Buttons | Where-Object { $_.Text.Replace('&', '').Trim() -eq 'Create' } | Select-Object -First 1
        if (-not $create) { $итог.failed = 'no-button' }
        else {
            $null = Invoke-UiaButton -Button $create.Hwnd
            $сохранение = Ждать-Окно $process $ОкноСохранения $StepTimeoutSec $clock
            if (-not $сохранение) { $итог.failed = 'no-save-dialog' }
            else {
                # Кнопку согласия системного окна ищем по id: её текст переводится языком системы.
                $ok = [Lab.Win32]::GetDlgItem($сохранение.Window.Hwnd, 1)
                $null = Invoke-UiaButton -Button $ok
                $рендер = Ждать-Окно $process $ОкноРендера $StepTimeoutSec $clock
                if (-not $рендер) { $итог.failed = 'no-render-window' }
                else {
                    $итог.render_start_s = [math]::Round($clock.Elapsed.TotalSeconds, 2)
                    # Конец рендера — как у наблюдателя: любой диалог, кроме самого окна рендера. Порядок «окно
                    # исчезло, потом встал диалог» не гарантирован, на нём наблюдатель уже обжёгся.
                    $until = $clock.Elapsed.TotalSeconds + $RenderTimeoutSec
                    $готово = $null
                    while ($clock.Elapsed.TotalSeconds -lt $until -and -not $process.HasExited -and -not $готово) {
                        $готово = Диалоги $process | Where-Object { $_.Window.Hwnd -ne $рендер.Window.Hwnd } | Select-Object -First 1
                        if (-not $готово) { Start-Sleep -Milliseconds $IntervalMs }
                    }
                    if (-not $готово) { $итог.failed = 'render-timeout' }
                    else {
                        $итог.render_done_s = [math]::Round($clock.Elapsed.TotalSeconds, 2)
                        $кнопка = $готово.Buttons | Where-Object { $_.Text.Replace('&', '').Trim() -eq 'Ok' } | Select-Object -First 1
                        if ($кнопка) { $null = Invoke-UiaButton -Button $кнопка.Hwnd }
                    }
                }
            }
        }
    }
}

if ($titleAt -and -not $process.HasExited) {
    $main = Главное-Окно $process
    if ($main) { [void][Lab.Win32]::PostMessage($main.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }   # WM_CLOSE
}
if ($process.WaitForExit($ExitTimeoutSec * 1000)) {
    $итог.exit_s = [math]::Round($clock.Elapsed.TotalSeconds, 2)
    $итог.code = $process.ExitCode
}
[pscustomobject]$итог | ConvertTo-Json -Compress
if ($итог.failed -or $null -eq $итог.exit_s) { exit 1 }
