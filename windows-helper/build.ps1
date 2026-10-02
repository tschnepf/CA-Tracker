<#
.SYNOPSIS
  Tests and publishes the SMC CA Tracker app, and optionally builds its installer.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1
  powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1 -SelfContained   # for PCs without .NET 10
  powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1 -Installer       # installer + latest.json (needs Inno Setup 6)
#>
param(
    [switch]$SelfContained,
    [switch]$SkipTests,
    [switch]$Installer,
    [string]$Output = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
# Everything shown is also written to build.log (next to this script), to send along when a build fails.
Start-Transcript -Path (Join-Path $PSScriptRoot 'build.log') -Force | Out-Null
try {
    if (-not $SkipTests) {
        dotnet test 'tests\SmcCaTracker.Core.Tests\SmcCaTracker.Core.Tests.csproj' -c Release | Out-Default
        if ($LASTEXITCODE -ne 0) { throw 'The app tests failed.' }
    }

    # The installer ships the .NET runtime with the app, so PCs don't need .NET installed.
    if ($Installer) { $SelfContained = $true }
    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

    # Earlier builds also produced the Chrome bridge; remove it so dist holds only the app.
    foreach ($old in @('SmcCaTracker.NativeHost.exe', 'SmcCaTracker.NativeHost.dll', 'SmcCaTracker.NativeHost.pdb',
            'SmcCaTracker.NativeHost.deps.json', 'SmcCaTracker.NativeHost.runtimeconfig.json')) {
        Remove-Item -LiteralPath (Join-Path $Output $old) -ErrorAction SilentlyContinue
    }

    dotnet publish 'src\SmcCaTracker.App\SmcCaTracker.App.csproj' -c Release -r win-x64 --self-contained $selfContainedValue -o $Output | Out-Default
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }

    Write-Host ''
    Write-Host "CA Tracker built into $Output"

    if ($Installer) {
        [xml]$props = Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props')
        $version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
        $iscc = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) { throw 'Inno Setup 6 is needed for -Installer: https://jrsoftware.org/isdl.php' }

        & $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer\SMC-CA-Tracker.iss') | Out-Default
        if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

        $installerName = "SMC-CA-Tracker-Setup-$version.exe"
        # The app refuses to run an installer whose SHA-256 differs from the one published here.
        $sha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $PSScriptRoot "installer\output\$installerName")).Hash.ToLowerInvariant()
        $latest = [ordered]@{ version = $version; installer = $installerName; sha256 = $sha256; notes = "SMC CA Tracker $version" }
        $latest | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'installer\output\latest.json')
        Write-Host "Installer: installer\output\$installerName (copy it and latest.json to the company's update location)"
    }
    else {
        Write-Host 'Run SmcCaTracker.exe. Connect to Helix or Procore in Settings -> Connections to start syncing.'
    }
}
catch {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    Stop-Transcript | Out-Null
    Pop-Location
}
