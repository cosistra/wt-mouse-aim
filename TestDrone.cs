using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // TEST DRONE (v0.81 harness, v0.87 phase 2 — the mod's own control law flies it).
    //
    // Every measurement this project has ever taken cost a human sitting in a cockpit for the length
    // of the card. A four-replicate suite of `fixedwing-v2` is ~12 minutes of someone watching a
    // marker sweep. This file removes the pilot from that loop: the mod spawns its own aircraft, owns
    // its ControlInputs, and destroys it again — so a sweep can run N airframes at once, unattended.
    //
    // PHASE 2 (v0.87) CLOSED THE LOOP. A drone starts a test card on its own first pilot step, and
    // `Drone.Fly` chases that card's demand through `ChaseController.Apply` — the same law, the same
    // per-aircraft controller and the same per-aircraft recorder the human flies, so a drone capture
    // and a crewed capture measure the same thing. The built-in level-hold below survives for the one
    // case with nothing to chase (no card running) and is still NOT the mod's control law.
    //
    // WHY N DRONES AND NOT ONE. The unit of measurement is a REPLICATE SET, not a run — a single
    // capture has no spread, so a change cannot be called real from it (that is the whole argument
    // behind `ScenarioRepeat`). Replicates flown back-to-back cost wall clock linearly; replicates
    // flown side-by-side cost one card length total. That only holds if the replicates stay
    // INDEPENDENT, which is what the stagger below is for.
    //
    // WHERE IT WORKS. `Spawner.SpawnAircraft` is not itself `[Server]`, but it ends in
    // `ServerObjectManager.Spawn`, which needs an active server. Single player IS a host
    // (`StartHostAsync(new HostOptions(SocketType.Offline, GameState.SinglePlayer, ...))`), so this
    // works in SP and while hosting, and never as an MP client. We check and refuse rather than
    // letting Mirage throw into the game loop.
    internal static class TestDrone
    {
        // Lane geometry. Drones are laid out ABEAM the player on the heading he was flying when the
        // key was pressed — parallel courses, so nothing converges on anything.
        //   AbeamM  : how far out the first lane sits. Far enough that the drone cannot collide with,
        //             be shot at by, or visually clutter whatever is happening near the player.
        //   LaneM   : lateral gap between consecutive drones. SIZED BY THE TURN CARDS, not by taste:
        //             the sustained-turn family flies a full 360 at the bank clamp, and at the 250 m/s
        //             entry condition a 72 deg banked turn has radius v^2/(g*tan phi) = 62500/(9.81*
        //             3.08) = 2.07 km, i.e. a 4.1 km CIRCLE. At the old 2 km gap two neighbouring
        //             lanes flying the same card swept overlapping ground tracks — they only ever
        //             missed because the launch stagger put them at different points on it. 6 km
        //             clears the widest card circle with a lane's width to spare.
        // ponytail: fixed lateral lanes, no altitude stacking and no re-check that the lane is clear.
        // The range mission (`harness/WTM-Range`) is deliberately empty, which is what makes that
        // safe. If a card ever flies over a populated map, give each lane its own altitude block.
        private const float AbeamM = 8000f;
        private const float LaneM  = 6000f;

        // Hitch reporting. A "hitch" is any rendered frame that took more than this; during one,
        // Unity runs several FixedUpdates back to back all reporting the SAME unscaledDeltaTime, so
        // we log on the rising edge only or a single 300 ms stall would print fifteen identical lines.
        private const float HitchSec = 0.050f;

        // How long a drone may sit with no card running before it despawns itself (v0.90). Sized by
        // what it has to clear, not by taste: the gap between NextCard closing one recorder and
        // StartCard opening the next is a placement tick plus a frame, so anything near zero would
        // despawn a drone between its own replicates. 5 s also happens to be long enough to watch the
        // sky empty, which is worth seeing once.
        // ponytail: a const, not a Cfg knob — nothing about a run wants a different value, and the
        // despawn key already covers "get rid of it now".
        private const float IdleDespawnSec = 5f;

        private static readonly List<Drone> _live = new List<Drone>();
        // Keyed by Aircraft.GetInstanceID() so the per-pilot postfix is a dictionary probe, not a
        // scan over the live list — that postfix runs once per pilot per fixed step for EVERY
        // aircraft in the mission, drones and AI and the player alike.
        private static readonly Dictionary<int, Drone> _byAircraftId = new Dictionary<int, Drone>();
        private static int _nextId = 1;

        // --- staggered-launch state (see RequestLaunch) ---
        private static int        _pending;      // drones still to be launched from the current request
        private static int        _slot;         // lane index of the NEXT one
        private static float      _nextAt;       // Time.time it is due
        private static Vector3    _laneBase;     // player position when the key was pressed
        private static Vector3    _laneRight;    // horizontal right of his heading (the lane axis)
        private static Quaternion _laneRot;      // spawn attitude: his heading, wings level, nose on the horizon

        // --- WHAT THE CARD SAID (v0.90). Resolved ONCE in RequestLaunch, from the same preflight the
        // launch line prints, and read by every lane of that batch. Resolving per lane instead would
        // let a checkbox ticked mid-stagger change the airframe half way through a batch, which is a
        // heterogeneous batch nobody asked for and no artifact would explain.
        //
        // Empty / <= 0 means "the card doesn't say", and then the Drone knob stands exactly as it
        // did before this existed. That fallback is what keeps a hand-configured launch working.
        //
        // v0.90b: the whole preflight is kept rather than three fields copied out of it, so the run
        // board can call the SAME three resolvers below against a FRESH preview and be guaranteed to
        // agree with what a launch would do. It must not refresh this one — a batch mid-stagger would
        // change airframe half way through.
        private static ScenarioPlayer.Preflight _plan;

        // The hot-path gate. Everything in this file that runs per-tick early-outs on this single
        // read, exactly like ScenarioPlayer's null-card check — with no drone alive the harness costs
        // one int compare per pilot per fixed step.
        public static bool Idle => _byAircraftId.Count == 0;

        // FRAME TIME, sampled on the RENDERED FRAME (v0.81; corrected in v0.92.1 — it said "on the
        // fixed step" and did exactly that, which is the bug: see SampleFrameTime). The stagger below
        // exists because a frame hitch lands on whatever segment is running when it happens; if all N
        // replicates are flying the same segment at that instant, one hitch corrupts all N identically
        // and they stop being independent samples. That is an assumption until it is instrumented, so:
        // this is the last `Time.unscaledDeltaTime` seen by Update(), exposed for the recorder to
        // sample as a column. Unscaled on purpose — the scaled one would hide a hitch as a timeScale
        // change.
        public static float FrameDt { get; private set; }

        // "Which drone is this aircraft, if any?" — 1..N for a drone, 0 for anything else (the player
        // first of all). One dictionary probe. Exists so ManeuverRecorder can put the drone number in
        // its filename without this file having to know the recorder exists: N concurrent captures in
        // one folder are unreadable if the only thing telling them apart is a take number.
        public static int DroneIdOf(int aircraftId) =>
            _byAircraftId.TryGetValue(aircraftId, out var d) ? d.Id : 0;

        // =========================================================================================
        // LAUNCH
        // =========================================================================================
        // Hotkey entry point. Captures the lane geometry ONCE, here, then lets FixedTick launch the
        // drones one at a time — so the layout is relative to where the player was when he asked,
        // not to wherever he has flown by the time the last one appears.
        public static void RequestLaunch()
        {
            if (!Cfg.DroneEnabled.Value) return;
            if (_pending > 0)
            {
                WTMouseAimPlugin.Log.LogWarning($"[drone] a launch of {_pending} more is already in progress — ignoring.");
                return;
            }

            Vector3 fwd = Vector3.forward;
            _laneBase = Vector3.zero;
            try
            {
                // LANES ARE RELATIVE TO WHOEVER IS WATCHING. With an aircraft that is his; with none
                // (menu, dead, ejected, spectating) it is the CAMERA. v0.90 — the old fallback was the
                // scene origin, and it was worse than "still a well-defined place": the origin is the
                // SAME point on every press, so a second launch while spectating put lane k exactly
                // where the first one did, and each drone's card anchor is its own spawn point. The
                // camera is both visible and observer-dependent. Fail-soft, like the probes.
                if (AimRig.TryGetContext(out var me, out _) && me != null)
                {
                    _laneBase = me.transform.position;
                    Vector3 f = me.transform.forward; f.y = 0f;
                    if (f.sqrMagnitude > 1e-6f) fwd = f.normalized;
                }
                else
                {
                    var cam = Camera.main;                    // stdlib; no game type, no new reflection seam
                    if (cam != null)
                    {
                        _laneBase = cam.transform.position;
                        Vector3 f = cam.transform.forward; f.y = 0f;
                        if (f.sqrMagnitude > 1e-6f) fwd = f.normalized;
                    }
                }
            }
            catch { /* geometry is best-effort; the defaults above are valid */ }

            _laneRight = Vector3.Cross(Vector3.up, fwd);      // horizontal right of the heading
            _laneRot   = Quaternion.LookRotation(fwd, Vector3.up);
            // Start past whatever is still up, rather than at 0. Auto-despawn (below) normally empties
            // the sky between batches, so this is the belt to that braces: it makes "press it twice"
            // safe even when the observer has not moved, which lane 0 alone does not.
            _slot      = _live.Count;
            _nextAt    = Time.time;                           // first one goes on the next fixed step

            // THE CARD IS THE TEST, SO THE CARD PICKS THE METAL (v0.90). A drone exists to fly a card,
            // and a card already declares the airframe it was designed on and the speed/altitude it
            // intends. Matching Drone/DroneAirframe, DroneSpawnAlt and DroneSpawnSpeed to it by hand
            // was three chances to get it wrong per batch, and getting it wrong does not refuse — the
            // card's own placement will drag the aircraft to its startSpeed/startAlt anyway, so the
            // only visible trace was a violent entry transient on the first replicate.
            //
            // The `cls` field is deliberately NOT consulted here: it is a PILOT-TYPE filter applied per
            // aircraft after spawn (does this card suit what I'm flying?), not a statement about what
            // to spawn. Reading it as one would let "Plane" mean an airframe.
            // Preview() already reports "" / 0 for every field when nothing is selected, so there is
            // no Cards>0 guard to write here: the fallbacks below ARE the no-card case.
            var pre = _plan = ScenarioPlayer.Preview();
            // AFTER the preview, not before: since v0.91 the fleet size is one of the things the card
            // gets to decide, and CountOf reads it off `_plan`. Ordering the other way round is how it
            // was written when the count could only come from Cfg, and it would silently pin every
            // batch to the global again.
            _pending = CountOf(pre);

            WTMouseAimPlugin.Log.LogInfo(
                $"[drone] launching {_pending} x '{string.Join(",", AirframeList())}' (by lane, wrapping) at {SpawnAlt():0} m / "
                + $"{SpeedText(pre)}, {Cfg.DroneStaggerSec.Value:0.#}s apart, lanes {AbeamM:0} m + {LaneM:0} m abeam.");
            // WHO DECIDED, ITEM BY ITEM. This is the operator's ONLY confirmation that the card drove
            // the spawn — "4000 m" alone looks the same whether the card asked for it or the knob just
            // happened to be there, and the difference is exactly what the self-describing card was
            // built to make visible. Printed even with no card, because "no card" is the case where a
            // silent line would be read as "the card is driving it".
            if (pre.Cards == 0)
                WTMouseAimPlugin.Log.LogWarning(
                    "[drone] no card is selected — the drones will fly the built-in level-hold, and the spawn "
                    + "state comes entirely from the Drone settings. Tick one in F1 > 'Scenario Cards'.");
            else
            {
                // Built up front rather than nested inside the interpolation below: an interpolated
                // string INSIDE an interpolation hole is legal C# but breaks check-architecture.py's
                // string stripper, which pairs quotes left to right and then loses its brace depth —
                // the top-level types after this point stop being found. Cheap to avoid, and the
                // flatter line is easier to read anyway.
                string armPart = string.IsNullOrEmpty(pre.ArmKnob)
                               ? "" : $", A/B on '{pre.ArmKnob}' from {pre.ArmSrc}";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[drone] card '{pre.Name}' ({pre.Cards} selected, {pre.Duration:0}s each, x{pre.Repeat} "
                    + $"from {pre.RepeatSrc}{armPart}): "
                    + $"airframe '{AirframeOf(pre)}' [{(string.IsNullOrEmpty(pre.Airframe) ? "DroneAirframe" : "card")}], "
                    + $"{AltOf(pre):0} m [{(pre.StartAlt > 0f ? "card" : "DroneSpawnAlt")}], "
                    + $"{SpeedText(pre)} [{(SpeedFromCard(pre) ? "card" : "DroneSpawnSpeed")}], "
                    + $"{_pending} drone(s) [{pre.CountSrc}].");
            }
        }

        // THE SPAWN STATE, CARD-FIRST. Taken as arguments rather than read off `_plan` so the run
        // board can ask the same three questions of a FRESH preview before the key is pressed — and
        // get, by construction, the answers the launch will use. Three functions with one caller each
        // would be indirection; three with two callers that must never disagree are the point (the
        // launch log and the board are both the operator's confirmation of what will fly).
        internal static string AirframeOf(ScenarioPlayer.Preflight p) =>
            string.IsNullOrEmpty(p.Airframe) ? (Cfg.DroneAirframe.Value ?? "") : p.Airframe;
        internal static float  AltOf(ScenarioPlayer.Preflight p)   => p.StartAlt   > 0f ? p.StartAlt   : Cfg.DroneSpawnAlt.Value;

        // SPEED COMES IN TWO SHAPES SINCE v0.93, and only one of them has a batch-wide answer.
        //   SpeedOf     — no lane in hand. The ABSOLUTE form only; a corner-relative card has no
        //                 single number for the batch by construction, so this deliberately does not
        //                 pretend to one. Its callers (the run board, SpeedText) branch on
        //                 StartSpeedCorner and say "1.00x corner (per airframe)" instead of printing
        //                 a fallback that no lane will be placed at.
        //   SpeedOfLane — the real answer, for the lane about to spawn. Used by the spawn velocity AND
        //                 by the v0.92 envelope gate, which must check the speed the placement will
        //                 later write, not the card's raw number.
        internal static float  SpeedOf(ScenarioPlayer.Preflight p) => p.StartSpeed > 0f ? p.StartSpeed : Cfg.DroneSpawnSpeed.Value;

        internal static float SpeedOfLane(ScenarioPlayer.Preflight p, string jsonKey)
        {
            // The policy itself lives in ScenarioPlayer, next to the placement that has to agree with
            // it — this is the Cfg fallback and nothing else, exactly like AltOf above.
            float v = ScenarioPlayer.ResolveStartSpeed(p.StartSpeed, p.StartSpeedCorner, jsonKey);
            return v > 0f ? v : Cfg.DroneSpawnSpeed.Value;
        }

        // The entry speed as the OPERATOR needs to read it, for the launch log and the run board. A
        // corner-relative card gets the multiple, not a number: printing one number for a per-lane
        // quantity is the misleading half of the only confirmation he gets.
        internal static string SpeedText(ScenarioPlayer.Preflight p) =>
            p.StartSpeedCorner > 0f ? $"{p.StartSpeedCorner:0.00}x corner (per airframe)"
                                    : $"{SpeedOf(p):0} m/s";

        // Did the CARD decide the entry speed, in either of its forms? (The board and the log spell
        // the answer differently, so they share the test and not the wording.)
        internal static bool SpeedFromCard(ScenarioPlayer.Preflight p) =>
            p.StartSpeedCorner > 0f || p.StartSpeed > 0f;
        // Unlike the three above, the Cfg fallback lives in ScenarioPlayer.ResolveCount — because the
        // "as many as the airframe list names" rule needs the CARD, and a Preflight with no card
        // already carries Count 0. So this is a clamp and a no-card guard, not a second policy.
        internal static int    CountOf(ScenarioPlayer.Preflight p) =>
            Mathf.Clamp(p.Count > 0 ? p.Count : Cfg.DroneCount.Value, 1, 16);

        private static float SpawnAlt() => AltOf(_plan);
        // No SpawnSpeed() twin: since v0.93 the entry speed is a per-LANE question (see SpeedOfLane),
        // and a no-argument accessor is precisely how the gate and the placement would end up
        // checking different speeds. LaunchDue resolves it once with the lane's key in hand.

        // =========================================================================================
        // THE FIXED STEP. Called from WTMouseAimPlugin.FixedUpdate — a real fixed-step hook that
        // exists whether or not any drone is alive, which the per-pilot postfix does not (with zero
        // drones there is no pilot of ours for it to fire on). Deliberately NOT a coroutine: the
        // stagger has to be counted on the same clock the run is measured on.
        // =========================================================================================
        public static void FixedTick()
        {
            if (_pending > 0) LaunchDue();
            if (_live.Count > 0) PruneDead();
        }

        private static float _hitchArmed;   // Time.time the current hitch was first reported (edge gate)

        // CALLED FROM Update(), NOT FixedTick — and that is the whole content of the v0.92.1 fix.
        //
        // `Time.unscaledDeltaTime` returns the RENDERED-frame delta only when read from a per-frame
        // callback. Read from inside FixedUpdate, Unity substitutes `fixedUnscaledDeltaTime`, which is
        // a CONSTANT — so from v0.86 (when the column was added) to v0.92 this sampled the fixed step
        // and called it frame time.
        //
        // Measured on R27, which is why this is not a theoretical tidy-up: `frameMs` read exactly
        // 16.70 ms on all 223,899 rows of a 352-capture batch. One distinct value, zero variance, on
        // the column whose entire purpose is showing that a frame hitch landed on one replicate's
        // segment and not another's. It also MISSED a 119 ms hitch that the log caught while four
        // recorders were open and sampling through it — the value only ever moved when Unity's
        // catch-up machinery engaged, i.e. it was a coarse stall flag masquerading as a budget meter.
        // The direct cost: the harness could not answer "is there frame headroom for more drones?",
        // which is the question the stagger and this column exist to make answerable.
        //
        // The hitch WARNING is unaffected in kind but becomes far more sensitive here, since it now
        // sees the actual frame times rather than only the ones big enough to distort the fixed clock.
        internal static void SampleFrameTime()
        {
            float dt = Time.unscaledDeltaTime;
            bool rising = dt >= HitchSec && FrameDt < HitchSec;
            FrameDt = dt;   // sampled ALWAYS, harness on or off — it is a recorder signal, not a drone signal
            // The LOG, though, is gated: an installer who never touches the harness should not find
            // warnings about his frame rate in a mouse-aim mod's log.
            if (rising && Cfg.DroneEnabled.Value && Time.time - _hitchArmed > 0.25f)
            {
                _hitchArmed = Time.time;
                WTMouseAimPlugin.Log.LogWarning($"[drone] frame hitch: {dt * 1000f:0} ms (fixed step {Time.fixedDeltaTime * 1000f:0} ms)");
            }
        }

        private static void LaunchDue()
        {
            if (Time.time < _nextAt) return;
            _nextAt = Time.time + Mathf.Max(0f, Cfg.DroneStaggerSec.Value);
            _pending--;

            string key = AirframeForLane(_slot);
            Vector3 pos = _laneBase + _laneRight * (AbeamM + LaneM * _slot);
            // DroneSpawnAlt is MSL in the DATUM frame — the same frame `Aircraft.GlobalPosition().y`
            // and every card's startAlt are expressed in. Round-tripping a global y through
            // ToLocalPosition converts it without this file needing to know whether the floating
            // origin shifts y at all (ScenarioPlayer's placement dodges the same question).
            pos.y = new GlobalPosition(0f, SpawnAlt(), 0f).ToLocalPosition().y;
            _slot++;

            // THE LANE'S OWN ENTRY SPEED (v0.93). Resolved once, here, and used by the gate and the
            // spawn velocity both — with a corner-relative card these differ per lane, and asking
            // twice (or asking the batch-wide SpeedOf) is how a lane gets gated on one speed and
            // placed at another.
            float laneSpeed = SpeedOfLane(_plan, key);

            // FEASIBILITY BEFORE THE SPAWN (v0.92). Refusing here rather than after registering the
            // aircraft is the entire value of the check: an unflyable lane then costs nothing and,
            // more to the point, never writes a capture. `null` flows into the SAME skip-or-cancel
            // decision an unknown jsonKey takes below — one policy for "this lane will not happen",
            // not a second one bolted alongside it. Still live under v0.93: a corner multiple is not
            // automatically flyable (2.0x corner is over Vmax on most of the roster), it is just no
            // longer a number chosen without reference to the airframe.
            var d = EntrySpeedFlyable(key, laneSpeed)
                  ? Spawn(key, pos, _laneRot, _laneRot * Vector3.forward * laneSpeed)
                  : null;
            if (d == null)
            {
                // With ONE airframe the next lane fails identically (no server, bad jsonKey, no
                // Spawner), so drop the rest instead of printing the same warning once per drone.
                // With a LIST the batch is heterogeneous and that inference is wrong — a single bad
                // jsonKey must cost its own lane and nothing else — so skip and carry on. Either way
                // the refusal is a log line, never a silent no-op.
                if (AirframeList().Length > 1)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[drone] lane {_slot - 1} ('{key}') was refused — skipping it; {_pending} lane(s) still to launch.");
                    return;
                }
                _pending = 0;
                WTMouseAimPlugin.Log.LogWarning("[drone] launch aborted — the remaining drones in this request were cancelled.");
            }
        }

        // PER-DRONE AIRFRAME (v0.86). `Cfg.DroneAirframe` is a COMMA LIST of Encyclopedia jsonKeys,
        // indexed by lane and wrapped — so a single value (every config file that exists today) still
        // puts that one airframe in every lane, byte-identical to v0.85, and "FS12, KR67" flies one of
        // each. Deliberately no new config key and no matrix scheme: `Spawn` already takes the airframe
        // per call, so this is the whole change.
        // ponytail: airframe only. LOADOUT is still `null` at the Spawn call — the game's loadout
        // parameter is a `Loadout` object, not a name, and nobody should write that against a guessed
        // shape. When the API is known, the lane index is the hook: give Drone a per-lane loadout the
        // same way, and the .airframe.json sidecar already records the resulting stations/masses/drag
        // per capture, so the analysis side needs no change.
        // v0.90: a card that names its airframe overrides the whole list, not one lane — the card is
        // one test, and a batch flying it on a mix of airframes is not replicates of anything
        // (compare-runs.py refuses to pool across jsonKeys for exactly that reason). A heterogeneous
        // batch is still available: leave `airframe` out of the card and use the Cfg list.
        // Between markers: debugtests/test-fleet-resolve.py extracts these two verbatim and checks
        // them against ScenarioPlayer.CountKeys, which must count exactly the tokens this splits —
        // they are a deliberate count-only / assignment pair, and a disagreement means the fleet size
        // and the lane assignment come from two different readings of one string.
        // --- FLEET-RESOLVE BEGIN ---
        private static string[] AirframeList()
        {
            // One path, not two: AirframeOf already picks the card's key over the list, and a jsonKey
            // never contains a comma — so splitting a card's single key just yields that key.
            var parts = AirframeOf(_plan).Split(',');
            int n = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length > 0) parts[n++] = s;
            }
            if (n == 0) return new[] { "" };   // Spawn refuses an empty key with its own log line
            System.Array.Resize(ref parts, n);
            return parts;
        }

        private static string AirframeForLane(int slot)
        {
            var list = AirframeList();
            return list[slot % list.Length];
        }
        // --- FLEET-RESOLVE END ---

        // =========================================================================================
        // CAN THIS AIRFRAME HOLD THE ENTRY CONDITION AT ALL? (v0.92)
        //
        // A card declares ONE entry speed and a batch may name ten airframes. Nothing refuses the
        // mismatch today: the lane spawns, the placement writes a speed the aircraft cannot hold,
        // and the capture measures the decay back to whatever it CAN hold — which scores perfectly
        // well and answers a different question. That is the failure mode v0.90/v0.91 removed from
        // the airframe/alt/speed knobs, one level down: not a wrong value but an IMPOSSIBLE one.
        // The shipped grid has three of them by construction (AIRFRAMES.md): every `oblique-*`,
        // `sweep-*` and `e1`-`e3` card asks for 250 m/s, and `CAS1` tops out at 205.6, `COIN` at
        // 141.7, the rotorcraft lower still.
        //
        // Asked BEFORE the spawn, off `Encyclopedia.Lookup` (a public static
        // Dictionary<string, UnitDefinition> filled by `Encyclopedia.AfterLoad`, decompile :9715) —
        // an aircraft instance would defeat the point, which is to refuse without creating a unit.
        // =========================================================================================

        // What one airframe's definition publishes about its envelope. Nested rather than a
        // top-level type because it is the return shape of the one function below, not a subsystem.
        internal struct Envelope
        {
            public float VStall;   // m/s
            public float VMax;     // m/s
            public float Corner;   // m/s — the speed the devs' own table calls its manoeuvring point
            public float GLimit;   // g
        }

        // FAIL-SOFT, exactly like the FBW/canard/helo probes in ChaseController: `false` means "we
        // could not read it", NEVER "the bounds are zero". A zero Vmax taken as a real bound would
        // refuse every lane on an airframe we merely failed to look up, and a probe that cancels a
        // batch because a field was missing is worse than no probe at all. The out-value is
        // untouched on false by construction, so there is no zero for a caller to mistake for data.
        // The FBW's own corner speed, read off the PREFAB — no aircraft instance, same as everything
        // else in this block. `ControlsFilter.GetFlyByWireParameters()` is public (:65521) and packs
        // cornerSpeed at index 2 (`FlyByWire.GetParameters()`, :64786), so this needs no reflection —
        // it is the same public accessor the v0.55 FBW probe in ChaseController already reads
        // `_fbwCorner` out of, just asked of a prefab instead of a flying aircraft. `GetComponentInChildren`
        // with `includeInactive` because a prefab's hierarchy is inactive and `HeloControlsFilter`
        // (:35847) derives from `ControlsFilter`, so rotorcraft resolve through the same call.
        //
        // FAIL-SOFT, and NaN is the sentinel on purpose: 0 is a speed and would silently become an
        // entry condition. Cached per jsonKey — both to keep this off the per-lane launch path and
        // because the cache IS the once-per-airframe warning: the value (sentinel included) is
        // computed exactly once, so the log line cannot repeat.
        private static readonly Dictionary<string, float> _fbwCornerByKey = new Dictionary<string, float>();

        private static float FbwCornerSpeed(string jsonKey)
        {
            if (_fbwCornerByKey.TryGetValue(jsonKey ?? "", out float cached)) return cached;
            float v = float.NaN;
            try
            {
                if (Encyclopedia.i != null && Encyclopedia.i.TryGetPrefab(jsonKey ?? "", out var prefab) && prefab != null)
                {
                    var cf = prefab.GetComponentInChildren<ControlsFilter>(true);
                    if (cf != null)
                    {
                        var (_, p) = cf.GetFlyByWireParameters();
                        if (p != null && p.Length > 2 && p[2] > 0f) v = p[2];
                    }
                }
            }
            catch { /* fall through to the sentinel — never throw into the launch path */ }
            if (float.IsNaN(v))
                WTMouseAimPlugin.Log.LogWarning(
                    $"[drone] '{jsonKey}': could not read ControlsFilter.FlyByWire.cornerSpeed off the prefab — "
                    + "falling back to the encyclopedia's AI cornerSpeed, which the flight model does not use. "
                    + "A startSpeedCorner card will enter THIS lane at a different aerodynamic state than the rest "
                    + "of the fleet; pin an absolute startSpeed if that matters.");
            _fbwCornerByKey[jsonKey ?? ""] = v;
            return v;
        }

        internal static bool TryEnvelope(string jsonKey, out Envelope e)
        {
            e = default(Envelope);
            try
            {
                // The SAME readiness test the spawn uses, not a second one: `Lookup` is populated by
                // `Encyclopedia.AfterLoad`, the loader callback that also makes `i` non-null, so one
                // question answers both. (The null check on the dictionary itself is the cheap
                // belt — a static field the game clears is not ours to assume about.)
                if (Encyclopedia.i == null || Encyclopedia.Lookup == null) return false;
                if (!Encyclopedia.Lookup.TryGetValue(jsonKey ?? "", out var ud)) return false;
                var ad = ud as AircraftDefinition;
                if (ad == null || ad.aircraftInfo == null || ad.aircraftParameters == null) return false;

                // KM/H -> M/S. `aircraftInfo` is the encyclopedia's display block and is in km/h at
                // every use site in the game (:2583, :10258-10259); `aircraftParameters` is already
                // m/s. Mixing the two is a silent factor of 3.6.
                e.VStall = ad.aircraftInfo.stallSpeed / 3.6f;   // :62791
                e.VMax   = ad.aircraftInfo.maxSpeed   / 3.6f;   // :62789
                // NOT `aircraftParameters.maxSpeed`, which is the obvious field and the wrong one:
                // it is a NORMALIZER (`aircraft.speed / maxSpeed` at :15554, :15919, :70152) reading
                // a flat 600 for every fast jet, so a check built on it concludes that the 141 m/s
                // Cricket can do 250. The two agree only for rotorcraft.
                // CORNER SPEED: the FLIGHT MODEL's, not the AI's. There are TWO `cornerSpeed` fields
                // and until v0.96 this read the wrong one. `aircraftParameters.cornerSpeed` (:62924)
                // is consumed only by the AI — throttle policy (:12993), glideslope (:13624), effort
                // scaling (:15773) — while the thing that actually shapes the stick is
                // `ControlsFilter.FlyByWire.cornerSpeed` (:64704): it is the pitch-rate demand's
                // saturation speed (`targetPitchAngVel = pitch * gLimitPositive * 9.81 /
                // max(speed, cornerSpeed * 0.75)`, :64859) and the G-limit knee (:64672). Measured
                // over the whole capture corpus (1604 sidecars, both numbers already recorded) they
                // differ by 0.556x (Darkreach 100 vs 180) to 1.417x (AttackHelo1 170 vs 120) — so a
                // `startSpeedCorner` card, whose entire claim is "every lane enters at the same
                // aerodynamic state", was spreading the fleet over 2.2x of true FBW corner. Falls
                // back to the AI value, never to zero.
                float fbwCorner = FbwCornerSpeed(jsonKey);
                e.Corner = float.IsNaN(fbwCorner) ? ad.aircraftParameters.cornerSpeed : fbwCorner;
                e.GLimit = ad.aircraftParameters.aircraftGLimit; // :62910
                // A definition that publishes no Vmax has nothing to check against; report unknown
                // rather than hand back a ceiling of zero that would refuse everything.
                return e.VMax > 0f;
            }
            catch { return false; }   // never throw into the launch path
        }

        // THE MARGINS, and why these rather than the round numbers.
        //   Floor 1.10x Vstall — not "can it stay airborne" but "can it manoeuvre": 1.10 Vs leaves
        //     1.21 g of load before the stall, i.e. ~34 deg of sustained bank, the least that lets a
        //     card measure a control law instead of a stall. The obvious 1.20 was rejected on the
        //     SHIPPED grid: its tightest legitimate pairing is `stol-*` at 90 m/s on `SmallFighter1`
        //     (Vstall exactly 75.0), a ratio of exactly 1.200 — so a 1.2 floor would decide a card
        //     AIRFRAMES.md calls flyable by the float rounding of `stallSpeed / 3.6`. 1.10 gives
        //     that pairing 9% of clearance and still refuses anything genuinely near the stall.
        //   Ceiling 0.95x Vmax — an airframe pinned at Vmax has no energy left to manoeuvre with,
        //     and cannot HOLD the condition either: the placement writes the speed once and thrust
        //     alone has to keep it. The 250 m/s family clears this on every airframe that can fly it
        //     at all (tightest: `Darkreach` at 0.895) and fails it on exactly the three AIRFRAMES.md
        //     names — `CAS1` (0.95 x 205.6 = 195.3), `COIN` (134.6), and all three rotorcraft.
        // ponytail: speed only. Altitude has no per-airframe bound to check against — there is no
        // service ceiling anywhere in the decompile (AIRFRAMES.md trap 5), and `maxEditorHeight` is
        // an editor placement limit, not a physics one.
        //
        // Between markers because debugtests/test-fleet-resolve.py compiles these two verbatim and
        // asserts the documented roster pairings against them — in particular that `stol-*` at
        // 90 m/s on `SmallFighter1` (Vstall exactly 75.0) clears the floor with room to spare, which
        // is the whole reason the floor is not 1.20. A Python copy of the numbers would agree with
        // itself after someone "rounded them up".
        // --- ENTRY-MARGINS BEGIN ---
        private const float StallMargin = 1.10f;
        private const float VMaxMargin  = 0.95f;
        // --- ENTRY-MARGINS END ---

        // Returns false having ALREADY logged the reason, so the caller only has to skip the lane —
        // the same division of labour as the unknown-jsonKey refusal inside `Spawn`. An UNKNOWN
        // envelope never refuses.
        private static bool EntrySpeedFlyable(string jsonKey, float speed)
        {
            if (!TryEnvelope(jsonKey, out var e)) return true;

            float lo = e.VStall * StallMargin, hi = e.VMax * VMaxMargin;
            // Built up front rather than nested in the interpolation below — an interpolated string
            // inside an interpolation hole is legal C# but breaks check-architecture.py's string
            // stripper (see the same note in RequestLaunch).
            string bound = speed < lo ? $"below the {StallMargin:0.00}x Vstall floor of {lo:0.#} m/s (Vstall {e.VStall:0.#})"
                         : speed > hi ? $"above the {VMaxMargin:0.00}x Vmax ceiling of {hi:0.#} m/s (Vmax {e.VMax:0.#})"
                         : null;
            if (bound == null) return true;

            WTMouseAimPlugin.Log.LogWarning(
                $"[drone] refused: '{jsonKey}' cannot fly the {speed:0.#} m/s entry condition — it is {bound}; "
                + $"FBW corner {e.Corner:0.#} m/s, {e.GLimit:0.#} g. Give this airframe its OWN card — a slower "
                + "startSpeed on the shared one re-bands every other lane at once.");
            return false;
        }

        // An aircraft can leave without us: it can be shot down, fly into the sea, or be cleaned up
        // by the mission. Unity reports a destroyed object as `null` WITHOUT throwing, so a stale
        // dictionary entry never announces itself — it just keeps a recycled instance id mapped to a
        // corpse. Prune every fixed step; the list is a handful of entries.
        private static void PruneDead()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var d = _live[i];
                if (d.Aircraft == null || d.Aircraft.disabled)
                {
                    WTMouseAimPlugin.Log.LogInfo($"[drone] #{d.Id} is gone (destroyed or disabled by the game) — deregistered.");
                    _live.RemoveAt(i);
                    _byAircraftId.Remove(d.AircraftId);
                    ForgetState(d.AircraftId);
                    continue;
                }

                // AUTO-DESPAWN WHEN THE CARD IS DONE (v0.90). ONE rule — "not flying a card" — covers
                // suite complete, aborted, refused, and never-started, so there is no path that leaves
                // an aircraft circling unattended. That matters beyond tidiness: a live drone keeps a
                // full complex-physics aero job and all three of its per-aircraft registries alive, and
                // that is the same frame budget the launch stagger exists to protect, measured by the
                // very `frameMs` column added to detect it.
                //
                // The grace window is not politeness — it is the gap between NextCard closing one
                // recorder and StartCard opening the next, during which `Playing` is legitimately
                // false mid-suite. Anything shorter than a placement tick would despawn a drone
                // between its own replicates.
                if (!d.CardStarted) continue;                 // pre-first-pilot-step; the stagger owns this window

                bool playing = false;
                try { playing = ScenarioPlayer.For(d.Aircraft).Playing; }
                catch { /* fail-soft: a throwing lookup reads as "not playing" and the grace timer runs */ }

                if (playing) { d.IdleSince = -1f; continue; }
                if (d.IdleSince < 0f) { d.IdleSince = Time.time; continue; }
                if (Time.time - d.IdleSince < IdleDespawnSec) continue;
                Despawn(d, "card finished");
            }
        }

        // EVERY per-aircraft subsystem the mod keeps, dropped in one place. Called from BOTH removal
        // paths (deliberate despawn and the prune of a drone the game took from us) and keyed by the
        // CACHED id for the same reason the dictionary is: the aircraft may already be destroyed.
        // One function, not three call sites duplicated twice — the next per-aircraft registry gets
        // added here and cannot be forgotten on one of the two paths (which is how a StreamWriter
        // survives its aircraft and a capture ends with no '# stop' line, reading as a clean run).
        // The card goes first: aborting it is what closes the recorder cleanly with a reason.
        private static void ForgetState(int aircraftId)
        {
            ScenarioPlayer.Forget(aircraftId);      // v0.86: card playback state
            ManeuverRecorder.Forget(aircraftId);    // v0.86: closes an open capture
            ChaseController.Forget(aircraftId);     // v0.82: integrators / filters / probes
            // v0.94: the A/B arm assignment, which deliberately SURVIVES Forget (the per-replicate
            // reset drops the controller every run and must not un-sweep the experiment), so despawn
            // is one of only two places that clears it — the other being the suite's own Finish.
            // Without this line a recycled instance id would inherit a dead batch's arm.
            ChaseController.SetArm(aircraftId, null, false);
        }

        // =========================================================================================
        // SPAWN / DESPAWN
        // =========================================================================================
        // Spawn ONE uncrewed aircraft. Returns null on any failure, having spawned nothing — a
        // half-spawned drone (registered but unflyable, or flying but unregistered) is worse than
        // none, so every failure path either happens before the Instantiate or destroys what it made.
        //
        // The call shape is copied from the encyclopedia browser's throwaway spawn (the game's own
        // "an aircraft nobody owns" call): player=null, loadout=null, livery=default, hangar=null,
        // HQ=null, skill=0, bravery=0. We differ in exactly two places, both deliberate:
        //   * fuelLevel 1.0, not 0 — a museum exhibit does not need to fly and this does. Full tank
        //     also pins mass the same way ScenarioEntryFuel does for a crewed card.
        //   * a unique name per drone, so `UnitRegistry.RegisterCustomID` does not log "2 different
        //     units had the same name".
        //
        // HQ = null IS THE AI SWITCH. `Pilot_OnInitialize` calls `SetStartingAiState()` for a
        // playerless aircraft on the server, and that method's first act is:
        //     if (aircraft.NetworkHQ == null) { SwitchState(parkedState); return; }
        // so with no HQ the AI states are never even constructed — there is no combat brain to fight
        // us for the stick. `PilotParkedState.FixedUpdateState` is empty, so the parked state costs
        // nothing; its `EnterState` sets throttle=0/brake=1 but ONLY below 1 m radar altitude, which
        // is why this spawns airborne. The explicit SwitchState(null) below is belt and braces.
        //
        // NOT the player-facing chain (`CmdRequestSpawnAircraft` / `Airbase.TrySpawnAircraft` /
        // `Hangar.*`): that is a rate-limited [ServerRpc] needing a real Player and a free hangar.
        public static Drone Spawn(string jsonKey, Vector3 worldPos, Quaternion rot, Vector3 velocity)
        {
            if (!Cfg.DroneEnabled.Value)
            {
                WTMouseAimPlugin.Log.LogWarning("[drone] refused: Drone/DroneEnabled is off (F1 > Drone).");
                return null;
            }

            GameObject go = null;
            Aircraft ac = null;
            try
            {
                var sp = NetworkSceneSingleton<Spawner>.i;
                if (sp == null)
                {
                    WTMouseAimPlugin.Log.LogWarning("[drone] refused: no Spawner in the scene (not in a mission?).");
                    return null;
                }
                // THE SERVER GATE, asked of the exact object that will enforce it. `SpawnAircraft`
                // carries no [Server] attribute, so nothing stops the call — but its last act is
                // `ServerObjectManager.Spawn`, which needs an active server. Asking the Spawner's own
                // NetworkBehaviour is the same question ServerObjectManager will ask, so a clean
                // refusal here can never disagree with reality.
                if (!sp.IsServer)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        "[drone] refused: no active server. The harness works in single player and while "
                        + "HOSTING (single player is a host), but never as a multiplayer client.");
                    return null;
                }
                if (Encyclopedia.i == null)
                {
                    WTMouseAimPlugin.Log.LogWarning("[drone] refused: the Encyclopedia has not loaded yet.");
                    return null;
                }
                // TryGetPrefab already writes its own Debug.LogError naming the missing key, so this
                // only has to say which knob to fix.
                if (!Encyclopedia.i.TryGetPrefab(jsonKey, out var prefab) || prefab == null)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[drone] refused: no aircraft prefab for jsonKey '{jsonKey}' — check Drone/DroneAirframe.");
                    return null;
                }

                int id = _nextId++;
                ac = sp.SpawnAircraft(null, prefab, null, 1f, default(LiveryKey), worldPos.ToGlobalPosition(),
                                      rot, velocity, null, null, "wtm-drone-" + id, 0f, 0f);
                if (ac == null)
                {
                    WTMouseAimPlugin.Log.LogWarning("[drone] refused: SpawnAircraft returned nothing.");
                    return null;
                }
                go = ac.gameObject;

                // THE ONE CHECK THAT MUST NEVER BE SKIPPED. The postfix writes ControlInputs for
                // anything in the dictionary, so a player aircraft finding its way in there would
                // have the harness flying the human. A drone is spawned with player=null by
                // construction; verify it rather than trust it, and destroy it if the answer surprises
                // us. `Aircraft.Player` is the same property `CheckIfLocalSim` reads, so it is the
                // game's own definition of "someone is sitting in this", not our approximation of it.
                if (ac.Player != null)
                {
                    WTMouseAimPlugin.Log.LogError("[drone] spawned aircraft reports a Player — refusing to register it and destroying it.");
                    Object.Destroy(go);
                    return null;
                }

                // Belt and braces on top of the HQ=null path above: no pilot state at all. The call
                // site is null-safe (`currentState?.FixedUpdateState(this)`), so this is a legal
                // state, not a hack.
                try
                {
                    if (ac.pilots != null && ac.pilots.Length > 0 && ac.pilots[0] != null)
                        ac.pilots[0].SwitchState(null);
                }
                catch (System.Exception e)
                {
                    WTMouseAimPlugin.Log.LogWarning($"[drone] could not clear the pilot state ({e.GetType().Name}) — the parked state stays, which is harmless.");
                }

                var d = new Drone(id, ac);
                _live.Add(d);
                _byAircraftId[d.AircraftId] = d;
                WTMouseAimPlugin.Log.LogInfo(
                    $"[drone] #{id} '{jsonKey}' spawned at ({worldPos.x:0}, {worldPos.y:0}, {worldPos.z:0}) local / "
                    + $"{d.HoldAlt:0} m MSL, {velocity.magnitude:0} m/s, hdg {rot.eulerAngles.y:0}deg, "
                    // Crew count is the airframe property that broke R26: every seat fires the pilot
                    // postfix, so a two-seater double-stepped the card clock AND the control law until
                    // the guard in OnPilotStep. It is prefab data with no code-side definition (there
                    // is no `crew` anywhere in the decompile), so the log line is the only place an
                    // operator can learn that `trainer` has two seats and `Fighter1` has one.
                    + $"{(ac.pilots != null ? ac.pilots.Length : 0)} crew. {_live.Count} live.");
                return d;
            }
            catch (System.Exception e)
            {
                // The probe contract: never throw into the game loop, and never leave a half-spawn
                // flying around unowned. Anything already instantiated is destroyed outright (not via
                // DisableUnit — it was never registered with us and may not be fully initialised).
                WTMouseAimPlugin.Log.LogWarning($"[drone] spawn failed ({e.GetType().Name}: {e.Message}) — nothing was left behind.");
                try { if (go != null) Object.Destroy(go); } catch { /* nothing else to try */ }
                return null;
            }
        }

        // Idempotent: a drone already gone, already removed, or null is a no-op.
        //
        // The removal path is the game's own (`RemoveUnitOutcome.RemoveUnit`): DisableUnit, then a
        // deferred Destroy 2 s later so the network layer and the wreck logic see the disable first.
        // `Unit.OnDestroy` unregisters from `UnitRegistry` and the spatial grid by itself.
        //
        // Two things to know rather than fix:
        //   * `Aircraft.ServerDisableUnit` calls `ReportKilled()` unless the aircraft is landed at a
        //     friendly airbase, so despawning posts a kill message to the HUD. Cosmetic.
        //   * `UnitRegistry.persistentUnitLookup` is NEVER pruned by the game, so every spawn leaks
        //     one dictionary entry for the life of the mission. A few hundred entries is nothing;
        //     do NOT reach into that dictionary to "fix" it — the game reads it from several places.
        public static void Despawn(Drone d, string reason = "requested")
        {
            if (d == null) return;
            _live.Remove(d);
            _byAircraftId.Remove(d.AircraftId);
            ForgetState(d.AircraftId);
            var ac = d.Aircraft;
            if (ac == null) return;
            try
            {
                ac.DisableUnit();
                Object.Destroy(ac.gameObject, 2f);
                WTMouseAimPlugin.Log.LogInfo($"[drone] #{d.Id} despawned ({reason}). {_live.Count} live.");
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[drone] #{d.Id} despawn failed ({e.GetType().Name}: {e.Message}) — dropped from the registry anyway.");
            }
        }

        // Idempotent, and it also cancels a launch still in progress — otherwise the panic key would
        // clear the sky and then watch the stagger refill it.
        public static void DespawnAll()
        {
            if (_pending > 0)
            {
                WTMouseAimPlugin.Log.LogInfo($"[drone] cancelling {_pending} pending launch(es).");
                _pending = 0;
            }
            for (int i = _live.Count - 1; i >= 0; i--) Despawn(_live[i]);
        }

        // =========================================================================================
        // THE PER-DRONE WRITE. Called from the postfix, once per fixed step per pilot.
        // =========================================================================================
        internal static void OnPilotStep(Pilot p)
        {
            if (_byAircraftId.Count == 0 || p == null) return;
            var ac = p.aircraft;
            if (ac == null) return;
            if (!_byAircraftId.TryGetValue(ac.GetInstanceID(), out var d)) return;   // every other aircraft, including the player's

            // A DRONE WHOSE PILOT DIED IS NOT A TEST ARTICLE ANY MORE, AND `PruneDead` CANNOT SEE IT.
            // Its predicate is `Aircraft == null || Aircraft.disabled`, and the game never
            // self-disables an Aircraft on damage: `Unit.disabled` is written only by
            // ServerDisableUnit / ReturnToInventory / OnDestroy, and `WaitRemoveAircraft` is fired
            // FROM the disabled hook — so a shot-down drone keeps a live GameObject with
            // `disabled == false` indefinitely. That is why one stayed registered until the mission
            // quit in R25. Catch it HERE, the one place holding the `Pilot` the damage lands on.
            // (An airframe destroyed without killing the pilot is covered too, one layer out: the
            // card's own altitude floor aborts it on the way down, and the idle rule then despawns.)
            // The dead/ejected check was an early-return before v0.90 — it must stay ahead of every
            // write below, since the original method early-returns on both and only the postfix runs.
            if (p.dead || p.ejected || ac.disabled)
            {
                Despawn(d, p.ejected ? "pilot ejected" : p.dead ? "pilot killed" : "disabled by the game");
                return;
            }

            // ONCE PER AIRCRAFT PER FIXED STEP — NOT once per PILOT. `Aircraft.pilots` is an ARRAY
            // (Aircraft:60288 in the 0.34 decompile), every `Pilot` registers itself with `JobManager`
            // in its own Awake (Pilot:85535), and `JobManager.PilotAeroInputs` walks that flat list
            // calling `Pilot_OnAeroInputsApplied` on each one (:168794). So a TWO-SEAT airframe runs
            // this postfix TWICE per fixed step, and everything below it twice with it.
            //
            // Measured in R26, and it is not a cosmetic doubling: `trainer` and `FastBomber1` (two
            // seats) flew a 6 s segment in 2.97 s and a 30 s segment in 14.95 s, against 5.97/29.95
            // for the single-seat `Fighter1`/`Multirole1` — a 2x-rate stimulus. Worse, the control law
            // was double-stepped inside one physics step: integrators and rate filters advanced twice
            // per dt, and every finite difference taken against a cached previous attitude (rollRate =
            // (t.up - _prevUp)/dt) read ZERO on the second call, because nothing had moved. The two
            // airframes' captures are not comparable to the other two and were not measuring the law.
            //
            // The stamp, not the game's own `aircraft.pilots[0] == p` identity idiom (:85536, :85645):
            // a pilot that dies returns PartResult.Remove and is dropped from JobManager's list
            // (:168804), so keying on pilot 0 would silently stop ticking a drone whose front-seater
            // was killed — and it would never reach the despawn above either, since that check sits on
            // the INVOKING pilot. The stamp keeps flying on whichever crew member is still alive, and
            // leaving the death check upstream of it means ANY seat's death still despawns.
            if (d.LastStep == Time.fixedTime) return;
            d.LastStep = Time.fixedTime;

            try
            {
                var sp = ScenarioPlayer.For(ac);

                // START THE CARD ON THIS DRONE'S FIRST PILOT STEP (v0.87), not at Spawn. By the time
                // the game gives an aircraft its own fixed step it is fully constructed, and the card's
                // first act is a PLACEMENT that rigid-moves every part rigidbody
                // (ScenarioPlayer.PlaceOnCondition) — not a thing to do to a half-built assembly.
                // Per drone, at ITS OWN spawn instant, which is what preserves the launch stagger: one
                // key that started N cards together would put every replicate on the same segment
                // boundary, precisely what the stagger exists to prevent.
                // StartSuite is the same body the player's run key calls (no second copy to drift), and
                // it refuses with its own [card] log line when no card is enabled for this airframe
                // class — the drone then just level-holds, which is the phase-1 behaviour.
                // ponytail: fire-and-forget, one attempt. A drone whose suite finishes despawns itself
                // `IdleDespawnSec` later — see PruneDead, which owns that clock because it is the one
                // thing that runs every fixed step regardless of which `Fly` delegate is installed.
                if (!d.CardStarted)
                {
                    d.CardStarted = true;
                    sp.StartSuite(ac);
                }

                // TEST-CARD DEMAND (v0.86) — THIS drone's card, ticked HERE so it gets the same
                // zero-tick property the player's card gets from the seam prefix: the demand for this
                // fixed step is written immediately before Fly reads it, inside the same
                // Pilot_OnAeroInputsApplied invocation. A card ticked from FixedUpdate instead would
                // sit on an unspecified side of JobManager.FixedUpdateEarly's ScheduleJobs, i.e. a
                // frame-rate-dependent zero-order hold between the stimulus and the response — which
                // is exactly the coupling the harness exists to remove. No-op (a dict probe and a
                // null check) when this drone is not flying a card.
                sp.Tick(ac);
                var fly = d.Fly;
                if (fly == null || !fly(d)) return;     // the controller declined to command this tick

                // THROTTLE/BRAKE, exactly as the player's seam postfix does it immediately after
                // Apply (v0.87). A card owns the throttle; without this a drone would fly the whole
                // card at whatever ControlInputs.throttle happened to hold — and 0 is the game's
                // airbrake trigger (Airbrake.Update), which is how R18 read a bad throttle as a
                // control-law energy failure. No-op when this drone is not flying a card.
                sp.OwnInputs(ac);

                // FBW IS NOT AUTOMATIC. `Aircraft.FilterInputs()` — which runs the
                // RelaxedStabilityController and then ControlsFilter/FlyByWire — is called ONLY from
                // pilot states, and this aircraft has none. Raw ControlInputs would therefore reach
                // the control surfaces unfiltered, which is a DIFFERENT plant from the one the mod's
                // law was tuned against (the FBW reads pitch/yaw as a commanded angular RATE). Call
                // it ourselves, here, immediately after the write and before the next tick's aero job.
                ac.FilterInputs();
            }
            catch (System.Exception e)
            {
                // A throwing controller must not take the game's pilot loop with it. Cut this drone
                // loose rather than repeat the exception 50 times a second.
                WTMouseAimPlugin.Log.LogWarning($"[drone] #{d.Id} controller threw ({e.GetType().Name}: {e.Message}) — despawning it.");
                Despawn(d);
            }
        }

        // =========================================================================================
        // THE BUILT-IN LEVEL-HOLD — DELIBERATELY TRIVIAL, AND NOT THE MOD'S CONTROL LAW.
        //
        // This is a two-gain cascade that holds wings level at the spawn altitude. It shares nothing
        // with `ChaseController` — no probes, no schedules, no achievability cap, no AoA envelope —
        // and it must never be mistaken for it or compared against it. Its ONLY job is to answer
        // "did the inputs land, and is the physics real?" with an aircraft that visibly holds
        // altitude instead of falling out of the sky.
        //
        // ponytail: pure P on both axes, no rate damping of our own, no speed hold. The game's FBW
        // already provides rate damping (that is why FilterInputs is called above). v0.87 keeps it
        // for the ONE job it still has — an idle drone with no card has no demand to chase, and an
        // aircraft nobody is flying falls into the sea. Never tune it, and never compare a level-hold
        // capture against a card capture: they are not the same controller.
        // =========================================================================================
        private const float HoldThrottle = 0.6f;   // fixed position, not a speed hold — one loop, not two
        private const float VsPerAltErr  = 0.05f;  // m/s of commanded climb per metre of altitude error
        private const float VsMax        = 25f;    // m/s cap on that command
        private const float PitchPerVs   = 0.03f;  // stick per m/s of climb-rate error
        private const float RollGain     = 2.0f;   // stick per unit of t.right.y

        internal static bool LevelHold(Drone d)
        {
            var ac = d.Aircraft;
            var rb = ac != null ? ac.rb : null;
            var ci = ac != null ? ac.GetInputs() : null;
            if (rb == null || ci == null) return false;
            var t = ac.transform;

            // ROLL. Sign table (CLAUDE.md): `t.right.y` < 0 = right wing DOWN, positive `ci.roll` =
            // roll right. Right wing UP is a left bank, corrected by rolling right — so the error
            // feeds through with a plus sign and there is no case analysis to get backwards.
            ci.roll = Mathf.Clamp(RollGain * t.right.y, -1f, 1f);

            // PITCH. Altitude error -> commanded climb rate -> stick. Nose-up is NEGATIVE ci.pitch.
            float vsWant = Mathf.Clamp((d.HoldAlt - ac.GlobalPosition().y) * VsPerAltErr, -VsMax, VsMax);
            ci.pitch = Mathf.Clamp(-PitchPerVs * (vsWant - rb.velocity.y), -1f, 1f);

            ci.yaw      = 0f;
            ci.throttle = HoldThrottle;   // exact 0 is the game's airbrake trigger (Airbrake.Update), so never write it
            ci.brake    = 0f;
            return true;
        }

        // =========================================================================================
        // THE MOD'S CONTROL LAW, ON A DRONE (v0.87 — phase 2, and the whole point of the harness).
        //
        // A drone flying a card chases ITS OWN demand through ChaseController.Apply: the same law,
        // the same pipeline, the same one-controller-per-aircraft instance the human flies. That is
        // what makes a drone capture comparable to a crewed one — the alternative (a second
        // controller for uncrewed aircraft) would be measuring something nobody flies.
        //
        // NO CARD, NO CHASE. A drone with no card running has nothing to chase, so it keeps the
        // level-hold above and stays airborne. Deliberately not "hold the last demand": a finished
        // card's final direction is a stale stimulus, and chasing it would fill the log with
        // anomalies about a manoeuvre nobody asked for.
        // =========================================================================================
        internal static bool ChaseCard(Drone d)
        {
            var ac = d.Aircraft;
            if (ac == null) return false;
            var sp = ScenarioPlayer.For(ac);
            if (!sp.Playing) return LevelHold(d);

            // A CARD WITH NO DEMAND. Every path that starts a card writes one before Fly reads it
            // (the placement writes the level forward, an unforced card writes its first segment), so
            // this should be unreachable — which is exactly why it is checked rather than assumed:
            // Vector3.zero does not throw, it reads as "off = 0, already on target" and would fly a
            // whole unattended run as a plausible-looking null result. Abort is self-limiting (the
            // card is gone, so this fires once) and puts the reason in the CSV's own '# stop' line.
            if (sp.AimDemand.sqrMagnitude < 1e-6f)
            {
                sp.Abort("no aim demand written — nothing to chase");
                WTMouseAimPlugin.Log.LogWarning($"[drone] #{d.Id} card is running but wrote no aim demand — aborted.");
                return LevelHold(d);
            }

            if (ChaseController.For(ac).FlyUncrewed(ac, sp.AimDemand)) return true;

            // The instructor declined this tick (Enabled / WriteControl off, a rotorcraft without
            // ControlRotorcraft, a detached cockpit). Every one of those is persistent, so falling
            // back to the level-hold and carrying on would fly the REST of the card with a different
            // controller and still write a capture that reads as a clean run. End it instead, with
            // the reason in the CSV's own '# stop' line, and say so once under [drone].
            sp.Abort("the instructor is not flying this aircraft (Enabled / WriteControl / ControlRotorcraft?)");
            WTMouseAimPlugin.Log.LogWarning(
                $"[drone] #{d.Id} the control law declined to engage — card aborted; level-hold from here.");
            return LevelHold(d);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // One uncrewed aircraft under harness control. Deliberately a class and not a struct: the phase-2
    // controller will hang per-aircraft state off it (that is the other half of making the law
    // parallel-safe), and copying that around by value is exactly the bug that would be hardest to see.
    internal sealed class Drone
    {
        public readonly int      Id;            // stable, human-readable, matches the unit name "wtm-drone-N"
        public readonly Aircraft Aircraft;
        public readonly int      AircraftId;    // cached GetInstanceID(): the dictionary key must survive the aircraft being destroyed
        public readonly float    HoldAlt;       // MSL at spawn — what the built-in level-hold flies

        // Has this drone been offered a test card? One attempt, on its first pilot step (see
        // OnPilotStep), so a suite that finishes — or was refused — is not restarted every tick.
        public bool CardStarted;

        // `Time.time` at which this drone was first seen with no card running, or -1 while it is
        // flying one. PruneDead's auto-despawn clock. Per drone and not a shared timer, because the
        // launch stagger means N drones finish at N different instants — which is the point.
        public float IdleSince = -1f;

        // `Time.fixedTime` of the last step this drone was actually flown. The de-duplicator for a
        // MULTI-CREW airframe, whose every seat fires the pilot postfix independently — see the long
        // note at the guard in TestDrone.OnPilotStep. Per drone because the seats of one aircraft are
        // what collide, and -1 rather than 0 so the very first fixed step of a session is not eaten.
        public float LastStep = -1f;

        // WHAT FLIES THIS DRONE. Return true if inputs were written (the caller then runs the game's
        // FBW over them), false to leave this tick alone. It lives HERE, per drone, rather than as one
        // static on TestDrone, because N drones need N independent controllers — a single shared
        // delegate would force every drone through one instance's state, which is the same
        // whole-file-of-statics problem the control law is being unwound from right now.
        //
        // v0.87 (phase 2): ChaseCard — the mod's real control law when this drone is flying a card,
        // and the trivial built-in level-hold when it is not.
        public System.Func<Drone, bool> Fly = TestDrone.ChaseCard;

        public Drone(int id, Aircraft ac)
        {
            Id         = id;
            Aircraft   = ac;
            AircraftId = ac.GetInstanceID();
            float alt  = 0f;
            try { alt = ac.GlobalPosition().y; } catch { /* fail-soft: 0 just means the hold flies to sea level */ }
            HoldAlt = alt;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // THE DRONE SEAM. `Pilot.Pilot_OnAeroInputsApplied` is where the game gives every pilot its fixed
    // step, and it is the same seam the player path writes from (`PilotPlayerState.FixedUpdateState`
    // is called from inside it) — so a drone's inputs land at exactly the same point in the frame as
    // a human's, which is the only way a capture from one is comparable to a capture from the other.
    //
    // WHY NOT A MonoBehaviour FixedUpdate. `JobManager.FixedUpdateEarly` runs `ScheduleJobs()` — which
    // schedules the aero and control-surface jobs — BEFORE it calls `PilotAeroInputs()`. Writing from
    // an arbitrary FixedUpdate would land on an unspecified side of that boundary depending on Unity's
    // script execution order; writing from here is on the same side as the game's own pilots, always.
    //
    // The original early-returns on `aircraft.remoteSim` and returns Remove for a dead/ejected pilot.
    // A Harmony postfix runs regardless of either, so `OnPilotStep` re-checks both.
    [HarmonyPatch(typeof(Pilot), "Pilot_OnAeroInputsApplied")]
    internal static class TestDronePatch
    {
        private static void Postfix(Pilot __instance)
        {
            if (TestDrone.Idle) return;      // one int compare — the cost of this file with no drone alive
            TestDrone.OnPilotStep(__instance);
        }
    }
}
