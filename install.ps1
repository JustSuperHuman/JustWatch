# JustWatch installer
#
#   irm https://raw.githubusercontent.com/JustSuperHuman/JustWatch/main/install.ps1 | iex
#
# Downloads the latest release, installs it for the current user (no admin),
# adds Start Menu + sign-in shortcuts and starts JustWatch. Run it again to
# upgrade. Optional environment variables:
#   JUSTWATCH_INSTALL_DIR   install location (default %LOCALAPPDATA%\Programs\JustWatch)
#   JUSTWATCH_VERSION       release tag to install, e.g. v1.0.0 (default: latest)
#   JUSTWATCH_NO_STARTUP=1  don't start JustWatch when you sign in
#   JUSTWATCH_NO_LAUNCH=1   don't start JustWatch after installing

# Wrapped in a script block so `irm | iex` leaves the caller's session untouched.
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $repo = 'JustSuperHuman/JustWatch'
    $asset = 'JustWatch-win-x64.zip'
    $installDir = if ($env:JUSTWATCH_INSTALL_DIR) { $env:JUSTWATCH_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\JustWatch' }
    $appDir = Join-Path $installDir 'app'

    function Step($text) { Write-Host "  > $text" -ForegroundColor Cyan }

    Write-Host ''
    Write-Host '  JustWatch installer' -ForegroundColor White
    Write-Host ''

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'JustWatch requires 64-bit Windows 10 or 11.' }

    # 1. Find the release
    $release = if ($env:JUSTWATCH_VERSION) { "tags/$($env:JUSTWATCH_VERSION)" } else { 'latest' }
    Step "Finding JustWatch release ($release)"
    $info = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/$release" -Headers @{ 'User-Agent' = 'JustWatch-Installer' }
    $download = $info.assets | Where-Object name -eq $asset | Select-Object -First 1
    if (-not $download) { throw "Release $($info.tag_name) has no $asset." }

    # 2. Download and unpack to a temp folder
    $temp = Join-Path ([IO.Path]::GetTempPath()) "JustWatch-install-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp $asset
        Step "Downloading $($info.tag_name) ($([math]::Round($download.size / 1MB)) MB)"
        Invoke-WebRequest $download.browser_download_url -OutFile $zip -UseBasicParsing
        Expand-Archive $zip -DestinationPath (Join-Path $temp 'app') -Force
        if (-not (Test-Path (Join-Path $temp 'app\JustWatch.exe'))) { throw 'The download did not contain JustWatch.exe.' }

        # 3. Stop a running copy so files can be replaced
        $running = Get-Process JustWatch -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) }
        if ($running) {
            Step 'Stopping the running JustWatch'
            $running | Stop-Process -Force
            Start-Sleep -Seconds 2
        }

        # 4. Install
        Step "Installing to $appDir"
        New-Item -ItemType Directory -Path $appDir -Force | Out-Null
        robocopy (Join-Path $temp 'app') $appDir /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit $LASTEXITCODE)." }
        $global:LASTEXITCODE = 0
    } finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    $exe = Join-Path $appDir 'JustWatch.exe'
    $uninstaller = Join-Path $installDir 'uninstall.ps1'
    Invoke-WebRequest "https://raw.githubusercontent.com/$repo/main/uninstall.ps1" -OutFile $uninstaller -UseBasicParsing

    # 5. Shortcuts, sign-in start and Apps & features entry
    Step 'Adding Start Menu shortcut'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'JustWatch.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $appDir
    $lnk.Description = 'Keep your local dev servers running from the tray'
    $lnk.Save()

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ($env:JUSTWATCH_NO_STARTUP -ne '1') {
        Step 'Starting JustWatch when you sign in'
        Set-ItemProperty $runKey -Name 'JustWatch' -Value "`"$exe`" --minimized"
    }

    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\JustWatch'
    New-Item $uninstallKey -Force | Out-Null
    $props = @{
        DisplayName     = 'JustWatch'
        DisplayVersion  = $info.tag_name.TrimStart('v')
        Publisher       = 'JustSuperHuman'
        DisplayIcon     = $exe
        InstallLocation = $installDir
        URLInfoAbout    = "https://github.com/$repo"
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""
    }
    foreach ($p in $props.GetEnumerator()) { Set-ItemProperty $uninstallKey -Name $p.Key -Value $p.Value }
    Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

    # 6. Launch
    if ($env:JUSTWATCH_NO_LAUNCH -ne '1') {
        Step 'Starting JustWatch'
        Start-Process $exe -WorkingDirectory $appDir
    }

    Write-Host ''
    Write-Host "  JustWatch $($info.tag_name) installed. Look for the eye icon in your system tray." -ForegroundColor Green
    Write-Host "  Settings: http://localhost:6767/services" -ForegroundColor DarkGray
    Write-Host "  Config:   $env:APPDATA\JustWatch\justwatch-config.json" -ForegroundColor DarkGray
    Write-Host ''
}
