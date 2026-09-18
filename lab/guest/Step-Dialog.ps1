# Зонд опыта. Не часть продукта, см. lab/README.md.
# Разведка одного шага: нажать кнопку в диалоге процесса и описать, что появилось следом.
param(
    [string] $ProcessName = 'proshow',
    [string] $Dialog = 'Old Show format detected.',
    [string] $Button = '^(OK|ОК)$',
    [ValidateSet('command', 'click')] [string] $Method = 'command',
    [int] $WaitSec = 60
)
. "$PSScriptRoot\Win32.ps1"
$proc = Get-Process $ProcessName -ErrorAction Stop | Select-Object -First 1

$dlg = Get-ProcessWindows $proc.Id | Where-Object Text -eq $Dialog | Select-Object -First 1
if (-not $dlg) { "диалога «$Dialog» нет; окна процесса:"; Get-ProcessWindows $proc.Id | ForEach-Object { '  ' + (Format-Window $_) }; exit 2 }
$btn = Get-ChildControls $dlg.Hwnd | Where-Object { $_.Class -eq 'Button' -and $_.Text -match $Button } | Select-Object -First 1
if (-not $btn) { "кнопки $Button в диалоге нет; его окна:"; Get-ChildControls $dlg.Hwnd | ForEach-Object { '  ' + (Format-Window $_) }; exit 2 }

"диалог: " + (Format-Window $dlg)
"кнопка: " + (Format-Window $btn)
"передний план: " + (Format-Window (Get-WindowInfo ([Lab.Win32]::GetForegroundWindow())))
if ($Method -eq 'click') { "BM_CLICK отправлен: " + (Invoke-ButtonClick $btn) }
else { "WM_COMMAND отправлен: " + (Invoke-Button $btn) }

$before = @($dlg.Hwnd)
$clock = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Milliseconds 200
    $windows = @(Get-ProcessWindows $proc.Id)
    $gone = -not ($windows | Where-Object { $_.Hwnd -eq $dlg.Hwnd })
    $new = @($windows | Where-Object { $_.Owner -ne [IntPtr]::Zero -and $before -notcontains $_.Hwnd })
} while (-not $new -and $clock.Elapsed.TotalSeconds -lt $WaitSec -and -not $proc.HasExited)

"диалог закрылся: $gone, через {0:N1} с" -f $clock.Elapsed.TotalSeconds
"окна процесса:"
foreach ($w in $windows) {
    '  ' + (Format-Window $w) + " владелец=0x{0:X}" -f $w.Owner.ToInt64()
    if ($w.Owner -ne [IntPtr]::Zero) { Get-ChildControls $w.Hwnd | ForEach-Object { '    ' + (Format-Window $_) } }
}
