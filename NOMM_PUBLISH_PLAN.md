# Publishing & Releasing — WT Mouse Aim

How this mod is distributed and how to cut a new release.

## Distribution model

The mod is **hosted entirely on this repo** (`cosistra/wt-mouse-aim`):

- The source lives here.
- Each version is a **GitHub Release** with the built `NuclearOption-MouseAim.dll` as the asset.

[**NOMM**](https://github.com/Combat787/NOMM) (the Nuclear Option Mod Manager) discovers the mod
through the [**NOMNOM**](https://github.com/KopterBuzz/NOMNOM) registry — a community index of
small JSON manifests. NOMNOM does **not** host the file; its manifest just points at this repo's
release URL, and NOMM downloads the DLL directly from here. The manifest for this mod is
[`NuclearOption-MouseAim.nomnom.json`](NuclearOption-MouseAim.nomnom.json).

```
this repo (release + DLL)  ◄── NOMNOM manifest points here ──  NOMM downloads from here
```

## Cutting a release

Everything is automated by [`release.ps1`](release.ps1). The version is read from
`PluginVersion` in `WTMouseAimPlugin.cs` — that's the single source of truth.

1. Bump `PluginVersion` in `WTMouseAimPlugin.cs` (and the Awake load-line string if features
   changed).
2. Run:
   ```powershell
   ./release.ps1 -Notes "short summary of the change"
   ```
   This builds Release, commits pending changes, tags `vX.Y.Z`, pushes, creates the GitHub
   Release with the DLL attached, computes the SHA-256, and writes the version / download URL /
   hash into the NOMNOM manifest.

Requirements: the .NET SDK, the `gh` CLI authenticated (`gh auth status`), and the game install
referenced by `<GamePath>` in the csproj (the build needs the game's managed assemblies).

## First-time NOMNOM registration (one time only)

NOMNOM has to learn the mod exists once. After that its hourly job auto-detects new releases.

1. Fork [`KopterBuzz/NOMNOM`](https://github.com/KopterBuzz/NOMNOM).
2. Add the manifest as `modManifests/NuclearOption-MouseAim.json` (copy
   `NuclearOption-MouseAim.nomnom.json`).
3. Open a PR against `main`. CI validates the schema; a maintainer merges.

Once merged, the mod appears in NOMM within ~1 hour. **No further NOMNOM PRs are needed** —
because `autoUpdateArtifacts` is `True` and the repo follows NOMNOM's conventions (one mod per
repo, one asset per release, parseable `vX.Y.Z` tags), NOMNOM ingests future releases on its own.

## Key manifest facts

| Field | Value |
|---|---|
| `id` | `NuclearOption-MouseAim` (matches the DLL AssemblyName) |
| `displayName` | `WT Mouse Aim` |
| `type` | `plugin` |
| `gameVersion` | `0.33` — bump when validated against a newer game patch |
| `githubOwner` / `githubRepoName` | `cosistra` / `wt-mouse-aim` |
| `autoUpdateArtifacts` | `True` |
| dependency | `BepInEx.ConfigurationManager` `18.4.1` (for F1 live tuning) |

## Notes

- `gameVersion` is currently set to `0.33`. Confirm against the live game patch and bump as the
  mod is validated on newer versions.
- The NOMNOM README mentions opening an issue on a `NOModManifestTesting` repo — that link is
  stale; the real path is a PR to `KopterBuzz/NOMNOM` as described above.
