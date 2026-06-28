using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // Which control law produces the per-axis stick targets (A/B switch, v0.38). Legacy is the
    // accreted v0.37 law (default during development); BankToTurn / EvolvedLegacy are the phased
    // rearch laws. Cycled in flight via ControlLawKey (F9); selectable live in the F1 menu. See the
    // phased rearchitecture plan: Apply branches on this to pick ApplyLegacy/ApplyBankToTurn/ApplyEvolvedLegacy.
    internal enum ControlLawMode { Legacy, BankToTurn, EvolvedLegacy }

    // ---------------------------------------------------------------------------------------------
    // The chase controller (thin "instructor"). Point-and-chase law from brihernandez/MouseFlight
    // (MIT), spec §3.1/3.2: compute the marker direction in the body frame, pitch/yaw toward it, and
    // blend roll between "bank toward target" (far off) and "wings level" (on target). We fully OWN
    // the stick while active (native is skipped); only a short ramp on disengage. The game's FBW/
    // AutoTrimmer limit G/AoA downstream, so no integral term — just P + a slew-rate limit.
    internal static class ChaseController
    {
        private static bool  _active;     // owning the stick this frame
        private static bool  _wasActive;  // last frame (edge detection)
        private static float _outP, _outR, _outY;
        private static float _disRamp;    // 1->0 disengage blend
        private static Vector3 _prevFwd;  // last frame's nose direction (for pitch/yaw rate damping)
        private static Vector3 _prevUp;   // last frame's up axis (for roll-rate damping)
        private static bool  _prevFwdValid;
        private static float _lastChaseLog; // throttle the [chase] trace to ~5/sec

        // Per-axis manual override-on-touch state. _eng* is 0 (mouse-aim owns the axis) .. 1 (you own it);
        // _mApply* freezes the manual value at release so the axis eases back to chase, not to zero.
        private static float _engP, _engR, _engY;
        private static float _mApplyP, _mApplyR, _mApplyY;
        private static Player _rewired;   // cached Rewired player 0 (same one PilotPlayerState reads)

        // Fine-regime integrator state (v0.24). The game's FBW is a rate-command law, so a proportional
        // outer loop asymptotes and parks short; these wind in the steady bias that closes the last bit.
        private static float _iPitch, _iYaw;

        // Fly Level autopilot (v0.24). When active, the chase ignores the marker and flies straight-and-
        // level at the heading captured on toggle-on (horizontal projection of the nose at that instant).
        public static bool FlyLevelActive;
        private static Vector3 _levelHeading = Vector3.forward; // world-space, horizontal, unit

        // Anomaly detection state (v0.25). Event-only logger: each detector keeps a little rolling state
        // and emits ONE [anomaly] line on the triggering frame, with a per-type cooldown.
        private static float _offMin = float.MaxValue;          // closest approach during the current command (overshoot)
        private static float _prevOff;                          // last frame's off (closing test)
        private static float _huntWinStart;                     // start of the 1 s sign-flip window
        private static int   _flipsP, _flipsY;                  // output sign-flips this window (hunt)
        private static float _prevSignP, _prevSignY;            // last non-trivial output sign per axis
        private static float _missTimer;                        // seconds off has stayed high while saturated (no progress)
        private static float _missAnchorOff;                    // off captured at the start of the current stall window
        private static float _yawWagWinStart;                   // start of the low-speed yaw-wag window
        private static int   _yawWagFlips;                      // yaw output sign-flips this window (low-speed wag)
        private static float _prevSignYW;                       // last non-trivial yaw output sign (wag counter)
        private static float _wobbleWinStart;                   // start of the high-speed roll-wobble window
        private static int   _wobbleFlips;                      // roll output sign-flips this window (high-speed wobble)
        private static float _prevSignRW;                       // last non-trivial roll output sign (wobble counter)
        private static float _anOvershootT, _anOverRollT, _anHuntT, _anMissT, _anYawWagT, _anWobbleT; // per-type cooldown stamps

        // Anomaly index + on-screen flash (v0.25.2). Every anomaly that clears its cooldown gets the next
        // sequential number (monotonic across the whole session, so #N is unambiguous even across respawns).
        // OnGUI flashes "#N type" for AnomalyFlashSec so you can call out which one felt wrong while flying.
        public  const  float AnomalyFlashSec = 4f;              // how long the on-screen index stays up (or until the next)
        private static int   _anomalyIndex;                     // running count of fired anomalies this session
        public  static int   LastAnomalyIndex;                  // surfaced to OnGUI
        public  static string LastAnomalyType = "";             // surfaced to OnGUI
        public  static float LastAnomalyTime  = -999f;          // Time.time of the last fired anomaly

        // Intent instrumentation (v0.26). The instructor classifies each frame into a ChasePhase so the
        // plan is legible on the HUD ("PHASE: ALIGN"), and emits ONE [maneuver] summary line per completed
        // turn — the "how did the planned path actually work out" record. Phase is surfaced always; the
        // maneuver tracker only runs while AnomalyLogging is on (it reuses the overshoot flag).
        public  static string LastPhase = "";                   // surfaced to OnGUI + the [anomaly] line
        private static bool   _manvActive;                      // a maneuver (off rose above AlignAngle) is in progress
        private static float  _manvStartT, _manvStartOff, _manvPeakOff; // start time / start & peak off
        private static float  _manvAlignT, _manvCaptureT;       // sec to first |phi|<20deg / first off<FineAngle (-1 = not yet)
        private static float  _manvPeakBank, _manvPeakG, _manvPeakRoll; // peak |bank| / |g| / |rollRate| over the maneuver
        private static float  _manvSettle;                      // sec off has stayed under FineAngle (capture-settle timer)
        private static bool   _manvOvershot;                    // an overshoot anomaly fired during this maneuver

        // Recent-frame ring buffer (v0.25.1): every FixedUpdate we stash a compact state snapshot here but
        // log NOTHING. When a detector fires, DumpTrail emits the last ~20 frames as a single [anomaly:trail]
        // line so the lead-UP to the event is visible (how the wag built / how the bank blew past) without
        // any continuous spam. Formatting only happens on a real anomaly, so the buffer itself is free.
        private struct AnFrame { public float t, off, bank, tgtBank, p, r, y, yr, rr, rf, spd, g; }
        private static readonly AnFrame[] _ring = new AnFrame[64];
        private static int   _ringHead;     // next write index
        private static int   _ringCount;    // valid entries (<= _ring.Length)
        private static float _lastTrailT;   // one trail dump per second across all anomaly types
        private static float _rollRateFilt;    // low-pass-filtered roll rate feeding the damping term (anti high-speed roll PIO)

        // Measured-reactive yaw-weakness estimate (v0.35). _yawEffFilt is a low-passed raw measure of how
        // much nose-yaw rate each unit of yaw command is buying (deg/s per stick); _yawWeak in [0,1] rises
        // when the rudder is empirically failing to CLOSE the heading error despite being commanded toward
        // it, and decays toward 0 otherwise. It has memory (the LPF) so it pre-biases the next nudge in the
        // same regime. _prevAzErr feeds the heading-closing rate. Reset on engage with the other detectors.
        private static float _yawWeak;          // 0 = rudder is working, 1 = rudder ineffective -> bank-and-pull
        private static float _yawEffFilt;       // low-passed |yawRate|/|outY| (diagnostic, logged to the CSV)
        private static float _prevAzErr;        // last frame's azimuth error (deg) for d|azErr|/dt
        private static bool  _prevAzErrValid;   // skip the first frame's bogus derivative
        private static float _closeRateFilt;    // low-passed heading-closing rate (deg/s) — noise-robust derivative

        // Hover / "flown-like-a-helicopter" regime state (v0.43). _collective latches the airframe class
        // (true = takeoffDistance==0 = heli/hover-VTOL) on engage; _heliBlend in [0,1] is the per-frame
        // regime blend (0 = fixed-wing bank-to-turn, 1 = hover yaw-to-point), computed in Apply from
        // forward airspeed + AutoHover. _vFwd/_hoverOn are surfaced for the CSV/trace. EvolvedLegacy only.
        internal static bool  _collective;      // airframe is collective (heli / hover-VTOL); fixed-wing => always 0 heliBlend
        internal static float _heliBlend;       // 0 = full fixed-wing, 1 = full hover yaw-to-point
        internal static float _vFwd;            // forward-direction component of velocity (m/s) — the regime signal
        internal static float _speed;           // total airspeed magnitude (m/s) — surfaced for the debug HUD
        internal static bool  _hoverOn;         // game's AutoHover engaged this frame (forces heliBlend=1)

        public static bool IsFlying => _active;

        // Toggle Fly Level. On engage, latch the current horizontal heading so we hold THIS course (not
        // wherever the nose happens to drift). Capturing the heading here keeps Apply() purely reactive.
        public static void ToggleFlyLevel(Aircraft aircraft)
        {
            FlyLevelActive = !FlyLevelActive;
            if (FlyLevelActive && aircraft != null)
            {
                Vector3 f = aircraft.transform.forward;
                Vector3 h = new Vector3(f.x, 0f, f.z);
                // Degenerate (pointing near straight up/down): fall back to current velocity heading, else
                // keep whatever we last held so we never latch a zero vector.
                if (h.sqrMagnitude < 1e-4f && aircraft.rb != null)
                    h = new Vector3(aircraft.rb.velocity.x, 0f, aircraft.rb.velocity.z);
                if (h.sqrMagnitude >= 1e-4f) _levelHeading = h.normalized;
                _iPitch = _iYaw = 0f; // don't carry marker-chase windup into the level hold
            }
            if (Cfg.DebugLogging.Value)
                WTMouseAimPlugin.Log.LogInfo($"[flylevel] {(FlyLevelActive ? "ON  hdg=" + _levelHeading.ToString("0.00") : "OFF")}");
        }

        // Latest pilot G-tolerance (PilotPlayerState.pilotStrength), 1 = fine, <0.2 = blacked/redded out
        // and the game has zeroed all stick input. Surfaced for the overlay's G-LOC warning. Seeded to 1
        // so we never flash the warning before the first read.
        public static float PilotStrength { get; private set; } = 1f;

        // The instructor's own slewed stick command this frame (before any manual override blends on top),
        // surfaced for the top-left debug readout: "what the instructor is saying". 0 when not flying.
        public static float LastPitch { get; private set; }
        public static float LastRoll  { get; private set; }
        public static float LastYaw   { get; private set; }

        // The player's manual stick/keyboard/pedal source. Null until Rewired is ready.
        private static Player RewiredPlayer()
        {
            if (_rewired == null && ReInput.isReady)
                _rewired = ReInput.players.GetPlayer(0);
            return _rewired;
        }

        // Override-on-touch for one axis: push past the deadzone and you INSTANTLY take that axis
        // (engagement->1, manual value frozen); release and engagement eases 1->0 over ManualReturnTime so
        // the axis hands smoothly back to the still-running chase output. Returns the blended command.
        private static float BlendManual(float manual, float chase, ref float eng, ref float applied,
                                         float dz, float ret, float dt)
        {
            manual = Mathf.Clamp(manual, -1f, 1f);
            if (Mathf.Abs(manual) > dz) { applied = manual; eng = 1f; }   // instant grab
            else eng = Mathf.MoveTowards(eng, 0f, dt / Mathf.Max(0.01f, ret)); // ease back to mouse-aim
            return Mathf.Lerp(chase, applied, eng);
        }

        // Called from the prefix. Returns true if WE own the stick (native should be skipped).
        public static bool BeginFrame(Aircraft aircraft, bool fixedWing, float pilotStrength)
        {
            PilotStrength = pilotStrength; // surface for the overlay's G-LOC warning (runs every FixedUpdate)
            _collective = !fixedWing;      // latch airframe class for the hover-regime blend (EvolvedLegacy)
            // NOTE: deliberately NOT gated on Guards.MenusOpen(). While a menu/map is up the sim keeps
            // running, and we want the instructor to keep flying the plane toward the frozen marker
            // (where you last aimed) instead of disengaging and flying straight. The mouse is frozen
            // separately in AimRig, so the aim direction holds; only the autopilot keeps tracking it.
            // NOTE: also deliberately NOT gated on camera mode. If you aim somewhere in the cockpit and
            // then switch to an external/orbit camera, the instructor keeps flying the plane toward that
            // marker (the reticle is drawn in those views too). Only the mouse-driven aiming is cockpit-
            // only; the chase itself runs in any view.
            bool active =
                Cfg.Enabled.Value &&
                Cfg.WriteControl.Value &&
                (fixedWing || Cfg.ControlRotorcraft.Value) &&  // collective aircraft (helis/VTOLs) only when opted in
                pilotStrength >= 0.2f &&                 // not blacked out
                aircraft.cockpit != null && !aircraft.cockpit.IsDetached();

            if (active && !_wasActive)
            {
                // Engage: seed our output from the current (native) stick so the takeover is smooth,
                // and hide the native virtual-joystick crosshair so it can't compete for the mouse.
                var ci = aircraft.GetInputs();
                _outP = ci.pitch; _outR = ci.roll; _outY = ci.yaw;
                _iPitch = _iYaw = 0f;  // fresh integrator on each engage
                _offMin = float.MaxValue; _prevOff = 0f; _missTimer = 0f; _missAnchorOff = 0f; // fresh anomaly detectors
                _flipsP = _flipsY = 0; _prevSignP = _prevSignY = 0f; _huntWinStart = 0f;
                _yawWagFlips = 0; _prevSignYW = 0f; _yawWagWinStart = 0f;
                _wobbleFlips = 0; _prevSignRW = 0f; _wobbleWinStart = 0f;
                _manvActive = false; _manvSettle = 0f; _manvOvershot = false; LastPhase = ""; // fresh maneuver tracker
                _ringHead = _ringCount = 0; // clear the context buffer for this command
                _prevFwdValid = false; // don't compute a huge rotation rate across the engage gap
                _yawWeak = 0f; _yawEffFilt = 0f; _closeRateFilt = 0f; _prevAzErrValid = false; // fresh yaw-weakness estimate
                HideNativeVirtualJoystick();
                WTMouseAimPlugin.Log.LogInfo($"WT Mouse Aim: ON ({(fixedWing ? "fixed-wing" : "rotorcraft")}) — chase control engaged.");
            }
            else if (!active && _wasActive)
            {
                _disRamp = 1f; // begin smooth handoff back to native
                WTMouseAimPlugin.Log.LogInfo("WT Mouse Aim: OFF — native control.");
            }

            _active = active;
            _wasActive = active;
            return active;
        }

        // Called from the postfix every frame (native may or may not have run).
        public static void Apply(Aircraft aircraft)
        {
            var ci = aircraft.GetInputs();
            if (ci == null) return;
            float dt = Time.fixedDeltaTime;

            if (_active)
            {
                Transform t = aircraft.transform;

                // The direction the instructor is flying the nose toward. Normally the world-locked aim
                // marker; in Fly Level mode it's the latched heading flown as TRUE level — see below.
                bool flyLevel = Cfg.FlyLevelEnabled.Value && FlyLevelActive;
                Vector3 aimDir;
                if (flyLevel)
                {
                    // "True level" = the VELOCITY VECTOR on the horizon (zero climb rate), not the nose.
                    // The plane flies at +AoA, so a nose-on-horizon target would leave the flight path
                    // descending by the AoA (the old behaviour — and the source of the unexplained
                    // sink/overshoot). Pitch the locked horizontal heading UP by the live angle-of-attack
                    // so that, once settled, the velocity vector sits level. AoA is the signed angle of the
                    // velocity off the nose about the body-right axis — the exact quantity the game's own
                    // RelaxedStabilityController reads (TargetCalc.GetAngleOnAxis, decompiled). Guard a
                    // near-zero velocity (no meaningful AoA) and clamp so a stall spike can't pitch wildly.
                    float aoaDeg = 0f;
                    if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 4f)
                        aoaDeg = Mathf.Clamp(
                            TargetCalc.GetAngleOnAxis(t.forward, aircraft.rb.velocity, t.right), -20f, 20f);
                    // Rotate the horizontal heading up about world-right-of-heading by the AoA, so the nose
                    // leads the (level) velocity vector by exactly the AoA.
                    Vector3 hRight = Vector3.Cross(Vector3.up, _levelHeading); // points to the right of the course
                    aimDir = hRight.sqrMagnitude > 1e-6f
                        ? (Quaternion.AngleAxis(-aoaDeg, hRight) * _levelHeading).normalized
                        : _levelHeading;
                }
                else aimDir = AimRig.AimForward;

                // Marker direction in the body frame (unit): x = right, y = up, z = forward.
                Vector3 local = t.InverseTransformDirection(aimDir);
                float off = Vector3.Angle(t.forward, aimDir); // degrees the nose is off the target
                float sens = Cfg.PitchYawSensitivity.Value;

                // Nose rotation rate (world delta of the forward axis), split into the body up/right
                // components: how fast the nose is currently pitching up and yawing right. This is the
                // PLANE'S own motion (independent of the mouse), so damping on it never fights a flick.
                float pitchRate = 0f, yawRate = 0f, rollRate = 0f, noseTurnDeg = 0f;
                if (_prevFwdValid && dt > 1e-5f)
                {
                    Vector3 noseRate = (t.forward - _prevFwd) / dt;
                    pitchRate = Vector3.Dot(noseRate, t.up);    // +: nose swinging up
                    yawRate   = Vector3.Dot(noseRate, t.right); // +: nose swinging right
                    // Roll rate about the body forward axis: as the plane rolls right (right wing dropping)
                    // the up axis leans toward the right wing, so d(up)/dt . right > 0. Forward barely moves
                    // in a pure roll, so this needs the up axis, not noseRate.
                    rollRate  = Vector3.Dot((t.up - _prevUp) / dt, t.right); // +: rolling right
                    noseTurnDeg = Vector3.Angle(t.forward, _prevFwd); // total nose rotation this FixedUpdate
                }
                _prevFwd = t.forward;
                _prevUp  = t.up;
                _prevFwdValid = true;

                // PITCH/YAW: straight PROPORTIONAL pull toward the marker, MINUS a damping term on the
                // nose's own turn rate so the inputs fade out as it closes instead of overshooting (the
                // cure for the side-to-side wobble). In this game nose-up = NEGATIVE ci.pitch, so for an
                // above-nose marker -local.y already gives a pull-up command; +pitchRate*damp opposes the
                // climb as the nose arrives. The command tapers linearly to 0 as the offset closes, so the
                // nose decelerates into the marker instead of saturating full-deflection right up to the
                // crossing (which, with zero damping, was the limit-cycle hunt). Convergent (command(0)=0):
                // the nose still arrives and zeroes out — NOT a joystick.
                //
                // (v0.18's FineShape sqrt "fine assist" was removed in v0.19: it boosted small offsets so
                // hard that even a 6deg offset saturated the rudder, which is exactly what made it hunt.
                // v0.22 logs showed the opposite failure: at ~1deg of pure azimuth error the yaw P-term
                // was ~0.05 stick and the heading closed with a ~6 s time constant — "never aligns". The
                // FINE-CAPTURE boost below is the measured middle ground: a LINEAR gain ramp (max 1+boost
                // at zero offset, gone at FineAngle) instead of the sqrt's unbounded low-end slope, with
                // the rate damping and the output slew limiter still active against hunting.)
                float damp = Cfg.ChaseDamping.Value;
                float fineBlend = Mathf.Clamp01(1f - off / Mathf.Max(1f, Cfg.FineAngle.Value));
                float fineGain  = 1f + Cfg.FineGainBoost.Value * fineBlend;

                // BODY-FRAME ROLL-THEN-PULL (v0.26). One shared plan for all three axes: roll the lift
                // vector (body-up) onto the target, then pull up into it. Attitude-robust at any pitch (no
                // horizon-frame bank that degenerates when the nose is steep) and a pull is ALWAYS a pull
                // (no bunt). The target's bearing AROUND the boresight is the heart of it:
                //   phi       = where the target sits, measured from straight-up (the lift vector): 0 = dead
                //               above the nose, +90 = off the right wing, -90 = off the left, ±180 = below.
                //   alignFrac = signed lift-vector alignment: +1 target above, 0 to the side, -1 below.
                float phi       = Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
                float lateral   = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                float alignFrac = lateral > 1e-4f ? local.y / lateral : 1f;

                // REGIME BLEND — a continuous ramp from the fine direct-nudge law (small errors) to the
                // roll-to-align law (big turns). 0 inside FineAngle, 1 at/above AlignAngle.
                float bigTurn = Mathf.Clamp01((off - Cfg.FineAngle.Value)
                                              / Mathf.Max(1f, Cfg.AlignAngle.Value - Cfg.FineAngle.Value));

                // WORLD-FRAME AZIMUTH ERROR (deg, + = marker right of the nose heading) — the heading the
                // rudder is asked to close. Drives the fine bank servo AND the v0.35 yaw-weakness estimate,
                // so it's computed up here ahead of the pitch/roll commands. Degenerate headings (nose
                // straight up/down) zero it for this frame.
                Vector3 aimHW  = new Vector3(aimDir.x, 0f, aimDir.z);
                Vector3 noseHW = new Vector3(t.forward.x, 0f, t.forward.z);
                float azErr = (aimHW.sqrMagnitude > 1e-6f && noseHW.sqrMagnitude > 1e-6f)
                    ? Vector3.SignedAngle(noseHW, aimHW, Vector3.up) : 0f;

                // MEASURED YAW-WEAKNESS (v0.35). Is the rudder actually CLOSING the heading? Watch the
                // standing azimuth error and how fast it's shrinking. A small side nudge the rudder handles
                // well (low speed) closes quickly -> weakness stays ~0, near-stock feel. The same nudge at
                // high speed lingers, because rudder/sideslip barely turns the flight path -> weakness rises
                // and the assist banks-and-pulls instead. Purely measured (no speed formula); the closing
                // rate is low-passed so frame-to-frame jitter doesn't fake progress. _yawEffFilt is a raw
                // |yawRate|/|outY| effectiveness, kept ONLY for the CSV so the estimator can be calibrated.
                // _outY here is last frame's committed yaw output (this frame's is slewed below).
                float yawEffInst = Mathf.Abs(_outY) > 0.1f ? Mathf.Abs(yawRate) / Mathf.Abs(_outY) : _yawEffFilt;
                _yawEffFilt += (dt / (0.5f + dt)) * (yawEffInst - _yawEffFilt);

                float closeRaw = _prevAzErrValid ? (Mathf.Abs(_prevAzErr) - Mathf.Abs(azErr)) / Mathf.Max(dt, 1e-4f) : 0f;
                _closeRateFilt += (dt / (0.2f + dt)) * (closeRaw - _closeRateFilt);
                bool manualNow = _engP > 0f || _engR > 0f || _engY > 0f;
                float weakInst = 0f;
                if (Cfg.YawAssistEnabled.Value && !manualNow && !flyLevel && Mathf.Abs(azErr) > 1.5f)
                    weakInst = 1f - Mathf.Clamp01(_closeRateFilt / 6f); // 0 = heading closing fast, 1 = stalled
                // Asymmetric low-pass: attack at YawAssistResponse, release ~4x slower so the estimate has
                // MEMORY — once the rudder is shown weak in this regime, the next nudge is already assisted.
                float wTau = weakInst > _yawWeak ? Cfg.YawAssistResponse.Value : Cfg.YawAssistResponse.Value * 4f;
                _yawWeak = Mathf.Clamp01(_yawWeak + (dt / (wTau + dt)) * (weakInst - _yawWeak));
                _prevAzErr = azErr; _prevAzErrValid = true;

                // ASSIST FACTOR — shift this side correction into bank-and-pull, strong only when the rudder
                // is measured weak AND we're in the small/mid regime. Fades out as bigTurn->1 (the v0.26
                // roll-to-align law already banks-and-pulls big turns), so it targets exactly the small-nudge
                // case that stalls on the rudder today.
                float assist = Cfg.YawAssistEnabled.Value ? _yawWeak * (1f - bigTurn) * Cfg.YawAssistStrength.Value : 0f;

                // FINE BANK SERVO target bank (v0.25), now assist-aware (v0.35): collapse the heading
                // deadband and bank harder when the rudder is weak, so a small side nudge actually banks to
                // turn instead of waiting on the rudder. Still capped at MaxBankAngle. Computed here (ahead
                // of pitch) so the coordinating pull below can size itself off the commanded bank.
                float azDz   = Cfg.FineBankDeadzone.Value * (1f - assist);
                float azBank = Mathf.Abs(azErr) <= azDz ? 0f : (Mathf.Abs(azErr) - azDz) * Mathf.Sign(azErr);
                // v0.36: when the rudder is measured weak, bank STEEPLY (BankAuthGain) so a small side
                // nudge commands a loaded-turn bank instead of the shallow one that mushes. This LINEAR
                // (bank proportional to error) servo is the low-speed / strong-yaw behaviour, left intact.
                float bankGain   = Cfg.FineBankGain.Value * (1f + Cfg.BankAuthGain.Value * assist);
                float linBank    = azBank * bankGain;
                // v0.37 — THE high-speed fix. The linear servo can't serve both 150 m/s and 400 m/s with
                // one slope: a given bank turns ~3x faster slow than fast, so the slope that nails the slow
                // plane leaves the fast one mushing the last few degrees (12deg bank @ 400 m/s ~ 0.3deg/s).
                // Size the bank from a target TURN-RATE instead: omega = k*azErr (rad/s), and the bank that
                // physically holds it is phi = atan(omega*V/g). V is in the formula, so the SAME error asks
                // for a steep loaded bank when fast and a gentle one when slow — automatically. Blend linear
                // -> turn-rate by measured yaw-weakness*(1-bigTurn): low speed / strong yaw keep the linear
                // servo exactly; weak-yaw/high-speed take the loaded bank. Self-rolls out as azErr -> 0.
                //   v0.37.1: the turn-rate path sees a NEAR-RAW error (a tiny fixed noise gate only), NOT the
                //   big anti-wobble deadzone azBank carries. With azBank the deadzone (~1-1.5deg in the tail)
                //   was subtracted before the turn-rate math, so a real 2deg error was seen as ~0.6deg, the
                //   commanded bank collapsed to ~25deg (max ~1.1g) and the loaded turn unwound at a CLIFF
                //   around 2deg — the v0.37 tail plateaued there. atan() on the raw error rolls out smoothly
                //   instead. The linear servo keeps its full deadzone (low-speed wobble guard) untouched.
                float vMag     = aircraft.rb != null ? aircraft.rb.velocity.magnitude : 200f;
                _speed = vMag;  // surface total airspeed for the debug HUD

                // HOVER REGIME BLEND (v0.43, used by EvolvedLegacy). Bank-to-turn needs forward speed to
                // produce a turn; on a collective aircraft (heli / hover-VTOL) at low FORWARD speed it just
                // lays the aircraft over uselessly. _heliBlend ramps 0->1 as the nose-direction speed drops
                // from HeliForwardSpeed to HeliHoverSpeed, and is forced to 1 while the game's AutoHover owns
                // attitude. Fixed-wing airframes are always 0 (law identical to the graduated v0.42).
                _vFwd    = aircraft.rb != null ? Vector3.Dot(aircraft.rb.velocity, t.forward) : vMag;
                _hoverOn = aircraft.IsAutoHoverEnabled();
                _heliBlend = _collective
                    ? Mathf.Clamp01((Cfg.HeliForwardSpeed.Value - _vFwd)
                                    / Mathf.Max(1f, Cfg.HeliForwardSpeed.Value - Cfg.HeliHoverSpeed.Value))
                    : 0f;
                if (_hoverOn) _heliBlend = 1f;

                float azTR     = Mathf.Abs(azErr) <= 0.5f ? 0f : (Mathf.Abs(azErr) - 0.5f) * Mathf.Sign(azErr); // raw error, noise gate only
                float omegaDes = azTR * Mathf.Deg2Rad * Cfg.AssistTurnRateGain.Value;            // rad/s, signed
                float bankTR   = Mathf.Atan(omegaDes * Mathf.Max(50f, vMag) / 9.81f) * Mathf.Rad2Deg; // deg, signed
                float bankBlend  = Cfg.YawAssistEnabled.Value ? _yawWeak * (1f - bigTurn) : 0f;
                float targetBank = Mathf.Clamp(Mathf.Lerp(linBank, bankTR, bankBlend),
                                               -Cfg.MaxBankAngle.Value, Cfg.MaxBankAngle.Value);

                // PITCH ANTI-OVERSHOOT BRAKE (v0.25): extra rate damping that ramps in as the nose nears
                // the target (off small) while it's still swinging, so a fast large-angle pull (takeoff
                // climb-out) decelerates ONTO the marker instead of crossing it and settling back. Fades
                // out beyond ~25deg so it never slows the initial pull-in, and opposes RATE only so it
                // can't fight a settled attitude (vanishes once the nose stops turning).
                float brakeGate = Mathf.Clamp01(1f - off / 25f);
                float pitchDamp = damp + Cfg.PitchBrake.Value * brakeGate;

                // FINE-REGIME LEAKY INTEGRATOR (v0.24). The game's fly-by-wire reads our pitch/yaw as a
                // commanded ANGULAR RATE and PID-tracks it (decompiled ControlsFilter.FlyByWire), so a pure
                // proportional outer loop asymptotes and parks a fraction of a degree short — and on relaxed-
                // stability airframes RelaxedStabilityController dilutes small pitch toward an AoA-cancel.
                // This integrator winds in exactly the steady bias those two leave out. It's the same error
                // signal as the proportional term (so it reinforces), gated to the fine cone (fineBlend),
                // leaked toward zero so it can't run away, hard-capped, and suspended on manual override /
                // Fly Level. As the error -> 0 the steady value -> 0, so it stays convergent (no limit cycle).
                float iCap = Cfg.FineIntegralCap.Value;
                if (Cfg.FineIntegralGain.Value > 0f && iCap > 0f && !flyLevel)
                {
                    float ki = Cfg.FineIntegralGain.Value, leak = Cfg.FineIntegralLeak.Value;
                    if (_engP > 0f) _iPitch = 0f; // you own pitch — don't wind against your stick
                    else _iPitch = Mathf.Clamp(_iPitch + (-local.y * ki * fineBlend - _iPitch * leak) * dt, -iCap, iCap);
                    if (_engY > 0f) _iYaw = 0f;
                    else _iYaw   = Mathf.Clamp(_iYaw   + ( local.x * ki * fineBlend - _iYaw   * leak) * dt, -iCap, iCap);
                }
                else { _iPitch = _iYaw = 0f; }

                // ---- PER-AXIS CONTROL LAW (A/B switch, v0.38) ----------------------------------------
                // Branch on the active law to turn the shared pre-compute above into the three target stick
                // values tgtP/tgtR/tgtY. Everything above (aimDir/local/off/phi/rates/azErr/flight state/
                // yaw-weakness/targetBank/integrator) and everything below (slew, manual override, write-out,
                // anomaly/recorder/phase) is SHARED across all laws — only the per-axis shaping differs here.
                // pullGate/yawScale/coordPull are surfaced for the debug trace (the only post-stage consumers).
                float tgtP, tgtR, tgtY, pullGate, yawScale, coordPull;
                switch (Cfg.ControlLawMode.Value)
                {
                    case NuclearOptionMouseAim.ControlLawMode.BankToTurn:
                        ApplyBankToTurn(t, local, off, vMag, sens, fineGain, alignFrac, bigTurn, targetBank, azErr,
                            phi, pitchRate, yawRate, rollRate, pitchDamp, damp, assist, dt,
                            out tgtP, out tgtR, out tgtY, out pullGate, out yawScale, out coordPull);
                        break;
                    case NuclearOptionMouseAim.ControlLawMode.EvolvedLegacy:
                        ApplyEvolvedLegacy(t, local, off, vMag, sens, fineGain, alignFrac, bigTurn, targetBank, azErr,
                            phi, pitchRate, yawRate, rollRate, pitchDamp, damp, assist, dt,
                            out tgtP, out tgtR, out tgtY, out pullGate, out yawScale, out coordPull);
                        break;
                    default: // Legacy (the hard-won v0.37 law; default during development)
                        ApplyLegacy(t, local, off, vMag, sens, fineGain, alignFrac, bigTurn, targetBank, azErr,
                            phi, pitchRate, yawRate, rollRate, pitchDamp, damp, assist, dt,
                            out tgtP, out tgtR, out tgtY, out pullGate, out yawScale, out coordPull);
                        break;
                }

                // Slew-rate-limit the chase outputs (anti-jerk against mouse jitter / a fresh flick).
                // Symmetric on all three axes. _out* stay PURE chase values — manual override blends on
                // TOP of them below, so when you release a manual axis it hands back to the live chase.
                float slew = Cfg.OutputSlew.Value * dt;
                _outP = Mathf.MoveTowards(_outP, tgtP, slew);
                _outY = Mathf.MoveTowards(_outY, tgtY, slew);
                _outR = Mathf.MoveTowards(_outR, tgtR, slew);
                LastPitch = _outP; LastYaw = _outY; LastRoll = _outR; // surface for the top-left readout

                // Manual override-on-touch (per axis) — ALL THREE AXES IDENTICAL, roll included: when you
                // push an axis past the deadzone your input takes it over instantly; when you're not
                // touching it, the instructor flies it. Pitch/yaw hand back to their proportional pull;
                // roll hands back to the instructor's bank-to-turn (which levels the wings on-target). The
                // chase keeps running underneath (_out*), so the handback is seamless on every axis.
                float pOut = _outP, rOut = _outR, yOut = _outY;
                if (Cfg.ManualOverride.Value)
                {
                    var pl = RewiredPlayer();
                    if (pl != null)
                    {
                        float dz  = Cfg.ManualDeadzone.Value;
                        float ret = Cfg.ManualReturnTime.Value;
                        pOut = BlendManual(pl.GetAxis("Pitch"), _outP, ref _engP, ref _mApplyP, dz, ret, dt);
                        rOut = BlendManual(pl.GetAxis("Roll"),  _outR, ref _engR, ref _mApplyR, dz, ret, dt);
                        yOut = BlendManual(pl.GetAxis("Yaw"),   _outY, ref _engY, ref _mApplyY, dz, ret, dt);
                        // A stick/pedal nudge drops Fly Level — you've taken the controls back.
                        if (FlyLevelActive && (_engP >= 1f || _engR >= 1f || _engY >= 1f))
                            ToggleFlyLevel(aircraft);
                    }
                }
                else { _engP = _engR = _engY = 0f; }

                ci.pitch = Mathf.Clamp(pOut, -1f, 1f);
                ci.roll  = Mathf.Clamp(rOut, -1f, 1f);
                ci.yaw   = Mathf.Clamp(yOut, -1f, 1f);
                _disRamp = 1f; // primed for the next disengage

                // Current bank (deg, + = right wing down) — shared by the anomaly detectors and the trace.
                float bank = -Mathf.Asin(Mathf.Clamp(t.right.y, -1f, 1f)) * Mathf.Rad2Deg;

                // PHASE (v0.26): classify the instructor's plan this frame so it's legible on the HUD and in
                // the [anomaly] line. The plan is "roll the lift vector onto the target, then pull up into it".
                LastPhase = ClassifyPhase(flyLevel, off, phi, bigTurn, noseTurnDeg);

                // ANOMALY DETECTION (v0.25): event-only logger. Stays silent in normal flight and fires a
                // single [anomaly] line when a command misbehaves (overshoot / over-roll / hunt / miss).
                // This is the cheap-to-hand-back logger; the verbose [chase] trace below stays off by default.
                // TrackManeuver runs alongside it (reuses the overshoot flag) and emits one [maneuver] summary
                // per completed turn — the "how did the planned path actually work out" record.
                if (Cfg.AnomalyLogging.Value)
                {
                    DetectAnomalies(aircraft, off, bank, targetBank, bigTurn, yawRate, rollRate, dt);
                    TrackManeuver(aircraft, off, phi, bank, rollRate, dt);
                }

                // MANEUVER RECORDER (v0.35): when the user has armed a capture (RecordKey), write the live
                // control state to the CSV at RecordRateHz. Reuses everything already computed this frame —
                // no recompute. Throttling/IO live inside Sample(); it's a no-op when not recording.
                if (ManeuverRecorder.IsRecording)
                {
                    float elevErr = (Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f))
                                   - Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f))) * Mathf.Rad2Deg;
                    float spdR = aircraft.rb != null ? aircraft.rb.velocity.magnitude : -1f;
                    float aoaR = 0f;
                    if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 4f)
                        aoaR = TargetCalc.GetAngleOnAxis(t.forward, aircraft.rb.velocity, t.right);
                    ManeuverRecorder.Sample(off, azErr, elevErr, phi, bigTurn, bank, targetBank,
                        _outP, _outR, _outY, pitchRate, yawRate, rollRate, _yawEffFilt, _yawWeak,
                        spdR, aoaR, aircraft.gForce, LastPhase, flyLevel, _engP, _engR, _engY, _heliBlend, _vFwd,
                        _rollRateFilt, _iPitch, _iYaw, bankTR, bankBlend);
                }

                // Chase trace. Normal cadence ~2.5/sec; inside 10deg ("fine capture") ~5/sec at higher
                // precision — the last-few-degrees stall is exactly what we're diagnosing. (v0.44: halved
                // from 5/10/sec so a DebugLogging run stays readable / low-context without losing shape.)
                //   off       : nose->marker angle before this frame's command takes effect
                //   elevE/azE : world-frame error split (elevation vs azimuth, deg). A persistent elevE
                //               with azE~0 is gravity droop; a persistent azE is lateral residual.
                //   P/D (p/y) : the two additive terms inside tgt, per axis — proportional pull vs the
                //               rate-damping bite. If D ~cancels P while off stays put, the damping is
                //               strangling the final closure (the prime suspect).
                //   spd/bank/g: flight-state context for the residual.
                if (Cfg.DebugLogging.Value)
                {
                    bool fine = off < 10f; // fine-capture regime: the regime under investigation
                    if (Time.time - _lastChaseLog >= (fine ? 0.2f : 0.4f) &&
                        (off > 0.02f || Mathf.Abs(_outP) > 0.005f || Mathf.Abs(_outY) > 0.005f || Mathf.Abs(_outR) > 0.005f))
                    {
                        _lastChaseLog = Time.time;
                        float pTermP = -local.y * sens * fineGain * pullGate, dTermP = pitchRate * pitchDamp; // tgtP = (P+D)*PitchGain
                        float pTermY =  local.x * sens * fineGain * yawScale, dTermY = -yawRate  * damp;       // tgtY = (P+D)*YawGain
                        float elevE = (Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f))) * Mathf.Rad2Deg;
                        float spd  = aircraft.rb != null ? aircraft.rb.velocity.magnitude : -1f;
                        string f = fine ? "0.000" : "0.00";
                        WTMouseAimPlugin.Log.LogInfo(
                            $"[chase] t={Time.time:0.000} off={off:0.000}deg phi={phi:0.0} bigTurn={bigTurn:0.00} elevE={elevE:0.00} azE={azErr:0.00} noseTurn={noseTurnDeg:0.000} " +
                            $"fineG={fineGain:0.00} pull={pullGate:0.00} yawSc={yawScale:0.00} phase={LastPhase} tgtBank={targetBank:0.0} yawWeak={_yawWeak:0.00} assist={assist:0.00} coordPull={coordPull:0.000} iP/iY=({_iPitch.ToString(f)},{_iYaw.ToString(f)}){(flyLevel ? " LVL" : "")} " +
                            $"P(p/y)=({pTermP.ToString(f)},{pTermY.ToString(f)}) D(p/y)=({dTermP.ToString(f)},{dTermY.ToString(f)}) " +
                            $"tgt P/R/Y=({tgtP.ToString(f)},{tgtR.ToString(f)},{tgtY.ToString(f)}) " +
                            $"out P/R/Y=({_outP.ToString(f)},{_outR.ToString(f)},{_outY.ToString(f)}) " +
                            $"fin=({ci.pitch:0.00},{ci.roll:0.00},{ci.yaw:0.00}) man=({_engP:0.0},{_engR:0.0},{_engY:0.0}) " +
                            $"spd={spd:0} bank={bank:0.0} g={aircraft.gForce:0.0}");
                    }
                }
            }
            else if (_disRamp > 0f)
            {
                // Disengaging: native just wrote ci. Ramp from our last output back to native.
                _disRamp = Mathf.Max(0f, _disRamp - Cfg.AuthorityRamp.Value * dt);
                float a = _disRamp;
                ci.pitch = Mathf.Clamp(Mathf.Lerp(ci.pitch, _outP, a), -1f, 1f);
                ci.roll  = Mathf.Clamp(Mathf.Lerp(ci.roll,  _outR, a), -1f, 1f);
                ci.yaw   = Mathf.Clamp(Mathf.Lerp(ci.yaw,   _outY, a), -1f, 1f);
                LastPitch = LastYaw = LastRoll = 0f; // instructor not flying — readout reads zero
            }
            else { LastPitch = LastYaw = LastRoll = 0f; }
        }

        // ---- CONTROL LAW: LEGACY (v0.37) ---------------------------------------------------------
        // The accreted v0.37 per-axis law, extracted VERBATIM from Apply (pure refactor, no behaviour
        // change — with ControlLawMode=Legacy the mod is byte-for-byte identical to before). Takes the
        // shared pre-computed locals from Apply, mutates the legacy member integrators/filters it already
        // owned (_iPitch/_iYaw are wound above; _rollRateFilt is the roll-rate low-pass), and returns the
        // three target stick values plus the pullGate/yawScale/coordPull terms the debug trace logs.
        private static void ApplyLegacy(
            Transform t, Vector3 local, float off, float vMag, float sens, float fineGain, float alignFrac, float bigTurn,
            float targetBank, float azErr, float phi, float pitchRate, float yawRate, float rollRate,
            float pitchDamp, float damp, float assist, float dt,
            out float tgtP, out float tgtR, out float tgtY,
            out float pullGate, out float yawScale, out float coordPull)
        {
            // off/vMag are unused by Legacy (Phase-1 flight-state inputs added to the shared signature);
            // it senses speed via the targetBank computed in Apply. Kept for signature parity across laws.
            // PITCH — pull up toward the target, gated so a big turn only pulls once the lift vector is
            // ON it (and NEVER pushes: the gate is clamped at 0, killing the negative-G bunt the old
            // |local.y| symmetric coord term produced when a roll swung the target momentarily below the
            // nose). In the fine cone (bigTurn->0) the gate is 1 — the old direct pull, so a gentle
            // nose-down to a low marker is still allowed. -local.y is the pull command (nose-up = -pitch).
            pullGate = Mathf.Lerp(1f, Mathf.Clamp01(alignFrac), Cfg.RollPitchCoordination.Value * bigTurn);
            // COORDINATING PULL (v0.35, reshaped v0.36) — the "pitch INTO the bank" half of the assist
            // and the REAL driver of the correction. Once banked, a level turn needs back-pressure or
            // gravity just drops the nose and the bank does nothing (the v0.35 tail mushed at 0.7-0.9g
            // with ~0 pull). Add a nose-up pull (nose-up = NEGATIVE pitch) proportional to the commanded
            // bank, scaled by assist. v0.37: the pull stays at FULL strength outside CoordPullReleaseAngle
            // (~2 deg) and only eases inside it — so the loaded turn holds its G right through the tail of
            // the correction (the v0.36 taper over the whole 6 deg fine cone was already half-gone at 3 deg,
            // so the nose mushed) and then releases cleanly onto aim. With the turn-rate bank above keeping
            // sin(targetBank) high in the tail, this is what actually loads the G. ALWAYS a pull: clamped
            // >= 0 (never a push/bunt) and capped (CoordPullCap). Convergent: -> 0 as azErr -> 0 (taper)
            // and as the bank rolls out.
            float pullTaper = Mathf.Clamp01(Mathf.Abs(azErr) / Mathf.Max(0.5f, Cfg.CoordPullReleaseAngle.Value));
            coordPull = Mathf.Clamp(
                Cfg.CoordPullGain.Value * Mathf.Abs(Mathf.Sin(targetBank * Mathf.Deg2Rad))
                * pullTaper * assist,
                0f, Cfg.CoordPullCap.Value);
            tgtP = Mathf.Clamp((-local.y * sens * fineGain * pullGate + _iPitch + pitchRate * pitchDamp - coordPull) * Cfg.PitchGain.Value, -1f, 1f);

            // YAW — ease rudder authority down during a big turn (the logs showed yaw pinned ±1 adding to
            // the messy feel) so the bank + pull do the work; full authority returns in the fine cone for
            // final alignment. v0.36: ALSO fade the rudder P-term out as the assist rises (YawWeakFade) —
            // when the rudder is measured weak it's pure sideslip/drag doing nothing for the heading, so
            // commit to bank+pull instead. Keep the small capped _iYaw integrator for final low-speed
            // alignment (assist ~ 0 there, so this is identically stock at low speed).
            yawScale = Mathf.Lerp(1f, Cfg.TurnYawScale.Value, bigTurn);
            float yawWeakFade = 1f - Cfg.YawWeakFade.Value * assist;
            tgtY = Mathf.Clamp(( local.x * sens * fineGain * yawScale * yawWeakFade + _iYaw - yawRate * damp) * Cfg.YawGain.Value, -1f, 1f);

            // ROLL (v0.26): blend the FINE wings-level/azimuth bank servo (small errors) with a BODY-
            // FRAME roll-to-align (big turns), in matched sin-magnitude units so RollGain/RollDamping
            // keep their meaning. targetBank was computed up front (assist-aware bank servo) so the
            // coordinating pull could size off it; here it just becomes the roll error.
            //   eFine  — the v0.25 bank servo error: a target bank proportional to the heading (azimuth)
            //            error, capped at MaxBankAngle, null at that bank. t.right.y is the world-up
            //            component on the right wing (0 = level, <0 = right wing down), so
            //            (t.right.y + sin(targetBank)) is the bank error. Used inside the fine cone where
            //            the horizon bank is meaningful and the wings should level on-heading. v0.35: the
            //            deadband/gain that built targetBank are assist-aware, so when the rudder is weak
            //            a small side nudge banks instead of waiting on it (the wobble-guard deadband
            //            still applies at assist 0 — normal on-heading flight is unchanged).
            //   eAlign — roll the SHORT way to put the target at 12 o'clock: monotonic in phi (no false
            //            equilibrium except exactly ±180°, broken by any noise), so a target straight off
            //            the wing or below still rolls in the short way instead of pinning. This is
            //            attitude-robust where eFine degenerates (steep nose => meaningless horizon bank).
            // bigTurn blends fine->align; subtract the roll RATE (RollDamping) so it eases on without
            // overshooting. As the turn completes off shrinks, bigTurn->0, and the fine servo levels out.
            float eFine  = t.right.y + Mathf.Sin(targetBank * Mathf.Deg2Rad);
            float eAlign = Mathf.Clamp(phi / 90f, -1.5f, 1.5f);
            float rollErr = Mathf.Lerp(eFine, eAlign, bigTurn);

            // ROLL-RATE LOW-PASS (v0.31) — fix for the high-speed roll wobble. When level on-heading the
            // roll P-term (eFine) is ~0, so the command is essentially -rollRate*RollDamping*RollGain. The
            // rollRate is a one-frame finite difference (~60 Hz); at high dynamic pressure the airframe is
            // responsive enough that this DELAYED rate feedback flips from damping to DRIVING at ~6-7 Hz —
            // a self-sustaining limit cycle (logs: R=±0.05 tracking rr=±0.2, bank<0.2deg). Its amplitude is
            // set by the loop delay, not the gain, which is why the v0.30 qScale gain-cut to 0.35x left it
            // unchanged. Smoothing the rate (first-order LPF, time constant RollRateSmoothing) rolls off the
            // high-freq content so the damping only opposes real low-freq roll motion — breaking the cycle
            // while keeping turn damping. tau=0 -> raw rate (old behaviour).
            float rollTau = Cfg.RollRateSmoothing.Value;
            if (rollTau > 1e-4f) _rollRateFilt += (dt / (rollTau + dt)) * (rollRate - _rollRateFilt);
            else _rollRateFilt = rollRate;
            float rollRateF = _rollRateFilt;

            tgtR = Mathf.Clamp((rollErr - rollRateF * Cfg.RollDamping.Value) * Cfg.RollGain.Value, -1f, 1f);
        }

        // ---- CONTROL LAW: BANK-TO-TURN (Phase 1, repaired v0.41) ---------------------------------
        // Full-sphere point-and-pull law. ROLL the lift vector (body-up) onto the target via phi
        // (eAlign = phi/90, clamped ±1.5 — full authority at phi=±180, no dead spot below the nose),
        // then PULL with a speed-aware load factor; in the fine cone use a SIGNED direct nudge that
        // allows nose-down. bigTurn blends fine<->big-turn regimes (same as Legacy). No new config
        // added: reuses BankToTurnVmin/OmegaMax/Deadband/AssistTurnRateGain/RollGain/RollDamping/
        // RollRateSmoothing/PitchGain/YawGain/TurnYawScale/ChaseDamping and the wound _iPitch/_iYaw.
        private static void ApplyBankToTurn(
            Transform t, Vector3 local, float off, float vMag, float sens, float fineGain, float alignFrac, float bigTurn,
            float targetBank, float azErr, float phi, float pitchRate, float yawRate, float rollRate,
            float pitchDamp, float damp, float assist, float dt,
            out float tgtP, out float tgtR, out float tgtY,
            out float pullGate, out float yawScale, out float coordPull)
        {
            const float g = 9.81f;

            // --- ROLL: lift vector onto target, full-sphere (no phi=180 dead spot) ------------------
            // phi is the target's bearing around the boresight: 0=above/12-o'clock, ±90=off a wing,
            // ±180=below. eAlign = phi/90 (clamped to ±1.5) rolls the SHORT way to put the target at
            // 12 o'clock. Unlike the old sin(phi) shaping (which gave ~0 at phi=±180 and created a
            // DEAD SPOT for below-nose targets), the linear clamp stays at full authority through
            // phi=±180, so the aircraft rolls toward inverted to point the lift vector at a below target.
            // In the fine cone (bigTurn~0) blend to eLevelFine (wings-level) so there's no spurious
            // bank when nearly on target.
            float eAlign     = Mathf.Clamp(phi / 90f, -1.5f, 1.5f);
            float eLevelFine = t.right.y;                              // wings-level error: 0=level, <0=right wing down
            float rollErr    = Mathf.Lerp(eLevelFine, eAlign, bigTurn);

            // ROLL-RATE LOW-PASS (anti high-speed roll-PIO, identical to Legacy/EvolvedLegacy).
            // The bank loop MUST be PD-damped; the low-pass prevents the high-freq delay from
            // turning rate feedback into a self-sustaining limit cycle (v0.31 fix, v0.38 noted).
            float rollTau = Cfg.RollRateSmoothing.Value;
            if (rollTau > 1e-4f) _rollRateFilt += (dt / (rollTau + dt)) * (rollRate - _rollRateFilt);
            else _rollRateFilt = rollRate;
            tgtR = Mathf.Clamp((rollErr - _rollRateFilt * Cfg.RollDamping.Value) * Cfg.RollGain.Value, -1f, 1f);

            // --- PITCH: fine = signed direct nudge (allows nose-down); big = speed-aware load-factor ---
            // FINE CONE (bigTurn~0): -local.y*sens*fineGain is SIGNED — target above => local.y>0 =>
            // nose-up (negative pitch); target BELOW => local.y<0 => nose-DOWN (positive pitch). This
            // restores the nose-down capability that was broken when the old code clamped pull to >=0.
            //
            // BIG TURN (bigTurn~1): compute the speed-aware load factor from the pointing error past the
            // deadband. The desired turn rate omega = Kturn*errDeg(rad/s) capped at OmegaMax; the
            // load factor that physically holds that turn at speed V is n = sqrt(1+(omega*V/g)^2).
            // The pull (nDes-1)*sens is gated by pullGate=clamp01(alignFrac): ROLL BEFORE PULL —
            // the pull only engages once the lift vector faces the target (alignFrac>0). Clamped >=0
            // so the big-turn pull is always nose-up; it is FADED OUT (bigTurn->0) before the fine
            // signed nudge takes over, so nose-down is always available in the fine cone.
            float offRad = Mathf.Max(0f, off - Cfg.BankToTurnDeadband.Value) * Mathf.Deg2Rad;
            float omega  = Mathf.Min(offRad * Cfg.AssistTurnRateGain.Value, Cfg.BankToTurnOmegaMax.Value);
            float V      = Mathf.Max(Cfg.BankToTurnVmin.Value, vMag);
            float nDes   = Mathf.Sqrt(1f + (omega * V / g) * (omega * V / g));
            pullGate     = Mathf.Clamp01(alignFrac);                  // roll-then-pull gate (0 when lift vector faces away)
            float bigPull   = (nDes - 1f) * sens * pullGate;          // >=0, nose-UP in big turns
            float finePitch = -local.y * sens * fineGain;             // SIGNED: nose-down for below target
            coordPull = bigPull;                                       // surfaced for debug trace
            // nose-up = NEGATIVE pitch; fine term already signed; big pull enters as -bigPull (nose-up).
            tgtP = Mathf.Clamp((Mathf.Lerp(finePitch, -bigPull, bigTurn) + _iPitch + pitchRate * pitchDamp) * Cfg.PitchGain.Value, -1f, 1f);

            // --- YAW: coordination, full in fine cone, eased in big turn (same as Legacy) -----------
            yawScale = Mathf.Lerp(1f, Cfg.TurnYawScale.Value, bigTurn);
            tgtY = Mathf.Clamp((local.x * sens * fineGain * yawScale + _iYaw - yawRate * damp) * Cfg.YawGain.Value, -1f, 1f);
        }

        // ---- CONTROL LAW: EVOLVED LEGACY (Phase 2, v0.40) ----------------------------------------
        // NOTE: body-rate Cascade (originally planned for this slot) is deferred.
        // This slot now holds the EvolvedLegacy law: a copy of ApplyLegacy's body with two targeted
        // changes:
        //   2a — Universal speed-aware bank: use atan(omega*V/g) for the bank target at ALL speeds/
        //        regimes, not just when yaw-weakness is high. Decouples the high-speed bank sizing
        //        from the weakness estimator (_yawWeak gating), making the loaded-turn bank the
        //        default behaviour rather than the assist-path behaviour.
        //   2b — Final-leg align-hold: keep the roll-to-align contribution engaged through the final
        //        degrees until |azErr| is also small, so the law doesn't level early and park short.
        // Everything else (pitch, yaw, fine integrator winding, roll-rate low-pass) is byte-for-byte
        // Legacy — the same convergence properties, same anti-PIO, same no-bunt gate.
        private static void ApplyEvolvedLegacy(
            Transform t, Vector3 local, float off, float vMag, float sens, float fineGain, float alignFrac, float bigTurn,
            float targetBank, float azErr, float phi, float pitchRate, float yawRate, float rollRate,
            float pitchDamp, float damp, float assist, float dt,
            out float tgtP, out float tgtR, out float tgtY,
            out float pullGate, out float yawScale, out float coordPull)
        {
            // off/vMag are used here (unlike Legacy where vMag was unused).

            // PITCH — identical to Legacy.
            pullGate = Mathf.Lerp(1f, Mathf.Clamp01(alignFrac), Cfg.RollPitchCoordination.Value * bigTurn);

            // CHANGE 2a — compute the speed-aware bank target LOCALLY and unconditionally.
            // Legacy uses the passed-in targetBank which blends linear->atan() only when _yawWeak is high.
            // Here we always use atan(omega*V/g) — the same formula as the turn-rate path in Apply's
            // shared pre-compute (v0.37.1: tiny noise gate on raw azErr, not the big deadzone azBank carries).
            const float gAcc = 9.81f;
            float azTR  = Mathf.Abs(azErr) <= 0.5f ? 0f : (Mathf.Abs(azErr) - 0.5f) * Mathf.Sign(azErr); // raw err, noise gate
            float omega  = azTR * Mathf.Deg2Rad * Cfg.AssistTurnRateGain.Value;                              // rad/s, signed
            float Vb     = Mathf.Max(Cfg.BankToTurnVmin.Value, vMag);                                        // airspeed floor (reuse BankToTurnVmin)
            float bankTRdeg = Mathf.Atan(omega * Vb / gAcc) * Mathf.Rad2Deg;                                // speed-correct bank, signed deg
            float tBankE = Mathf.Clamp(bankTRdeg, -Cfg.MaxBankAngle.Value, Cfg.MaxBankAngle.Value);         // universal bank target
            // HOVER REGIME (v0.43): fade the commanded bank to zero as _heliBlend->1. This neutralises the
            // eFine leveler base (eFine = t.right.y + sin(0) = t.right.y) and self-cancels the coordinating
            // pull (coordPull ∝ sin(tBankE) -> 0). NOTE: it does NOT by itself stop roll — the eAlign
            // roll-to-marker branch below is gated separately by _heliBlend (v0.46 fix); both gates are
            // needed for a true wings-level hover. Fixed-wing => _heliBlend==0 => tBankE unchanged (v0.42).
            tBankE *= (1f - _heliBlend);

            // COORDINATING PULL — same structure as Legacy but sizes off tBankE (so the pull matches the
            // bank actually commanded, not the passed-in targetBank from the weakness-gated pre-compute).
            float pullTaper = Mathf.Clamp01(Mathf.Abs(azErr) / Mathf.Max(0.5f, Cfg.CoordPullReleaseAngle.Value));
            coordPull = Mathf.Clamp(
                Cfg.CoordPullGain.Value * Mathf.Abs(Mathf.Sin(tBankE * Mathf.Deg2Rad))
                * pullTaper * assist,
                0f, Cfg.CoordPullCap.Value);
            tgtP = Mathf.Clamp((-local.y * sens * fineGain * pullGate + _iPitch + pitchRate * pitchDamp - coordPull) * Cfg.PitchGain.Value, -1f, 1f);

            // YAW — Legacy, plus the hover-regime authority boost (v0.43). With the wings held level by the
            // bank suppression above, local.x is ~the horizontal pointing error, so the existing yaw term
            // already points the nose at the marker; in hover it's the ONLY thing turning the aircraft, so
            // blend the scale up toward HeliYawScale as _heliBlend->1 to give the tail rotor the authority.
            yawScale = Mathf.Lerp(1f, Cfg.TurnYawScale.Value, bigTurn);
            yawScale = Mathf.Lerp(yawScale, Cfg.HeliYawScale.Value, _heliBlend);
            float yawWeakFade = 1f - Cfg.YawWeakFade.Value * assist;
            tgtY = Mathf.Clamp(( local.x * sens * fineGain * yawScale * yawWeakFade + _iYaw - yawRate * damp) * Cfg.YawGain.Value, -1f, 1f);

            // ROLL — same eFine/eAlign structure as Legacy, with two changes:
            //   2a: use tBankE (universal speed-aware bank) instead of passed-in targetBank for eFine.
            //   2b: the blend weight is MAX(bigTurn, lateralHold) so the align contribution stays engaged
            //       while |azErr| > EvolvedAlignHoldDeg, even after bigTurn has faded to 0. Convergent:
            //       as both off->0 and azErr->0, lateralHold->0 and bigTurn->0, so rollErr->eFine (wings-
            //       level). No residual bank once on-target; no limit cycle. yawScale already restores full
            //       yaw authority as bigTurn->0 (legacy behaviour preserved — align-hold does NOT gate yaw).
            //   HOVER REGIME (v0.46 fix): zeroing tBankE above only neutralises eFine. The eAlign branch
            //       (roll-TO-the-marker = bank-to-turn) is independent of it, so in hover it kept banking
            //       the heli toward the target while the boosted yaw also swung the nose = roll+yaw at once
            //       (confirmed in mouseaim-rec 20260628-100746: heliBlend=1 yet |outR|>|outY|, bank->46deg).
            //       Gate blendWeight by (1-_heliBlend) so at full hover blendWeight->0 => rollErr=eFine=
            //       t.right.y = pure wings-leveler; yaw alone points the nose. Fixed-wing (_heliBlend==0)
            //       is byte-identical to before.
            float eFine  = t.right.y + Mathf.Sin(tBankE * Mathf.Deg2Rad); // 2a: tBankE (universal speed-aware)
            float eAlign = Mathf.Clamp(phi / 90f, -1.5f, 1.5f);
            // 2b: lateral-error hold weight — stays > 0 while |azErr| > EvolvedAlignHoldDeg.
            float lateralHold = Mathf.Clamp01(Mathf.Abs(azErr) / Mathf.Max(0.01f, Cfg.EvolvedAlignHoldDeg.Value));
            float blendWeight = Mathf.Max(bigTurn, lateralHold) * (1f - _heliBlend); // 2b + hover gate: kill roll-to-align in hover
            float rollErr = Mathf.Lerp(eFine, eAlign, blendWeight);

            // ROLL-RATE LOW-PASS — identical to Legacy (anti high-speed roll PIO).
            float rollTau = Cfg.RollRateSmoothing.Value;
            if (rollTau > 1e-4f) _rollRateFilt += (dt / (rollTau + dt)) * (rollRate - _rollRateFilt);
            else _rollRateFilt = rollRate;
            float rollRateF = _rollRateFilt;

            tgtR = Mathf.Clamp((rollErr - rollRateF * Cfg.RollDamping.Value) * Cfg.RollGain.Value, -1f, 1f);
        }

        private static void HideNativeVirtualJoystick()
        {
            var fh = SceneSingleton<FlightHud>.i;
            if (fh != null && fh.virtualJoystickPos != null)
            {
                fh.SetVirtualJoystick(Vector3.zero);
                fh.virtualJoystickPos.gameObject.SetActive(false);
            }
        }

        // Event-only anomaly logger (v0.25). Each detector keeps small rolling state and fires at most
        // once per cooldown, writing a single compact [anomaly] line. Runs every FixedUpdate while flying,
        // so it catches the exact frame a command goes wrong without the per-frame [chase] spam.
        private static void DetectAnomalies(Aircraft ac, float off, float bank, float targetBank, float bigTurn, float yawRate, float rollRate, float dt)
        {
            float now = Time.time;
            float spdNow = ac.rb != null ? ac.rb.velocity.magnitude : -1f;

            // Stash this frame in the ring buffer FIRST (logs nothing; dumped only if an event fires below).
            _ring[_ringHead] = new AnFrame {
                t = now, off = off, bank = bank, tgtBank = targetBank,
                p = _outP, r = _outR, y = _outY, yr = yawRate, rr = rollRate, rf = _rollRateFilt, spd = spdNow, g = ac.gForce };
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;

            // MANUAL-OVERRIDE GATE — when you're on the stick/pedals (any axis manually engaged, or easing back
            // after release) the airframe's attitude/rates are being driven by YOU, not the instructor, so the
            // chase law's reaction to your input would false-fire over-roll/wobble/overshoot/etc. Suspend all
            // detection while engaged and RESET the rolling windows so a flip count accrued during manual flight
            // doesn't fire the instant control hands back. The ring buffer above still fills, so an anomaly just
            // after handback keeps its pre-frames. _eng* are 0 whenever ManualOverride is off, so this is inert then.
            if (_engP > 0f || _engR > 0f || _engY > 0f)
            {
                _huntWinStart = _yawWagWinStart = _wobbleWinStart = now;
                _flipsP = _flipsY = _yawWagFlips = _wobbleFlips = 0;
                _prevSignP = _prevSignY = _prevSignYW = _prevSignRW = 0f;
                _missTimer = 0f;
                _offMin = off; _prevOff = off;
                return;
            }

            // OVERSHOOT — the nose closed toward the marker then the error grew back. Track the closest
            // approach; if off rebounds past it by AnomalyOvershootDeg after getting reasonably close
            // (<15deg), the nose crossed/passed the target. A large fresh command resets the baseline.
            if (off < _offMin) _offMin = off;
            if (_offMin < 5f && off > _offMin + Cfg.AnomalyOvershootDeg.Value)
            {
                Anomaly("overshoot", $"min={_offMin:0.0} rebound={(off - _offMin):0.0}deg", ref _anOvershootT, now, ac, off, bank);
                _manvOvershot = true; // mark the in-progress maneuver as having overshot (reused by [maneuver])
                _offMin = off; // start a fresh approach window
            }
            if (off > 40f) _offMin = off;

            // OVER-ROLL — actual bank overshot what the law actually asked for. GATED to the PURE fine regime
            // (bigTurn <= 0, i.e. off < FineAngle): only there does the fine bank servo alone govern roll, so
            // targetBank is the real commanded bank. Above FineAngle the v0.26 body-frame roll-to-align law
            // legitimately commands more bank than the azimuth servo would, so comparing to targetBank there
            // false-fires all through a normal turn (the bigTurn<0.5 gate let that happen — v0.26.1 fix). Also
            // require the bank to be DEEPENING (roll rate still increasing |bank|, same sign as bank) so a
            // turn-exit roll-out — bank momentarily above target but actively levelling — doesn't trip it.
            if (bigTurn <= 0f && Mathf.Abs(bank) > Mathf.Abs(targetBank) + Cfg.AnomalyOverRollDeg.Value
                && Mathf.Sign(rollRate) == Mathf.Sign(bank))
                Anomaly("over-roll", $"bank={bank:0.0} target={targetBank:0.0}", ref _anOverRollT, now, ac, off, bank);

            // HUNT — rapid output sign-flapping on pitch or yaw within a 1 s window while the error is NOT
            // meaningfully closing: a limit cycle / wing-rock rather than honest convergence.
            if (now - _huntWinStart > 1f) { _huntWinStart = now; _flipsP = _flipsY = 0; }
            CountFlip(_outP, ref _prevSignP, ref _flipsP);
            CountFlip(_outY, ref _prevSignY, ref _flipsY);
            if ((_flipsP >= 4 || _flipsY >= 4) && off > _prevOff - 0.5f)
            {
                Anomaly("hunt", $"flipsP={_flipsP} flipsY={_flipsY}", ref _anHuntT, now, ac, off, bank);
                _flipsP = _flipsY = 0; // consume the window so it doesn't re-fire every frame
            }

            // YAW-WAG (low speed) — the nose wags left/right on the takeoff roll / low-speed regime, where
            // rudder/aero authority is low and the cruise-tuned yaw loop over-corrects. Counts yaw OUTPUT
            // reversals in a 1 s window and flags repeated flapping while slow (independent of off, since the
            // wag happens while roughly on heading). Separate counter/window from the cruise "hunt" detector.
            if (now - _yawWagWinStart > 1f) { _yawWagWinStart = now; _yawWagFlips = 0; }
            CountFlip(_outY, ref _prevSignYW, ref _yawWagFlips);
            if (spdNow >= 0f && spdNow < Cfg.AnomalyLowSpeed.Value && _yawWagFlips >= 3)
            {
                Anomaly("yaw-wag", $"flips={_yawWagFlips} spd={spdNow:0}", ref _anYawWagT, now, ac, off, bank);
                _yawWagFlips = 0; // consume the window
            }

            // ROLL-WOBBLE (high speed) — a small roll limit-cycle: the bank rocks back and forth (the +/-0.1
            // roll output that flickers at high speed) because the cruise-tuned roll loop is over-effective at
            // high dynamic pressure. Counts ROLL output reversals in a 1 s window and flags repeated flapping
            // while fast AND roughly on-heading (small off), so an honest hard turn's roll-in/out doesn't trip
            // it. Separate counter/window from the yaw-wag and the pitch/yaw hunt detectors.
            if (now - _wobbleWinStart > 1f) { _wobbleWinStart = now; _wobbleFlips = 0; }
            CountFlip(_outR, ref _prevSignRW, ref _wobbleFlips);
            if (spdNow > Cfg.AnomalyWobbleSpeed.Value && off < 10f && _wobbleFlips >= 4)
            {
                Anomaly("roll-wobble", $"flips={_wobbleFlips} spd={spdNow:0}", ref _anWobbleT, now, ac, off, bank);
                _wobbleFlips = 0; // consume the window
            }

            // PERSISTENT-MISS — off stuck high while an axis is near saturation: the instructor is fighting
            // itself / not finding the efficient line. v0.26.1: require a genuine STALL, not an honest long
            // turn. With the G-limiter gone a big reorientation legitimately holds full elevator at high G for
            // many seconds while off steadily closes (logs showed off 90->19 over ~30 s) — that must NOT flag.
            // Anchor off when the stall window opens; if off drops progressDeg below the anchor the turn is
            // closing, so reset and re-anchor. Only fire when the timer reaches 2 s with off still within
            // progressDeg of the anchor — i.e. closing slower than progressDeg/2 deg/s (genuinely not closing).
            // progressDeg=4 => fires only below ~2 deg/s; the honest max-rate turns close at ~2.4 deg/s.
            const float progressDeg = 4f;
            bool saturated = Mathf.Abs(_outP) > 0.9f || Mathf.Abs(_outY) > 0.9f || Mathf.Abs(_outR) > 0.9f;
            if (off > 8f && saturated)
            {
                if (_missTimer <= 0f) _missAnchorOff = off;            // open a fresh stall window — anchor here
                else if (off < _missAnchorOff - progressDeg)           // closed progressDeg => making real progress
                { _missTimer = 0f; _missAnchorOff = off; }
                _missTimer += dt;
            }
            else _missTimer = 0f;
            if (_missTimer > 2f && off > _missAnchorOff - progressDeg)
            {
                Anomaly("persistent-miss", $"stuck {_missTimer:0.0}s off~{_missAnchorOff:0} (<{progressDeg:0}deg progress)", ref _anMissT, now, ac, off, bank);
                _missTimer = 0f;
            }

            _prevOff = off;
        }

        // Count a sign flip on an output axis, ignoring near-zero noise. Updates the running sign in place.
        private static void CountFlip(float val, ref float prevSign, ref int flips)
        {
            if (Mathf.Abs(val) < 0.05f) return;
            float s = Mathf.Sign(val);
            if (prevSign != 0f && s != prevSign) flips++;
            prevSign = s;
        }

        // Emit one [anomaly] line (per-type cooldown via the ref stamp so a single event can't flood) to
        // both the BepInEx log (drives the on-screen flash, handy live) and the dedicated anomaly file.
        // No per-anomaly gain snapshot: gains are logged once at startup + on every change ([config] lines)
        // and embedded in each recording's header, so repeating them here only burned log/context. Instead
        // we tag the active control law and, when a recording is running, the CSV it belongs to.
        private static void Anomaly(string type, string detail, ref float lastStamp, float now, Aircraft ac, float off, float bank)
        {
            if (now - lastStamp < 1f) return; // per-type cooldown
            lastStamp = now;
            // Assign the next sequential index and flash it on-screen so the pilot can call out "#N felt wrong".
            _anomalyIndex++;
            LastAnomalyIndex = _anomalyIndex; LastAnomalyType = type; LastAnomalyTime = now;
            float spd = ac.rb != null ? ac.rb.velocity.magnitude : -1f;
            string rec = ManeuverRecorder.CurrentFile;
            string line =
                $"[anomaly #{_anomalyIndex}] {type} t={now:0.000} {detail} off={off:0.0} bank={bank:0.0} phase={LastPhase} " +
                $"out P/R/Y=({_outP:0.00},{_outR:0.00},{_outY:0.00}) spd={spd:0} g={ac.gForce:0.0}{(FlyLevelActive ? " LVL" : "")} " +
                $"law={Cfg.ControlLawMode.Value}{(rec.Length > 0 ? $" rec={rec}" : "")}";
            WTMouseAimPlugin.Log.LogWarning(line);
            AnomalyLog.Write(line);
            if (Cfg.AnomalyContext.Value) DumpTrail(now);
        }

        // Dump the last ~20 ring-buffer frames as ONE compact [anomaly:trail] line so the lead-up to the
        // event is visible (how the wag built, how the bank blew past its target) without per-frame spam.
        // Throttled to once per second across all anomaly types so a multi-type frame doesn't repeat it.
        // Per frame: t | off / bank>tgtBank | P,R,Y outputs | yaw rate | spd. Oldest → newest, left → right.
        private static void DumpTrail(float now)
        {
            if (now - _lastTrailT < 1f || _ringCount == 0) return;
            _lastTrailT = now;
            int n = Mathf.Min(_ringCount, 20);
            var sb = new System.Text.StringBuilder("[anomaly:trail] (t off b>tgt P,R,Y yr rr rf spd)");
            for (int i = n - 1; i >= 0; i--)
            {
                int idx = ((_ringHead - 1 - i) % _ring.Length + _ring.Length) % _ring.Length;
                AnFrame f = _ring[idx];
                sb.Append($" {f.t:0.00}:{f.off:0}/{f.bank:0}>{f.tgtBank:0}/{f.p:0.00},{f.r:0.00},{f.y:0.00}/{f.yr:0.00}/{f.rr:0.00}/{f.rf:0.00}/{f.spd:0}");
            }
            string line = sb.ToString();
            WTMouseAimPlugin.Log.LogWarning(line);
            AnomalyLog.Write(line);
        }

        // PHASE CLASSIFICATION (v0.26). Map the current frame onto the instructor's plan so it's legible
        // ("roll the lift vector onto the target, then pull up into it"). |phi| small = target is at 12
        // o'clock (lift vector on it → pull); |phi| large = target off to the side (need to roll).
        //   LEVEL — Fly Level autopilot owns the plane (ignoring the marker).
        //   HOLD  — on target and settled (tiny off, nose barely moving).
        //   FINE  — inside the fine cone, closing the last few degrees.
        //   ALIGN — big turn, target still well off the lift vector: rolling it onto the target (pull gated).
        //   PULL  — big turn, lift vector roughly on the target: pulling up into it.
        //   TURN  — transitional (mid blend), neither cleanly aligning nor pulling.
        private static string ClassifyPhase(bool flyLevel, float off, float phi, float bigTurn, float noseTurnDeg)
        {
            if (flyLevel) return "LEVEL";
            if (off < Cfg.FineAngle.Value)
                return (off < 1.5f && noseTurnDeg < 0.15f) ? "HOLD" : "FINE";
            float aphi = Mathf.Abs(phi);
            if (bigTurn > 0.5f) return aphi > 25f ? "ALIGN" : "PULL";
            return "TURN";
        }

        // PER-MANEUVER SUMMARY (v0.26). A maneuver begins when off rises above AlignAngle and ends when the
        // nose settles back under FineAngle for ~0.3 s. On completion we emit ONE [maneuver] line — start/peak
        // off, time-to-align (first |phi|<20°, "lift vector on target"), time-to-capture (first off<FineAngle),
        // and peak bank / G / roll rate — the "how did the planned path actually work out" record. Cheap: one
        // line per completed turn. Reset on engage alongside the anomaly state.
        private static void TrackManeuver(Aircraft ac, float off, float phi, float bank, float rollRate, float dt)
        {
            float now = Time.time;

            if (!_manvActive && off > Cfg.AlignAngle.Value)
            {
                _manvActive = true;
                _manvStartT = now; _manvStartOff = off; _manvPeakOff = off;
                _manvAlignT = -1f; _manvCaptureT = -1f;
                _manvPeakBank = 0f; _manvPeakG = 0f; _manvPeakRoll = 0f;
                _manvSettle = 0f; _manvOvershot = false;
            }
            if (!_manvActive) return;

            _manvPeakOff  = Mathf.Max(_manvPeakOff, off);
            _manvPeakBank = Mathf.Max(_manvPeakBank, Mathf.Abs(bank));
            _manvPeakG    = Mathf.Max(_manvPeakG, Mathf.Abs(ac.gForce));
            _manvPeakRoll = Mathf.Max(_manvPeakRoll, Mathf.Abs(rollRate));
            if (_manvAlignT   < 0f && Mathf.Abs(phi) < 20f)        _manvAlignT   = now - _manvStartT;
            if (_manvCaptureT < 0f && off < Cfg.FineAngle.Value)   _manvCaptureT = now - _manvStartT;

            // Capture-settle: once off stays under FineAngle for ~0.3 s the turn is done — emit and reset.
            _manvSettle = off < Cfg.FineAngle.Value ? _manvSettle + dt : 0f;
            if (_manvSettle >= 0.3f)
            {
                float dur = now - _manvStartT;
                string align   = _manvAlignT   >= 0f ? $"{_manvAlignT:0.00}s"   : "n/a";
                string capture = _manvCaptureT >= 0f ? $"{_manvCaptureT:0.00}s" : "n/a";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[maneuver] start={_manvStartOff:0}deg peak={_manvPeakOff:0}deg dur={dur:0.00}s " +
                    $"toAlign={align} toCapture={capture} peakBank={_manvPeakBank:0}deg peakG={_manvPeakG:0.0} " +
                    $"peakRollRate={_manvPeakRoll:0.00} overshoot={(_manvOvershot ? "Y" : "n")}");
                _manvActive = false;
                _manvSettle = 0f;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The seam patch. PlayerAxisControls runs each FixedUpdate (then Aircraft.FilterInputs).
    //   PREFIX  — when we're actively flying a fixed-wing, return false to SKIP the native body
    //             entirely. That's the fix for "the controls fight each other": the native mouse
    //             virtual-joystick / keyboard never writes the stick while we own it.
    //   POSTFIX — always runs (Harmony runs postfixes even when a prefix skips the original). When
    //             active it writes our chase output; on disengage it briefly ramps our last output
    //             back toward the native stick so the handoff is smooth.
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class PilotPlayerStatePatch
    {
        private static int _lastAircraftId;

        // Return false => skip native PlayerAxisControls (we own the stick this frame).
        private static bool Prefix(PilotPlayerState __instance)
        {
            if (!TryResolve(__instance, out var aircraft, out bool fixedWing, out float pilotStrength))
                return true; // can't resolve — let native run

            int id = aircraft.GetInstanceID();
            if (id != _lastAircraftId)
            {
                _lastAircraftId = id;
                string name = aircraft.definition != null ? aircraft.definition.name : "<unknown>";
                bool hasHover = false;
                try { var cfilt = aircraft.GetControlsFilter(); if (cfilt != null) hasHover = cfilt.HasAutoHover(); } catch { /* ignore */ }
                WTMouseAimPlugin.Log.LogInfo(
                    $"[seam] now flying '{name}' — fixedWing={fixedWing} collective={!fixedWing} hasAutoHover={hasHover} " +
                    $"(takeoffDistance={aircraft.GetAircraftParameters().takeoffDistance:0.##}); hover regime ramps {Cfg.HeliHoverSpeed.Value:0}..{Cfg.HeliForwardSpeed.Value:0} m/s fwd (collective aircraft only).");
            }

            bool active = ChaseController.BeginFrame(aircraft, fixedWing, pilotStrength);

            // Skip the native body ONLY in the cockpit. There, native's mouse virtual-joystick would
            // fight us for the stick, so we own it outright. In external/orbit views we let native RUN —
            // it also processes the view / free-look axes, and skipping it killed 3rd-person free-look.
            // Our postfix still overwrites the flight controls afterward, so the plane keeps chasing the
            // marker either way; we just no longer steal the view processing in external cameras.
            bool ownStick = active && CameraStateManager.cameraMode == CameraMode.cockpit;
            return !ownStick;
        }

        private static void Postfix(PilotPlayerState __instance)
        {
            if (TryResolve(__instance, out var aircraft, out _, out _))
                ChaseController.Apply(aircraft);
        }

        private static bool TryResolve(PilotPlayerState inst, out Aircraft aircraft, out bool fixedWing, out float pilotStrength)
        {
            aircraft = null; fixedWing = false; pilotStrength = 1f;
            // PilotPlayerState never populates base.aircraft (it uses pilot.aircraft directly and
            // skips base.Initialize), so read the `pilot` field and go through public Pilot.aircraft.
            var pilot = Traverse.Create(inst).Field("pilot").GetValue<Pilot>();
            if (pilot == null) return false;
            aircraft = pilot.aircraft;
            if (aircraft == null) return false;
            fixedWing = aircraft.GetAircraftParameters().takeoffDistance > 0f;
            // GLOC: native zeroes inputs when pilotStrength < 0.2 (blackout). Read it so we can defer.
            pilotStrength = Traverse.Create(inst).Field("pilotStrength").GetValue<float>();
            return true;
        }
    }
}
