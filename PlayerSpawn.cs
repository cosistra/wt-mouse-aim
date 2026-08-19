using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // SANDBOX (v0.95): put the OPERATOR airborne on one key, so the control law can be hand-flown
    // without building a mission first.
    //
    // WHY THIS IS NOT IN TestDrone.cs. That file is the uncrewed harness, and its single most
    // load-bearing invariant is that an aircraft can only enter its dictionary through `Spawn`,
    // which asserts `ac.Player == null`. This file does the exact opposite — it spawns WITH a player
    // — so putting the two in one file would sit a `player != null` path next to the assertion that
    // there is no such path. Separate file, and nothing here ever touches the drone registry.
    //
    // TWO CASES, and the split is the whole design:
    //   A. Already in an aircraft -> PLACE it. No spawn, so no aircraft is lost and no state is
    //      rebuilt. This is `ScenarioPlayer.PlaceOnCondition` minus the card: same
    //      ResetGLoadTrackers + MoveAssembly pair, which is shared rather than copied because both
    //      halves were learned by destroying the airframe (see ScenarioPlayer for the full note).
    //   B. Not in one -> SPAWN one and let the game seat you. This is `TestDrone.Spawn` with two
    //      arguments changed (`player`, `HQ`); everything downstream — pilot state, camera, HUD,
    //      map icon, throttle and gear — is the game's own doing and must NOT be reimplemented.
    internal static class PlayerSpawn
    {
        // The one entry point. Never throws into the game loop (Update calls it straight from a
        // keypress), and every refusal is a log line — a key that appears to do nothing has to be
        // explainable after the fact, the same rule the drone harness runs under.
        internal static void Trigger()
        {
            try
            {
                // The game's own definition of "local", the same one AimRig and ChaseController use.
                if (GameManager.GetLocalAircraft(out var ac) && ac != null && ac.rb != null)
                { Place(ac); return; }
                SpawnAround();
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[sandbox] failed ({e.GetType().Name}: {e.Message}).");
            }
        }

        // --- CASE A: place the aircraft the operator is already in. -------------------------------
        //
        // Position is KEPT (only altitude moves) and heading is KEPT. That is the difference from the
        // card placement, which snaps back to a run anchor: a card needs every replicate to start in
        // the same place, whereas a pilot pressing this wants to be where they were pointing, higher
        // and faster. There is no anchor here on purpose — this is not a measured run.
        private static void Place(Aircraft ac)
        {
            var rb = ac.rb;
            float alt = Cfg.SandboxAlt.Value, spd = Cfg.SandboxSpeed.Value;

            var g = ac.GlobalPosition();
            Vector3 gp0 = new Vector3(g.x, g.y, g.z);
            float alt0 = gp0.y, v0 = rb.velocity.magnitude;

            // Flatten the nose to the horizon. Vanishes only if pointing exactly vertical, in which
            // case keep the raw forward rather than snapping to an arbitrary world axis.
            Vector3 f0 = ac.transform.forward; f0.y = 0f;
            Vector3 fwd = f0.sqrMagnitude > 1e-6f ? f0.normalized : ac.transform.forward.normalized;

            // MUST precede the velocity write: the game derives G as a velocity difference across
            // fixed steps, so a teleport without this reads as an infinite-G event and damages or
            // kills the pilot. Covers every seat, which is why the two-seaters survive it.
            ScenarioPlayer.ResetGLoadTrackers(ac);

            Quaternion rot1 = Quaternion.LookRotation(fwd, Vector3.up);
            ScenarioPlayer.MoveAssembly(ac, rb, rot1 * Quaternion.Inverse(rb.rotation), rb.position,
                                        new Vector3(0f, alt - alt0, 0f), rot1, fwd * spd);

            // RE-CENTRE THE MARKER on the new nose. The aim marker is WORLD-locked, so snapping the
            // aircraft wings-level moves the nose out from under it and leaves the instructor chasing
            // whatever the marker was pointing at before — placed out of a bank that is tens of
            // degrees, and the law's first command after the teleport would be a hard correction the
            // operator never asked for. `PlaceOnCondition` has the same hazard and solves it the same
            // way (its `SetDemand(fwd)`), for the same measured reason. Level flight means the nose
            // IS the demand, so this is the honest value rather than a papered-over transient.
            AimRig.SetAimForward(fwd);

            // Same reason the card placement does it: the controller's integrators, rate filters and
            // finite differences all straddle the teleport, and a discontinuity in `_prevUp` reads as
            // ~60 deg/s of roll rate that never happened. For() rebuilds it on the next tick.
            ChaseController.Forget(ac);

            WTMouseAimPlugin.Log.LogInfo(
                $"[sandbox] placed: {v0:0} -> {spd:0} m/s, {alt0:0} -> {alt:0} m, wings level, "
                + "position and heading kept, controller reset.");
        }

        // --- CASE B: spawn one and get seated in it. ----------------------------------------------
        private static void SpawnAround()
        {
            var sp = NetworkSceneSingleton<Spawner>.i;
            if (sp == null)
            {
                WTMouseAimPlugin.Log.LogWarning("[sandbox] refused: no Spawner in the scene (not in a mission?).");
                return;
            }
            // THE SERVER GATE, asked of the object that will enforce it — `SpawnAircraft` carries no
            // [Server] attribute but ends in `ServerObjectManager.Spawn`. Identical to the drone gate,
            // and identical for the same reason: a refusal here can never disagree with reality.
            if (!sp.IsServer)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    "[sandbox] refused: no active server. Works in single player and while HOSTING "
                    + "(single player is a host), never as a multiplayer client.");
                return;
            }
            if (Encyclopedia.i == null)
            {
                WTMouseAimPlugin.Log.LogWarning("[sandbox] refused: the Encyclopedia has not loaded yet.");
                return;
            }

            if (!GameManager.GetLocalPlayer<NuclearOption.Networking.Player>(out var player) || player == null)
            {
                WTMouseAimPlugin.Log.LogWarning("[sandbox] refused: no local player.");
                return;
            }
            // Pass the player's OWN faction, never null: SetFaction(null) drops you out of your
            // faction entirely, which is a far more confusing outcome than a refusal.
            if (player.HQ == null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    "[sandbox] refused: no faction HQ yet — pick a faction/slot once, then press the key.");
                return;
            }

            string key = Cfg.SandboxAirframe.Value;
            if (!Encyclopedia.i.TryGetPrefab(key, out var prefab) || prefab == null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    $"[sandbox] refused: no aircraft prefab for jsonKey '{key}' — check Sandbox/SandboxAirframe.");
                return;
            }

            // Spawn ahead of and above the camera, on the camera's heading. The camera is the only
            // thing that reliably exists here: with no aircraft the operator is spectating or dead,
            // so there is no aircraft position to work from — the same reason the drone lanes fall
            // back to Camera.main rather than to the world origin.
            var cam = Camera.main;
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            Vector3 basePos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 pos = new Vector3(basePos.x, 0f, basePos.z) + fwd * 500f;
            pos.y = Cfg.SandboxAlt.Value;

            float spd = Cfg.SandboxSpeed.Value;
            var rot = Quaternion.LookRotation(fwd, Vector3.up);

            // The drone call with `player` and `HQ` filled in. Everything the human needs downstream
            // is the game's: Player.SetAircraft, the pilot's playerState (SetStartingAiState is
            // skipped precisely BECAUSE player != null), the cockpit camera, the HUD and the map
            // icon. Do not hand-roll any of it. Swapping while alive is supported — the game ejects
            // the old airframe itself — so there is no despawn-first dance.
            var ac = sp.SpawnAircraft(player, prefab, null, 1f, default(LiveryKey),
                                      pos.ToGlobalPosition(), rot, fwd * spd,
                                      null, player.HQ, "wtm-sandbox", 0f, 0f);
            if (ac == null)
            {
                WTMouseAimPlugin.Log.LogWarning("[sandbox] refused: SpawnAircraft returned nothing.");
                return;
            }

            WTMouseAimPlugin.Log.LogInfo(
                $"[sandbox] spawned '{key}' at {Cfg.SandboxAlt.Value:0} m, {spd:0} m/s, "
                + $"hdg {rot.eulerAngles.y:0}deg, {(ac.pilots != null ? ac.pilots.Length : 0)} crew. "
                + "Any aircraft you were in is the game's to eject.");
        }
    }
}
