# Airframe reference

The roster, and the numbers a card author needs to decide whether an entry condition is flyable.
Look a key up here before putting it in a card's `airframe` list or in `Drone/DroneAirframe` — a
jsonKey that does not exist costs a refused lane, and an entry condition the airframe cannot reach
costs a capture that measures the placement instead of the control law.

**Source.** `Encyclopedia.aircraft` (`List<AircraftDefinition>`), decompile
`Assembly-CSharp.decompiled.cs:9691`, keyed into the public static `Encyclopedia.Lookup` at `:9718`.
There is **no JSON or text data file** for this — the definitions are Unity ScriptableObjects inside
`NuclearOption_Data/resources.assets`, which is why this table exists rather than a "just read the
fields" note. Cross-validated against the mod's own `.airframe.json` sidecar for `Multirole1`: 6/6
overlapping fields exact.

## The 14 jsonKeys

10 fixed-wing, 3 rotary/tiltwing, 1 event-only. Speeds in **m/s** (converted here — do not re-derive,
see trap 2), mass in kg, `turnR` in m.

**Two corner-speed columns, and they are different numbers** — see [trap 6](#six-traps-in-the-underlying-fields).
**FBW corner is the one that flies the aircraft** and the one `startSpeedCorner` resolves against
since v0.96; AI corner is what `aircraftParameters.cornerSpeed` publishes and it reaches nothing but
the bot pilot. Use the FBW column for anything about control, the AI column only to recognise a
number you read somewhere else.

| jsonKey | unitName | code | class | Vstall | Vmax | FBW corner | AI corner | gLimit | turnR m | mass kg |
|---|---|---|---|---|---|---|---|---|---|---|
| `Fighter1` | FS-12 Revoker | FS-12 | fixed-wing | 72.2 | 401.4 | **160** | 180 | 9.0 | 1500 | 8680 |
| `Multirole1` | KR-67 Ifrit | KR-67 | fixed-wing (canard/RSC) | 66.7 | 416.7 | **160** | 180 | 9.0 | 1700 | 16040 |
| `SmallFighter1` | FS-20 Vortex | FS-20 | fixed-wing STOVL | 75.0 | 415.3 | **155** | 180 | 9.0 | 1200 | (prefab) |
| `trainer` | T/A-30 Compass | T/A-30 | fixed-wing (**2 seats**) | 50.0 | 294.4 | **130** | 160 | 9.0 | 1000 | 6111 |
| `VTOLTrainer1` | VT-7 Vagrant | VT-7 | fixed-wing VTOL (3-post ducted) | 50.0 | 294.4 | **160** | 160 | 8.0 | 1250 | 5890 |
| `CAS1` | A-19 Brawler | A-19 | fixed-wing (propfan) | 61.1 | 205.6 | **160** | 200 | 7.5 | 1000 | 10620 |
| `COIN` | CI-22 Cricket | CI-22 | fixed-wing STOL | 38.9 | 141.7 | **110** | 90 | 6.5 | 1000 | 3100 |
| `EW1` | EW-25 Medusa | EW-25 | fixed-wing STOVL | 33.3 | 286.1 | **130** | 120 | 6.0 | 1250 | (prefab) |
| `FastBomber1` | Alkyon AB-4 | AB-4 | fixed-wing (**2 seats**) | 63.9 | 479.2 | **200** | 180 | 5.0 | 3000 | 34100 |
| `Darkreach` | SFB-81 Darkreach | SFB-81 | fixed-wing | 66.7 | 279.2 | **100** | 180 | 5.0 | 1500 | 54311 |
| `AttackHelo1` | SAH-46 Chicane | SAH-46 | helicopter | 0 | 100.0 | **170**† | 120 | 3.0 | 750 | 7550 |
| `UtilityHelo1` | UH-90 Ibis | UH-90 | helicopter (compound) | 0 | 133.9 | — | 120 | 3.5 | 750 | 7300 |
| `QuadVTOL1` | VL-49 Tarantula | VL-49 | quad tiltwing | 0 | 148.6 | — | 120 | 3.0 | 750 | 28900 |
| `UFO` | ??? | ??? | **event-only — ignore.** `isEventContent=1`, gated by `MissionManager.AllowEventContent` (`UnitDefinition.IsAllowed :89961`). A clone of `Fighter1`'s envelope at 79 t | | | | | | | |

**Where the FBW column comes from, per airframe: the capture corpus, not the decompile.** It is a
serialized field on the prefab, so it has **no value anywhere in the decompiled source** — the only
way to read one is to look at an instance, and the recorder already does (`Recording.cs` writes
`fbwCornerSpeed` into every `.airframe.json` sidecar from `GetFlyByWireParameters()[2]`). Every number
above is that field, unanimous across every capture of that airframe in `captures.db`:
`Multirole1` n=305, `Fighter1`/`FastBomber1` n=240, `trainer` n=195, `Darkreach` n=120,
`SmallFighter1`/`EW1`/`VTOLTrainer1` n=96, `CAS1`/`COIN` n=48, `AttackHelo1` **n=1**.
`UtilityHelo1`, `QuadVTOL1` and `UFO` have **never been flown by this project**, so the cell is blank
rather than guessed — fly one and re-read its sidecar.

† `AttackHelo1`'s 170 is the **base** `FlyByWire`'s serialized field, which is what the mod's
pre-spawn probe reads and therefore what a corner-relative card would use on it — but a rotorcraft
never runs the base FBW (`HeloControlsFilter` overrides `Filter` and flies a private `heloFlyByWire`),
so unlike the fixed-wing rows it does not describe the aircraft that actually flies. On this airframe
it is moot anyway — 170 is above its whole Vmax (100), so a corner-relative card refuses the lane
outright (below). The other two rotorcraft have **no measured FBW value at all**: the probe will read
whatever their prefab carries and nothing here has seen it, while their fail-soft fallback (AI 120)
*is* inside their Vmax ceiling — so unlike `AttackHelo1` a corner-relative card may well spawn them,
at a speed this table cannot vouch for. Fly one and read its sidecar before writing that card.

`Attacker1` is **not** a jsonKey. It appeared in this repo's docs as an example for years and has zero
occurrences in the game data; the natural real substitute is `CAS1`.

## What the shipped grid can actually fly

Stated plainly because it is the actionable part:

- **The 250 m/s entry condition in every `oblique-*`, `sweep-*` and `e1`–`e3` card is UNFLYABLE by
  `CAS1` (Vmax 205.6), `COIN` (141.7) and all three rotorcraft.** Putting one of those in such a
  card's `airframe` list does not refuse — the placement writes a speed the airframe cannot hold and
  the capture measures the decay.
- **The `stol-*` cards' 90 m/s entry is above every airframe's stall**, so nothing refuses there
  either, but it is below sensible-maneuver speed for `Fighter1`/`SmallFighter1` (FBW corner 160/155).
- One card is one test: an airframe that cannot fly the entry condition needs **its own card**, not a
  slower `startSpeed` on the shared one — that re-bands every other lane at once.
- **Or `startSpeedCorner` (v0.93)**, which is usually the better answer for a roster card: the entry
  speed becomes a multiple of the **FBW corner** column above, resolved per lane, so every airframe is
  entered at its own best-turn-rate point instead of at a shared number. `1.0x` is 160 m/s for
  `Fighter1`, 110 for `COIN`, 100 for `Darkreach`.
- **Since v0.96 all ten fixed-wing keys clear the v0.92 envelope gate at `1.0x`, `CAS1` included.**
  The old trap — `CAS1` refused at `1.0x` because its corner (200) sat above 0.95 × its Vmax (195.3) —
  was an artefact of gating on the *AI* field; its FBW corner is 160, comfortably inside. `0.95x` also
  clears all ten and stays what the shipped `oblique-*-c` family uses. **No rotorcraft flies a
  corner-relative card**: `AttackHelo1`'s 170 is above its entire Vmax (100), and the other two have
  no measured value (see the † note above).
- **A corner-relative capture from before v0.96 is NOT comparable with one after it.** The
  `oblique-*-c` family resolved `0.95x` against the AI column until v0.96, so `Fighter1` entered at
  171 m/s and now enters at 152; `Darkreach` entered at 171 and now enters at 95. Compare `-c`
  captures within one build, and read the `# entry` header, which carries the speed actually placed.

## Six traps in the underlying fields

Each is why the table above exists instead of a pointer to the ScriptableObject.

1. **`aircraftParameters.maxSpeed` IS NOT a Vmax for jets.** It is a *normalizer*
   (`aircraft.speed / aircraftParameters.maxSpeed` at `:15557`, `:15922`, `:70341`) and reads a flat
   `600` for every fast jet. The Vmax column above is `aircraftInfo.maxSpeed / 3.6`. The two agree for
   rotorcraft and diverge by ~50% for `Fighter1`.
2. **`aircraftInfo` is in km/h**, divided by 3.6 at every use site (`:2584`, `:10261-10262`).
   `aircraftParameters` is already m/s. Mixing the two is the easy mistake.
3. **`aircraftInfo.emptyWeight` is template junk.** 10700 is shared by
   `Fighter1`/`Multirole1`/`SmallFighter1`, 7260 by all three rotorcraft, 5200 by
   `CAS1`/`trainer`/`VTOLTrainer1`. Display-only, never read in flight logic. Use `mass`.
4. **`UnitDefinition.mass` (`:89911`) is DRY mass**, overwritten at Encyclopedia load by
   `CacheMass()` → `GetPrefabMass()` (`:89975`). The sidecar's `massKg` 25563 for `Multirole1` =
   mass 16040 + fuel 8200 + stores 1373.
5. **No service ceiling exists.** Zero hits for "ceiling" in the decompile. Altitude is bounded only
   by physics: `LevelInfo.GetAirDensity(alt)` (`:21776`) reads a 64-sample chart spanning 0–30 km, and
   `GetSpeedOfSound(alt) = max(340 − 0.005·alt, 290)` (`:21783`). The nearest per-airframe hint is
   `UnitDefinition.maxEditorHeight` = 10000 m for everything except `QuadVTOL1` (5000 m). This is why
   the `alpha-*` cards reach the AoA ceiling by climbing to 8000 m rather than by asking for it.
6. **THERE ARE TWO `cornerSpeed` FIELDS AND THEY HOLD DIFFERENT NUMBERS.**
   `aircraftParameters.cornerSpeed` is the **AI's**; `ControlsFilter.FlyByWire.cornerSpeed` is the
   **flight control system's**, and it is the one the aircraft is actually flown by. Found in R29
   (2026-07-30) by reading the launch log: the `[card] entry condition set … (0.95x corner)` line and
   the `[fbw]` probe line disagreed on the same aircraft. **Since v0.96 the mod reads the FBW one**
   (`TestDrone.FbwCornerSpeed` → `Envelope.Corner`, fail-soft to the AI value with a named warning,
   never to zero), so `startSpeedCorner` and the pre-spawn envelope gate are both on the FBW field.
   The trap survives the fix because the AI field is still the obvious one: it is what
   `Encyclopedia.Lookup` hands you, what the sidecar calls `cornerSpeed` (the FBW one is
   `fbwCornerSpeed`), and it is **not** what the FBW flies.

   | | `AircraftParameters.cornerSpeed` (`:63097`) | `ControlsFilter.FlyByWire.cornerSpeed` (`:64877`) |
   |---|---|---|
   | lives beside | `takeoffDistance`, `turningRadius`, `approachSpeed`, `landingSpeed` | the FBW rate-limiter gains |
   | who reads it | **AI pilot code, and nothing else** — target selection `:12421`, throttle `:12996`, glideslope `:13627`, `:13930`, approach `:14124`, `:14237`, waypoint nav `:15776`, `:15790` | the FBW itself |
   | what it does | describes the aircraft to a bot | **sets the pitch-rate command**: `targetPitchAngVel = inputs.pitch * gLimitPositive * 9.81f / Mathf.Max(aircraft.speed, cornerSpeed * 0.75f)` (`:65032`), plus pitch authority scaling `:64844`, the g-limit rate conversion `:64845` and the q reference `:65017` |

   Measured disagreement over the whole capture corpus (both numbers are in every sidecar; the two
   columns in the table above are the per-airframe result): the ratio FBW/AI runs from **0.556×**
   (`Darkreach` 100 vs 180) to **1.417×** (`AttackHelo1` 170 vs 120), and only `VTOLTrainer1` agrees.
   Different distributions, not a rounding difference: the AI set has four airframes at 180 and values
   of 90 and 120 that appear nowhere in the FBW set. The practical size of it: a `startSpeedCorner`
   card, whose whole claim is that every lane enters at the same aerodynamic state, was spreading a
   ten-airframe fleet over **2.2× of true FBW corner**.

   **For anything about how the aircraft is CONTROLLED, use the FBW value.** `targetPitchAngVel` is
   exactly the quantity this mod commands, so the FBW `cornerSpeed` is the breakpoint of the
   authority the control law has to work with — above `cornerSpeed * 0.75` pitch-rate authority
   falls as `1/speed`, below it the scaling is flat. The encyclopedia value has no effect on flight
   whatsoever.

   **Reading it before a spawn** (no aircraft instance), which is what v0.96 does:
   `Encyclopedia.i.TryGetPrefab(jsonKey, out prefab)` — already used by `TestDrone.Spawn` — then
   `prefab.GetComponentInChildren<ControlsFilter>(true)` (`includeInactive`: a prefab's hierarchy is
   inactive, and `HeloControlsFilter` `:36005` derives from `ControlsFilter`, so rotorcraft resolve
   through the same call) and `GetFlyByWireParameters()`, which is **public** (`:65710`) and packs
   `cornerSpeed` at **index 2** (`FlyByWire.GetParameters()`, `:64959`). **No reflection is needed** —
   this is the same public accessor the v0.55 live FBW probe reads, just asked of a prefab. The value
   is serialized on the prefab, so it is correct pre-spawn — `ApplyParameters` (`:64969`) is the
   in-game dev tuning panel (`:42876`, `:42889`), not a load path.

## Querying it before a spawn

What makes a feasibility gate possible at all. `Encyclopedia.Lookup` is only non-null after
`Encyclopedia.AfterLoad()`; `TestDrone.cs` already guards on `Encyclopedia.i == null` at its spawn.

```csharp
if (Encyclopedia.Lookup.TryGetValue(jsonKey, out var ud) && ud is AircraftDefinition ad) {
    float vStall = ad.aircraftInfo.stallSpeed / 3.6f;   // :62964
    float vMax   = ad.aircraftInfo.maxSpeed   / 3.6f;   // :62962
    var p = ad.aircraftParameters;                      // :62973
    float aiCorner = p.cornerSpeed;                     // :63097 — the AI's. NOT what the FBW flies:
                                                        // for control, read trap 6's prefab FBW value

    float gLim   = p.aircraftGLimit;                    // :63083
    float turnR  = p.turningRadius;                     // :63095
    bool  vtol   = p.verticalLanding;                   // :63093
    float mass   = ad.mass;                             // :89977
}
```

Seat count is **not** in there — there is no `crew` field anywhere in the decompile. It is prefab
data, which is why the harness logs `ac.pilots.Length` on the spawn line and why the two-seaters are
marked in the table above. That mattered before v0.90.1: every seat fires the pilot postfix
independently, so a two-seat drone ran the card clock and the control law twice per fixed step.
