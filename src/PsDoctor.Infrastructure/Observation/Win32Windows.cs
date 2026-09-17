using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Объявления Win32 для окон куста: перечисление, классы, тексты. Только то, что нужно наблюдателю.</summary>
[SupportedOSPlatform("windows")]
internal static class Win32Windows
{
    public const uint GwOwner = 4;
    public const uint WmClose = 0x0010;

    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, [Out] char[] buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, [Out] char[] buffer, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>
    /// Текст окна. <c>GetWindowText</c> у окна чужого процесса не шлёт ему сообщений, а читает текст, сохранённый
    /// системой, — поэтому не виснет на не отвечающей программе. Текст, нарисованный программой, так не читается.
    /// </summary>
    public static string Text(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return "";
        }
        var buffer = new char[length + 1];
        var copied = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(copied, 0));
    }

    public static List<IntPtr> TopLevelWindows()
    {
        var found = new List<IntPtr>();
        EnumProc callback = (hwnd, _) =>
        {
            found.Add(hwnd);
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    /// <summary>Видимые дочерние окна на любой глубине, в порядке перечисления.</summary>
    public static List<IntPtr> VisibleChildren(IntPtr parent)
    {
        var found = new List<IntPtr>();
        EnumProc callback = (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }
            return true;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    public static Point? Cursor() => GetCursorPos(out var point) ? point : null;
}
