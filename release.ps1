<#
.SYNOPSIS
    One-step release for WT Mouse Aim: build, tag, publish a GitHub Release, and refresh the
    NOMNOM manifest.

.DESCRIPTION
    Single source of truth for the version is PluginVersion in WTMouseAimPlugin.cs. The script:
      1. Commits any pending changes (unless -NoCommit).
      2. Builds the Release DLL.
      3. Tags vX.Y.Z and pushes the branch + tag.
      4. Creates a GitHub Release with the DLL as an asset (gh CLI).
      5. Computes the SHA-256, writes version / downloadUrl / hash into the NOMNOM manifest, and
         commits that as a follow-up so the tree ends clean.

    Commit-then-build is deliberate (NOMNOM policy clause 2.2 - "DLL must be consistent with the
    source"). The compiler stamps SourceRevisionId from HEAD at build time, so building first would
    ship a DLL pointing at the PREVIOUS commit and send anyone verifying the binary to the wrong
    source. The manifest bump lands AFTER the tag on purpose: the tag must stay on the exact commit
    the DLL was built from.

    After the first release is listed in NOMNOM, future versions are picked up automatically by
    NOMNOM's hourly auto-update job - you still run this script to build + publish the release,
    but you do NOT need to touch NOMNOM again.

.PARAMETER Notes
    Release notes / commit summary. Defaults to the tag name.

.PARAMETER NoCommit
    Skip the auto-commit step (assume the working tree is already committed).

.PARAMETER Deploy
    Also copy the built DLL into the local game's BepInEx plugins folder (dev convenience).

.PARAMETER SkipArchCheck
    Skip the ARCHITECTURE.md drift check that runs before the build.

.EXAMPLE
    ./release.ps1 -Notes "pole-stable horizon leveling"
#>
[CmdletBinding()]
param(
    [string]$Notes,
    [switch]$NoCommit,
    [switch]$Deploy,
    [switch]$SkipArchCheck
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

# Fail before anything is committed, not after (this guard used to sit down at the tag step, where
# an already-taken tag aborted the run with a stray release commit already made).
if (git tag -l $tag) { throw "Tag $tag already exists. Bump PluginVersion or delete the tag." }

# --- 1b. Architecture-diagram drift gate -------------------------------------------------------
# ARCHITECTURE.md is the system map agents (and humans) navigate by, so a drifted diagram must not
# ship. Checks files/types/patches against the node index and the ARCH-VERSION stamp against
# PluginVersion. Skippable (-SkipArchCheck); a missing Python is a warning, not a blocked release.
if (-not $SkipArchCheck) {
    Step "Checking ARCHITECTURE.md is current"
    # No ?? operator here on purpose: this script must stay runnable in Windows PowerShell 5.1.
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $py) {
        Write-Host "python not found - skipping the architecture check." -ForegroundColor Yellow
    } else {
        & $py.Source 'debugtests/check-architecture.py'
        if ($LASTEXITCODE -ne 0) {
            throw "ARCHITECTURE.md is out of date (see above). Update it, or re-run with -SkipArchCheck."
        }
    }
}

# --- 2. Commit pending changes -----------------------------------------------------------------
# Must run BEFORE the build so the DLL's embedded SourceRevisionId names the released commit.
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

# --- 3. Build ----------------------------------------------------------------------------------
Step "Building Release"
dotnet build $csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
if (-not (Test-Path $dll)) { throw "Build did not produce $dll" }

$hash = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.ToLower()
Write-Host "SHA-256: $hash"

# --- 4. Tag + push -----------------------------------------------------------------------------
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

# Commit the bump as a follow-up (it can only be written once the DLL exists and is hashed, i.e.
# after the tag). Left uncommitted it just drifts until some later release sweeps it up, which is
# how the in-repo manifest ended up two releases behind the registry.
if (-not $NoCommit) {
    if (git status --porcelain -- $manifest) {
        git add -- $manifest
        git commit -m "$tag - NOMNOM manifest bump"
        git push origin HEAD
    }
}

# --- 7. Optional local deploy ------------------------------------------------------------------
if ($Deploy) {
    # The game path is no longer stored in the csproj (v0.59 made the build auto-discover it), so
    # ask the locator. It prints exactly one stdout line "<game>|<bepinexCore>"; its diagnostics go
    # to stderr, hence the '|' filter rather than trusting the line count.
    $locateScript = Join-Path $PSScriptRoot 'build\locate-game.ps1'
    $locateOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $locateScript -GamePath ''
    $resultLine = @($locateOut) | Where-Object { $_ -like '*|*' } | Select-Object -Last 1
    if (-not $resultLine) { throw "locate-game.ps1 returned no game path - cannot deploy. Set NUCLEAR_OPTION_PATH." }
    $gamePath = $resultLine.Split('|')[0]
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
