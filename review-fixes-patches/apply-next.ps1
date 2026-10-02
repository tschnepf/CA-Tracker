# Applies any patch in this folder that isn't already on the current branch, then builds.
# powershell -ExecutionPolicy Bypass -File C:\Source\SMC-CA-Tracker\review-fixes-patches\apply-next.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (git status --porcelain --untracked-files=no) { throw 'Commit or stash your local changes first.' }
if ((git branch --show-current) -ne 'review-fixes') { git checkout review-fixes }

$applied = git log --format=%s
$pending = Get-ChildItem $PSScriptRoot -Filter '0*.patch' | Sort-Object Name | Where-Object {
    $subject = (Select-String -Path $_.FullName -Pattern '^Subject: (\[PATCH[^\]]*\] )?(.*)$' | Select-Object -First 1).Matches[0].Groups[2].Value
    $applied -notcontains $subject
}
if ($pending) {
    git am --whitespace=nowarn ($pending | ForEach-Object FullName)
    if ($LASTEXITCODE -ne 0) { throw 'git am failed; run "git am --abort" and send me the output.' }
} else { Write-Host 'Nothing new to apply.' }
git log --oneline -8

Write-Host ''
Write-Host 'Building (close CA Tracker first)...'
powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'windows-helper\build.ps1')
