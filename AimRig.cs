using System.Runtime.InteropServices;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // Shared "should the mod be passive right now?" checks. True when the player is in a menu, the
    // map is maximized, a radial/leaderboard is up, or the game is paused (timeScale 0). Used to
    // freeze the aim marker and the camera follow so they don't drift while you're not flying.
    internal static class Guards
    {
        public static bool MenusOpen() =>
            Time.timeScale == 0f ||
            DynamicMap.mapMaximized ||
            RadialMenuMain.IsInUse() ||
            Leaderboard.IsOpen();
    }

    // ---------------------------------------------------------------------------------------------
    // The aim rig. The marker is a WORLD-LOCKED follow point (spec §2): the mouse rotates a world-space
    // aim direction and the chase flies the nose ONTO that point. The point stays exactly where you put
    // it in the world — the plane follows it and the offset eases to zero as the nose arrives. This is
    // point-and-chase, NOT a joystick: the marker never rides the airframe (that was the v0.14 mistake),
    // and nothing here ever snaps it back to the nose. The only things that move it are the mouse nudge
    // and the cone clamp. Re-seeds to the boresight whenever we (re)acquire an aircraft.
    internal static class AimRig
    {
        private static Vector3 _aimForward = Vector3.zero; // world-space unit direction (the marker)
        private static int _lastAircraftId = -1;
        private static Vector2 _smoothedDelta;             // one-pole-smoothed mouse delta
        private static bool _captured;                     // we are reading the mouse for aiming this frame
        private static Vector3 _prevNoseDbg = Vector3.zero; // last Update's nose dir (diagnostic: plane-driven marker decay)
        private static Vector3 _prevAimDbg  = Vector3.zero; // last Update's marker dir (diagnostic: total marker rotation)
        private static float   _lastAimLog;                 // throttle the [aim] trace to ~5/sec
        private static bool _captureFresh;                 // true on the frame aiming begins (drop the recenter-warp jump)
        private static bool _managing;                     // we are currently driving the cursor regime at all
        private static Vector2 _lookDelta;                 // smoothed mouse delta exposed for 3p free-look (same units as aim)

        // Win32 raw mouse: GetCursorPos gives a true hardware delta from frame 1, immune to Unity's
        // focus-gated legacy "Mouse X/Y" axis (which stays dead until the window gets a focus event,
        // i.e. an alt-tab). We recenter the OS cursor to the primary-screen centre each captured frame
        // so big sweeps never clamp at the desktop edge.
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        // Unity's legacy "Mouse X" axis ≈ hardwarePixels x 0.1 (its default InputManager sensitivity).
        // Multiplying the raw Win32 pixel delta by the same factor keeps MouseSensitivity meaning the
        // same thing whether we read via Win32 or the Unity axis.
        private const float PixelToAxis = 0.1f;

        public static Vector3 AimForward => _aimForward;
        public static Vector3 MouseAimPos(Aircraft ac) => ac.transform.position + _aimForward * Cfg.AimDistance.Value;
        // Smoothed mouse delta during 3rd-person free-look (same units/smoothing as the aim), zero otherwise.
        // CameraOrbitPatch multiplies it by MouseSensitivity so free-look feel == aim feel.
        internal static Vector2 LookDelta => _lookDelta;

        public static void Update()
        {
            bool ok = TryGetContext(out var ac, out var cam);
            bool context = Cfg.Enabled.Value && ok && !ac.disabled; // dead plane => release cursor, stop aiming

            bool frozen    = AimFrozen();
            bool inCockpit = CameraStateManager.cameraMode == CameraMode.cockpit;
            bool inOrbit   = CameraStateManager.cameraMode == CameraMode.orbit;

            if (!context)
            {
                ReleaseCursor();       // hand the cursor back to the game (normal pointer for menus)
                _lastAircraftId = -1;  // force re-seed on next acquire
                _smoothedDelta = Vector2.zero;
                return;
            }

            // Choose a cursor regime that COOPERATES with the game's CursorManager (see ApplyCursorRegime):
            //   aimCapture — mouse-aim (cockpit OR orbit), not frozen: hidden + lockState=None + Win32
            //                recentre (raw delta from frame 1, no alt-tab needed).
            //   flyHidden  — flying but not aim-capturing (frozen / free-look): hidden + lockState=Locked,
            //                the game's own flying regime. 3rd-person orbit free-look ONLY reads the look
            //                axes when the cursor is hidden (CameraOrbitState gates on !Cursor.visible), so
            //                leaving it visible — our old release behaviour — was exactly why free-look
            //                died in 3p. This is also what lets the RMB/Free-Look freeze orbit the camera.
            //   visible    — a menu/pause/UI wants the pointer.
            bool flying     = Cfg.WriteControl.Value;  // context is already true here
            bool menuWants  = Guards.MenusOpen() || CursorManager.GetFlags() != CursorFlags.None;
            bool flyHidden  = flying && !menuWants;
            bool aimCapture = flyHidden && !frozen && (inCockpit || inOrbit);
            // Orbit free-look: WE drive the camera with the mouse (so it gets the same sensitivity as
            // aiming), so capture the cursor exactly like aiming (None+hidden + Win32 recenter) instead of
            // handing the look to native. Cockpit free-look stays native (Locked+hidden, below).
            bool lookCapture = flyHidden && frozen && inOrbit;
            ApplyCursorRegime(flyHidden, aimCapture || lookCapture);

            Transform t = ac.transform;
            int id = ac.GetInstanceID();
            if (id != _lastAircraftId || _aimForward == Vector3.zero)
            {
                _aimForward = t.forward; // seed on the nose
                _lastAircraftId = id;
                _smoothedDelta = Vector2.zero;
            }

            // The marker is WORLD-LOCKED: once placed in world space it stays exactly where you put it
            // while the plane flies its nose onto it (point-and-chase). Nothing here pulls it back toward
            // the nose; only the mouse nudge below and the cone clamp move it. Nudge it by the mouse delta
            // only while we own the cursor for aiming; otherwise it stays parked in the world (frozen
            // during menus/map/pause/free-look/external cam).
            Vector2 raw = Vector2.zero;
            if (aimCapture || lookCapture)
            {
                raw = ReadMouseDelta();
                float sm = Mathf.Clamp01(Cfg.MouseSmoothing.Value);
                _smoothedDelta = Vector2.Lerp(raw, _smoothedDelta, sm); // sm=0 -> raw, higher -> smoother/laggier
            }
            else
            {
                _smoothedDelta = Vector2.zero; // drop stale delta so it doesn't lurch on resume
            }

            // Expose the look delta for orbit free-look (CameraOrbitPatch). Same smoothed delta the aim
            // uses, so applying MouseSensitivity camera-side makes free-look and aiming feel identical.
            _lookDelta = lookCapture ? _smoothedDelta : Vector2.zero;

            if (aimCapture)
            {
                float sens = Cfg.MouseSensitivity.Value;
                float pan  = _smoothedDelta.x;
                float tilt = _smoothedDelta.y * (Cfg.InvertPitch.Value ? -1f : 1f);
                // Pick the rotation frame by view. Cockpit: the airframe up/right (== screen, since the
                // cockpit view rolls with the plane). Orbit: a HORIZON-LOCKED screen frame derived from the
                // camera's forward — "mouse up = up on screen" regardless of airframe roll/tilt (MouseFlight's
                // screen-relative aim, MouseFlightController.cs:136-137). We deliberately do NOT use the
                // camera's own up/right: the orbit cam carries a little roll (and lags), which would feed back
                // into the aim and let the marker slowly walk off on its own (the "drift" that a camera-toggle
                // reset). Re-deriving right = up x forward kills that roll feedback.
                Vector3 upAxis, rightAxis;
                if (inOrbit && cam != null)
                {
                    Vector3 camFwd = cam.transform.forward;
                    rightAxis = Vector3.Cross(Vector3.up, camFwd);
                    if (rightAxis.sqrMagnitude < 1e-4f)        // looking near-straight up/down: no stable horizon
                    { rightAxis = cam.transform.right; upAxis = cam.transform.up; }
                    else
                    { rightAxis.Normalize(); upAxis = Vector3.Cross(camFwd, rightAxis).normalized; }
                }
                else { upAxis = t.up; rightAxis = t.right; }
                _aimForward = Quaternion.AngleAxis(pan * sens, upAxis)
                            * Quaternion.AngleAxis(-tilt * sens, rightAxis)
                            * _aimForward;
            }

            // Clamp the marker into the cone around the CURRENT nose — but ONLY when it actually exceeds
            // the cone. Vector3.RotateTowards has a near-parallel degeneracy: when the marker is a hair off
            // the nose (under ~0.3deg) its rotation axis collapses and it snaps the marker ONTO the nose
            // instead of leaving it alone, then re-eats every tiny mouse nudge. That glued the marker to
            // the boresight ("snaps to 0, impossible to ease off") — the opposite of a max-angle clamp.
            // Guarding on offset > MaxAimAngle makes it a true limiter: inside the cone it's a genuine
            // no-op (marker perfectly free near centre), and it only pulls back when you over-command.
            Vector3 preClamp = _aimForward;
            if (Vector3.Angle(t.forward, _aimForward) > Cfg.MaxAimAngle.Value)
                _aimForward = Vector3.RotateTowards(t.forward, _aimForward, Cfg.MaxAimAngle.Value * Mathf.Deg2Rad, 0f).normalized;

            // Per-frame diagnostics. The "snaps to 0 / can't leave centre" question is: does the marker
            // sit still while the nose eats it (plane-driven decay), or does the marker itself fail to move?
            //   markerOff  : current nose->marker angle (the residual we're steering on)
            //   markerMoved: how far the marker rotated in WORLD since last Update (mouse + clamp)
            //   noseMoved  : how far the NOSE rotated in world since last Update (plane chasing in) — if this
            //                tracks markerOff shrinking, the plane is eating the offset (one-frame arrival)
            //   clamp      : how much the cone clamp pulled the marker back toward the nose this frame
            if (Cfg.DebugLogging.Value && Time.time - _lastAimLog >= 0.4f) // v0.44: ~2.5/sec (halved)
            {
                float markerOff   = Vector3.Angle(t.forward, _aimForward);
                float markerMoved = _prevAimDbg  == Vector3.zero ? 0f : Vector3.Angle(_aimForward, _prevAimDbg);
                float noseMoved   = _prevNoseDbg == Vector3.zero ? 0f : Vector3.Angle(t.forward, _prevNoseDbg);
                float clamp       = Vector3.Angle(preClamp, _aimForward);
                bool  active      = raw.sqrMagnitude > 1e-6f || markerOff > 0.02f || markerMoved > 0.01f || noseMoved > 0.01f;
                if (active)
                {
                    _lastAimLog = Time.time;
                    TryGetContext(out _, out var dcam);
                    float camOff = dcam != null ? Vector3.Angle(dcam.transform.forward, t.forward) : -1f;
                    WTMouseAimPlugin.Log.LogInfo(
                        $"[aim] t={Time.time:0.000} f={Time.frameCount} raw=({raw.x:0.000},{raw.y:0.000}) " +
                        $"markerOff={markerOff:0.000}deg markerMoved={markerMoved:0.000} noseMoved={noseMoved:0.000} " +
                        $"clamp={clamp:0.000} camOff={camOff:0.00} cap={_captured}");
                }
            }
            _prevAimDbg  = _aimForward;
            _prevNoseDbg = t.forward;
        }

        // Raw hardware mouse delta via Win32 — works from frame 1 with no alt-tab, unlike Unity's
        // focus-gated legacy axis. Read the OS cursor, take its delta from the primary-screen centre,
        // then warp it back to centre so the next frame measures from the same origin and big sweeps
        // never clamp at the desktop edge. Screen Y is +down, so we flip it to +up. The factor brings
        // the pixel delta onto Unity's legacy-axis scale so MouseSensitivity is read-backend independent.
        private static Vector2 ReadMouseDelta()
        {
            int cx = GetSystemMetrics(SM_CXSCREEN) / 2;
            int cy = GetSystemMetrics(SM_CYSCREEN) / 2;
            if (!GetCursorPos(out POINT p))
            {
                SetCursorPos(cx, cy);
                return Vector2.zero;
            }
            float dx = p.X - cx;
            float dy = p.Y - cy;
            SetCursorPos(cx, cy);                 // recenter for the next frame
            if (_captureFresh)
            {
                _captureFresh = false;            // first captured frame is just the warp-to-centre; ignore it
                return Vector2.zero;
            }
            return new Vector2(dx, -dy) * PixelToAxis;
        }

        // Three cursor regimes, scoped so each one cooperates with what reads the mouse:
        //   aimCapture (cockpit mouse-aim) — WE own the cursor: unlocked + hidden, and our Win32
        //              recenter produces the raw delta. lockState=None is essential: Locked would let
        //              Unity warp the cursor to centre too and zero our GetCursorPos delta.
        //   flyHidden  (3rd-person/orbit, or Free Look held) — the game's own flying regime: hidden +
        //              Locked. Orbit free-look only reads the look axes while the cursor is hidden, so
        //              this is what keeps 3p free-look alive. We do NOT touch Win32 here.
        //   visible    (menu/pause/UI) — a normal pointer.
        private static void ApplyCursorRegime(bool flyHidden, bool aimCapture)
        {
            _managing = true;
            if (aimCapture && !_captured) _captureFresh = true; // drop the recenter-warp jump as aiming starts
            _captured = aimCapture;
            if (aimCapture)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = false;
            }
            else if (flyHidden)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        // Hand the cursor back to the game when we stop managing it (no aircraft / mod off / scene change).
        // Leave a normal pointer as a safe default, then let CursorManager re-assert its own state.
        private static void ReleaseCursor()
        {
            if (!_managing) return;
            _managing = false;
            _captured = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            CursorManager.Refresh();
        }

        // Is the aim reticle currently frozen? True while the player holds the game's bound "Free Look"
        // OR (when RightClickFreeze is on) the right mouse button. While frozen the marker stops following
        // the mouse and stays parked in the world — the plane keeps flying to it while the camera free-
        // looks around (War Thunder–style). Same test drives both camera patches, so cockpit and orbit
        // agree on when to hand the view back to the game's free-look.
        public static bool AimFrozen()
        {
            var pi = GameManager.playerInput;
            bool freeLook = pi != null && pi.GetButton("Free Look");
            return freeLook || (Cfg.RightClickFreeze.Value && Input.GetMouseButton(1));
        }

        // True when we have a local aircraft and a main camera — in ANY camera mode. (The marker keeps
        // flying and is drawn in external/orbit views too; only the mouse-driven nudge is cockpit-only,
        // gated separately in Update.)
        public static bool TryGetContext(out Aircraft ac, out Camera cam)
        {
            ac = null; cam = null;
            var csm = SceneSingleton<CameraStateManager>.i;
            if (csm == null || csm.mainCamera == null)
                return false;
            if (!GameManager.GetLocalAircraft(out ac) || ac == null)
                return false;
            cam = csm.mainCamera;
            return true;
        }
    }
}
