# JustWatch uninstaller
#
#   irm https://raw.githubusercontent.com/JustSuperHuman/JustWatch/main/uninstall.ps1 | iex
#
# Removes the app, shortcuts and sign-in entry. Your configuration in
# %APPDATA%\JustWatch is kept; set JUSTWATCH_REMOVE_CONFIG=1 to delete it too.

# Wrapped in a script block so `irm | iex` leaves the caller's session untouched.
& {
    $ErrorActionPreference = 'Stop'
    $installDir = if ($env:JUSTWATCH_INSTALL_DIR) { $env:JUSTWATCH_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\JustWatch' }
    $appDir = Join-Path $installDir 'app'

    Write-Host ''
    Write-Host '  Uninstalling JustWatch' -ForegroundColor White

    Get-Process JustWatch -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Seconds 1

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $run = (Get-ItemProperty $runKey -Name JustWatch -ErrorAction SilentlyContinue).JustWatch
    if ($run -and $run.Contains($appDir)) { Remove-ItemProperty $runKey -Name JustWatch }

    Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'JustWatch.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\JustWatch' -Recurse -Force -ErrorAction SilentlyContinue

    # This script may live inside the install folder, so delete it from a separate process.
    if (Test-Path $installDir) {
        Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-Command', "Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$installDir' -Recurse -Force")
    }

    if ($env:JUSTWATCH_REMOVE_CONFIG -eq '1') {
        Remove-Item (Join-Path $env:APPDATA 'JustWatch') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '  Removed configuration.' -ForegroundColor DarkGray
    } else {
        Write-Host "  Kept your configuration in $env:APPDATA\JustWatch" -ForegroundColor DarkGray
    }

    Write-Host '  JustWatch uninstalled.' -ForegroundColor Green
    Write-Host ''
}
