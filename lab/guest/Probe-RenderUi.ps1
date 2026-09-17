# Разведка запуска рендера (действие render Э4.0): что видит UI Automation в окнах ProShow и есть ли у главного
# окна меню Win32. Ничего не нажимает. Выполняется в сеансе пользователя, пока владелец держит открытым нужное —
# меню, окно рендера:
#
#   Invoke-InSession.ps1 -Script Probe-RenderUi.ps1 -Arguments '-Depth 8'
#
# Вывод — текст с отступами: окна Win32 процесса, меню главного окна по позициям с id пунктов, затем дерево UIA
# (UIA3, сырое представление) каждого видимого окна верхнего уровня: тип, имя, класс, AutomationId, хэндл,
# прямоугольник и доступные шаблоны.
param(
    [int] $Depth = 8,
    [int] $MaxNodes = 3000,
    [string] $ProcessName = 'proshow',
    # Нажать пункт открытого всплывающего меню (#32768) через Invoke по номеру команды — вместо выгрузки.
    [string] $InvokeMenuItem,
    # Послать главному окну WM_COMMAND с номером команды, меню не открывая, — вместо выгрузки.
    [int] $Command,
    # Нажать кнопку с этим текстом в окне процесса с владельцем, затем выгрузить окна через 3 с.
    [string] $PressButton,
    # Вписать путь в поле имени файла стандартного окна сохранения (#32770, Edit с id 1001) — перед нажатием.
    [string] $SetFileName,
    # Ждать, пока исчезнет окно с этим заголовком, и печатать, что стало с окнами и файлом вывода.
    [string] $WaitWindowGone,
    [string] $OutputFile,
    [int] $WaitSec = 1800,
    # Закрыть главное окно WM_CLOSE — как действие close наблюдателя — и подождать выхода.
    [switch] $CloseMain
)
. "$PSScriptRoot\Win32.ps1"
$ErrorActionPreference = 'Stop'

