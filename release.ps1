<#
.SYNOPSIS
    One-step release for WT Mouse Aim: build, tag, publish a GitHub Release, and refresh the
    NOMNOM manifest.

.DESCRIPTION
    Single source of truth for the version is PluginVersion in WTMouseAimPlugin.cs. The script:
      1. Builds the Release DLL.
      2. Commits any pending changes (unless -NoCommit).
      3. Tags vX.Y.Z and pushes the branch + tag.
      4. Creates a GitHub Release with the DLL as an asset (gh CLI).
      5. Computes the SHA-256 and writes version / downloadUrl / hash into the NOMNOM manifest.

    After the first release is listed in NOMNOM, future versions are picked up automatically by
    NOMNOM's hourly auto-update job - you still run this script to build + publish the release,
    but you do NOT need to touch NOMNOM again.

.PARAMETER Notes
    Release notes / commit summary. Defaults to the tag name.

.PARAMETER NoCommit
    Skip the auto-commit step (assume the working tree is already committed).

.PARAMETER Deploy
    Also copy the built DLL into the local game's BepInEx plugins folder (dev convenience).

.EXAMPLE
    ./release.ps1 -Notes "pole-stable horizon leveling"
#>
[CmdletBinding()]
param(
    [string]$Notes,
    [switch]$NoCommit,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$csproj      = 'NuclearOption-MouseAim.csproj'
$source      = 'WTMouseAimPlugin.cs'
$dll         = 'bin/Release/NuclearOption-MouseAim.dll'
$manifest    = 'NuclearOption-MouseAim.nomnom.json'
$repoSlug    = 'cosistra/wt-mouse-aim'

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- 1. Resolve version from source (single source of truth) -----------------------------------
$verMatch = Select-String -Path $source -Pattern 'PluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $verMatch) { throw "Could not find PluginVersion in $source" }
$version = $verMatch.Matches[0].Groups[1].Value
$tag = "v$version"
if (-not $Notes) { $Notes = $tag }
Step "Releasing $tag"

# --- 2. Build ----------------------------------------------------------------------------------
Step "Building Release"
dotnet build $csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
if (-not (Test-Path $dll)) { throw "Build did not produce $dll" }

$hash = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.ToLower()
Write-Host "SHA-256: $hash"

# --- 3. Commit pending changes -----------------------------------------------------------------
if (-not $NoCommit) {
    $dirty = git status --porcelain
    if ($dirty) {
        Step "Committing pending changes"
        git add -A
        git commit -m "$tag - $Notes"
    } else {
        Write-Host "Working tree clean - nothing to commit."
    }
}

# --- 4. Tag + push -----------------------------------------------------------------------------
if (git tag -l $tag) { throw "Tag $tag already exists. Bump PluginVersion or delete the tag." }
Step "Tagging and pushing"
git tag $tag
git push origin HEAD
git push origin $tag

# --- 5. GitHub Release -------------------------------------------------------------------------
Step "Publishing GitHub Release"
$exists = $false
try { gh release view $tag --repo $repoSlug *> $null; if ($LASTEXITCODE -eq 0) { $exists = $true } } catch {}
if ($exists) {
    gh release upload $tag $dll --repo $repoSlug --clobber
} else {
    gh release create $tag $dll --repo $repoSlug --title $tag --notes $Notes
}

# --- 6. Refresh NOMNOM manifest ----------------------------------------------------------------
# Targeted string edits rather than ConvertFrom/ConvertTo-Json: PS 5.1 can collapse single-element
# arrays on round-trip (breaking the schema). The artifact "version" is the FIRST "version" in the
# file (it precedes the dependency block), so replace only the first match - a global replace would
# also clobber the dependency's version.
Step "Updating NOMNOM manifest ($manifest)"
$downloadUrl = "https://github.com/$repoSlug/releases/download/$tag/NuclearOption-MouseAim.dll"
$text = Get-Content -Raw -LiteralPath $manifest
# Static [regex]::Replace has no count overload (a trailing 1 is parsed as RegexOptions.IgnoreCase
# and replaces ALL matches — that clobbered the dependency's version up to v0.50). Instance .Replace
# does take a count.
$text = ([regex]'("version":\s*")[^"]*(")').Replace($text, "`${1}$version`${2}", 1)
$text = [regex]::Replace($text, '("downloadUrl":\s*")[^"]*(")', "`${1}$downloadUrl`${2}")
$text = [regex]::Replace($text, '("hash":\s*")[^"]*(")', "`${1}sha256:$hash`${2}")
Set-Content -LiteralPath $manifest -Value $text -Encoding UTF8 -NoNewline

# --- 7. Optional local deploy ------------------------------------------------------------------
if ($Deploy) {
    $gamePath = (Select-String -Path $csproj -Pattern '<GamePath>(.*?)</GamePath>').Matches[0].Groups[1].Value
    $dest = Join-Path $gamePath 'BepInEx/plugins/WTMouseAim'
    Step "Deploying to $dest"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $dll $dest -Force
}

Step "Done - $tag published"
Write-Host "  Release : https://github.com/$repoSlug/releases/tag/$tag"
Write-Host "  Hash    : sha256:$hash"
Write-Host "  Manifest: $manifest (updated)"
Write-Host ""
Write-Host "If this is the FIRST release, submit $manifest to NOMNOM as modManifests/NuclearOption-MouseAim.json" -ForegroundColor Yellow
Write-Host "Otherwise NOMNOM's hourly job will pick up this release automatically." -ForegroundColor Yellow
