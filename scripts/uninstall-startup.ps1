$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($name in @('JustWatch', 'JustGainsBuilder')) {
    Remove-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue
}
foreach ($name in @('JustWatch.lnk', 'JustGains Builder.lnk')) {
    $path = Join-Path ([Environment]::GetFolderPath('Startup')) $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
Write-Host 'JustWatch auto-start removed.'
