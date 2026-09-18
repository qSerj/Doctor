# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Обёртка задачи планировщика lab-ui в гостевой системе стенда. Сюда не пишется ничего опытного:
# она берёт C:\lab\ui\job.ps1 с параметрами из job.json, собирает весь вывод в out.txt
# и последней пишет код завершения в exit.txt — по его появлению хост понимает, что работа кончилась.
$dir = 'C:\lab\ui'
$out = Join-Path $dir 'out.txt'
$code = 0
try {
    $params = @{}
    $json = Get-Content (Join-Path $dir 'job.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json) { $json.PSObject.Properties | ForEach-Object { $params[$_.Name] = $_.Value } }
    & (Join-Path $dir 'job.ps1') @params *>&1 | Out-File $out -Encoding utf8 -Width 400
    if ($LASTEXITCODE) { $code = $LASTEXITCODE }
}
catch {
    $_ | Out-String | Out-File $out -Encoding utf8 -Append
    $code = 1
}
Set-Content (Join-Path $dir 'exit.txt') $code