if (-not ('Lab.Menu' -as [type])) {
    Add-Type -Namespace Lab -Name Menu -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetMenu(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr hwnd, uint msg, System.IntPtr wParam, string lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetMenuItemCount(System.IntPtr menu);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetSubMenu(System.IntPtr menu, int pos);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetMenuItemID(System.IntPtr menu, int pos);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetMenuState(System.IntPtr menu, uint id, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetMenuString(System.IntPtr menu, uint id, System.Text.StringBuilder s, int n, uint flags);
'@
}

if (-not ('Lab.UiaTree' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace Lab {
    // Слоты таблиц — как в UIAutomationClient.h; не нужные здесь методы — заглушки, чтобы не сбить порядок.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void get_ControlViewWalker(); void get_ContentViewWalker();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationTreeWalker get_RawViewWalker();
    }
    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTreeWalker {
        void GetParentElement();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement GetFirstChildElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement element);
        void GetLastChildElement();
        [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement GetNextSiblingElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement element);
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }
    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationInvokePattern { void Invoke(); }

    public static class UiaTree {
        static readonly int[] Patterns = { 30031, 30028, 30036, 30037, 30041, 30043, 30044, 30090 };
        static readonly string[] PatternNames = { "Invoke", "ExpandCollapse", "SelectionItem", "Selection", "Toggle", "Value", "Window", "LegacyIAccessible" };

        // Всё в своём потоке MTA и с пределом времени: не отвечающее окно не должно вешать зонд.
        public static string Dump(IntPtr hwnd, int depth, int maxNodes, int timeoutMs) {
            var text = new StringBuilder();
            var thread = new Thread(() => {
                try {
                    var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                    var walker = automation.get_RawViewWalker();
                    int nodes = 0;
                    Walk(walker, automation.ElementFromHandle(hwnd), 0, depth, maxNodes, ref nodes, text);
                    if (nodes >= maxNodes) text.AppendLine("… предел узлов " + maxNodes);
                } catch (Exception e) { text.AppendLine("сбой: " + e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!thread.Join(timeoutMs)) text.AppendLine("… таймаут " + timeoutMs + " мс");
            lock (text) return text.ToString();
        }

        // Нажать Invoke у потомка окна с данным AutomationId (у пунктов меню #32768 это номер команды).
        public static string InvokeById(IntPtr hwnd, string automationId, int timeoutMs) {
            string result = "timeout";
            var thread = new Thread(() => {
                try {
                    var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                    var walker = automation.get_RawViewWalker();
                    var child = walker.GetFirstChildElement(automation.ElementFromHandle(hwnd));
                    while (child != null && !(Value(child, 30011) is string s && s == automationId)) child = walker.GetNextSiblingElement(child);
                    if (child == null) { result = "no-item"; return; }
                    var pattern = child.GetCurrentPattern(10000) as IUIAutomationInvokePattern;
                    if (pattern == null) { result = "no-invoke-pattern"; return; }
                    pattern.Invoke();
                    result = "invoked";
                } catch (Exception e) { result = e.GetType().Name + " 0x" + e.HResult.ToString("X8"); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join(timeoutMs);
            return result;
        }

        // Нажать Invoke у кнопки по её хэндлу — как press наблюдателя.
        public static string InvokeHandle(IntPtr button, int timeoutMs) {
            string result = "timeout";
            var thread = new Thread(() => {
                try {
                    var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                    var pattern = automation.ElementFromHandle(button).GetCurrentPattern(10000) as IUIAutomationInvokePattern;
                    if (pattern == null) { result = "no-invoke-pattern"; return; }
                    pattern.Invoke();
                    result = "invoked";
                } catch (Exception e) { result = e.GetType().Name + " 0x" + e.HResult.ToString("X8"); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join(timeoutMs);
            return result;
        }

        static void Walk(IUIAutomationTreeWalker walker, IUIAutomationElement element, int level, int depth, int maxNodes, ref int nodes, StringBuilder text) {
            if (element == null || nodes >= maxNodes) return;
            nodes++;
            lock (text) text.Append(new string(' ', level * 2)).AppendLine(Describe(element));
            if (level >= depth) return;
            IUIAutomationElement child = null;
            try { child = walker.GetFirstChildElement(element); } catch (COMException) { }
            while (child != null && nodes < maxNodes) {
                Walk(walker, child, level + 1, depth, maxNodes, ref nodes, text);
                try { child = walker.GetNextSiblingElement(child); } catch (COMException) { child = null; }
            }
        }

        static string Describe(IUIAutomationElement e) {
            var patterns = new List<string>();
            for (int i = 0; i < Patterns.Length; i++) if (Value(e, Patterns[i]) is bool b && b) patterns.Add(PatternNames[i]);
            var rect = Value(e, 30001) as double[];
            return string.Format("{0} «{1}» класс={2} id={3} hwnd=0x{4:X} rect={5} [{6}]{7}",
                ControlType(Value(e, 30003)), Value(e, 30005), Value(e, 30012), Value(e, 30011),
                Convert.ToInt64(Value(e, 30020) ?? 0), rect == null ? "-" : string.Join(",", Array.ConvertAll(rect, r => ((int)r).ToString())),
                string.Join(",", patterns), Value(e, 30010) is bool enabled && !enabled ? " выключен" : "");
        }

        static object Value(IUIAutomationElement e, int id) {
            try { return e.GetCurrentPropertyValue(id); } catch (COMException) { return null; }
        }

        static string ControlType(object id) {
            switch (id is int n ? n : 0) {
                case 50000: return "Button"; case 50002: return "CheckBox"; case 50003: return "ComboBox"; case 50004: return "Edit";
                case 50005: return "Hyperlink"; case 50006: return "Image"; case 50007: return "ListItem"; case 50008: return "List";
                case 50009: return "Menu"; case 50010: return "MenuBar"; case 50011: return "MenuItem"; case 50013: return "RadioButton";
                case 50018: return "Tab"; case 50019: return "TabItem"; case 50020: return "Text"; case 50021: return "ToolBar";
                case 50025: return "Custom"; case 50026: return "Group"; case 50032: return "Window"; case 50033: return "Pane";
                case 50037: return "TitleBar"; default: return "тип " + id;
            }
        }
    }
}
'@
}

function Write-Menu([IntPtr] $Menu, [int] $Level) {
    $count = [Lab.Menu]::GetMenuItemCount($Menu)
    for ($i = 0; $i -lt $count; $i++) {
        $s = New-Object Text.StringBuilder 256
        [void][Lab.Menu]::GetMenuString($Menu, [uint32]$i, $s, 256, 0x400)   # MF_BYPOSITION
        $state = [Lab.Menu]::GetMenuState($Menu, [uint32]$i, 0x400)
        $sub = [Lab.Menu]::GetSubMenu($Menu, $i)
        $id = if ($sub -eq [IntPtr]::Zero) { [Lab.Menu]::GetMenuItemID($Menu, $i) } else { 'подменю' }
        '{0}[{1}] «{2}» id={3} state=0x{4:X}' -f ('  ' * $Level), $i, $s, $id, $state
        if ($sub -ne [IntPtr]::Zero -and $Level -lt 4) { Write-Menu $sub ($Level + 1) }
    }
}

if ($WaitWindowGone) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { "процесса $ProcessName нет"; exit 2 }
    $seen = $false
    while ($clock.Elapsed.TotalSeconds -lt $WaitSec -and -not $process.HasExited) {
        $windows = @(Get-ProcessWindows $process.Id)
        $target = $windows | Where-Object { $_.Text -eq $WaitWindowGone }
        if ($target) { $seen = $true }
        elseif ($seen) { break }
        Start-Sleep -Milliseconds 500
    }
    't={0:N1} c, окно «{1}» {2}' -f $clock.Elapsed.TotalSeconds, $WaitWindowGone, $(if ($seen) { 'исчезло' } else { 'не появлялось' })
    if (-not $process.HasExited) {
        foreach ($w in Get-ProcessWindows $process.Id) {
            'окно ' + (Format-Window $w) + (' владелец=0x{0:X}' -f $w.Owner.ToInt64())
            if ($w.Owner -ne [IntPtr]::Zero) { Get-ChildControls $w.Hwnd | ForEach-Object { '  ' + (Format-Window $_) } }
        }
    }
    else { "программа вышла, код $($process.ExitCode)" }
    if ($OutputFile -and (Test-Path $OutputFile)) { 'файл: {0} байт, записан {1:HH:mm:ss}' -f (Get-Item $OutputFile).Length, (Get-Item $OutputFile).LastWriteTime }
    exit 0
}

$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if (-not $processes) { "процесса $ProcessName нет"; exit 2 }
foreach ($process in $processes) {
    "== процесс $($process.Id)"
    if ($InvokeMenuItem) {
        $menu = Get-ProcessWindows $process.Id | Where-Object Class -eq '#32768' | Select-Object -First 1
        if (-not $menu) { 'открытого меню нет'; exit 3 }
        'Invoke пункта {0}: {1}' -f $InvokeMenuItem, [Lab.UiaTree]::InvokeById($menu.Hwnd, $InvokeMenuItem, 5000)
        Start-Sleep -Seconds 3
        Get-ProcessWindows $process.Id | ForEach-Object { 'окно ' + (Format-Window $_) + (' владелец=0x{0:X}' -f $_.Owner.ToInt64()) }
        continue
    }
    if ($CloseMain) {
        $main = Get-ProcessWindows $process.Id | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text } | Select-Object -First 1
        if (-not $main) { 'главного окна нет'; exit 3 }
        [void][Lab.Win32]::PostMessage($main.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
        $вышла = $process.WaitForExit(60000)
        'WM_CLOSE окну 0x{0:X}: вышла={1}' -f $main.Hwnd.ToInt64(), $вышла
        if (-not $вышла) { Get-ProcessWindows $process.Id | ForEach-Object { 'окно ' + (Format-Window $_) } }
        continue
    }
    if ($SetFileName) {
        $edit = Get-ProcessWindows $process.Id | Where-Object { $_.Class -eq '#32770' } |
            ForEach-Object { Get-ChildControls $_.Hwnd } | Where-Object { $_.Class -eq 'Edit' -and $_.Id -eq 1001 } | Select-Object -First 1
        if (-not $edit) { 'поля имени файла нет'; exit 3 }
        [void][Lab.Menu]::SendMessage($edit.Hwnd, 0x000C, [IntPtr]::Zero, $SetFileName)   # WM_SETTEXT
        'имя файла: ' + (Get-WindowInfo $edit.Hwnd).Text
    }
    if ($PressButton) {
        $button = Get-ProcessWindows $process.Id | Where-Object { $_.Owner -ne [IntPtr]::Zero } |
            ForEach-Object { Get-ChildControls $_.Hwnd } | Where-Object { $_.Class -eq 'Button' -and $_.Text.Replace('&', '').Trim() -eq $PressButton } | Select-Object -First 1
        if (-not $button) { "кнопки «$PressButton» нет"; exit 3 }
        'Invoke кнопки {0}: {1}' -f (Format-Window $button), [Lab.UiaTree]::InvokeHandle($button.Hwnd, 5000)
        Start-Sleep -Seconds 3
        $PressButton = $null
    }
    if ($Command) {
        $main = Get-ProcessWindows $process.Id | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text } | Select-Object -First 1
        'WM_COMMAND {0} окну 0x{1:X}: {2}' -f $Command, $main.Hwnd.ToInt64(), [Lab.Win32]::PostMessage($main.Hwnd, 0x0111, [IntPtr]$Command, [IntPtr]::Zero)
        Start-Sleep -Seconds 3
        Get-ProcessWindows $process.Id | ForEach-Object { 'окно ' + (Format-Window $_) + (' владелец=0x{0:X}' -f $_.Owner.ToInt64()) }
        continue
    }
    $windows = @(Get-ProcessWindows $process.Id)
    foreach ($w in $windows) { 'окно ' + (Format-Window $w) + (' владелец=0x{0:X}' -f $w.Owner.ToInt64()) }

    foreach ($main in $windows | Where-Object { $_.Owner -eq [IntPtr]::Zero -and $_.Text }) {
        $menu = [Lab.Menu]::GetMenu($main.Hwnd)
        "`n== меню Win32 окна 0x{0:X}: {1}" -f $main.Hwnd.ToInt64(), $(if ($menu -eq [IntPtr]::Zero) { 'нет' } else { "0x{0:X}" -f $menu.ToInt64() })
        if ($menu -ne [IntPtr]::Zero) { Write-Menu $menu 0 }
    }

    foreach ($w in $windows) {
        "`n== UIA окна " + (Format-Window $w)
        [Lab.UiaTree]::Dump($w.Hwnd, $Depth, $MaxNodes, 60000)
    }
}
