<#
  publish-release.ps1 - tag, push, and create GitHub Release
  Run AFTER bumping the version and committing all changes.

  Usage (run from repo root):
    powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Version 1.4.2

  Prerequisites:
    - git and gh CLI installed and authenticated
    - Version already bumped in csproj / installer.iss / README / CHANGELOG
    - docs\RELEASE_NOTES_v{Version}.md exists
    - All changes committed on main

  NOTE: keep this file ASCII-only (Windows PowerShell 5.1 ANSI encoding issue).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $tag      = "v$Version"
    $zipName  = "AirPlayer-$Version-$Runtime.zip"
    $zipPath  = Join-Path $root "publish\$zipName"
    $notesPath = Join-Path $root "docs\RELEASE_NOTES_v$Version.md"

    # ── 0. Resolve gh path (winget installs to Program Files, may not be in PATH yet) ──
    $gh = (Get-Command gh -ErrorAction SilentlyContinue)?.Source
    if (-not $gh) {
        $gh = "${env:ProgramFiles}\GitHub CLI\gh.exe"
        if (-not (Test-Path $gh)) { throw "gh CLI not found. Install via: winget install --id GitHub.cli" }
    }

    # ── 1. Check prerequisites ────────────────────────────────────────────────
    Write-Host "[1/4] Checking prerequisites ..."
    if (-not (Test-Path $notesPath)) {
        throw "Release notes not found: $notesPath"
    }
    $status = git status --porcelain
    if ($status) {
        throw "Working tree has uncommitted changes. Commit everything before publishing."
    }

    # ── 2. Build release package ──────────────────────────────────────────────
    Write-Host "[2/4] Building release package ..."
    & (Join-Path $PSScriptRoot "build-release.ps1") -Version $Version -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "build-release.ps1 failed" }
    if (-not (Test-Path $zipPath)) { throw "Zip not found after build: $zipPath" }

    # ── 3. Tag and push ───────────────────────────────────────────────────────
    Write-Host "[3/4] Tagging and pushing ..."
    $existingTag = git tag -l $tag
    if ($existingTag) {
        Write-Host "  Tag $tag already exists, skipping tag creation"
    } else {
        git tag -a $tag -m "AirPlayer $tag"
    }
    git push origin main --tags

    # ── 4. Create GitHub Release ──────────────────────────────────────────────
    Write-Host "[4/4] Creating GitHub Release $tag ..."
    & $gh release create $tag $zipPath `
        --title "AirPlayer $tag" `
        --notes-file $notesPath

    Write-Host ""
    Write-Host "Done!" -ForegroundColor Green
    Write-Host "  Tag:     $tag"
    Write-Host "  Zip:     $zipPath"
    Write-Host "  Release: https://github.com/joyjoyfresh/Airplayer/releases/tag/$tag"
}
finally {
    Pop-Location
}
