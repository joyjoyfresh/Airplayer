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
    $tag       = "v$Version"
    $zipName   = "AirPlayer-$Version-$Runtime.zip"
    $zipPath   = Join-Path $root "publish\$zipName"
    $setupPath = Join-Path $root "publish\AirPlayer-$Version-setup.exe"
    $notesPath = Join-Path $root "docs\RELEASE_NOTES_v$Version.md"

    # ── 0. Resolve gh path (winget installs to Program Files, may not be in PATH yet) ──
    $gh = (Get-Command gh -ErrorAction SilentlyContinue)?.Source
    if (-not $gh) {
        $gh = "${env:ProgramFiles}\GitHub CLI\gh.exe"
        if (-not (Test-Path $gh)) { throw "gh CLI not found. Install via: winget install --id GitHub.cli" }
    }

    # ── 1. Check prerequisites ────────────────────────────────────────────────
    Write-Host "[1/5] Checking prerequisites ..."
    if (-not (Test-Path $notesPath)) {
        throw "Release notes not found: $notesPath"
    }
    $status = git status --porcelain
    if ($status) {
        throw "Working tree has uncommitted changes. Commit everything before publishing."
    }

    # ── 2. Build release package ──────────────────────────────────────────────
    Write-Host "[2/5] Building portable zip ..."
    & (Join-Path $PSScriptRoot "build-release.ps1") -Version $Version -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "build-release.ps1 failed" }
    if (-not (Test-Path $zipPath)) { throw "Zip not found after build: $zipPath" }

    # ── 2.5. Build installer (requires Inno Setup 6) ─────────────────────────
    Write-Host "[3/5] Building installer (Inno Setup) ..."
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        Write-Warning "  Inno Setup not found at $iscc -- skipping installer build."
        $setupPath = $null
    } else {
        & $iscc (Join-Path $PSScriptRoot "installer.iss") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed (installer build error)" }
        if (-not (Test-Path $setupPath)) { throw "Setup exe not found after build: $setupPath" }
        Write-Host "  Installer: $setupPath"
    }

    # ── 3. Tag and push ───────────────────────────────────────────────────────
    Write-Host "[4/5] Tagging and pushing ..."
    $existingTag = git tag -l $tag
    if ($existingTag) {
        Write-Host "  Tag $tag already exists, skipping tag creation"
    } else {
        git tag -a $tag -m "AirPlayer $tag"
    }
    git push origin main --tags

    # ── 4. Create GitHub Release ──────────────────────────────────────────────
    Write-Host "[5/5] Creating GitHub Release $tag ..."
    $assets = @($zipPath)
    if ($setupPath -and (Test-Path $setupPath)) { $assets += $setupPath }
    & $gh release create $tag @assets `
        --title "AirPlayer $tag" `
        --notes-file $notesPath

    Write-Host ""
    Write-Host "Done!" -ForegroundColor Green
    Write-Host "  Tag:     $tag"
    Write-Host "  Zip:     $zipPath"
    if ($setupPath) { Write-Host "  Setup:   $setupPath" }
    Write-Host "  Release: https://github.com/joyjoyfresh/Airplayer/releases/tag/$tag"
}
finally {
    Pop-Location
}
