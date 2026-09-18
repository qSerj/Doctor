# Зонд опыта. Не часть продукта, см. lab/README.md.
# Опыт Ш3 Э4.0: чем нажимается кнопка диалога ProShow, когда диалог НЕ на переднем плане,
# и гасит ли ключ -nowarnings оба диалога. Мышь и клавиатура не трогаются; курсор только читается.
# Выполняется в сеансе пользователя: Invoke-InSession.ps1 -Script Probe-BackgroundClick.ps1
#
#   -Mode click -Method click|command|uia — дождаться диалога, вывести вперёд свежий Блокнот, нажать кнопку:
#       click   — BM_CLICK кнопке (17.09.2026: в неактивном #32770 не сработал);
#       command — WM_COMMAND с BN_CLICKED родителю кнопки, хэндл кнопки в lParam;
#       uia     — Invoke COM-клиента UI Automation (UIA3) по хэндлу кнопки
#   -Mode nowarnings — запуск с -nowarnings, только смотреть, какие окна появятся
#
# Строка JSON на событие. Проект перед каждым прогоном заново берётся из папки обмена:
# программа переписывает .pxc при закрытии.
param(
    [ValidateSet('click', 'nowarnings')] [string] $Mode = 'click',
    [ValidateSet('click', 'command', 'uia')] [string] $Method = 'click',
    [string] $Source = '\\VBoxSvr\exchange\projects\p1',
    [string] $Work = 'C:\lab\p1',
    # Имя файла шоу не записывается здесь: имена проектов в открытый репозиторий не попадают.
    [Parameter(Mandatory = $true)] [string] $ShowFile,
    [string] $Program = 'C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe',
    [string[]] $Presses = @('^(OK|ОК)$', '^Ok$'),
    [int] $DialogTimeoutSec = 120,
    [int] $TitleTimeoutSec = 180,
    [int] $ExitTimeoutSec = 60
)
. "$PSScriptRoot\Win32.ps1"
$ErrorActionPreference = 'Stop'
if (-not ('Lab.Probe' -as [type])) {
    Add-Type -Namespace Lab -Name Probe -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindow(System.IntPtr hwnd);
'@
}
if (-not ('Lab.Uia' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace Lab {
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache();
        void BuildUpdatedCache(); void GetCurrentPropertyValue(); void GetCurrentPropertyValueEx(); void GetCachedPropertyValue();
        void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }
    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationInvokePattern { void Invoke(); }

    public static class Uia {
        // Всё — в своём потоке MTA: Invoke у кнопки, открывающей следующий модальный диалог, может не вернуться.
        public static string Invoke(IntPtr button, int timeoutMs) {
            string result = "timeout";
            var thread = new Thread(() => {
                try {
                    var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                    var element = automation.ElementFromHandle(button);
                    var pattern = element.GetCurrentPattern(10000) as IUIAutomationInvokePattern;
                    if (pattern == null) { result = "no-invoke-pattern"; return; }
                    pattern.Invoke();
                    result = "invoked";
                } catch (Exception e) { result = e.GetType().Name + ": " + e.Message; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join(timeoutMs);
            return result;
        }
    }
}
'@
}

$clock = [Diagnostics.Stopwatch]::StartNew()
function Emit([hashtable] $Data) {
    $Data.t = [math]::Round($clock.Elapsed.TotalSeconds, 2)
    [pscustomobject]$Data | ConvertTo-Json -Compress -Depth 5
}
function Get-Cursor { $p = New-Object Lab.Win32+POINT; [void][Lab.Win32]::GetCursorPos([ref]$p); "$($p.X),$($p.Y)" }
function Describe-Foreground {
    $h = [Lab.Win32]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { return @{ hwnd = 0 } }
    $w = Get-WindowInfo $h
    $p = 0; [void][Lab.Win32]::GetWindowThreadProcessId($h, [ref]$p)
    $name = (Get-Process -Id $p -ErrorAction SilentlyContinue).ProcessName
    @{ hwnd = $h.ToInt64(); class = $w.Class; text = $w.Text; process = $name }
}
function Get-Dialogs([int] $ProcessId) {
    @(Get-ProcessWindows $ProcessId | Where-Object { $_.Owner -ne [IntPtr]::Zero -and @('#32770', 'AGDSDocParent') -contains $_.Class })
}
function Describe-Dialog($Dialog) {
    $children = @(Get-ChildControls $Dialog.Hwnd)
    @{
        hwnd    = $Dialog.Hwnd.ToInt64()
        class   = $Dialog.Class
        title   = $Dialog.Text
        texts   = @($children | Where-Object Class -eq 'Static' | ForEach-Object Text | Where-Object { $_ })
        buttons = @($children | Where-Object Class -eq 'Button' | ForEach-Object { @{ text = $_.Text; id = $_.Id } })
    }
}

Get-Process proshow -ErrorAction SilentlyContinue | ForEach-Object { Emit @{ event = 'refused'; reason = 'program-running'; pid = $_.Id }; exit 2 }

robocopy $Source $Work /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy проекта: код $LASTEXITCODE" }
$show = Join-Path $Work $ShowFile

$arguments = if ($Mode -eq 'nowarnings') { "-nowarnings `"$show`"" } else { "`"$show`"" }
$proc = Start-Process $Program -ArgumentList $arguments -WorkingDirectory $Work -PassThru
Emit @{ event = 'launched'; pid = $proc.Id; arguments = $arguments; cursor = (Get-Cursor); foreground = (Describe-Foreground) }

$seen = @{}
$pressIndex = 0
$title = $null
$notepad = $null
$deadline = $TitleTimeoutSec
while ($clock.Elapsed.TotalSeconds -lt $deadline -and -not $proc.HasExited) {
    foreach ($d in Get-Dialogs $proc.Id) {
        $key = $d.Hwnd.ToInt64()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        Emit @{ event = 'dialog'; dialog = (Describe-Dialog $d); foreground = (Describe-Foreground) }
        if ($Mode -ne 'click') { continue }

        if ($pressIndex -ge $Presses.Count) { Emit @{ event = 'stop'; reason = 'no-press-left' }; $deadline = 0; break }
        $pattern = $Presses[$pressIndex++]
        $button = Get-ChildControls $d.Hwnd | Where-Object { $_.Class -eq 'Button' -and $_.Text -match $pattern } | Select-Object -First 1
        if (-not $button) { Emit @{ event = 'stop'; reason = 'no-button'; pattern = $pattern }; $deadline = 0; break }

        # Увести диалог с переднего плана: каждый раз свежий Блокнот — прежний второй раз вперёд не выходит.
        if ($notepad -and -not $notepad.HasExited) { Stop-Process -Id $notepad.Id -Force }
        $notepad = Start-Process notepad.exe -PassThru; [void]$notepad.WaitForInputIdle(10000)
        $pushed = [Lab.Probe]::SetForegroundWindow($notepad.MainWindowHandle)
        Start-Sleep -Milliseconds 300
        $foreground = Describe-Foreground
        $cursor = Get-Cursor
        $posted = switch ($Method) {
            'click' { Invoke-ButtonClick $button }
            'command' { Invoke-Button $button }
            'uia' { [Lab.Uia]::Invoke($button.Hwnd, 5000) }
        }
        $clickClock = [Diagnostics.Stopwatch]::StartNew()
        while ([Lab.Probe]::IsWindow($d.Hwnd) -and $clickClock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
        Emit @{
            event               = 'press'
            method              = $Method
            button              = $button.Text
            dialog_foreground   = ($foreground.hwnd -eq $key)
            notepad_pushed      = $pushed
            foreground          = $foreground
            posted              = $posted
            closed              = -not [Lab.Probe]::IsWindow($d.Hwnd)
            closed_after_sec    = [math]::Round($clickClock.Elapsed.TotalSeconds, 2)
            cursor_moved        = ($cursor -ne (Get-Cursor))
        }
        if ([Lab.Probe]::IsWindow($d.Hwnd)) { Emit @{ event = 'stop'; reason = 'not-closed' }; $deadline = 0; break }
    }
    if ($deadline -eq 0) { break }

    $main = Get-ProcessWindows $proc.Id | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text } | Select-Object -First 1
    if ($main -and $main.Text -ne $title) {
        $title = $main.Text
        Emit @{ event = 'title'; title = $title }
    }
    # После заголовка с файлом подождать ещё немного: не встанет ли диалог позже.
    if ($title -and $title.Contains($ShowFile) -and $deadline -eq $TitleTimeoutSec) { $deadline = $clock.Elapsed.TotalSeconds + 15 }
    Start-Sleep -Milliseconds 250
}

if (-not $proc.HasExited) {
    $main = Get-ProcessWindows $proc.Id | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text } | Select-Object -First 1
    $open = @(Get-Dialogs $proc.Id)
    if ($main -and -not $open) {
        [void][Lab.Win32]::PostMessage($main.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
        $exited = $proc.WaitForExit($ExitTimeoutSec * 1000)
        Emit @{ event = 'close'; exited = $exited; dialogs_after = @(Get-Dialogs $proc.Id | ForEach-Object { Describe-Dialog $_ }) }
    }
    else {
        Emit @{ event = 'left-open'; dialogs = @($open | ForEach-Object { Describe-Dialog $_ }) }
    }
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Emit @{ event = 'killed' } }
}
else {
    Emit @{ event = 'exited-by-itself'; code = $proc.ExitCode }
}
if ($notepad -and -not $notepad.HasExited) { Stop-Process -Id $notepad.Id -Force }
Emit @{ event = 'done'; cursor = (Get-Cursor) }
