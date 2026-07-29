using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // TEST DRONE (v0.81, phase 1 of the uncrewed harness).
    //
    // Every measurement this project has ever taken cost a human sitting in a cockpit for the length
    // of the card. A four-replicate suite of `fixedwing-v2` is ~12 minutes of someone watching a
    // marker sweep. This file removes the pilot from that loop: the mod spawns its own aircraft, owns
    // its ControlInputs, and destroys it again — so a sweep can run N airframes at once, unattended.
    //
    // PHASE 1 IS THE HARNESS ONLY. Nothing here is wired to ChaseController or ScenarioPlayer; the
    // built-in level-hold below exists purely to prove that written inputs reach the physics. Phase 2
    // attaches the real law through `Drone.Fly` (see that field).
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
        //   LaneM   : lateral gap between consecutive drones.
        // ponytail: fixed lateral lanes, no altitude stacking and no re-check that the lane is clear.
        // The range mission (`harness/WTM-Range`) is deliberately empty, which is what makes that
        // safe. If a card ever flies over a populated map, give each lane its own altitude block.
        private const float AbeamM = 8000f;
        private const float LaneM  = 2000f;

        // Hitch reporting. A "hitch" is any rendered frame that took more than this; during one,
        // Unity runs several FixedUpdates back to back all reporting the SAME unscaledDeltaTime, so
        // we log on the rising edge only or a single 300 ms stall would print fifteen identical lines.
        private const float HitchSec = 0.050f;

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

        public static IReadOnlyList<Drone> Live => _live;

        // The hot-path gate. Everything in this file that runs per-tick early-outs on this single
        // read, exactly like ScenarioPlayer's null-card check — with no drone alive the harness costs
        // one int compare per pilot per fixed step.
        public static bool Idle => _byAircraftId.Count == 0;

        // FRAME TIME, sampled on the fixed step (v0.81). The stagger below exists because a frame
        // hitch lands on whatever segment is running when it happens; if all N replicates are flying
        // the same segment at that instant, one hitch corrupts all N identically and they stop being
        // independent samples. That is an assumption until it is instrumented, so: this is the last
        // `Time.unscaledDeltaTime` seen by a fixed step, exposed for the recorder to sample as a
        // column. Unscaled on purpose — the scaled one would hide a hitch as a timeScale change.
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
                // No aircraft (menu, dead, spectating) is not a refusal: lanes then key off the scene
                // origin, which is still a well-defined place to put a drone. Fail-soft, like the probes.
                if (AimRig.TryGetContext(out var me, out _) && me != null)
                {
                    _laneBase = me.transform.position;
                    Vector3 f = me.transform.forward; f.y = 0f;
                    if (f.sqrMagnitude > 1e-6f) fwd = f.normalized;
                }
            }
            catch { /* geometry is best-effort; the defaults above are valid */ }

            _laneRight = Vector3.Cross(Vector3.up, fwd);      // horizontal right of the heading
            _laneRot   = Quaternion.LookRotation(fwd, Vector3.up);
            _pending   = Mathf.Clamp(Cfg.DroneCount.Value, 1, 16);
            _slot      = 0;
            _nextAt    = Time.time;                           // first one goes on the next fixed step
            WTMouseAimPlugin.Log.LogInfo(
                $"[drone] launching {_pending} x '{string.Join(",", AirframeList())}' (by lane, wrapping) at {Cfg.DroneSpawnAlt.Value:0} m / "
                + $"{Cfg.DroneSpawnSpeed.Value:0} m/s, {Cfg.DroneStaggerSec.Value:0.#}s apart, lanes {AbeamM:0} m + {LaneM:0} m abeam.");
        }

        // =========================================================================================
        // THE FIXED STEP. Called from WTMouseAimPlugin.FixedUpdate — a real fixed-step hook that
        // exists whether or not any drone is alive, which the per-pilot postfix does not (with zero
        // drones there is no pilot of ours for it to fire on). Deliberately NOT a coroutine: the
        // stagger has to be counted on the same clock the run is measured on.
        // =========================================================================================
        public static void FixedTick()
        {
            SampleFrameTime();
            if (_pending > 0) LaunchDue();
            if (_live.Count > 0) PruneDead();
        }

        private static float _hitchArmed;   // Time.time the current hitch was first reported (edge gate)

        private static void SampleFrameTime()
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
            pos.y = new GlobalPosition(0f, Cfg.DroneSpawnAlt.Value, 0f).ToLocalPosition().y;
            _slot++;

            var d = Spawn(key, pos, _laneRot, _laneRot * Vector3.forward * Cfg.DroneSpawnSpeed.Value);
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
        private static string[] AirframeList()
        {
            var parts = (Cfg.DroneAirframe.Value ?? "").Split(',');
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

        // An aircraft can leave without us: it can be shot down, fly into the sea, or be cleaned up
        // by the mission. Unity reports a destroyed object as `null` WITHOUT throwing, so a stale
        // dictionary entry never announces itself — it just keeps a recycled instance id mapped to a
        // corpse. Prune every fixed step; the list is a handful of entries.
        private static void PruneDead()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var d = _live[i];
                if (d.Aircraft != null && !d.Aircraft.disabled) continue;
                WTMouseAimPlugin.Log.LogInfo($"[drone] #{d.Id} is gone (destroyed or disabled by the game) — deregistered.");
                _live.RemoveAt(i);
                _byAircraftId.Remove(d.AircraftId);
                ForgetState(d.AircraftId);
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
                    + $"{d.HoldAlt:0} m MSL, {velocity.magnitude:0} m/s, hdg {rot.eulerAngles.y:0}deg. {_live.Count} live.");
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
        public static void Despawn(Drone d)
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
                WTMouseAimPlugin.Log.LogInfo($"[drone] #{d.Id} despawned. {_live.Count} live.");
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
            if (_byAircraftId.Count == 0 || p == null || p.dead || p.ejected) return;
            var ac = p.aircraft;
            if (ac == null || ac.disabled) return;
            if (!_byAircraftId.TryGetValue(ac.GetInstanceID(), out var d)) return;   // every other aircraft, including the player's

            try
            {
                // TEST-CARD DEMAND (v0.86) — THIS drone's card, ticked HERE so it gets the same
                // zero-tick property the player's card gets from the seam prefix: the demand for this
                // fixed step is written immediately before Fly reads it, inside the same
                // Pilot_OnAeroInputsApplied invocation. A card ticked from FixedUpdate instead would
                // sit on an unspecified side of JobManager.FixedUpdateEarly's ScheduleJobs, i.e. a
                // frame-rate-dependent zero-order hold between the stimulus and the response — which
                // is exactly the coupling the harness exists to remove. No-op (a dict probe and a
                // null check) when this drone is not flying a card.
                ScenarioPlayer.For(ac).Tick(ac);
                var fly = d.Fly;
                if (fly == null || !fly(d)) return;     // the controller declined to command this tick

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
        // already provides rate damping (that is why FilterInputs is called above), and phase 2
        // replaces this whole function with the real law, so tuning it would be tuning something
        // that is scheduled for deletion.
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

        // THE PHASE-2 HOOK. Return true if inputs were written (the caller then runs the game's FBW
        // over them), false to leave this tick alone. It lives HERE, per drone, rather than as one
        // static on TestDrone, because N drones need N independent controllers — a single shared
        // delegate would force every drone through one instance's state, which is the same
        // whole-file-of-statics problem the control law is being unwound from right now.
        //
        // Phase 2 attaches ChaseController here. Until then it is the trivial level-hold, which is
        // NOT the mod's control law — see the comment on TestDrone.LevelHold.
        public System.Func<Drone, bool> Fly = TestDrone.LevelHold;

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
