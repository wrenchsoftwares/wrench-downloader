param(
    [Parameter(Position=0)]
    [string]$Message
)

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $PSScriptRoot

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "         Wrench Downloader - Push to Main (PS)          " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$status = git status --porcelain
if (-not $status) {
    Write-Host "No unstaged/uncommitted changes detected." -ForegroundColor Yellow
    Write-Host "Checking for unpushed commits..." -ForegroundColor Gray
    git push origin main
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = Read-Host "Enter commit message (press Enter for auto-generated timestamp)"
}

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "Update $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
}

Write-Host "`nStaging all changes..." -ForegroundColor Green
git add -A

Write-Host "Committing with message: '$Message'..." -ForegroundColor Green
git commit -m "$Message"

Write-Host "`nPushing to origin main..." -ForegroundColor Green
git push origin main

Write-Host "`nSuccessfully pushed to main!" -ForegroundColor Green
