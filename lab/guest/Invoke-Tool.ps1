# Выполнить по очереди команды стороннего инструмента в сеансе пользователя и показать, что он
# сделал: вывод, код завершения, положение курсора и окно на переднем плане до и после.
# Нужен для проверки «рук»: нажатие по смыслу не должно двигать мышь.
param(
    [string] $Exe = 'C:\lab\tools\uiamcp\uiamcp.exe',
    [object[]] $Commands = @(),      # список команд, каждая — массив аргументов
    [int] $MaxLines = 40,
    [switch] $Launch                 # оконная программа: запустить и не ждать её завершения
)
. "$PSScriptRoot\Win32.ps1"

function Get-State {
    $p = New-Object Lab.Win32+POINT
    [void][Lab.Win32]::GetCursorPos([ref]$p)
    "курсор ({0},{1}), передний план: {2}" -f $p.X, $p.Y, (Format-Window (Get-WindowInfo ([Lab.Win32]::GetForegroundWindow())))
}

foreach ($cmd in $Commands) {
    if ($Launch) {
        # Вывод оконной программы в конвейер не перенаправлять: PowerShell тогда ждёт её завершения.
        $argv = @($cmd | ForEach-Object { '"{0}"' -f $_ })
        "до: " + (Get-State)
        $proc = Start-Process $Exe -ArgumentList $argv -PassThru
        "запущен pid $($proc.Id): $Exe $($argv -join ' ')"
        continue
    }
    $argv = @($cmd | ForEach-Object { [string]$_ })
    "=== " + ($argv -join ' ')
    "  до:    " + (Get-State)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Exe @argv 2>&1 | ForEach-Object { "$_" }
    "  код {0}, {1:N2} с" -f $LASTEXITCODE, $clock.Elapsed.TotalSeconds
    "  после: " + (Get-State)
    $out | Select-Object -First $MaxLines | ForEach-Object { "  | $_" }
    if (@($out).Count -gt $MaxLines) { "  | … ещё {0} строк" -f (@($out).Count - $MaxLines) }
}
