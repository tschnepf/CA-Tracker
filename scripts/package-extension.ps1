$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw (Join-Path $root 'manifest.json') | ConvertFrom-Json
$dist = Join-Path $root 'dist'
$install = Join-Path $dist 'helix-my-activity'
$versionedArchive = Join-Path $dist ("helix-my-activity-{0}.zip" -f $manifest.version)
$currentArchive = Join-Path $dist 'helix-my-activity.zip'
$distFull = [IO.Path]::GetFullPath($dist).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$installFull = [IO.Path]::GetFullPath($install)

if (-not $installFull.StartsWith($distFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The unpacked install folder must remain inside the workspace dist directory."
}

if (Test-Path -LiteralPath $install) {
    Remove-Item -LiteralPath $install -Recurse -Force
}
New-Item -ItemType Directory -Path $install -Force | Out-Null

$files = @(
    'manifest.json',
    'background.js',
    'bridge-core.js',
    'page-bridge.js',
    'activity-core.js',
    'ui.js',
    'content-script.js',
    'README.md',
    'PRIVACY.md'
)

foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $install $file)
}

Compress-Archive -Path (Join-Path $install '*') -DestinationPath $versionedArchive -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $versionedArchive -Destination $currentArchive -Force

$hash = Get-FileHash -LiteralPath $versionedArchive -Algorithm SHA256
[pscustomobject]@{
    InstallFolder = $install
    CurrentArchive = $currentArchive
    VersionedArchive = $versionedArchive
    Version = $manifest.version
    Sha256 = $hash.Hash
} | Format-List
