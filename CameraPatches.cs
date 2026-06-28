using HarmonyLib;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // Cockpit camera follow. Native CameraCockpitState.UpdateState drives the view from panView/
    // tiltView (aircraft-local degrees) into cam.transform.localRotation, locking forward when not
    // free-looking. We POSTFIX it and, while actively aiming (not free-looking), override the view
    // rotation to smoothly look toward the marker — so the camera leads where you're commanding the
    // nose. We leave FOV, shake, free-look, and TrackIR to the native code.
    [HarmonyPatch(typeof(CameraCockpitState), "UpdateState")]
    internal static class CockpitCameraPatch
    {
        private static float _pan, _tilt;
        private static bool  _overriding;

        private static void Postfix(CameraStateManager cam)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value || cam == null || cam.transform == null)
            { _overriding = false; return; }
            if (PlayerSettings.useTrackIR)
            { _overriding = false; return; } // don't fight head-tracking

            if (AimRig.AimFrozen())
            { _overriding = false; return; } // frozen (Free Look / RMB): let native free-look look around

            // NOTE: we deliberately KEEP steering the view toward the (now frozen) marker while a
            // menu/map/pause is up. The aim direction doesn't change there, but the camera should stay
            // looking where you're flying instead of native snapping it back to the nose.

            if (!AimRig.TryGetContext(out var ac, out _))
            { _overriding = false; return; }

            Transform t = ac.transform;
            Vector3 local = t.InverseTransformDirection(AimRig.AimForward); // body-frame unit marker
            float amt = Cfg.CameraFollowAmount.Value;
            // Match native Euler(tiltView, panView, 0): +panView looks right, +tiltView looks down.
            float panTarget  =  Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg * amt;
            float tiltTarget = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f))        * Mathf.Rad2Deg * amt;

            // On the rising edge of overriding, seed from the camera's current local angles so there's
            // no jump from whatever native last set.
            if (!_overriding)
            {
                Vector3 e = cam.transform.localEulerAngles;
                _pan  = Norm(e.y);
                _tilt = Norm(e.x);
                _overriding = true;
            }

            float k = Mathf.Clamp01(2f * Time.unscaledDeltaTime / Mathf.Max(Cfg.CameraFollowSmoothing.Value, 0.01f));
            _pan  = Mathf.Lerp(_pan,  panTarget,  k);
            _tilt = Mathf.Lerp(_tilt, tiltTarget, k);
            cam.transform.localRotation = Quaternion.Euler(_tilt, _pan, 0f);
        }

        private static float Norm(float deg) => (deg > 180f) ? deg - 360f : deg;
    }

    // ---------------------------------------------------------------------------------------------
    // 3rd-person (orbit) camera follow — WT-style: the camera stays behind/above the plane but always
    // faces the aim direction, marker near screen centre.
    //
    // v0.22 verdict on the pan/tilt-steering approach (prefix writes native panView/tiltView): the pan
    // half tracks heading fine, but the RESULT was off the aim by an elevation-dependent error of up to
    // ~25-33 deg ([orbitcam:res] camOffAim, FOV 50 => marker offscreen) — native's tilt rotates the
    // camera around the plane through an arm that already carries a built-in downward angle (the 0.8r
    // up-offset), then look-at-plane re-aims it, so tilt never maps 1:1 to view elevation. Not fixable
    // by a better tilt formula without modelling that whole chain.
    //
    // So v0.23 splits the job:
    //   * Prefix (kept): still writes panView/tiltView. While we fly it keeps the NATIVE pivot pose
    //     roughly on the aim so RMB free-look starts from approximately the current view, and free-look
    //     itself stays 100% native.
    //   * Postfix (new): after native CameraMotion/Inputs have run, override the FINAL camera pose:
    //     position rigidly from the live plane position (the earlier world-space override failed by
    //     smoothing the POSITION — at speed that lags then jumps; only the view DIRECTION is smoothed
    //     here, frame-rate independent slerp, MouseFlight-style pole guard), native's terrain linecast
    //     replicated, then look along the smoothed aim. While the reticle is frozen (RMB free-look) or
    //     native's look-at-target is engaged the override stands down and re-seeds from the live pose on
    //     return, so transitions ease instead of snapping.
    [HarmonyPatch(typeof(CameraOrbitState), "UpdateState")]
    internal static class CameraOrbitPatch
    {
        private static float _pan;   // last stable orbit yaw — held through the vertical singularity
        private static bool  _prevNearPole;    // edge detection: pole-hold enter/exit diagnostics
        private static bool  _prevFrozen;      // edge detection: RMB/Free-Look freeze enter/exit
        private static float _lastOrbitLog;    // throttle the [orbitcam] trace to ~5/sec
        private static float _lastOrbitResLog; // throttle the [orbitcam:res] trace to ~5/sec
        private static Quaternion _aimRot = Quaternion.identity; // smoothed view rotation (postfix override)
        private static bool _aimRotValid;      // false => re-seed _aimRot from the live camera pose
        private static bool _levelingSuppressed; // true while inside the pole deadzone (horizon leveling off)
        private static float _flYaw, _flPitch; // free-look look angles (deg): heading + elevation, horizon-locked
        private static bool  _freeLookSeeded;  // rising-edge guard: seed _flYaw/_flPitch once when free-look begins
        private static bool  _wasFrozenPose;   // last pose frame was free-look (release starts the timed return)
        private static Quaternion _returnFrom = Quaternion.identity; // free-look view we ease back FROM on release
        private static float _returnT;         // 0..1 progress of the release return animation
        private static bool  _returning;       // true while easing the view back to the flight direction

        private static void Prefix(CameraOrbitState __instance)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value) return;
            if (CameraStateManager.cameraMode != CameraMode.orbit) return;
            if (!AimRig.TryGetContext(out var ac, out _)) return;
            if (ac.disabled) return;                                // dead: leave the native camera alone

            Vector3 aim = AimRig.AimForward;

            // Native pivot base looks along followVector (smoothed flat velocity). panView yaws it from that
            // heading to the aim's heading (around world up); tiltView pitches it to the aim's elevation.
            // Native then orbits the (plane-parented) camera by exactly these and smooths via its own pivot
            // Lerp — so a flick eases in instead of snapping, with no positional lag.
            var tr = Traverse.Create(__instance);
            Vector3 follow = tr.Field("followVector").GetValue<Vector3>();
            Vector3 followH = new Vector3(follow.x, 0f, follow.z);
            Vector3 aimH    = new Vector3(aim.x,   0f, aim.z);

            // panView: signed yaw from the velocity heading to the aim heading (matches native
            // Rotate(0,panView,0,World)). Near vertical BOTH headings collapse — aimH->0 when aiming straight
            // up/down, and followH->0 in a vertical climb — so the heading turns to noise and the camera whips
            // around the plane, throwing the aim offscreen (the "drift"). There, HOLD the last stable yaw: with
            // the view pitched near-vertical the heading barely changes the shot, and a frozen yaw stays steady
            // instead of spinning. Mirrors MouseFlight's |forward.y|>0.9 pole guard. We refresh _pan EVERY
            // frame, even while frozen/free-looking, so releasing free-look returns straight to the live aim
            // instead of snapping from a stale heading.
            bool nearPole   = Mathf.Abs(aim.y) > 0.9f;
            bool freshValid = followH.sqrMagnitude > 1e-3f && aimH.sqrMagnitude > 1e-3f;
            float freshPan  = freshValid
                ? Vector3.SignedAngle(followH.normalized, aimH.normalized, Vector3.up)
                : _pan;
            if (!nearPole && freshValid)
                _pan = freshPan;

            bool frozen = AimRig.AimFrozen();
            bool dbg    = Cfg.DebugLogging.Value;

            // Native's own accumulated look offsets BEFORE we overwrite them — nonzero growth here while
            // we're aiming means native Inputs() is fighting us for the orbit (it reads the same mouse).
            float nativePan = 0f, nativeTilt = 0f;
            if (dbg)
            {
                nativePan  = tr.Field("panView").GetValue<float>();
                nativeTilt = tr.Field("tiltView").GetValue<float>();
            }

            // Edge events (not throttled): the exact moments the two suspect mechanisms kick in/out.
            //   POLE  — how stale did the held yaw get vs a fresh computation while we were holding it?
            //   FREEZE— what offsets native accumulated during free-look, and what we resume to.
            if (dbg && nearPole != _prevNearPole)
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] POLE {(nearPole ? "ENTER" : "EXIT")} t={Time.time:0.000} heldPan={_pan:0.0} " +
                    $"freshPan={freshPan:0.0} stale={Mathf.DeltaAngle(freshPan, _pan):0.0} aimY={aim.y:0.00} " +
                    $"followHmag={followH.magnitude:0.00} freshValid={freshValid}");
            if (dbg && frozen != _prevFrozen)
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] FREEZE {(frozen ? "ENTER" : "EXIT")} t={Time.time:0.000} " +
                    $"nativePan={nativePan:0.0} nativeTilt={nativeTilt:0.0} ourPan={_pan:0.0}");
            _prevNearPole = nearPole;
            _prevFrozen   = frozen;

            // tiltView: native Rotate(tiltView,0,0,Self) pitches the view DOWN for +tilt, so aiming UP needs a
            // negative tilt. Subtract CameraPitchOffset to lift the resting view: the stock orbit cam parks
            // ~0.8*up above and 2*back behind, then looks AT the plane — ~22 of downward look that shoves the
            // level reticle to the top of the screen; this raises it back toward centre. Clamp short of straight
            // up/down (80, not 90): past there the look-at-plane up-vector goes degenerate and the view rolls/breaks.
            float tilt = Mathf.Clamp(
                -Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg - Cfg.CameraPitchOffset.Value,
                -80f, 80f);

            // Steady-state trace: everything the pan/tilt computation depends on, ~2.5/sec (v0.44: halved).
            if (dbg && Time.time - _lastOrbitLog >= 0.4f)
            {
                _lastOrbitLog = Time.time;
                float aimHdg    = aimH.sqrMagnitude    > 1e-6f ? Vector3.SignedAngle(Vector3.forward, aimH.normalized,    Vector3.up) : float.NaN;
                float followHdg = followH.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(Vector3.forward, followH.normalized, Vector3.up) : float.NaN;
                float aimElev   = Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg;
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] t={Time.time:0.000} aimHdg={aimHdg:0.0} aimElev={aimElev:0.0} " +
                    $"followHdg={followHdg:0.0} followHmag={followH.magnitude:0.00} pan={_pan:0.0} tilt={tilt:0.0} " +
                    $"pole={(nearPole ? 1 : 0)} frozen={(frozen ? 1 : 0)} nativePan={nativePan:0.0} nativeTilt={nativeTilt:0.0} " +
                    $"curVis={(Cursor.visible ? 1 : 0)} curFlags={CursorManager.GetFlags()}");
            }

            if (frozen) return;                                     // frozen: native owns the orbit-look (free-look)

            tr.Field("panView").SetValue(_pan);
            tr.Field("tiltView").SetValue(tilt);
        }

        // After native CameraMotion/Inputs have run: take over the FINAL camera pose (see the block
        // comment above), then log where the camera actually ended up. camOffAim is the "is the marker
        // on screen" proxy — bigger than ~half the FOV and the aim marker is offscreen (the reported
        // failure). With the override active it should sit ~CameraPitchOffset and stay there.
        private static void Postfix(CameraOrbitState __instance, CameraStateManager cam)
        {
            if (cam == null || cam.mainCamera == null) return;
            bool overrode = ApplyAimPose(__instance, cam);

            if (!Cfg.DebugLogging.Value) return;
            if (CameraStateManager.cameraMode != CameraMode.orbit) return;
            if (!AimRig.TryGetContext(out var ac, out _)) return;
            if (Time.time - _lastOrbitResLog < 0.4f) return; // v0.44: ~2.5/sec (halved)
            _lastOrbitResLog = Time.time;
            Transform ct = cam.mainCamera.transform;
            WTMouseAimPlugin.Log.LogInfo(
                $"[orbitcam:res] t={Time.time:0.000} camOffAim={Vector3.Angle(ct.forward, AimRig.AimForward):0.0} " +
                $"camOffNose={Vector3.Angle(ct.forward, ac.transform.forward):0.0} fov={cam.mainCamera.fieldOfView:0} " +
                $"ovr={(overrode ? 1 : 0)} levelSuppressed={(_levelingSuppressed ? 1 : 0)}");
        }

        // The WT-style pose: camera 2r behind the (smoothed) aim direction and 0.8r above the plane,
        // looking along the aim pitched down by CameraPitchOffset. Returns true when it took the pose
        // this frame. Position is rigid to the live plane position (zero translational lag at speed);
        // only the view direction is smoothed (1 - exp(-rate*dt) slerp — frame-rate independent), with
        // MouseFlight's pole guard: near straight up/down keep the previous frame's up instead of world
        // up so the view doesn't flip/spin through the singularity. Native's terrain-collision pull-in
        // is replicated with native's own layerMask. Stand-down cases reset _aimRotValid so the next
        // override frame re-seeds smoothing from wherever the camera actually is — no snap on re-entry.
        private static bool ApplyAimPose(CameraOrbitState st, CameraStateManager cam)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            // Re-check the mode: native Inputs() (Center button etc.) may have switched state this frame.
            if (CameraStateManager.cameraMode != CameraMode.orbit) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            if (!AimRig.TryGetContext(out var ac, out _) || ac.disabled) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            var tr = Traverse.Create(st);
            if (tr.Field("lookAtTargetLerp").GetValue<float>() > 0f)        // native look-at-target engaged
            { _aimRotValid = false; _freeLookSeeded = false; return false; }

            Transform ct = cam.transform; // what native CameraMotion positions (mainCamera rides on it)
            Vector3 planePos = cam.cameraPivot != null ? cam.cameraPivot.position : ac.transform.position;

            // RMB / Free-Look: KEEP our orbit pose (same pivot, +0.8r up, CameraPitchOffset framing) and
            // look around with the MOUSE — instead of standing down to the native orbit, whose pivot looks
            // AT the plane with a different downward angle (that hand-off was the "snap a bit down" on RMB).
            // Horizon-locked yaw/pitch so the view can't roll or hit a pole singularity. Seeded from the
            // current aim view on entry, so engaging free-look is seamless; on release the view eases back
            // to the flight direction (the timed return below).
            if (AimRig.AimFrozen())
            {
                if (!_freeLookSeeded)
                {
                    Vector3 seed = (_aimRotValid ? _aimRot : ct.rotation) * Vector3.forward;
                    _flYaw   = Mathf.Atan2(seed.x, seed.z) * Mathf.Rad2Deg;
                    _flPitch = Mathf.Asin(Mathf.Clamp(seed.y, -1f, 1f)) * Mathf.Rad2Deg;
                    _freeLookSeeded = true;
                }
                // Drive the look with the SAME smoothed mouse delta + MouseSensitivity as aiming (from
                // AimRig), so free-look and aim feel identical. d.x = mouse right (+), d.y = mouse up (+).
                Vector2 d = AimRig.LookDelta;
                float sens = Cfg.MouseSensitivity.Value;
                _flYaw  += d.x * sens;
                _flPitch = Mathf.Clamp(_flPitch + d.y * sens * (Cfg.InvertPitch.Value ? -1f : 1f), -85f, 85f);

                Quaternion look = Quaternion.Euler(-_flPitch, _flYaw, 0f);
                PlaceOrbitCamera(tr, ct, planePos, look * Vector3.forward, look);
                _aimRotValid   = false; // release re-seeds the aim smoother (from this pose, via _wasFrozenPose)
                _wasFrozenPose = true;
                return true;
            }
            _freeLookSeeded = false;    // free-look ended; the next entry re-seeds the look angles

            Vector3 aim = AimRig.AimForward;
            if (aim.sqrMagnitude < 0.5f) return false;

            // Seed the smoother. Straight out of free-look, START A TIMED EASE back to the flight
            // direction: the plane keeps flying the heading it held (no commit) and the camera swings
            // back over FreeLookReturnTime. Otherwise just ease in from the live pose.
            if (_wasFrozenPose)
            {
                _aimRot      = Quaternion.Euler(-_flPitch, _flYaw, 0f); // the free-look view we return FROM
                _returnFrom  = _aimRot;
                _returnT     = 0f;
                _returning   = true;
                _aimRotValid = true;
                _wasFrozenPose = false;
            }
            else if (!_aimRotValid)
            {
                _aimRot      = ct.rotation;
                _aimRotValid = true;
                _returning   = false;
            }

            // Pole-stable horizon leveling with a hysteresis deadzone. Replaces the old hard
            // |aim.y|>0.9 world-up / prev-up switch, which flipped the camera's up instantly through
            // the singularity (an abrupt ~180deg roll over the top of a loop). Away from vertical we
            // level to the world horizon; inside the deadzone we hold the current (continuous) up so
            // nothing can flip; leveling eases back in over the band as we come out, and only
            // re-enables once past the far edge — hysteresis, so hovering right at the pole can't
            // oscillate. The OrbitAimSmoothing slerp below turns the up-vector hand-off into a smooth
            // roll to the new level side instead of a snap. holdUp comes from the previous smoothed
            // rotation, so the view direction never inverts through vertical.
            float vertical = 90f - Mathf.Abs(Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg); // 0 at pole, 90 at horizon
            float dz = Cfg.HorizonDeadzoneDeg.Value;
            if (!_levelingSuppressed && vertical <= 0f) _levelingSuppressed = true;
            else if (_levelingSuppressed && vertical >= dz) _levelingSuppressed = false;

            Vector3 holdUp = _aimRot * Vector3.up;   // continuous: never flips through the pole
            Vector3 upRef  = holdUp;
            if (!_levelingSuppressed)
            {
                Vector3 right = Vector3.Cross(Vector3.up, aim);
                if (right.sqrMagnitude > 1e-5f)
                {
                    Vector3 levelUp = Vector3.Cross(aim, right.normalized).normalized;
                    float w = dz > 0f ? Mathf.InverseLerp(0f, dz, vertical) : 1f; // ease leveling in over the band
                    upRef = Vector3.Slerp(holdUp, levelUp, w);
                }
            }
            Quaternion want = Quaternion.LookRotation(aim, upRef);
            if (_returning)
            {
                // Fixed-duration smoothstep swing from the free-look view back to the live flight dir.
                _returnT += Time.deltaTime / Mathf.Max(Cfg.FreeLookReturnTime.Value, 0.01f);
                if (_returnT >= 1f) { _returnT = 1f; _returning = false; }
                _aimRot = Quaternion.Slerp(_returnFrom, want, Mathf.SmoothStep(0f, 1f, _returnT));
            }
            else
            {
                _aimRot = Quaternion.Slerp(_aimRot, want,
                    1f - Mathf.Exp(-Cfg.OrbitAimSmoothing.Value * Time.deltaTime));
            }

            PlaceOrbitCamera(tr, ct, planePos, _aimRot * Vector3.forward, _aimRot);
            return true;
        }

        // Position the camera 2r behind the look direction and 0.8r above the plane (native's orbit
        // geometry), pull it in off terrain with native's own linecast, and look along lookRot pitched
        // down by CameraPitchOffset. Shared by the aim-tracking and free-look paths so engaging or
        // releasing free-look never changes the pivot/offset (the source of the old RMB snap).
        private static void PlaceOrbitCamera(Traverse tr, Transform ct, Vector3 planePos, Vector3 dir, Quaternion lookRot)
        {
            float maxR = tr.Field("followingMaxRadius").GetValue<float>();
            float zoom = tr.Field("viewDistAdjust").GetValue<float>();
            float r = 1f + maxR * (1f + zoom);                       // native's num2: zoom-aware orbit radius
            // Stock framing is 2r behind the look dir + 0.8r above. The three user offsets ride on top,
            // each scaled by r so they hold their feel across zoom. Side uses a horizontal "camera right"
            // (up x dir); guard the degenerate near-vertical look where that cross collapses.
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            Vector3 camPos = planePos
                - dir   * ((2f + Cfg.CameraDistanceOffset.Value) * r)
                + Vector3.up * ((0.8f + Cfg.CameraHeightOffset.Value) * r)
                + right * (Cfg.CameraSideOffset.Value * r);

            // Terrain pull-in, same math as native CameraMotion's linecast block.
            Vector3 armN = (planePos - camPos).normalized;
            int mask = tr.Field("layerMask").GetValue<int>();
            if (Physics.Linecast(planePos, camPos, out RaycastHit hitInfo, mask))
            {
                float dot = Mathf.Max(Vector3.Dot(armN, hitInfo.normal), 0.1f);
                camPos = hitInfo.point + armN / dot;
            }

            ct.SetPositionAndRotation(camPos, lookRot * Quaternion.Euler(Cfg.CameraPitchOffset.Value, 0f, 0f));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Diagnostic: log every native camera-state transition (Center button -> chase, death -> free,
    // look-at switches, etc.) so a "camera suddenly reframed" report can be attributed to a native
    // state switch vs our orbit math. cameraMode is updated inside EnterState, so the postfix sees
    // the new mode.
    [HarmonyPatch(typeof(CameraStateManager), "SwitchState")]
    internal static class CameraSwitchStatePatch
    {
        private static void Prefix(CameraStateManager __instance, out string __state)
        {
            __state = __instance.currentState != null ? __instance.currentState.GetType().Name : "<none>";
        }

        private static void Postfix(CameraStateManager __instance, string __state)
        {
            if (!Cfg.DebugLogging.Value) return;
            string now = __instance.currentState != null ? __instance.currentState.GetType().Name : "<none>";
            WTMouseAimPlugin.Log.LogInfo(
                $"[camstate] t={Time.time:0.000} {__state} -> {now} (mode={CameraStateManager.cameraMode})");
        }
    }
}
