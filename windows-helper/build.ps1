<#
.SYNOPSIS
  Tests and publishes the SMC CA Tracker Windows helper.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1
  powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1 -SelfContained   # for PCs without .NET 10
#>
param(
    [switch]$SelfContained,
    [switch]$SkipTests,
    [string]$Output = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        dotnet test 'tests\SmcCaTracker.Core.Tests\SmcCaTracker.Core.Tests.csproj' -c Release
        if ($LASTEXITCODE -ne 0) { throw 'The helper tests failed.' }
    }

    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
    foreach ($project in @(
            'src\SmcCaTracker.App\SmcCaTracker.App.csproj',
            'src\SmcCaTracker.NativeHost\SmcCaTracker.NativeHost.csproj')) {
        dotnet publish $project -c Release -r win-x64 --self-contained $selfContainedValue -o $Output
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }

    Write-Host ''
    Write-Host "CA Tracker built into $Output"
    Write-Host 'Next: run SmcCaTracker.exe, open Settings, and select Register with Chrome.'
}
finally {
    Pop-Location
}
