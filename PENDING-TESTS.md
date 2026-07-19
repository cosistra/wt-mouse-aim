# Pending flight tests

Shipped-but-unflown changes that must be verified in-game before / at the v0.59 release.

**Convention:** run each test, then **delete its entry** from this file. When the file is empty,
**delete the file.** An entry left here means the change has NOT been confirmed in the air yet.

Each entry: *what to fly* / *what to look for* / delete when it passes.

---

## v0.59 — AoA-utilization demand schedule (the loaded-jet pitch fix)

- [ ] **FS-12 Revoker, loaded, below 400 kt — the primary fix.**
  Fly the loaded Revoker in tracking turns below ~400 kt (the v58 Discord case was 229–330 kt).
  The rail-to-rail ~0.55 Hz pitch death oscillation (AoA swinging +43°/−18° on a ~23° ceiling)
  must be **gone**. Confirm via the `[anomaly]` log (no pitch relay / AoA blow-through lines) and a
  maneuver recording (F8) run through `debugtests/analyze-wobble.py`.

- [ ] **Regression — Compass / Trainer STOL near its ~10° alpha limiter.**
  Turn hard enough to engage the AoA schedule (it keys off utilization against the probed ceiling,
  which bites early on this low-limit airframe). Should feel **calmer** at the limiter, and **never
  weaker** in ordinary (non-near-ceiling) flight.

- [ ] **Regression — Ibis hover + transitions.**
  The rotorcraft path is untouched by v0.59. Confirm hover hold and hover↔forward transitions feel
  identical to v0.58 (no schedule leaking into collective airframes).

- [ ] **Regression — Multirole1, high-q tracking.**
  The schedule is inert below its AoA-utilization ceiling, so high-speed / high-q fine tracking
  should feel **identical** to v0.58. Verify no new softness or lag on boresight pulls.

- [ ] **Regression — KR-67 Ifrit, fixed-wing fine aim.**
  Canard-inversion ordering is unchanged in v0.59. Fly straight-line and fine tracking; the v0.57
  ~5 Hz pitch buzz must **not** return.

- [ ] **Data collection — Ifrit post-turn rudder oscillation.**
  Record (F8) the Ifrit's post-turn rudder/yaw oscillation. No recording of it exists yet; capturing
  one **unblocks GENERALITY-REVIEW.md finding 3** (Ifrit hover-yaw hypothesis awaiting data).

- [ ] **Build-system — auto-discovery on a clean machine.**
  On a machine where the game lives in a **non-default Steam library** and BepInEx is **absent**,
  run `dotnet build -c Release` with no `GamePath`/`NUCLEAR_OPTION_PATH` set. It must locate the game
  via Steam metadata and self-cache BepInEx under `.deps/` (download path), building with 0 errors.
