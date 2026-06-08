# WT Mouse Aim — Nuclear Option

A War Thunder–style mouse-aim ("smart instructor") mod for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/),
built as a BepInEx 5 / HarmonyX plugin.

The aim marker is a **world-locked desired direction** — point it where you want the
nose to go, and a thin instructor rolls and pulls the aircraft onto that vector. Because
the marker is world-locked (not an airframe-relative offset), the nose converges and the
marker slides back to the boresight as you arrive. The game's own fly-by-wire keeps the
aircraft inside its envelope. An IMGUI overlay draws the marker, boresight, and aim cone.

## How it works

- **Input seam:** a Harmony patch on `PilotPlayerState.PlayerAxisControls()` (runs each
  `FixedUpdate`). The prefix skips the native body while we own the stick in cockpit view;
  the postfix writes the chase output.
- **Aim marker (`AimRig`):** a persistent world-space `_aimForward` vector, nudged by the
  raw mouse delta (`Input.GetAxisRaw("Mouse X"/"Mouse Y")` under `lockState = Locked`) and
  cone-clamped to a max angle around the nose.
- **Instructor (`ChaseController`):** computes the marker in the body frame, then rolls +
  pulls toward it (brihernandez MouseFlight law), blending to wings-level as the nose
  arrives. PD damping on the nose rotation rate.
- **Cursor:** cooperates with the game's hidden + `Locked` flying regime, so 3rd-person
  free-look still works.

Everything is tunable live via the BepInEx config (F1 in-game).

## Build

Requires the .NET SDK. Point `GamePath` in `NuclearOption-MouseAim.csproj` at your
Nuclear Option install (the folder containing `NuclearOption.exe`, with BepInEx 5
installed into it), then:

```
dotnet build NuclearOption-MouseAim.csproj -c Release
```

Copy the built `NuclearOption-MouseAim.dll` into
`<game>\BepInEx\plugins\WTMouseAim\`.

## Requirements

- Nuclear Option
- [BepInEx 5](https://github.com/BepInEx/BepInEx) (Mono)

## License

MIT — see [LICENSE](LICENSE). Game code is **not** included or redistributed.
