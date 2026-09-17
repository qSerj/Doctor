# Нажатие кнопки через COM-клиент UI Automation (UIA3) — то же, что делает действие press наблюдателя.
# Подключается точкой: . "$PSScriptRoot\Uia.ps1"
#
# Почему именно так: опыт Ш3 Э4.0 17.09.2026 на диалоге не на переднем плане — BM_CLICK не закрыл «Old Show
# format detected.», WM_COMMAND не закрыл «Message», Invoke закрыл оба, курсор не двигался.
# Интерфейсы объявлены до нужного метода: порядок слотов таблицы — как в UIAutomationClient.h, лишние — заглушки.
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
        public const string Invoked = "invoked";
        public const string TimedOut = "timeout";

        // Вызов идёт в своём потоке MTA и ждётся не дольше timeoutMs: Invoke кнопки, за которой встаёт следующее
        // модальное окно, может не вернуться, и это не должно держать вызывающего.
        public static string Invoke(IntPtr button, int timeoutMs) {
            string result = TimedOut;
            var thread = new Thread(() => {
                try {
                    var automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                    var pattern = automation.ElementFromHandle(button).GetCurrentPattern(10000) as IUIAutomationInvokePattern;
                    if (pattern == null) { result = "no-invoke-pattern"; return; }
                    pattern.Invoke();
                    result = Invoked;
                } catch (Exception e) { result = e.GetType().Name + " 0x" + e.HResult.ToString("X8"); }
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

# Нажать кнопку с повтором при сбое, как ProgramRun наблюдателя: только что вставшее окно отвечает на Invoke
# сбоем COM, а через четверть секунды нажимается. Не вернувшийся вызов не повторяется: он мог нажать.
function Invoke-UiaButton {
    param(
        [Parameter(Mandatory = $true)] [IntPtr] $Button,
        [int] $InvokeTimeoutMs = 5000,
        [int] $RetryWindowMs = 3000,
        [int] $RetryPauseMs = 250
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0
    while ($true) {
        $attempts++
        $result = [Lab.Uia]::Invoke($Button, $InvokeTimeoutMs)
        if ($result -in @([Lab.Uia]::Invoked, [Lab.Uia]::TimedOut) -or $clock.ElapsedMilliseconds -ge $RetryWindowMs) { break }
        Start-Sleep -Milliseconds $RetryPauseMs
        if (-not [Lab.Win32]::IsWindow($Button)) { break }
    }
    [pscustomobject]@{ result = $result; attempts = $attempts }
}
