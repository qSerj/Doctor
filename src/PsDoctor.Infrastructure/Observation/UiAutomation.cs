using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Нажатие кнопки через COM-клиент UI Automation (UIA3): шаблон Invoke по хэндлу кнопки.
/// </summary>
/// <remarks>
/// Опыт Ш3 Э4.0, 17.09.2026, диалог не на переднем плане: <c>BM_CLICK</c> не закрыл «Old Show format detected.»,
/// <c>WM_COMMAND</c> не закрыл «Message», Invoke закрыл оба, курсор не сдвинулся. Поэтому один путь для обоих классов.
/// Интерфейсы объявлены до нужного метода: порядок слотов таблицы — как в UIAutomationClient.h, лишние — заглушки.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class UiAutomation
{
    public const string Invoked = "invoked";
    public const string TimedOut = "timeout";
    public const string NoInvokePattern = "no-invoke-pattern";

    private const int InvokePatternId = 10000;
    private static readonly Guid CuiAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    /// <summary>
    /// Нажимает кнопку. Вызов идёт в своём потоке MTA и ждётся не дольше <paramref name="timeout"/>: Invoke кнопки,
    /// за которой встаёт следующий модальный диалог, может не вернуться, и это не должно держать наблюдателя.
    /// </summary>
    /// <returns><see cref="Invoked"/> или имя сбоя: <see cref="TimedOut"/>, <see cref="NoInvokePattern"/>, тип исключения с кодом.</returns>
    public static string Invoke(IntPtr button, TimeSpan timeout)
    {
        var result = TimedOut;
        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromCLSID(CuiAutomation, throwOnError: true)!;
                var automation = (IUIAutomation)Activator.CreateInstance(type)!;
                var element = automation.ElementFromHandle(button);
                if (element.GetCurrentPattern(InvokePatternId) is not IUIAutomationInvokePattern pattern)
                {
                    result = NoInvokePattern;
                    return;
                }
                pattern.Invoke();
                result = Invoked;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
            {
                result = $"{exception.GetType().Name} 0x{exception.HResult:X8}";
            }
        })
        {
            IsBackground = true,
            Name = "psdoctor-uia",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(timeout);
        return result;
    }

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        [return: MarshalAs(UnmanagedType.Interface)]
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        void GetCurrentPropertyValue();

        void GetCurrentPropertyValueEx();

        void GetCachedPropertyValue();

        void GetCachedPropertyValueEx();

        void GetCurrentPatternAs();

        void GetCachedPatternAs();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);
    }

    [ComImport]
    [Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern
    {
        void Invoke();
    }
}
