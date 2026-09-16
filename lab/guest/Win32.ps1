# Окна и кнопки программы через Win32, без UI Automation, фокуса и мыши.
# Подключается точкой: . "$PSScriptRoot\Win32.ps1"
#
# Почему не UI Automation: .NET-клиент в Windows PowerShell 5.1 видит кнопки ProShow голыми
# панелями без шаблона Invoke. Диалоги же программы — настоящие окна Win32 с кнопками класса Button,
# и нажатие доходит сообщением WM_COMMAND прямо диалогу — так же, как его шлёт сама кнопка при щелчке.
if (-not ('Lab.Win32' -as [type])) {
    Add-Type -Namespace Lab -Name Win32 -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumChildWindows(System.IntPtr parent, EnumProc cb, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowEnabled(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetDlgCtrlID(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetWindow(System.IntPtr hwnd, uint cmd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetParent(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr hwnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
'@
}

function Get-WindowInfo([IntPtr] $Hwnd) {
    $cls = New-Object Text.StringBuilder 256; [void][Lab.Win32]::GetClassName($Hwnd, $cls, 256)
    $txt = New-Object Text.StringBuilder 1024; [void][Lab.Win32]::GetWindowText($Hwnd, $txt, 1024)
    [pscustomobject]@{
        Hwnd    = $Hwnd
        Class   = $cls.ToString()
        Text    = $txt.ToString()
        Id      = [Lab.Win32]::GetDlgCtrlID($Hwnd)
        Owner   = [Lab.Win32]::GetWindow($Hwnd, 4)   # GW_OWNER
        Enabled = [Lab.Win32]::IsWindowEnabled($Hwnd)
    }
}

# Видимые окна верхнего уровня процесса: главное окно и его диалоги.
function Get-ProcessWindows([int] $ProcessId) {
    $found = New-Object System.Collections.Generic.List[IntPtr]
    [void][Lab.Win32]::EnumWindows({
            param($h, $l)
            $p = 0; [void][Lab.Win32]::GetWindowThreadProcessId($h, [ref]$p)
            if ($p -eq $ProcessId -and [Lab.Win32]::IsWindowVisible($h)) { $found.Add($h) }
            $true
        }, [IntPtr]::Zero)
    foreach ($h in $found) { Get-WindowInfo $h }
}

# Видимые дочерние окна на любой глубине.
function Get-ChildControls([IntPtr] $Hwnd) {
    $found = New-Object System.Collections.Generic.List[IntPtr]
    [void][Lab.Win32]::EnumChildWindows($Hwnd, { param($h, $l) if ([Lab.Win32]::IsWindowVisible($h)) { $found.Add($h) }; $true }, [IntPtr]::Zero)
    foreach ($h in $found) { Get-WindowInfo $h }
}

# Нажать кнопку: WM_COMMAND с BN_CLICKED родителю кнопки. Сообщение ставится в очередь, поэтому
# вызов не ждёт, пока программа его обработает, и не зависает на следующем модальном окне.
function Invoke-Button($Button) {
    $wParam = [IntPtr]($Button.Id -band 0xFFFF)   # старшее слово BN_CLICKED = 0
    [Lab.Win32]::PostMessage([Lab.Win32]::GetParent($Button.Hwnd), 0x0111, $wParam, $Button.Hwnd)
}

function Format-Window($W) {
    "hwnd=0x{0:X} класс={1} id={2} текст=«{3}»" -f $W.Hwnd.ToInt64(), $W.Class, $W.Id, ($W.Text -replace '\s+', ' ')
}
