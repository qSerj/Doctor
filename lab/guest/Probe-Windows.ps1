# Зонд опыта. Не часть продукта, см. lab/README.md.
# Разведка: что видно в окнах процесса двумя путями — через Win32 (EnumWindows и дочерние окна)
# и через UI Automation (сырой обход дерева от каждого окна). Выполняется в сеансе пользователя.
param([string] $ProcessName = 'proshow')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace Lab -Name Win32 -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumChildWindows(System.IntPtr parent, EnumProc cb, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetDlgCtrlID(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetWindow(System.IntPtr hwnd, uint cmd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
'@

function Describe([IntPtr] $h) {
    $cls = New-Object Text.StringBuilder 256; [void][Lab.Win32]::GetClassName($h, $cls, 256)
    $txt = New-Object Text.StringBuilder 512; [void][Lab.Win32]::GetWindowText($h, $txt, 512)
    "hwnd=0x{0:X} класс={1} текст=«{2}» id={3} видно={4}" -f $h.ToInt64(), $cls, $txt, [Lab.Win32]::GetDlgCtrlID($h), [Lab.Win32]::IsWindowVisible($h)
}

$ae = [System.Windows.Automation.AutomationElement]
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
function Walk($el, [int] $depth) {
    if ($depth -gt 4) { return }
    $child = $walker.GetFirstChild($el)
    while ($child) {
        $c = $child.Current
        $patterns = ($child.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern', '' }) -join ','
        ('  ' * ($depth + 2)) + "uia: $($c.ControlType.ProgrammaticName -replace 'ControlType.', '') «$($c.Name)» класс=$($c.ClassName) id=$($c.AutomationId) hwnd=0x{0:X} [$patterns]" -f $c.NativeWindowHandle
        Walk $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

"передний план: " + (Describe ([Lab.Win32]::GetForegroundWindow()))
foreach ($proc in Get-Process $ProcessName -ErrorAction SilentlyContinue) {
    "процесс $($proc.Id), сеанс $($proc.SessionId)"
    $tops = New-Object System.Collections.Generic.List[IntPtr]
    [void][Lab.Win32]::EnumWindows({ param($h, $l) $p = 0; [void][Lab.Win32]::GetWindowThreadProcessId($h, [ref]$p); if ($p -eq $proc.Id) { $tops.Add($h) }; $true }, [IntPtr]::Zero)
    foreach ($h in $tops) {
        if (-not [Lab.Win32]::IsWindowVisible($h)) { continue }
        "  окно " + (Describe $h) + " владелец=0x{0:X}" -f [Lab.Win32]::GetWindow($h, 4).ToInt64()
        [void][Lab.Win32]::EnumChildWindows($h, { param($c, $l) if ([Lab.Win32]::IsWindowVisible($c)) { "    дочернее " + (Describe $c) | Write-Output }; $true }, [IntPtr]::Zero)
        try { Walk ($ae::FromHandle($h)) 0 } catch { "    uia: ошибка $($_.Exception.Message)" }
    }
}
