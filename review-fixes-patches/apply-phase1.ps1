# Applies the Phase 1 review-fix commits to a review-fixes branch and runs the build.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File C:\Source\SMC-CA-Tracker\review-fixes-patches\apply-phase1.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

if (git status --porcelain --untracked-files=no) {
    throw 'Commit or stash your local changes first (git status shows modified files).'
}
if (-not (git branch --list review-fixes)) { git checkout -b review-fixes } else { git checkout review-fixes }
git am --whitespace=nowarn (Get-ChildItem $PSScriptRoot -Filter '000*.patch' | Sort-Object Name | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'git am failed; run "git am --abort" and send me the output.' }
git log --oneline -6

Write-Host ''
Write-Host 'Building (close CA Tracker first)...'
powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'windows-helper\build.ps1')
