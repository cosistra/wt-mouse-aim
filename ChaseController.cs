using System.Collections.Generic;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // ONE fixed-wing control law now (EvolvedLegacy — the Unified A/B alternative was removed in
    // v0.65; see CHANGELOG). Rotorcraft are force-flown through the same law (every bit of heli
    // handling lives in it). No law enum / no in-flight law toggle any more.
    // ---------------------------------------------------------------------------------------------
    // The chase controller (thin "instructor"). Point-and-chase law from brihernandez/MouseFlight
    // (MIT), spec §3.1/3.2: compute the marker direction in the body frame, pitch/yaw toward it, and
    // blend roll between "bank toward target" (far off) and "wings level" (on target). We fully OWN
    // the stick while active (native is skipped); only a short ramp on disengage. The game's FBW/
    // AutoTrimmer limit G/AoA downstream, so no integral term — just P + a slew-rate limit.
    //
    // ONE INSTANCE PER AIRCRAFT (v0.82). Every field below is per-aircraft state — integrators,
    // filters, the ring buffer, the reflection probe caches, the phase/maneuver trackers. As statics
    // they were fine while exactly one aircraft was ever flown by the mod; the v0.81 drone harness
    // flies N at once, and N aircraft sharing one integrator does not produce a slightly worse
    // capture, it produces a meaningless one. Get a controller through `For(aircraft)` — never
    // `new` — so the same aircraft always gets the same state back. The handful of members that
    // remain `static` are genuinely process-global and each says why at its declaration.
    internal sealed class ChaseController
    {
        private bool  _active;     // owning the stick this frame
        private bool  _wasActive;  // last frame (edge detection)
        private float _outP, _outR, _outY;
        private float _disRamp;    // 1->0 disengage blend
        private Vector3 _prevFwd;  // last frame's nose direction (for pitch/yaw rate damping)
        private Vector3 _prevUp;   // last frame's up axis (for roll-rate damping)
        private bool  _prevFwdValid;
        private float _lastChaseLog; // throttle the [chase] trace to ~5/sec

        // Per-axis manual override-on-touch state. _eng* is 0 (mouse-aim owns the axis) .. 1 (you own it);
        // _mApply* freezes the manual value at release so the axis eases back to chase, not to zero.
        private float _engP, _engR, _engY;
        private float _mApplyP, _mApplyR, _mApplyY;
        private static Player _rewired;   // cached Rewired player 0 (same one PilotPlayerState reads)

        // Global hard-handoff state (v0.49). When ManualHandoffTime > 0 the per-axis blend is replaced
        // by a whole-instructor switch: ANY axis past the deadzone fully disables the chase on ALL three
        // axes (you fly the plane directly) and resets _manualHold to ManualHandoffTime while free-looking
        // or ManualHandoffAimTime while aiming (v0.50 — see the regime comment in Apply); the instructor
        // only re-engages once _manualHold counts back down to 0 (i.e. that many seconds after your last
        // input). _gEng in [0,1] is the global manual->instructor blend (1 = you own it, eases to 0 as
        // the chase takes back over) — the global twin of the per-axis _eng*.
        private float _manualHold;  // seconds the instructor stays off after the last manual input
        private float _gEng;        // 1 = full manual on all axes, 0 = full instructor

        // Fine-regime integrator state (v0.24). The game's FBW is a rate-command law, so a proportional
        // outer loop asymptotes and parks short; these wind in the steady bias that closes the last bit.
        private float _iPitch, _iYaw;

        // CLOSURE-STALL GATE (v0.83) — the persistence half of the integrator gate. Through v0.82 the
        // only thing that let _iPitch/_iYaw wind was fineBlend, a function of error MAGNITUDE (off <
        // FineAngle), so the term whose whole job is killing steady-state residual was switched OFF
        // exactly where a residual stood: R21's ten sustained-turn replicates parked at off ~10.2 deg
        // with FineAngle 6, and recorded iPitch at +/-0.001 against its 0.12 cap for the whole 30 s.
        // The condition for integral action is "the proportional path has FAILED to close this error",
        // not "the error is small". _stallFilt in [0,1] is that measurement: 1 = the nose is rotating
        // but none of that rotation is shrinking the error. _prevOffTrk is the previous frame's off
        // (< 0 = no valid previous sample). Deliberately NOT the anomaly detector's _prevOff — that one
        // is only updated inside DetectAnomalies, which runs only while AnomalyLogging is on, so
        // borrowing it would make the control law depend on a diagnostics toggle.
        private float _prevOffTrk = -1f;
        private float _stallFilt;

        // Fly Level autopilot (v0.24). When active, the chase ignores the marker and flies straight-and-
        // level at the heading captured on toggle-on (horizontal projection of the nose at that instant).
        public bool FlyLevelActive;
        private Vector3 _levelHeading = Vector3.forward; // world-space, horizontal, unit

        // Anomaly detection state (v0.25). Event-only logger: each detector keeps a little rolling state
        // and emits ONE [anomaly] line on the triggering frame, with a per-type cooldown.
        private float _offMin = float.MaxValue;          // closest approach during the current command (overshoot)
        private float _prevOff;                          // last frame's off (closing test)
        private float _huntWinStart;                     // start of the 1 s sign-flip window
        private int   _flipsP, _flipsY;                  // output sign-flips this window (hunt)
        private float _prevSignP, _prevSignY;            // last non-trivial output sign per axis
        private float _missTimer;                        // seconds off has stayed high while saturated (no progress)
        private float _missAnchorOff;                    // off captured at the start of the current stall window
        private float _yawWagWinStart;                   // start of the low-speed yaw-wag window
        private int   _yawWagFlips;                      // yaw output sign-flips this window (low-speed wag)
        private float _prevSignYW;                       // last non-trivial yaw output sign (wag counter)
        private float _wobbleWinStart;                   // start of the high-speed roll-wobble window
        private int   _wobbleFlips;                      // roll output sign-flips this window (high-speed wobble)
        private float _prevSignRW;                       // last non-trivial roll output sign (wobble counter)
        private float _anOvershootT, _anOverRollT, _anHuntT, _anMissT, _anYawWagT, _anWobbleT, _anStressT, _anAzlcT; // per-type cooldown stamps
        private float _azlcWinStart;                      // az-limit-cycle (v0.58) half-window start
        private int   _azlcFlips, _azlcFlipsPrev;         // azErr sign flips, current/previous half-window
        private float _azlcPeak, _azlcPeakPrev;           // peak |azErr| per half-window (envelope)
        private float _azlcOutRMin, _azlcOutRMax;         // outR span this half-window (is roll chasing?)
        private float _azlcPrevAz;                        // last azErr (flip edge detect)
        private float _stressTimer;                      // seconds past the airframe's g/AoA limits (overstress detector)

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
        public  string LastPhase = "";                   // surfaced to OnGUI + the [anomaly] line
        private bool   _manvActive;                      // a maneuver (off rose above AlignAngle) is in progress
        private float  _manvStartT, _manvStartOff, _manvPeakOff; // start time / start & peak off
        private float  _manvAlignT, _manvCaptureT;       // sec to first |phi|<20deg / first off<FineAngle (-1 = not yet)
        private float  _manvPeakBank, _manvPeakG, _manvPeakRoll; // peak |bank| / |g| / |rollRate| over the maneuver
        private float  _manvSettle;                      // sec off has stayed under FineAngle (capture-settle timer)
        private bool   _manvOvershot;                    // an overshoot anomaly fired during this maneuver

        // Recent-frame ring buffer (v0.25.1): every FixedUpdate we stash a compact state snapshot here but
        // log NOTHING. When a detector fires, DumpTrail emits the last ~20 frames as a single [anomaly:trail]
        // line so the lead-UP to the event is visible (how the wag built / how the bank blew past) without
        // any continuous spam. Formatting only happens on a real anomaly, so the buffer itself is free.
        private struct AnFrame { public float t, off, bank, tgtBank, p, r, y, yr, rr, rf, spd, g; }
        private readonly AnFrame[] _ring = new AnFrame[64];
        private int   _ringHead;     // next write index
        private int   _ringCount;    // valid entries (<= _ring.Length)
        private static float _lastTrailT;   // one trail dump per second across all anomaly types
        private float _rollRateFilt;    // low-pass-filtered roll rate feeding the damping term (anti high-speed roll PIO)
        private float _headingRateFilt; // low-passed world heading rate of the NOSE (deg/s, + = swinging right). Feeds the
                                               // v0.51 anticipatory lead on the turn-rate bank command (azErrPred in Apply). Nose-only
                                               // (marker-independent), so the lead can never fight a mouse flick.
        // Low-pass time constant shared by BOTH world-azimuth rate signals — the nose rate above and the
        // v0.78 marker rate below. v0.54 raised it 0.18->0.35: the ±3 deg/s ripple at 1.3-1.5 Hz was feeding
        // the brake-clamp rectifier (WOBBLE-FINDINGS UPDATE 4); ~2x more attenuation there costs ~0.2 s of
        // rollout timing. ONE const on purpose — the two signals meet inside the same omega, so a mismatched
        // tau would show up as a PHASE error between them, which no gain can tune out.
        // ponytail: fixed, not a Cfg knob; promote to Cfg if flight tuning ever wants it.
        private const float HdgRateTau = 0.35f;
        private float _tBankSlewed;     // v0.54: slew-limited EvolvedLegacy bank target (deg) — rate-limit state
        internal float _tBankFlown;     // bank target the active law's roll servo actually flies (deg) — recorder column tBankE
                                               // (the recorded targetBank is the shared yawWeak-gated blend, which EvolvedLegacy does
                                               // NOT fly; reading it cost two red herrings in the v0.53 analysis)
        private float _eAlignSlew;      // v0.57: slew-limited big-turn roll-alignment error — breaks the ~1 Hz tail-on
                                               // roll relay (phi sign-flips in one tick when the target crosses dead-astern;
                                               // eAlign followed it rail-to-rail, bypassing every bank-target slew/deadzone)
        private float _aoaPrev;         // v0.57: last frame's AoA (deg) for the gate's predictive lead
        private float _aoaRateFilt;     // v0.57: low-passed AoA rate (deg/s) feeding the predictive AoA gate
        private float _alphaSchedFilt = 1f; // v0.59: smoothed AoA-utilization demand schedule (1 = full demand,
                                               // down to the 0.3 q-clamp floor near this airframe's own alpha ceiling).
                                               // Fast attack / slow release — the hysteresis that stops demand snapping
                                               // hot again as AoA falls back through the ceiling mid-cycle (loaded-jet fix)

        // Measured-reactive yaw-weakness estimate (v0.35). _yawEffFilt is a low-passed raw measure of how
        // much nose-yaw rate each unit of yaw command is buying (deg/s per stick); _yawWeak in [0,1] rises
        // when the rudder is empirically failing to CLOSE the heading error despite being commanded toward
        // it, and decays toward 0 otherwise. It has memory (the LPF) so it pre-biases the next nudge in the
        // same regime. _prevAzErr feeds the heading-closing rate. Reset on engage with the other detectors.
        private float _yawWeak;          // 0 = rudder is working, 1 = rudder ineffective -> bank-and-pull
        private float _yawEffFilt;       // low-passed |yawRate|/|outY| (diagnostic, logged to the CSV)
        private float _prevAzErr;        // last frame's azimuth error (deg) for d|azErr|/dt
        private bool  _prevAzErrValid;   // skip the first frame's bogus derivative
        private float _closeRateFilt;    // low-passed heading-closing rate (deg/s) — noise-robust derivative

        // Measured pitch-effectiveness estimate (v0.60) — the pitch twin of _yawWeak, consumed by
        // ApplyEvolvedLegacy. 1 = the plant delivers the commanded pitch rate; < 1 = it's under-delivering
        // (loaded / mushing / low-q / damaged). Low-passed achieved/commanded ratio of the game FBW's
        // own pitch rate pair, with the same fast-attack/slow-release asymmetry (memory/hysteresis) as
        // _yawWeak. Computed in Apply's shared pre-compute; reset to 1 on engage.
        private float _pitchEff = 1f;
        // v0.65 C1 reversal threshold AND v0.67 dead-command self-probe level — one const so they can't
        // drift apart. Below this, _pitchEff means "reversed/lost plant" (C1 drops the floor); it is also
        // the level a GATED-OUT command drifts toward so the pitch loop keeps a ~15% self-probe alive
        // (v0.67 latch fix — see the estimator + C1 comments).
        private const float PEffRevThresh = 0.15f;

        // B2 fine-settle marker-stationary gate (v0.65). _aimRateFilt = low-passed angular speed of the
        // aim direction (deg/s); _settleOK latches quasi-stationary (below aimStillRate) so the law may
        // inject a small V-INDEPENDENT sub-gate micro-bank that closes a high-q azimuth residual without
        // re-arming the V-scaled bank relay. _settleOn = did that injection fire this frame (recorder
        // column settleOn). _prevAim/_prevAimValid feed the rate. Reset on engage like the other estimators.
        private Vector3 _prevAim;
        private bool    _prevAimValid;
        private float   _aimRateFilt;
        private bool    _settleOK;
        private bool    _settleOn;

        // MARKER AZIMUTH RATE (v0.78) — the SIGNED world-azimuth rate of the aim direction (deg/s,
        // + = sweeping right), in the same frame and sign convention as azErr and _headingRateFilt so the
        // three are directly comparable, and low-passed with the SAME HdgRateTau as the nose rate.
        // Deliberately NOT _aimRateFilt above: that one is an UNSIGNED total angular speed used only as a
        // "is the marker moving at all" gate, and an unsigned magnitude cannot feed a signed turn demand.
        // Fed forward into both omega sites — full rationale at the shared omegaDes site in Apply.
        // _aimAzAcId guards the shared _prevAim sample against an aircraft change (see the compute block).
        private float _aimAzRateFilt;
        private int   _aimAzAcId = -1;

        // Hover / "flown-like-a-helicopter" regime state (v0.43). _collective latches the airframe class
        // (true = takeoffDistance==0 = heli/hover-VTOL) on engage; _heliBlend in [0,1] is the per-frame
        // regime blend (0 = fixed-wing bank-to-turn, 1 = hover yaw-to-point), computed in Apply from
        // forward airspeed + AutoHover. _vFwd/_hoverOn are surfaced for the CSV/trace. EvolvedLegacy only.
        internal bool  _collective;      // airframe is collective (heli / hover-VTOL); fixed-wing => always 0 heliBlend
        internal float _heliBlend;       // 0 = full fixed-wing, 1 = full hover yaw-to-point
        internal float _vFwd;            // forward-direction component of velocity (m/s) — the regime signal
        internal float _speed;           // total airspeed magnitude (m/s) — surfaced for the debug HUD
        internal bool  _hoverOn;         // game's AutoHover engaged this frame (forces heliBlend=1)

        // FBW probe cache (v0.55). The decompiled ControlsFilter.FlyByWire.Filter showed the pitch stick
        // is a g-scaled RATE command whose gain collapses below corner speed, and that the in-game
        // "stability assist" toggle swaps that protected law for a flat pitch*maxPitchAngularVel with NO
        // protection (the two are identical above ~1.2x corner-speed dynamic pressure). These fields cache
        // the per-airframe numbers behind that law — the public GetFlyByWireParameters() array plus the
        // private serialized trio via AccessTools field refs — so the control law can command only what
        // the plane can actually fly (see the FBW PROBE block in Apply). Cached per aircraft instance id;
        // EVERYTHING fails soft: helicopters (HeloControlsFilter / FBW disabled) or a game update renaming
        // a field just leave the probe not-ok and the pre-0.55 behaviour intact.
        private int   _fbwAcId;                   // aircraft instance id the cache belongs to
        private ControlsFilter.FlyByWire _fbwFbw; // resolved FBW block (null = unavailable)
        private bool  _fbwEnabled;                // FBW Enabled flag for the cached airframe
        private float _fbwCorner, _fbwMaxPitchVel;// public params ([2]=cornerSpeed, [1]=maxPitchAngularVel)
        private float _fbwGLimit = 9f, _fbwAlphaLimit = 25f, _fbwAlphaLimStr = 0.05f; // private trio (defaults = decompiled class defaults)
        private bool  _fbwRefsTried, _fbwRefsOk;  // AccessTools field-ref bootstrap (attempted once per session)
        private AccessTools.FieldRef<ControlsFilter.FlyByWire, float> _fbwGLimRef, _fbwALimRef, _fbwALimStrRef;

        // CANARD PROBE cache (v0.57). The KR-67 Ifrit is the one airframe with a RelaxedStabilityController:
        // before the FBW ever sees the stick, the game REPLACES pitch with Lerp(AoA/canardRange, stick,
        // |stick|) (decompiled Aircraft.FilterInputs -> RelaxedStabilityController.FilterInput). Small
        // inputs act quadratically (x*|x| — a 0.05 stick delivers 0.0025) and the response is locally
        // REVERSED for 0 < x < a/2 — the v56 recordings show the resulting ~5.3 Hz fine-aim limit cycle
        // on the Ifrit only (buzz on up to 82% of a file, both assist states, back to v0.53). The mod
        // inverts the remap (see InvertCanardRemap) so the game delivers the pitch the law intended.
        // Same fail-soft rules as the FBW cache: missing component / renamed field => identity (old behaviour).
        private RelaxedStabilityController _rsCtrl; // null = airframe has none (everything but the Ifrit)
        private float _rsCanardRange;               // its serialized canardRange (deg)
        private bool  _rsRefsTried, _rsRefsOk;
        private AccessTools.FieldRef<Aircraft, RelaxedStabilityController> _rsCtrlRef;
        private AccessTools.FieldRef<RelaxedStabilityController, float> _rsRangeRef, _rsEffRef;

        // HELO FBW PROBE cache (v0.58). Rotorcraft don't fly the base flyByWire the v0.55 probe reads:
        // HeloControlsFilter OVERRIDES Filter entirely and runs its own private heloFlyByWire — a 3-axis
        // RATE-command PID (decompiled HeloControlsFilter.cs: target rates = stick * maxAngularVel, pitch
        // clamped to gLimit*9.81/max(V,10), tracked with ~0.3 s effective lag per the v57 CSV fits at
        // corr 0.88-0.97). These fields cache that block's per-airframe authority so the collective path
        // can command a rate normalized by what the airframe can actually deliver (the HELO RATE
        // NORMALIZATION block in ApplyEvolvedLegacy). The archetype refs drive the tilt/nozzle-angle
        // hover-regime blend. Same fail-soft contract as the FBW/canard probes: any miss leaves _heloOk
        // false / refs null and the exact pre-0.58 behaviour.
        private bool    _heloOk;                          // helo FBW resolved, Enabled, params sane
        private float   _heloGLimit    = 3f;              // its serialized gLimit (class default)
        private Vector3 _heloMaxAngVel = new Vector3(1f, 2f, 2f); // rad/s per unit stick (x=pitch, y=yaw, z=roll)
        private TiltWingController _twc;                  // tilt-wing VTOL gauge (null = not this archetype)
        private SwivelDuctSystem   _sds;                  // swivel-duct VTOL gauge (null = not this archetype)
        private bool _hasCompound;                        // CompoundHeloController present (log-only for now)

        // =========================================================================================
        // THE REGISTRY (v0.82) — one controller per aircraft, keyed by Aircraft.GetInstanceID(),
        // the same key TestDrone uses for its own dictionary.
        // =========================================================================================
        private static readonly Dictionary<int, ChaseController> _byAc = new Dictionary<int, ChaseController>();
        private Aircraft _ac;   // the aircraft this controller belongs to — read ONLY by the eviction sweep

        // THE HUD'S CONTROLLER. OnGUI has no aircraft in hand and must show the LOCAL PLAYER's
        // numbers — never a drone's, which is the whole reason the controller stopped being static.
        // BeginFrame publishes itself here only when its aircraft is the one the game calls local, so
        // a drone can never claim it. Null until the player has flown one fixed step, hence every
        // HUD read is null-conditional.
        internal static ChaseController Player { get; private set; }

        // The controller for this aircraft, created on first use. Never `new ChaseController()`
        // anywhere else: a second instance for the same aircraft is a silently-reset integrator.
        internal static ChaseController For(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (_byAc.TryGetValue(id, out var c)) return c;
            // EVICTION, on the MISS path only. A miss happens once per aircraft, not once per fixed
            // step, so the sweep costs nothing on the hot path while still bounding the dictionary
            // over an unattended session (every drone spawn and every player respawn is a new
            // aircraft). Unity reports a destroyed object as null WITHOUT throwing, so a dead entry
            // never announces itself — it just keeps a corpse mapped forever.
            Sweep();
            c = new ChaseController { _ac = ac };
            _byAc[id] = c;
            return c;
        }

        // Drop an aircraft's controller. Idempotent; safe to call for an aircraft that never had one.
        // TestDrone calls this from both removal paths (deliberate despawn and the prune of a drone
        // the game removed under us), which is what keeps a long unattended run flat.
        internal static void Forget(Aircraft ac) { if (ac != null) Forget(ac.GetInstanceID()); }

        internal static void Forget(int aircraftId)
        {
            if (_byAc.TryGetValue(aircraftId, out var c))
            {
                if (ReferenceEquals(c, Player)) Player = null;   // don't leave the HUD reading a corpse
                _byAc.Remove(aircraftId);
            }
        }

        // ponytail: linear scan + a throwaway list, on a path that runs once per new aircraft. The
        // dictionary holds single digits in practice (DroneCount caps at 16); if it ever holds
        // thousands, that is a missing Forget call, not a reason to index this.
        private static void Sweep()
        {
            List<int> dead = null;
            foreach (var kv in _byAc)
                if (kv.Value._ac == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead == null) return;
            foreach (int k in dead) Forget(k);
        }

        public bool IsFlying => _active;

        // Toggle Fly Level. On engage, latch the current horizontal heading so we hold THIS course (not
        // wherever the nose happens to drift). Capturing the heading here keeps Apply() purely reactive.
        public void ToggleFlyLevel(Aircraft aircraft)
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
        public float PilotStrength { get; private set; } = 1f;

        // The instructor's own slewed stick command this frame (before any manual override blends on top),
        // surfaced for the top-left debug readout: "what the instructor is saying". 0 when not flying.
        public float LastPitch { get; private set; }
        public float LastRoll  { get; private set; }
        public float LastYaw   { get; private set; }

        // The player's manual stick/keyboard/pedal source. Null until Rewired is ready.
        private static Player RewiredPlayer()
        {
            if (_rewired == null && ReInput.isReady)
                _rewired = ReInput.players.GetPlayer(0);
            return _rewired;
        }

        // "Is the human on the stick RIGHT NOW?" — the single definition of manual input: the same
        // three axes and the same ManualDeadzone the override block in Apply() uses (it calls this
        // for its own `anyInput`). Exposed so the ScenarioPlayer can abort a test card on a stick
        // touch instead of growing a second detector that could drift out of agreement with this one.
        // Deliberately NOT gated on Cfg.ManualOverride: "the pilot grabbed the controls" is true
        // whether or not the per-axis blend is enabled.
        public static bool ManualStickInput()
        {
            var pl = RewiredPlayer();
            if (pl == null) return false;
            float dz = Cfg.ManualDeadzone.Value;
            return Mathf.Abs(pl.GetAxis("Pitch")) > dz
                || Mathf.Abs(pl.GetAxis("Roll"))  > dz
                || Mathf.Abs(pl.GetAxis("Yaw"))   > dz;
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

        // FBW PROBE resolve (v0.55): (re)bind the cache above to this aircraft. Cheap after the first
        // call per airframe (an int compare). Returns true when the FBW block is present, Enabled and its
        // params are sane — the callers' gate for every v0.55 normalization/cap. All reflection guarded;
        // any failure leaves the probe not-ok for this airframe (fail SOFT, old behaviour).
        private bool ResolveFbw(Aircraft ac)
        {
            if (ac == null) return false;
            int id = ac.GetInstanceID();
            if (id != _fbwAcId)
            {
                _fbwAcId = id;
                _fbwFbw = null; _fbwEnabled = false;
                ResolveCanard(ac); // v0.57: rebind the canard probe on the same aircraft-change edge
                ResolveHelo(ac);   // v0.58: rebind the helo FBW + archetype probe on the same edge
                try
                {
                    var cf = ac.GetControlsFilter();
                    if (cf != null)
                    {
                        var (enabled, p) = cf.GetFlyByWireParameters();
                        _fbwEnabled     = enabled;
                        _fbwMaxPitchVel = p[1];
                        _fbwCorner      = p[2];
                        _fbwFbw         = cf.GetFlyByWire();
                        if (!_fbwRefsTried)
                        {
                            // One-time field-ref bootstrap for the private serialized trio the public
                            // params array doesn't carry. A rename in a game update lands here — the
                            // probe then runs with the decompiled class defaults (9g / 25deg / 0.05).
                            _fbwRefsTried = true;
                            try
                            {
                                _fbwGLimRef    = AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("gLimitPositive");
                                _fbwALimRef    = AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("alphaLimiter");
                                _fbwALimStrRef = AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("alphaLimiterStrength");
                                _fbwRefsOk = true;
                            }
                            catch (System.Exception e)
                            {
                                _fbwRefsOk = false;
                                WTMouseAimPlugin.Log.LogWarning($"[fbw] private field refs unavailable ({e.Message}) — using decompiled defaults (9g/25deg).");
                            }
                        }
                        if (_fbwRefsOk && _fbwFbw != null)
                        {
                            _fbwGLimit     = _fbwGLimRef(_fbwFbw);
                            _fbwAlphaLimit = _fbwALimRef(_fbwFbw);
                            _fbwAlphaLimStr = _fbwALimStrRef(_fbwFbw);
                        }
                        else { _fbwGLimit = 9f; _fbwAlphaLimit = 25f; _fbwAlphaLimStr = 0.05f; }
                        WTMouseAimPlugin.Log.LogInfo(
                            $"[fbw] '{(ac.definition != null ? ac.definition.name : "<unknown>")}' enabled={_fbwEnabled} " +
                            $"cornerSpeed={_fbwCorner:0.#} maxPitchAngVel={_fbwMaxPitchVel:0.##} gLimit={_fbwGLimit:0.#} " +
                            $"alphaLimiter={_fbwAlphaLimit:0.#} (strength {_fbwAlphaLimStr:0.###})");
                    }
                }
                catch (System.Exception e)
                {
                    _fbwFbw = null; _fbwEnabled = false;
                    WTMouseAimPlugin.Log.LogWarning($"[fbw] probe failed for this airframe ({e.Message}) — v0.55 normalization inactive.");
                }
            }
            return _fbwEnabled && _fbwFbw != null && _fbwCorner > 1f && _fbwGLimit > 0.1f;
        }

        // CANARD PROBE resolve (v0.57) — see the cache comment above. Called on the aircraft-change edge
        // inside ResolveFbw so both probes rebind together. Fail SOFT: any miss leaves _rsCtrl null and
        // the pitch path untouched (identity — exactly the pre-0.57 behaviour on every other airframe).
        private void ResolveCanard(Aircraft ac)
        {
            _rsCtrl = null; _rsCanardRange = 0f;
            try
            {
                if (!_rsRefsTried)
                {
                    _rsRefsTried = true;
                    try
                    {
                        _rsCtrlRef  = AccessTools.FieldRefAccess<Aircraft, RelaxedStabilityController>("relaxedStabilityController");
                        _rsRangeRef = AccessTools.FieldRefAccess<RelaxedStabilityController, float>("canardRange");
                        _rsEffRef   = AccessTools.FieldRefAccess<RelaxedStabilityController, float>("effectiveness");
                        _rsRefsOk = true;
                    }
                    catch (System.Exception e)
                    {
                        _rsRefsOk = false;
                        WTMouseAimPlugin.Log.LogWarning($"[canard] field refs unavailable ({e.Message}) — linearization inactive.");
                    }
                }
                if (_rsRefsOk)
                {
                    _rsCtrl = _rsCtrlRef(ac);
                    if (_rsCtrl != null)
                    {
                        _rsCanardRange = _rsRangeRef(_rsCtrl);
                        if (_rsCanardRange <= 0.1f) _rsCtrl = null; // insane range: stay identity
                    }
                    // v0.58 — log the resolve UNCONDITIONALLY. The v57 session shipped ZERO [canard] lines
                    // and we couldn't tell "field null" from "probe never ran": the buzz was gone, the fit
                    // was identity-correct, but the WHY (a Blueprinter/variant 'Multirole1' prefab with the
                    // serialized field unwired vs. the probe failing) was unprovable. childScan is LOG-ONLY
                    // forensics, NEVER a binding source: the game's own FilterInputs guards on this same
                    // FIELD, so when it's null the game applies no remap either — binding _rsCtrl from
                    // GetComponentInChildren and "inverting" would apply the inverse of a remap that isn't
                    // happening, i.e. CREATE the exact pitch warp v0.57 removed. field=false/childScan=true
                    // means an unwired clone (identity correct); false/false means no canard airframe.
                    WTMouseAimPlugin.Log.LogInfo(
                        $"[canard] resolve '{(ac.definition != null ? ac.definition.name : "<unknown>")}' " +
                        $"field={_rsCtrl != null} childScan={ac.GetComponentInChildren<RelaxedStabilityController>(true) != null}" +
                        (_rsCtrl != null ? $" canardRange={_rsCanardRange:0.#} eff={_rsEffRef(_rsCtrl):0.##} — pitch linearization active." : " — identity (matches the game)."));
                }
            }
            catch (System.Exception e)
            {
                _rsCtrl = null;
                WTMouseAimPlugin.Log.LogWarning($"[canard] probe failed ({e.Message}) — linearization inactive.");
            }
        }

        // HELO FBW + ARCHETYPE PROBE resolve (v0.58) — see the cache comment above. Called on the
        // aircraft-change edge inside ResolveFbw so all probes rebind together. heloFlyByWire is a
        // PRIVATE NESTED class (no compile-time type), so this reads it with Traverse instead of the
        // typed FieldRef pattern the other probes use. Fail SOFT: any miss leaves _heloOk false and
        // the archetype refs null — exact pre-0.58 behaviour (hot direct gains, speed-only blend).
        private void ResolveHelo(Aircraft ac)
        {
            _heloOk = false; _twc = null; _sds = null; _hasCompound = false;
            if (!_collective) return; // fixed-wing: nothing to probe, keep the refs null
            try
            {
                // Archetype components: tilt/nozzle gauges drive the hover-regime blend where present;
                // compound presence is log-only for now (same speed blend as a plain heli until we have
                // a compound recording to justify different constants).
                _twc = ac.GetComponentInChildren<TiltWingController>();
                _sds = ac.GetComponentInChildren<SwivelDuctSystem>();
                _hasCompound = ac.GetComponentInChildren<CompoundHeloController>() != null;

                if (ac.GetControlsFilter() is HeloControlsFilter hcf)
                {
                    object fbw = Traverse.Create(hcf).Field("heloFlyByWire").GetValue();
                    if (fbw != null)
                    {
                        var tf = Traverse.Create(fbw);
                        bool enabled  = tf.Field("Enabled").GetValue<bool>();
                        float gLim    = tf.Field("gLimit").GetValue<float>();
                        Vector3 mav   = tf.Field("maxAngularVel").GetValue<Vector3>();
                        Vector3 dcf   = tf.Field("directControlFactor").GetValue<Vector3>();
                        _heloOk = enabled && gLim > 0.1f && mav.x > 0.01f && mav.y > 0.01f;
                        if (_heloOk) { _heloGLimit = gLim; _heloMaxAngVel = mav; }
                        WTMouseAimPlugin.Log.LogInfo(
                            $"[helofbw] '{(ac.definition != null ? ac.definition.name : "<unknown>")}' enabled={enabled} " +
                            $"gLimit={gLim:0.#} maxAngularVel=({mav.x:0.##},{mav.y:0.##},{mav.z:0.##}) " +
                            $"directControl=({dcf.x:0.##},{dcf.y:0.##},{dcf.z:0.##}) " +
                            $"tiltwing={(_twc != null ? 1 : 0)} swivelduct={(_sds != null ? 1 : 0)} compound={(_hasCompound ? 1 : 0)}");
                    }
                }
            }
            catch (System.Exception e)
            {
                _heloOk = false;
                WTMouseAimPlugin.Log.LogWarning($"[helofbw] probe failed ({e.Message}) — helo rate normalization inactive (pre-0.58 behaviour).");
            }
        }

        // v0.57 — invert the game's relaxed-stability pitch remap. The game computes
        //   effective = Lerp(a, x, |x|) = a*(1-|x|) + x*|x|,   a = AoA/canardRange
        // (RelaxedStabilityController.FilterInput; runs BEFORE the FBW, both assist states, V > 30 m/s).
        // Solving effective(x) = p per sign-branch gives closed forms (branch by p vs a — e is increasing
        // on each side of the tiny O(a²) dip at x∈[0,a/2]):
        //   p >= a:  x = (a + sqrt(a² + 4(p−a)))/2      p < a:  x = (a − sqrt(a² + 4(a−p)))/2
        // so the FBW receives exactly the pitch the control law intended. Without this, near-boresight
        // corrections live inside the quadratic/reversed zone and limit-cycle at ~5 Hz (the Ifrit buzz).
        private static float InvertCanardRemap(float p, float a)
        {
            float x = p >= a ? (a + Mathf.Sqrt(Mathf.Max(0f, a * a + 4f * (p - a)))) * 0.5f
                             : (a - Mathf.Sqrt(Mathf.Max(0f, a * a + 4f * (a - p)))) * 0.5f;
            return Mathf.Clamp(x, -1f, 1f);
        }

        // Is the canard remap live THIS frame? (component present, engine running — the game zeroes
        // effectiveness on engine-off — and above the game's own 30 m/s gate.)
        private bool CanardActive(float vMag)
        {
            if (_rsCtrl == null || vMag <= 30f) return false;
            try { return _rsEffRef(_rsCtrl) != 0f; }
            catch { return false; }
        }

        // One-line FBW parameter summary for the maneuver-recorder header ("what plane was this, really").
        internal string FbwHeader(Aircraft ac)
        {
            try
            {
                if (ResolveFbw(ac))
                    return $"cornerSpeed={_fbwCorner:0.#} maxPitchAngVel={_fbwMaxPitchVel:0.##} gLimit={_fbwGLimit:0.#} " +
                           $"alphaLimiter={_fbwAlphaLimit:0.#} alphaLimiterStrength={_fbwAlphaLimStr:0.###} assist={(ac.flightAssist ? 1 : 0)}" +
                           (_rsCtrl != null ? $" canardRange={_rsCanardRange:0.#}" : "");
            }
            catch { /* fall through to unavailable */ }
            return "<unavailable>";
        }

        // Called from the prefix. Returns true if WE own the stick (native should be skipped).
        public bool BeginFrame(Aircraft aircraft, bool fixedWing, float pilotStrength)
        {
            // v0.82 — publish THIS controller as the HUD's, but only if this aircraft is the one the
            // game itself calls local. GetLocalAircraft is the game's own definition (the same one
            // AimRig.TryGetContext and the recorder use), so an uncrewed drone can never satisfy it,
            // and a failed read leaves the previous publication alone rather than blanking the HUD.
            if (GameManager.GetLocalAircraft(out var localAc) && ReferenceEquals(localAc, aircraft))
                Player = this;

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
                _azlcWinStart = 0f; _azlcFlips = _azlcFlipsPrev = 0; _azlcPeak = _azlcPeakPrev = 0f; _azlcPrevAz = 0f; // fresh az-limit-cycle window (v0.58)
                _manvActive = false; _manvSettle = 0f; _manvOvershot = false; LastPhase = ""; // fresh maneuver tracker
                _manualHold = 0f; _gEng = 0f; // fresh global hard-handoff state (no stale manual hold)
                _ringHead = _ringCount = 0; // clear the context buffer for this command
                _prevFwdValid = false; // don't compute a huge rotation rate across the engage gap
                _yawWeak = 0f; _yawEffFilt = 0f; _closeRateFilt = 0f; _prevAzErrValid = false; // fresh yaw-weakness estimate
                _headingRateFilt = 0f; _tBankSlewed = 0f; // fresh heading-rate lead (v0.51) + bank-target slew (v0.54)
                _eAlignSlew = 0f; // v0.61: clear cross-engagement carry (was a persistent static → stale seed on re-engage)
                _alphaSchedFilt = 1f; // fresh AoA-utilization demand schedule (v0.59)
                _pitchEff = 1f; // fresh measured pitch-effectiveness estimate (v0.60)
                _prevAimValid = false; _aimRateFilt = 0f; // fresh B2 marker-stationary gate (v0.65)
                _aimAzRateFilt = 0f; _aimAzAcId = -1;     // fresh marker-rate feed-forward (v0.78)
                _prevOffTrk = -1f; _stallFilt = 0f;       // fresh closure-stall gate (v0.83) — never wind on an engage step
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
        public void Apply(Aircraft aircraft)
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
                Vector3 prevFwdForHdg = _prevFwd;    // captured before the overwrite below — feeds the
                bool hadPrevFwd = _prevFwdValid;     // heading-rate measurement further down (v0.51)
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

                // B2 MARKER-STATIONARY GATE (v0.65) — angular speed of the aim direction (deg/s),
                // low-passed. HIGH = the user is sweeping the mouse, so the sub-gate fine-settle
                // micro-bank in ApplyEvolvedLegacy stands down (the main law + manual override own a
                // moving marker); quasi-stationary = it may inject. aimDir is already in hand — no new
                // game signal. Convergent/gated design is documented at the injection site.
                // The v0.78 feed-forward differentiates the SAME _prevAim sample, so the staleness guard is
                // hoisted here and covers both: on an aircraft change AimRig re-seeds the marker onto the new
                // nose, and differencing across that seed would emit one enormous bogus rate into both signals.
                int aimAcId = aircraft.GetInstanceID();
                if (aimAcId != _aimAzAcId) { _aimAzAcId = aimAcId; _prevAimValid = false; }
                if (_prevAimValid && dt > 1e-5f)
                {
                    float aimRate = Vector3.Angle(_prevAim, aimDir) / dt;
                    _aimRateFilt += (dt / (0.15f + dt)) * (aimRate - _aimRateFilt);
                    // MARKER AZIMUTH RATE (v0.78) — SIGNED, + = sweeping right, same frame as azErr and
                    // _headingRateFilt. Atan2(x,z)+DeltaAngle rather than a raw subtraction because the
                    // azimuth wraps: a plain difference spikes 360/dt (~21600 deg/s at the 60 Hz fixed step)
                    // every time the marker crosses ±180°, and fed forward into omega that single frame is a
                    // rail-to-rail bank command. DeltaAngle takes the short way round, so the wrap is a no-op.
                    // Source is aimDir, not AimRig.AimForward directly: outside Fly Level they are the same
                    // vector, and inside it aimDir is the LATCHED heading — the demand the loop is actually
                    // tracking — so a marker sweep during a level hold can't inject a turn nobody asked for.
                    float azNow  = Mathf.Atan2(aimDir.x,   aimDir.z)   * Mathf.Rad2Deg;
                    float azPrev = Mathf.Atan2(_prevAim.x, _prevAim.z) * Mathf.Rad2Deg;
                    float rawAR  = Mathf.DeltaAngle(azPrev, azNow) / dt;
                    _aimAzRateFilt += (dt / (HdgRateTau + dt)) * (rawAR - _aimAzRateFilt);
                }
                else _aimAzRateFilt = 0f; // first frame after engage / aircraft change: no valid previous direction
                _prevAim = aimDir; _prevAimValid = true;
                const float aimStillRate = 3f; // deg/s — above frame noise (~0.8 deg/s), below a deliberate sweep
                _settleOK = _aimRateFilt < aimStillRate;

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
                // fineGain moved BELOW the AoA-ceiling block in v0.59: the boost is gated by the
                // AoA-utilization schedule (_alphaSchedFilt), which that block updates. Nothing between
                // here and there reads fineGain (the integrator winds on fineBlend, not fineGain).

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
                // HEADING DEPROJECTION (v0.58) — azErr lives in the horizontal plane, so it inflates by
                // 1/cos(pitch) as the nose nears vertical: the v57 023018/022946 recordings show a
                // near-vertical zoom (cos(pitch) 0.13 / 0.01!) where a REAL sub-degree offset (off ~0.5)
                // read as azErr ±9.5 / ±170, and the V-scaled bank map chased that phantom rail-to-rail
                // into a growing 0.46 Hz roll limit cycle (bank anti-phase with target, corr −0.90) while
                // the body-frame error never moved. |noseHW| of the unit forward vector IS cos(pitch), so
                // multiplying the BANK-PATH errors by it (azBank + azErrPred below) undoes the projection
                // exactly: level flight is h≈1 at every speed (the v57 clean files sit 0.99–1.00, the
                // pathological ones 0.01–0.13 — clean separation, no threshold needed). Pitch/yaw fly
                // body-frame errors and the coordPull release taper keeps RAW azErr (v0.36 lesson), so a
                // capture near the vertical still closes via pitch/yaw — where heading is meaningless the
                // right bank command is simply none.
                float hdgConf = Mathf.Sqrt(noseHW.sqrMagnitude); // = cos(pitch); t.forward is unit-length

                // NOSE HEADING RATE (v0.51) — how fast the nose's world heading is swinging (deg/s,
                // + = right), low-passed. Feeds the anticipatory lead on the turn-rate bank command
                // below. Nose-only (no marker term), so it can never fight a mouse flick — same
                // principle as the pitch/yaw rate damping above. Degenerate frames (nose near
                // vertical, heading undefined) hold the last filtered value.
                if (hadPrevFwd && dt > 1e-5f)
                {
                    Vector3 prevNoseHW = new Vector3(prevFwdForHdg.x, 0f, prevFwdForHdg.z);
                    if (prevNoseHW.sqrMagnitude > 1e-6f && noseHW.sqrMagnitude > 1e-6f)
                    {
                        float rawHR = Vector3.SignedAngle(prevNoseHW, noseHW, Vector3.up) / dt;
                        _headingRateFilt += (dt / (HdgRateTau + dt)) * (rawHR - _headingRateFilt); // tau shared with the v0.78 marker rate
                    }
                }

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
                azBank *= hdgConf; // v0.58 heading deprojection — see the hdgConf comment above
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
                // v0.58 TILT-DRIVEN REGIME — on tilt-wing / swivel-duct VTOLs the game hands us the live
                // tilt state (TiltWingController.GetWingAngle / SwivelDuctSystem.GetNozzleAngle, both
                // decompiled), which is what actually decides whether bank-to-turn works, not speed.
                // max() with the speed blend: engines-up at speed still flies hover-style, engines-forward
                // at low speed falls back to the speed ramp. Refs are null on plain helis / fixed-wing.
                if (_twc != null || _sds != null)
                {
                    try
                    {
                        float tiltFrac = 0f;
                        if (_twc != null)
                        {
                            var (lo, hi) = _twc.GetAngleLimits();
                            // ponytail: assumes maxAngle = engines-up/hover => tiltFrac 1 in hover. Orientation
                            // UNCONFIRMED — one tiltwing transition flight must verify; flip to (hi-angle)/(hi-lo)
                            // if the blend reads backwards in the [seam]/HUD readout.
                            if (hi - lo > 1f)
                                tiltFrac = Mathf.Clamp01((_twc.GetWingAngle() - lo) / (hi - lo));
                        }
                        else tiltFrac = Mathf.Clamp01(_sds.GetNozzleAngle() / 90f);
                        _heliBlend = Mathf.Max(_heliBlend, tiltFrac);
                    }
                    catch { _twc = null; _sds = null; } // fail-soft: speed blend only for this airframe
                }
                if (_hoverOn) _heliBlend = 1f;

                // LIVE AoA (v0.55) — signed angle of the velocity off the nose about body-right, the same
                // quantity the game's own alpha limiter reads (+ = nose above the flight path = pulling).
                // Hoisted from the recorder block so the AoA ceiling below and the CSV share one value.
                float aoaNow = 0f;
                if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 4f)
                    aoaNow = TargetCalc.GetAngleOnAxis(t.forward, aircraft.rb.velocity, t.right);

                // FBW PROBE (v0.55) — what CAN this airframe do right now? Reconstruct the game's own
                // stick->pitch-rate gain from the per-airframe params (decompiled FlyByWire.Filter):
                //   protected law (assist ON, or q_ratio > 1.2):  gLimit*9.81/max(V, 0.75*Vc), scaled by
                //     clamp(q_ratio, 0.3, 1) below corner speed — the g-command the mod's gains were
                //     tuned against (offline fit on the v54 tester CSVs: corr +0.92..+0.99 at high q);
                //   raw law (assist OFF at low/mid q):            maxPitchAngularVel flat, NO protection.
                // omegaMax (the ASSIST-AWARE achievable rate) caps the turn-rate demand + freezes the
                // integrators; qSched is the v0.56 low-q pitch gain schedule applied inside the law (the
                // takeoff-oscillation fix — see the comment at the EvolvedLegacy pitch line). Deliberately
                // EXCLUDES the game's alpha-limiter term — the mod-side AoA ceiling below owns AoA, folding
                // it in here would double-count the protection.
                // ponytail: the game's assist<->protected crossfade is a tau~1s lerp (limitFactorSmoothed);
                // we use the binary condition — worst case ~1 s of mild mismatch right after toggling assist.
                bool  fbwResolved = ResolveFbw(aircraft);
                bool  fbwOk       = fbwResolved && !_collective; // collective aircraft: heli path stays untouched
                float omegaMax    = float.PositiveInfinity;      // rad/s the pitch channel can actually deliver
                float qSched      = 1f;                          // low-q demand schedule (1 = no change, at/above corner)
                if (fbwOk)
                {
                    float rho = 1.225f;
                    try { rho = aircraft.GetAirDensity(); } catch { /* fall back to sea level */ }
                    if (rho < 0.01f) rho = 1.225f;
                    float qRatio = vMag * vMag * rho / (_fbwCorner * _fbwCorner * 1.225f);
                    float rpsRef = _fbwGLimit * 9.81f / Mathf.Max(vMag, 0.75f * _fbwCorner);
                    if (qRatio < 1f) rpsRef *= Mathf.Clamp(qRatio, 0.3f, 1f);
                    omegaMax = (aircraft.flightAssist || qRatio > 1.2f) ? rpsRef : _fbwMaxPitchVel;
                    qSched   = Mathf.Clamp(qRatio, 0.3f, 1f); // the game's own q clamp, reused as the schedule
                }

                // MEASURED PITCH-EFFECTIVENESS (v0.60) — the pitch twin of _yawWeak, consumed by
                // ApplyEvolvedLegacy. Read the game FBW's OWN commanded vs achieved pitch rate (the recorder
                // already logs this pair) and low-pass the achieved/commanded ratio with the same
                // fast-attack/slow-release asymmetry as _yawWeak, so loadout/mass/density/damage/mush
                // all appear generically as achieved < commanded. Magnitude-only (the game rate pair is
                // +=nose-down; sign is irrelevant to a ratio). Fail-soft: any miss holds the last value.
                // Attack 0.10 s catches mush onset in ~2-3 frames without reacting to single-frame noise;
                // release 1.0 s gives the estimate memory so demand can't snap hot again as the plant
                // momentarily recovers mid-cycle. Regime (loop-shaping) constants, not per-plane.
                float pitchEffInst = _pitchEff; // default = hold (fail-soft: probe miss / manual / read error)
                if (fbwResolved && !_collective && _fbwFbw != null && !manualNow)
                {
                    try
                    {
                        // v0.64: SIGNED ratio. Through v0.63 both sides were Mathf.Abs'd, which made the
                        // estimator blind to the one failure it exists to catch: a plant that delivers the
                        // OPPOSITE of what was asked. In the v62 FS-12 captures |ach/cmd| read 1.00 through
                        // every departure — "plant healthy" — while the signed ratio was -1.00 for 52-89%
                        // of commanded frames (aircraft pitching nose-DOWN at 3x magnitude under a full
                        // nose-UP command, post-stall). Clamp01 now floors a reversed plant to 0 with no
                        // extra logic. Noise gate stays on MAGNITUDE — gating on the signed cmd would skip
                        // every nose-down command and silently make this a one-sided estimator.
                        float cmd = _fbwFbw.GetTargetPitchAngVel();
                        float ach = _fbwFbw.GetPitchAngVel();
                        // v0.67 C1-LATCH FIX (rec14). A gated-out command (|cmd| < 0.05) carries NO
                        // information — but pure-HOLDING _pitchEff there latches a transient low-q mush at
                        // ~0 forever: C1 keeps pitch ×0 → the stick sits at 0 → cmd stays < 0.05 → it can
                        // never re-measure (rec14 froze the pull ~4 s at railed bank). Instead, with no
                        // command to measure, floor the estimate at the small self-probe level via
                        // Max(_pitchEff, revThresh): a LATCHED-low estimate rises toward ~15% (inst > cur ⇒
                        // the slow RELEASE tau) so pitch re-establishes → cmd > 0.05 → re-measure; a HEALTHY
                        // estimate (already ≥ revThresh) is left untouched, so a brief neutral stick never
                        // drags a good jet's authority down. A genuine reversal produces cmd > 0.05 with ach
                        // reversed, so it re-drops on the next real command (bounded slow pulse, not a
                        // freeze) — the pre-C1 effFloor=0.3 gave this self-probe for free; C1 removed it,
                        // this restores it at the lower 0.15. Gated on a LIVE readable FBW (inside this
                        // block), so a probe miss still holds — no per-plane constant.
                        pitchEffInst = Mathf.Abs(cmd) > 0.05f ? Mathf.Clamp01(ach / cmd)
                                                             : Mathf.Max(_pitchEff, PEffRevThresh);
                    }
                    catch { /* leave hold */ }
                }
                const float pEffAtk = 0.10f, pEffRel = 1.0f; // regime constants: drop fast, recover slow
                float pTau = pitchEffInst < _pitchEff ? pEffAtk : pEffRel;
                _pitchEff = Mathf.Clamp01(_pitchEff + (dt / (pTau + dt)) * (pitchEffInst - _pitchEff));

                // MOD-SIDE AoA CEILING gates (v0.55) — computed here, applied to the pitch command
                // post-law below (and freezing _iPitch while biting). Mirrors the game's own limiter
                // shape (only cut the command DRIVING AoA further past the limit, per sign) so recovery
                // is never blocked: aoaGateUp bites as AoA nears +ceiling and scales only NOSE-UP
                // commands; aoaGateDn is the mirrored push guard. 1 = wide open.
                float aoaGateUp = 1f, aoaGateDn = 1f;
                float aoaRecover = 0f; // v0.59: restoring pitch past either AoA ceiling (+ = nose-down needed)
                if (!_collective)
                {
                    // v0.56: RELATIVE margin/fade (was fixed 4/6 deg). alphaLimiter spans 10..27 deg across
                    // airframes; the fixed 4-deg margin collapsed the Trainer (limiter 10) to a 6-deg ceiling
                    // with the fade starting at 0 deg AoA — 60-90% of pitch authority cut at ordinary 3-5 deg
                    // turning AoA (v55 recordings). Proportional margins free it; FS-12 (27) keeps exactly 4/6.
                    // ponytail: fixed fractions, promote to Cfg only if flight tuning demands it
                    float lim = fbwOk ? _fbwAlphaLimit : 25f;
                    float aoaMargin = Mathf.Min(4f, 0.15f * lim);
                    // v0.61: floor the fade at 4 deg. The proportional fade collapses to 2.5 deg on a low
                    // limiter (lim 10), narrower than the one-lead-time AoA overshoot a low-q plant produces —
                    // that turns the gate into a relay (the Trainer AoA pump). Floor keeps it a graded fade,
                    // never bang-bang. For lim >= 16 (0.25*lim >= 4) the floor is INACTIVE, so FS-12 (lim 27,
                    // fade 6) and every jet with lim >= 16 are byte-identical; only low-limit STOL/trainers widen.
                    float aoaFade = Mathf.Max(4f, Mathf.Min(6f, 0.25f * lim));
                    float aoaCeil = lim - aoaMargin;
                    // v0.57 PREDICTIVE LEAD — the assist-off anti-pump. The v56 recordings showed the
                    // reactive gate is a relay on a fast pull: AoA blows 1.3-2.5x past the ceiling before
                    // the fade bites (Trainer 20.4 deg on an 8.5 ceiling; CAS1 dragged to 9.7 g on a 6 g
                    // frame), the gate slams shut, AoA falls, the gate reopens into the same full pull —
                    // a growing 0.7 Hz buck cycle. Gate on PREDICTED AoA instead, and only lead INTO each
                    // ceiling (rate toward it), never out: the gate closes early on a fast approach but
                    // reopens on the real AoA — the asymmetry is hysteresis, which is what breaks a relay.
                    // ponytail: fixed 0.30 s lead / 0.15 s rate filter, promote to Cfg if tuning demands
                    const float aoaLead = 0.30f, aoaRateTau = 0.15f;
                    float aoaRate = dt > 1e-4f ? (aoaNow - _aoaPrev) / dt : 0f;
                    _aoaPrev = aoaNow;
                    _aoaRateFilt += (dt / (aoaRateTau + dt)) * (aoaRate - _aoaRateFilt);
                    float aoaPredUp = aoaNow + Mathf.Max(0f, _aoaRateFilt) * aoaLead; // rising -> +ceiling
                    float aoaPredDn = aoaNow + Mathf.Min(0f, _aoaRateFilt) * aoaLead; // falling -> -ceiling
                    aoaGateUp = Mathf.Clamp01((aoaCeil - aoaPredUp) / aoaFade);
                    aoaGateDn = Mathf.Clamp01((aoaCeil + aoaPredDn) / aoaFade);
                    // v0.59 AoA-UTILIZATION DEMAND SCHEDULE — the LOADED-jet fix (v58 discord FS-12 rec
                    // 015235: rail-to-rail ~0.55 Hz pitch relay at 160-170 m/s, AoA +43..-18 on a ~23 deg
                    // ceiling, iPitch ~0.003 — integrators innocent). qSched above keys off dynamic
                    // pressure ONLY, so a loaded airframe that needs high AoA to make its commanded G
                    // above corner speed reads as high-q while the plant is actually mushing: gain stays
                    // hot, the nose departs, and the gate+damping relay-cycle it. Fold live AoA
                    // utilization — against THIS airframe's probed ceiling — into the same schedule:
                    // 1 below utilStart, easing to the same 0.3 floor as the game's q clamp at the
                    // ceiling. Uses the PREDICTED AoA (same lead as the gates) so it bites into a fast
                    // pull, with fast-attack/slow-release smoothing (the _yawWeak hysteresis trick) so
                    // demand can't snap hot again as AoA falls back through the ceiling mid-cycle — that
                    // snap-back is what sustains a relay. Light jet at moderate AoA: util < utilStart,
                    // sched 1, behaviour byte-identical. Airframe-agnostic by construction: probed
                    // ceiling + live state, no per-plane constants.
                    // ponytail: fixed utilStart/floor/taus, promote to Cfg if flight tuning demands it.
                    const float utilStart = 0.6f, schedFloor = 0.3f, atkTau = 0.05f, relTau = 1.0f;
                    float aoaUtil  = Mathf.Max(aoaPredUp, -aoaPredDn) / Mathf.Max(1f, aoaCeil);
                    float schedRaw = Mathf.Lerp(1f, schedFloor, Mathf.Clamp01((aoaUtil - utilStart) / (1f - utilStart)));
                    float schedTau = schedRaw < _alphaSchedFilt ? atkTau : relTau;
                    _alphaSchedFilt += (dt / (schedTau + dt)) * (schedRaw - _alphaSchedFilt);
                    qSched = Mathf.Min(qSched, _alphaSchedFilt);
                    // v0.59 AoA RECOVERY BIAS — the gates only CUT the command driving AoA outward; past
                    // the ceiling the command is zero and recovery is left to raw aero + reactive damping,
                    // which is exactly the asymmetric relay the FS-12 cycle rode (+43 overshoot, then an
                    // unopposed -18 bunt). Command a restoring pitch proportional to the PREDICTED excess
                    // past either ceiling, normalized by the airframe-proportional fade width: continuous
                    // (no discontinuity to relay on), symmetric, exactly zero inside the envelope.
                    // v0.62: use a TWO-SIDED predicted AoA, NOT the gates' one-sided aoaPredUp/Dn. The
                    // gates clip the rate term per side on purpose (lead INTO a ceiling only = hysteresis,
                    // which is what stops THEM relaying). Feeding that same one-sided prediction to the
                    // recovery bias made it blind to the recovery it was itself producing: while AoA
                    // plunged from +43 at ~60 deg/s, max(0,rate) was 0, so the bias held near its +0.5
                    // asymptote until AoA was physically back inside — by which point the plant carried
                    // -2.1 rad/s of pitch rate (2.3x the FBW's own maxPitchAngVel) straight through to
                    // AoA -47, where the mirrored term fired with equal authority. Position feedback on a
                    // rate-input plant with no damping = bang-bang; v61 rec 103523/103331 show the ~1:1
                    // overshoot signature (+43 -> -47) at 0.54 Hz, both laws (this block is shared, post-
                    // dispatch). The symmetric lead IS the damping term: pred = AoA + rate*lead makes the
                    // bias a PD, so it fades as the recovery develops instead of holding to the crossing.
                    float aoaPredSym = aoaNow + _aoaRateFilt * aoaLead;
                    aoaRecover = (Mathf.Max(0f, aoaPredSym - aoaCeil) - Mathf.Max(0f, -aoaPredSym - aoaCeil)) / aoaFade;
                }
                else _alphaSchedFilt = 1f; // rotorcraft: schedule inert (the helo path is rate-normalized)

                // v0.67 AoA-MARGIN TURN CAP (rec16). omegaMax folds the g-limit (gLimit·9.81/V) but NOT the
                // live alpha-limiter, so at low q — where the wing hits its alpha ceiling BEFORE gLimit — the
                // turn-rate demand exceeded what the limiter would fly: tBankE railed to MaxBank, the pull was
                // gated, AoA pinned ~20° on a 23° ceiling, and the roll HUNTED around a bank the wing couldn't
                // sustain (rec16, off never closing). aoaGateUp already IS the live nose-up authority remaining
                // (1 wide-open .. 0 at the ceiling, predictive), so fold it into the achievable-rate cap: the
                // demanded turn shrinks to what the wing can pull at this AoA and the law stops asking for a
                // turn the limiter will chop. Floored at 0.3 so a wing AT the ceiling still holds a sustained
                // turn (mirrors the qSched floor) instead of leveling. Applied to omegaMax ITSELF, so BOTH
                // lockstep achievability sites (this shared block's omegaDes clamp AND EL's local omega clamp,
                // which receives this same capped omegaMax as a param) cap identically. fbwOk is already
                // !_collective — rotorcraft keep omegaMax untouched. Keyed to live AoA + probed ceiling only.
                if (fbwOk) omegaMax *= Mathf.Max(0.3f, aoaGateUp);

                // FINE-CAPTURE GAIN (moved below the AoA block in v0.59 — see the fineBlend comment above).
                // The boost is gated by the AoA-utilization schedule: at 3.5x near boresight it railed the
                // stick on ~5 deg of error exactly where the loaded plant was mushing (the kick that starts
                // the FS-12 relay). Gated by _alphaSchedFilt — NOT the speed qSched — so a low-speed LIGHT
                // jet keeps the current capture feel; only genuine near-ceiling AoA softens it.
                float fineGain = 1f + Cfg.FineGainBoost.Value * fineBlend * _alphaSchedFilt;
                // v0.58: NO fine boost on rotorcraft. The boost multiplies the outer P gain up to 3.5x
                // exactly at boresight — on the helo rate-command inner loop (~0.3 s lag) that made the
                // loop gain 10-15 s^-1 and limit-cycled at ~1 Hz on BOTH reported wobbles (v57 recs:
                // UH-90 pitch 0.88/1.08 Hz, RAH-72 hover yaw 1.16 Hz). Planes need the boost because
                // their FBW parks short; the helo PID has an integral compensator and doesn't.
                if (_collective) fineGain = 1f;

                // ANTICIPATORY LEAD (v0.51) — THE death-wobble fix. bankTR below is pure-proportional
                // in azErr, and 8 user recordings showed the achieved bank lags that command by a
                // constant ~0.7 s and overshoots it — at the observed 0.3–0.85 Hz that lag is ~90–180°
                // of phase, so the P-only loop self-sustains a limit cycle (±88° bank from ±6° azErr,
                // roll railed, at ALL speeds; violence scales with V). Command the bank for where the
                // heading WILL be instead: subtract headingRate·TurnLeadTime so the bank rolls out
                // EARLY, decelerating onto the marker instead of overshooting it.
                // Feeds ONLY the turn-rate bank paths (here + ApplyEvolvedLegacy's local copy); the
                // linear servo (azBank/linBank above) and the coordPull release taper keep RAW azErr —
                // release timing must track real arrival, not the prediction (v0.36→v0.37 mush lesson).
                // RELATIVE-RATE LEAD (v0.83) — the lead was leading against the wrong rate. azErr is the
                // angle between the nose heading and the marker heading, so its OWN derivative is
                //     d(azErr)/dt = markerAzRate - noseHeadingRate,
                // and the anticipatory term azErr + T*d(azErr)/dt is therefore
                //     azErr - (_headingRateFilt - _aimAzRateFilt) * T.
                // Through v0.82 the marker term was missing, i.e. the lead was the true derivative ONLY
                // when the marker stood still — which is exactly the regime v0.51 was measured in (eight
                // recordings of a pilot correcting onto a fixed point). Against a SWEEPING marker the nose
                // is deliberately rotating AT the target, and the absolute-rate form read that tracking
                // rotation as overshoot and braked it: R21 measured _headingRateFilt*T = 7.85 deg of lead
                // against a real 9.31 deg error (84% of it removed), with headingRate - aimRate = +0.009
                // deg/s — i.e. essentially ALL of the lead was cancelling rotation that was tracking the
                // target. It also fought the v0.78 feed-forward directly: that term adds aimRate to omega
                // at unit gain, while this one subtracted T*AssistTurnRateGain = 0.65*0.92 = 0.60 of the
                // same rate back out again, so 60% of the feed-forward never reached the plant.
                // With the relative rate the term becomes true PD damping on the azimuth error:
                //   - stationary marker  -> _aimAzRateFilt == 0 -> byte-identical to v0.82;
                //   - matched sustained turn -> the two rates cancel -> no braking, and the standing error
                //     finally sees the full configured AssistTurnRateGain instead of the predFloor's 30%.
                // BOUNDED BY CONSTRUCTION: the change can only move azErrPred by +T*_aimAzRateFilt, and the
                // brake/floor clamp below still confines the result to [azErr*predFloor, azErr]. So neither
                // sign of marker sweep can command more bank than the RAW error already justified — the
                // v0.52 relay argument survives untouched.
                // Cfg.RelativeTurnLead is the A/B lever: off = bit-identical to v0.82, no restart needed.
                // Feeds the SINGLE azErrPred site; ApplyEvolvedLegacy inherits it as a parameter, so the
                // v0.51/v0.55 two-site lockstep is unaffected (there is only one lead computation).
                float leadRate = _headingRateFilt - (Cfg.RelativeTurnLead.Value ? _aimAzRateFilt : 0f);
                float leadDeg  = leadRate * Cfg.TurnLeadTime.Value; // deg of lead ACTUALLY applied (recorder column)
                float azErrPred = azErr - leadDeg;
                // BRAKE-ONLY LEAD (v0.52). Unclamped, the lead term closed its own fast loop: near
                // boresight azErrPred is DOMINATED by -headingRate·lead (v51 recordings: 2.1-2.7× the
                // real error), and the V-scaled atan slope (~44° bank/° at 470 m/s) turned that ±2°
                // rate ripple into a ±65° bank relay → a new 1.1-1.35 Hz limit cycle in HOLD phase.
                // Clamp the prediction to [0, azErr]: the lead may shrink the error toward zero (early
                // rollout — what killed the slow wobble) but never flip its sign or exceed it, so the
                // commanded bank is always bounded by the REAL error and the relay can't self-sustain.
                // PROPORTIONAL FLOOR (v0.54). Floored at 0, the brake was a RECTIFIER: heading-rate
                // ripple pinned azErrPred to exactly 0 while 1.5-5.7 deg of real error remained (19
                // v0.53 recordings), commanding full wings-level mid-correction — the nose drifted, the
                // error regrew, and at speed the ~44 deg-bank-per-deg atan slope turned that sawtooth
                // into a bank target banging 0<->65 deg at ~1.5 Hz. Floor the prediction at a fraction
                // of the real error instead: rollout still starts early (the lead's job), but level
                // flight is never commanded while error remains; the floor self-releases as azErr -> 0.
                // v0.83: predFloor was reviewed for DELETION once the lead above went relative-rate, and
                // KEPT. What it protects is the v0.54 rectifier, which lives entirely in the STATIONARY-
                // marker regime (a pilot correcting onto a fixed point, heading-rate ripple pinning the
                // prediction to 0 while degrees of real error remain) — and that regime is precisely where
                // _aimAzRateFilt == 0 and the relative-rate lead is bit-identical to the absolute one. The
                // relative form removes nothing this floor was defending against; deleting it would re-open
                // the v0.53 0<->65 deg bank sawtooth. What the v0.83 change DOES do is make the floor
                // self-release in the sustained-tracking case: with the tracking rotation no longer
                // subtracted, azErrPred lands near azErr instead of at 0.30*azErr, so the floor stops being
                // the binding constraint (R21: binding on 100% of the settled window, holding the effective
                // proportional gain at 0.28 of a configured 0.92) without being removed from the regime it
                // was built for.
                const float predFloor = 0.30f; // ponytail: fixed fraction, promote to Cfg if flight tuning wants it
                azErrPred = azErr >= 0f ? Mathf.Clamp(azErrPred, azErr * predFloor, azErr)
                                        : Mathf.Clamp(azErrPred, azErr, azErr * predFloor);
                // v0.58 heading deprojection (see hdgConf above). Pure multiplicative, applied AFTER the
                // brake/floor clamp so the clamp bounds scale coherently (h·clamp(x,a,b) = clamp(h·x,h·a,h·b));
                // azErr and _headingRateFilt inflate by the same 1/cos(pitch), so scaling the result
                // deprojects both terms at once. ApplyEvolvedLegacy inherits this via the parameter —
                // the v0.51/v0.55 lockstep between the two bank-target sites is preserved untouched.
                azErrPred *= hdgConf;
                // SETTLE-EXIT RAMP (v0.67, rec30/rec31). The old presence gate was a HARD step:
                // |azErr|<=0.5 -> azTR=0, above 0.5 -> full azErrPred. Below 0.5 B2's V-independent micro-
                // bank owns the settle, but it can't close azimuth at high q, so any drift walks azErr across
                // 0.5, and at that instant the full V-scaled bankTR (22-44 deg/deg at 285 m/s) slammed a
                // 0.5-1 deg error into 16-44 deg of bank -> overshoot -> reversal -> 0.43 Hz relay re-armed
                // at the gate boundary (a step excitation the heading-rate lead can't anticipate). Ramp the
                // turn-rate demand in PROPORTIONALLY over [0.5, 0.5+azRampW] instead, so the lead/predFloor/
                // slew get a graded signal, not a step. STILL exactly 0 below 0.5 (B2 keeps ownership — no
                // dead zone reintroduced), and azErrPred is still predFloor-floored, so a genuine 1-2 deg
                // error still sizes a real bank (v0.61 reason the gate exists is preserved).
                const float azRampW = 1.5f; // deg; regime constant (geometry of the hand-off, not per-plane). LOCKSTEP with EL.
                float azTR = azErrPred * Mathf.Clamp01((Mathf.Abs(azErr) - 0.5f) / azRampW);
                float omegaDes = azTR * Mathf.Deg2Rad * Cfg.AssistTurnRateGain.Value;            // rad/s, signed
                // MARKER-RATE FEED-FORWARD (v0.78) — the standing sustained-turn lag. In the turn360 card the
                // marker sweeps at a constant 12.066 deg/s and the aircraft ACHIEVES that rate (12.02 deg/s,
                // ±0.01 across four replicates) while parked on a 9.54 deg azimuth lag (stdev 0.021 deg).
                // Nothing is saturated: |outP| max 0.587, |outR| max 0.793, 5.17 g of a 9 g limit, AoA 7.7 of
                // a 27 deg ceiling. That is not a fault, it is what a PURE-PROPORTIONAL loop must do. The only
                // integral term on this axis is _iYaw, and it winds on fineBlend = clamp01(1 - off/FineAngle),
                // which is EXACTLY 0 at off = 9.5 with FineAngle 6 — the capture shows both integrators flat at
                // 0.000 for the whole 30 s. So the rate has to be BOUGHT with error: e_ss = w/K = 12.07/1.31 =
                // 9.2 deg, which reproduces the measurement to a tenth of a degree.
                // Supply the rate instead of making the loop earn it. Gain is exactly 1.0 and there is no
                // multiplier to tune: matching the marker's own azimuth rate is KINEMATICS, not a per-plane
                // constant, so this satisfies the generality rule by construction. Added BEFORE the
                // achievability cap below, so the probed per-airframe omegaMax bounds the feed-forward exactly
                // like the proportional demand — it can never command a turn this airframe cannot fly — and
                // yawCapped therefore still reflects the TOTAL demand.
                // SAFE BY CONSTRUCTION: _aimAzRateFilt is identically zero whenever the marker is stationary,
                // and every other card segment (micro1-10, fine, az10/az30/az90/az150, elUp, elDn, reversal,
                // astern) commands a STEP to a fixed direction and then holds it. The feed-forward contributes
                // nothing there and those metrics cannot move. Only turn360 — and a human tracking a moving
                // marker — sees any change at all, which is exactly why this is scoreable in one session.
                // MarkerRateFeedForward is the A/B lever: off = bit-identical to v0.77, no restart needed.
                // LOCKSTEP: ApplyEvolvedLegacy's local omega copy adds the SAME term at the same point,
                // under the same rule as the azErrPred / achievability-cap lockstep notes.
                if (Cfg.MarkerRateFeedForward.Value) omegaDes += _aimAzRateFilt * Mathf.Deg2Rad;
                // ACHIEVABILITY CAP (v0.55) — the low-speed anti-windup. Below corner speed the FBW's
                // deliverable pitch rate collapses (x q_ratio, see the probe above) but the turn-rate
                // demand didn't know that: it kept asking for a turn the plane can't fly, the bank target
                // slammed +/-72 deg, coordPull loaded into the stall, and the outer loop wound up into the
                // reported compounding low-speed oscillation. Capping omegaDes at the achievable rate
                // shrinks bankTR = atan(omega*V/g) physically — coordPull (prop. to sin(bank)) shrinks with
                // it. yawCapped also freezes _iYaw below (don't wind what can't be flown).
                // v0.55 LOCKSTEP: ApplyEvolvedLegacy's local omega copy applies this SAME cap — the two
                // sites must stay identical (same rule as the azErrPred lockstep note in that method).
                bool yawCapped = Mathf.Abs(omegaDes) > omegaMax;
                if (yawCapped) omegaDes = Mathf.Clamp(omegaDes, -omegaMax, omegaMax);
                float bankTR   = Mathf.Atan(omegaDes * Mathf.Max(Cfg.BankSpeedFloor.Value, vMag) / 9.81f) * Mathf.Rad2Deg; // deg, signed (v0.60: shared floor with EvolvedLegacy — finding 8)
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
                // CLOSURE-STALL GATE (v0.83) — gate the integrator on error PERSISTENCE, not error
                // MAGNITUDE. fineBlend answers "is the error small"; the question that decides whether an
                // integral term belongs is "has the proportional path failed to close this error". Measure
                // that as a DIMENSIONLESS ratio — what fraction of the nose's own rotation this tick is
                // actually going into shrinking the error:
                //     closeFrac = (d(off)/dt shrinking) / (total nose rotation rate)
                // 1 = every degree the nose sweeps is a degree of error closed (a slew converging normally);
                // 0 = the nose is rotating hard and the error is not moving (tracking a sweeping marker
                // from behind — R21: nose 11.58 deg/s, error closing 0.033 deg/s, closeFrac 0.003).
                // A RATIO on purpose: an absolute deg/s "is it closing" threshold would be a per-airframe
                // constant in disguise (what counts as slow closure on a 19 deg/s fighter is fast on a
                // trainer), which the one-law rule forbids. noseTurnDeg is roll-blind (it differences the
                // FORWARD axis), so a pure roll-in cannot fake closure in either direction.
                float closeFrac = 1f; // no valid previous sample => assume closing => never wind on frame 1
                if (_prevOffTrk >= 0f && dt > 1e-5f)
                    closeFrac = Mathf.Clamp01(((_prevOffTrk - off) / dt) / Mathf.Max(noseTurnDeg / dt, 1e-3f));
                _prevOffTrk = off;
                // PERSISTENCE. The ratio alone cannot tell "stalled forever" from "hasn't started yet":
                // the instant a marker jumps, closure is 0 and the raw ratio screams stall. Only TIME
                // separates them, so hold it through an asymmetric filter — slow to believe a stall, quick
                // to drop it. This is the anti-windup for the whole change: through a full-authority roll-in
                // the gate only reaches ~0.25, and the moment the pull starts closing the error it collapses
                // inside 0.2 s, so the existing leak has the entire pull phase to bleed off whatever wound.
                // ponytail: 4 s attack — long against the law's own worst LEGITIMATE "not closing yet"
                // transient (a full roll-in is MaxBankAngle/BankSlewRate = 1.2 s at stock), short against a
                // sustained track (a 30 s turn360 segment). Replace with 4*MaxBankAngle/BankSlewRate if an
                // airframe ever shows a roll-in past ~2 s. Release matches the existing _closeRateFilt tau.
                const float stallAttackTau = 4.0f, stallReleaseTau = 0.2f;
                float stallInst = 1f - closeFrac;
                float stallTau  = stallInst > _stallFilt ? stallAttackTau : stallReleaseTau;
                _stallFilt += (dt / (stallTau + dt)) * (stallInst - _stallFilt);

                float iCap = Cfg.FineIntegralCap.Value;
                float iGate = 0f; // the wind gate ACTUALLY applied this frame (recorder column iGate)
                if (Cfg.FineIntegralGain.Value > 0f && iCap > 0f && !flyLevel)
                {
                    float ki = Cfg.FineIntegralGain.Value, leak = Cfg.FineIntegralLeak.Value;
                    // MAX, not replace: inside the fine cone the old blend still owns the gate, so fine
                    // capture is unchanged wherever the error is closing (stall ~0 there). yawCapped
                    // suppresses only the NEW path — winding against a turn demand the airframe cannot
                    // fly is the one way a persistence gate can wind forever, and it is exactly the
                    // regime a low-limit STOL trainer lives in. The existing !yawCapped guard on _iYaw
                    // makes this redundant for yaw and load-bearing for pitch, which in a banked turn is
                    // the axis actually flying the turn. IntegralStallGate off => iGate == fineBlend,
                    // i.e. bit-identical to v0.82.
                    iGate = Mathf.Max(fineBlend,
                        Cfg.IntegralStallGate.Value && !yawCapped ? _stallFilt : 0f);
                    if (_engP > 0f) _iPitch = 0f; // you own pitch — don't wind against your stick
                    else if (aoaGateUp >= 1f && aoaGateDn >= 1f) // v0.55 anti-windup: FREEZE while the AoA
                    // ceiling is biting — winding in nose-up bias the ceiling won't let fly would just
                    // dump in as a lurch on unload. Freeze (hold), not reset: the fine bias is still valid.
                        _iPitch = Mathf.Clamp(_iPitch + (-local.y * ki * iGate - _iPitch * leak) * dt, -iCap, iCap);
                    if (_engY > 0f) _iYaw = 0f;
                    else if (!yawCapped) // v0.55 anti-windup: same freeze while the turn-rate demand is
                    // beyond the achievable pitch rate — the heading can't close any faster than the cap.
                        _iYaw   = Mathf.Clamp(_iYaw   + ( local.x * ki * iGate - _iYaw   * leak) * dt, -iCap, iCap);
                }
                else { _iPitch = _iYaw = 0f; }

                // ---- PER-AXIS CONTROL LAW ------------------------------------------------------------
                // Turn the shared pre-compute above into the three target stick values tgtP/tgtR/tgtY. ONE
                // fixed-wing law now (EvolvedLegacy — the Unified A/B alternative was removed in v0.65);
                // rotorcraft are force-flown through it too (every bit of heli handling — bank fade, hover
                // roll gate, yaw boost, rate normalization — lives only there). Everything above (aimDir/
                // local/off/phi/rates/azErr/flight state/yaw-weakness/targetBank/integrator/_settleOK) and
                // below (slew, manual override, write-out, anomaly/recorder/phase) is SHARED — only the
                // per-axis shaping differs here. pullGate/yawScale/coordPull are surfaced for the debug trace.
                float tgtP, tgtR, tgtY, pullGate, yawScale, coordPull;
                _tBankFlown = targetBank; // the law overwrites this with its own slewed tBankE below
                ApplyEvolvedLegacy(t, local, off, vMag, sens, fineGain, alignFrac, bigTurn, targetBank, azErr, azErrPred,
                    phi, lateral, pitchRate, yawRate, rollRate, pitchDamp, damp, assist, dt, omegaMax, qSched,
                    out tgtP, out tgtR, out tgtY, out pullGate, out yawScale, out coordPull);

                // v0.56: the v0.55 assist-off pitch normalization (tgtP *= protected/achievable) was
                // DELETED — assist OFF is now a PERFORMANCE MODE: the game's raw law passes through at
                // full command, guarded by the AoA ceiling below + the low-q gain schedule inside the law
                // (both assist-independent). High-speed assist parity needs no mod-side scaling — the
                // game's own crossfade runs the identical protected law at q_ratio > 1.2.
                // MOD-SIDE AoA CEILING (v0.55) — the low-speed pitch-oscillation fix. Below corner speed
                // the game's own alpha limiter is weak (0.05/deg — AoA still hit +37 deg in the Draken
                // recordings) and with assist OFF it's absent entirely; the mod kept commanding full pitch
                // into the stall, the flight path decoupled from the nose, and the outer loop wound up
                // into the reported compounding oscillation. Scale the command that DRIVES AoA further
                // toward the game's per-airframe limiter (minus a fixed margin) — nose-up (tgtP < 0)
                // against the +ceiling, nose-down against the mirrored -ceiling — so recovery in the
                // opposite direction is never blocked. Active regardless of the assist state: assist-OFF
                // gets the AoA protection the game withholds.
                float tgtPRaw = tgtP; // v0.63: the law's pitch BEFORE the AoA block, for the recorder
                tgtP *= tgtP < 0f ? aoaGateUp : aoaGateDn;
                // v0.59 AoA RECOVERY BIAS — applied AFTER the gates (it must survive them: it's the term
                // that flies the nose back INSIDE the envelope) and BEFORE the canard inversion (it's a
                // desired-effective-pitch term like the rest of tgtP). Nose-down = POSITIVE ci.pitch, so
                // + excess past the +ceiling adds a positive (down) command; mirrored on the negative
                // side. Capped well below full deflection — it recovers, it doesn't bunt. The integrator
                // is already frozen whenever this is nonzero (the gates are < 1 there).
                // ponytail: fixed gain/cap, promote to Cfg if flight tuning demands it.
                // v0.64: scale the recovery bias by measured pitch authority. Past the ceiling on a
                // REVERSED plant this term was actively harmful, not merely useless — the v62 traces show
                // it commanding full nose-down at AoA 12 deg (rising, so predicted past the ceiling) which,
                // against the 0.31-0.75 s plant lag measured there, landed in phase with the next
                // downswing and sustained the cycle. If the plant is not following, adding command is
                // pumping. effFloor-free on purpose: unlike the law's P-term this SHOULD go to zero when
                // authority is zero, because there is nothing to recover with. Fixed-wing only, same
                // reason as the law scaling (the estimator is not live for rotorcraft).
                if (!_collective) aoaRecover *= _pitchEff;
                if (aoaRecover != 0f)
                    // v0.61: continuous (tanh) recovery bias — was a hard clamp(aoaRecover*0.25, ±0.5) that
                    // saturated to a FIXED ±0.5 step past the ceiling (a relay element on a low-q plant).
                    // tanh keeps the same initial slope (≈aoaRecover*0.25 for small excess, so in-envelope and
                    // light excursions are unchanged) and the same ±0.5 asymptote (no added authority), but
                    // rolls off smoothly instead of clipping — removes the discontinuity a relay rides.
                    tgtP = Mathf.Clamp(tgtP + 0.5f * (float)System.Math.Tanh(aoaRecover * 0.5f), -1f, 1f);

                // v0.57 CANARD LINEARIZATION — invert the Ifrit's relaxed-stability pitch remap so the
                // FBW receives the pitch the law (and the AoA gate above) intended. tgtP here is the
                // DESIRED EFFECTIVE pitch; what we hand the game is the stick that produces it. Applies
                // only while the game's own remap is live (component present, engine on, V > 30 m/s);
                // identity on every other airframe. Note this also carries the steady trim offset the
                // fine integrator used to grind against (cancelling `a` takes ~-0.22 stick at cruise AoA
                // — more than the whole iCap): the integrator now only mops up genuine residual.
                if (CanardActive(vMag))
                    tgtP = InvertCanardRemap(tgtP, aoaNow / _rsCanardRange);

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
                        float axP = pl.GetAxis("Pitch"), axR = pl.GetAxis("Roll"), axY = pl.GetAxis("Yaw");
                        float handoff = Cfg.ManualHandoffTime.Value;
                        bool anyInput = ManualStickInput(); // same axes/deadzone, one definition (see above)

                        if (handoff > 0f)
                        {
                            // GLOBAL HARD HANDOFF (v0.49) — "my controls / your controls". The complaint
                            // with the per-axis blend was that the instructor still carried some control
                            // (the untouched axes kept chasing, and even touched axes only blended). Here a
                            // single touch on ANY axis switches the WHOLE instructor OFF: you fly the plane
                            // directly on all three axes. We hold it off after your LAST input (_manualHold),
                            // so a quick stick-stir doesn't let the chase grab back between nudges. When the
                            // timer expires the chase eases in (_gEng 1->0 over ManualReturnTime) from your
                            // released (≈neutral) stick.
                            //
                            // TWO REGIMES (v0.50, the Frozander/MrBallsOfSteel report): while FREE-LOOKING
                            // (RMB / Free Look held — the marker is frozen, you can't aim) manual input is
                            // steering, so it drags the marker onto the nose (ManualReorients) and holds the
                            // instructor off for ManualHandoffTime. While AIMING (marker live under the
                            // mouse) manual input is a temporary correction: the marker STAYS on your target
                            // and the instructor resumes toward it ManualHandoffAimTime (default 0) after
                            // release — WT-style roll corrections / elevator pulls that never lose the aim.
                            bool frozen = AimRig.AimFrozen();
                            if (anyInput)
                            {
                                _manualHold = frozen ? handoff : Cfg.ManualHandoffAimTime.Value;
                                _gEng = 1f;
                                if (frozen && Cfg.ManualReorients.Value)
                                    AimRig.SetAimForward(t.forward);      // resume on the NEW heading
                                if (FlyLevelActive) ToggleFlyLevel(aircraft); // a nudge drops Fly Level
                            }
                            else _manualHold = Mathf.Max(0f, _manualHold - dt);

                            if (anyInput || _manualHold > 0f)
                            {
                                // Manual phase: instructor fully off, you own every axis (untouched axes
                                // sit at your neutral stick, so a hands-off pause just coasts the plane).
                                _gEng = 1f;
                                pOut = Mathf.Clamp(axP, -1f, 1f);
                                rOut = Mathf.Clamp(axR, -1f, 1f);
                                yOut = Mathf.Clamp(axY, -1f, 1f);
                            }
                            else
                            {
                                // Re-engaging: ease the chase back in from the (released ≈ neutral) stick.
                                _gEng = Mathf.MoveTowards(_gEng, 0f, dt / Mathf.Max(0.01f, ret));
                                pOut = Mathf.Lerp(_outP, Mathf.Clamp(axP, -1f, 1f), _gEng);
                                rOut = Mathf.Lerp(_outR, Mathf.Clamp(axR, -1f, 1f), _gEng);
                                yOut = Mathf.Lerp(_outY, Mathf.Clamp(axY, -1f, 1f), _gEng);
                            }
                            // Mirror the global blend onto the per-axis _eng* so the integrator suspend,
                            // yaw-weakness gate, HUD man=() readout and recorder all see "manual active".
                            _engP = _engR = _engY = _gEng;
                        }
                        else
                        {
                            // LEGACY PER-AXIS BLEND (v0.05..v0.48). Touch an axis and you instantly own
                            // just that axis; the mouse keeps flying the others. Release and it eases back.
                            pOut = BlendManual(axP, _outP, ref _engP, ref _mApplyP, dz, ret, dt);
                            rOut = BlendManual(axR, _outR, ref _engR, ref _mApplyR, dz, ret, dt);
                            yOut = BlendManual(axY, _outY, ref _engY, ref _mApplyY, dz, ret, dt);
                            // A stick/pedal nudge drops Fly Level — you've taken the controls back.
                            if (FlyLevelActive && (_engP >= 1f || _engR >= 1f || _engY >= 1f))
                                ToggleFlyLevel(aircraft);
                            // MANUAL RE-SEEDS THE AIM DIRECTION (v0.48; v0.50: only while free-looking —
                            // when you're aiming, a manual correction must not move your mouse target).
                            if (Cfg.ManualReorients.Value && anyInput && AimRig.AimFrozen())
                                AimRig.SetAimForward(t.forward);
                        }
                    }
                }
                else { _engP = _engR = _engY = 0f; _gEng = 0f; _manualHold = 0f; }

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
                    DetectAnomalies(aircraft, off, bank, targetBank, bigTurn, yawRate, rollRate, aoaNow, azErr, dt);
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
                    // v0.55: the game FBW's OWN target/actual pitch rate (rad/s, game frame: + = nose
                    // DOWN — recorded raw, the analyzer knows). Closes the "did the plane even try to
                    // follow?" question in every future report; 0 when the FBW is unreadable.
                    float fbwTgtPR = 0f, fbwPR = 0f;
                    if (fbwResolved && _fbwFbw != null)
                        try { fbwTgtPR = _fbwFbw.GetTargetPitchAngVel(); fbwPR = _fbwFbw.GetPitchAngVel(); }
                        catch { /* leave 0 */ }
                    ManeuverRecorder.Sample(off, azErr, elevErr, phi, bigTurn, bank, targetBank,
                        _outP, _outR, _outY, pitchRate, yawRate, rollRate, _yawEffFilt, _yawWeak,
                        spdR, aoaNow, aircraft.gForce, LastPhase, flyLevel, _engP, _engR, _engY, _heliBlend, _vFwd,
                        _rollRateFilt, _iPitch, _iYaw, bankTR, bankBlend, _headingRateFilt, azErrPred, _tBankFlown,
                        aircraft.flightAssist, fbwTgtPR, fbwPR,
                        tgtPRaw, aoaGateUp, aoaGateDn, aoaRecover, qSched, _pitchEff, _settleOn, _aimAzRateFilt,
                        iGate, leadDeg,
                        aircraft); // M0: the recorder reads alt/airDensity/pos/vel off it (fail-soft)
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
                            $"[chase] t={Time.time:0.000} off={off:0.000}deg phi={phi:0.0} bigTurn={bigTurn:0.00} elevE={elevE:0.00} azE={azErr:0.00} azPred={azErrPred:0.00} noseTurn={noseTurnDeg:0.000} " +
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

        // ---- CONTROL LAW: EVOLVED LEGACY (Phase 2, v0.40) ----------------------------------------
        // NOTE: body-rate Cascade (originally planned for this slot) is deferred.
        // This slot holds the EvolvedLegacy law. It began as a copy of the (now-removed, v0.60) Legacy
        // law's body with two targeted changes:
        //   2a — Universal speed-aware bank: use atan(omega*V/g) for the bank target at ALL speeds/
        //        regimes, not just when yaw-weakness is high. Decouples the high-speed bank sizing
        //        from the weakness estimator (_yawWeak gating), making the loaded-turn bank the
        //        default behaviour rather than the assist-path behaviour.
        //   2b — Final-leg align-hold: keep the roll-to-align contribution engaged through the final
        //        degrees until |azErr| is also small, so the law doesn't level early and park short.
        // Everything else (pitch, yaw, fine integrator winding, roll-rate low-pass) is byte-for-byte
        // Legacy — the same convergence properties, same anti-PIO, same no-bunt gate.
        private void ApplyEvolvedLegacy(
            Transform t, Vector3 local, float off, float vMag, float sens, float fineGain, float alignFrac, float bigTurn,
            float targetBank, float azErr, float azErrPred, float phi, float lateral, float pitchRate, float yawRate, float rollRate,
            float pitchDamp, float damp, float assist, float dt, float omegaMax, float qSched,
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
            // v0.51: PREDICTED error (azErr - headingRate*TurnLeadTime, computed in Apply), not raw —
            // the anticipatory lead that kills the death-wobble limit cycle. This local copy and the
            // shared bankTR block in Apply MUST stay in lockstep on which error they see.
            // v0.67 SETTLE-EXIT RAMP — LOCKSTEP with Apply's shared azTR site (see the full rationale
            // there): ramp the turn-rate demand in proportionally over [0.5, 0.5+azRampW] instead of the
            // hard 0.5° step, so the settle→bankTR hand-off is no longer a gain cliff that re-arms the
            // high-q relay (rec30). Still exactly 0 below 0.5° (B2 owns it); azErrPred still predFloor-floored.
            const float azRampW = 1.5f; // deg; regime constant. MUST match the shared site.
            float azTR = azErrPred * Mathf.Clamp01((Mathf.Abs(azErr) - 0.5f) / azRampW);
            float omega  = azTR * Mathf.Deg2Rad * Cfg.AssistTurnRateGain.Value;                              // rad/s, signed
            // MARKER-RATE FEED-FORWARD (v0.78) — LOCKSTEP with Apply's shared omegaDes site; the full measured
            // rationale lives there. Same term, same unit gain, and applied at the same point: BEFORE the
            // achievability cap below, so omegaMax bounds it too. A sustained marker sweep is now paid for by
            // the feed-forward instead of by a standing azimuth lag (measured 9.54 deg at 12.07 deg/s).
            if (Cfg.MarkerRateFeedForward.Value) omega += _aimAzRateFilt * Mathf.Deg2Rad;
            // v0.55 ACHIEVABILITY CAP — the SAME clamp Apply's shared bankTR block applies to omegaDes
            // (see the comment there); the two sites MUST stay in lockstep, same rule as the azErrPred
            // note above. At low speed this shrinks the bank target physically instead of letting it slam
            // +/-MaxBankAngle into a turn the collapsed pitch rate can't fly (the low-speed oscillation).
            omega = Mathf.Clamp(omega, -omegaMax, omegaMax);
            float Vb     = Mathf.Max(Cfg.BankSpeedFloor.Value, vMag);                                        // airspeed floor (shared with Apply's bankTR — v0.60)
            float bankTRdeg = Mathf.Atan(omega * Vb / gAcc) * Mathf.Rad2Deg;                                // speed-correct bank, signed deg
            float tBankE = Mathf.Clamp(bankTRdeg, -Cfg.MaxBankAngle.Value, Cfg.MaxBankAngle.Value);         // universal bank target
            // B2 FINE-SETTLE (v0.65). bankTR is zero here whenever |azErr| <= 0.5 (the omega presence gate),
            // so the last half-degree of azimuth gets no turn command and the law parks at the gate — rec13
            // ~0.5° at 220 m/s, rec20 1.48° at 271 m/s, where a coordinated turn (not yaw) is the only thing
            // that moves the flight path (yaw barely turns it at high q). Inject a SMALL, V-INDEPENDENT,
            // heavily-damped bank so that residual closes on a gentle turn. V-INDEPENDENT on purpose: the
            // V-scaled bankTR slope is 22-44°/° at speed (the rocking liability this must not re-arm), so the
            // sub-gate path must NOT inherit V. Convergent (kSettle*azErr -> 0 as azErr -> 0; no integrator)
            // and gated to a quasi-stationary marker (_settleOK) so it never fights the user's own sub-degree
            // aiming. The existing roll servo (with its -rollRateF*RollDamping term) flies it — no new loop.
            // Fixed-wing only; the hover fade below still zeroes it on a collective airframe. See §"Why B2-A
            // cannot limit-cycle" in the v0.65 plan for the full stability argument.
            _settleOn = false;
            const float settleGate = 0.5f; // = the bankTR presence gate (hand-off boundary)
            const float kSettle    = 8f;   // deg bank per deg azErr, V-INDEPENDENT; <=3x below the 22 deg/deg
                                           // that already rocked at 220 m/s, so loop gain stays inside margin
            const float settleCap  = 4f;   // deg — a transient can never command more than this (no large-signal relay)
            if (!_collective && _settleOK && Mathf.Abs(azErr) < settleGate && Mathf.Abs(tBankE) < 1e-3f)
            {
                float settleConf = new Vector2(t.forward.x, t.forward.z).magnitude; // = cos(pitch); same deprojection as bankTR (hdgConf)
                tBankE = Mathf.Clamp(kSettle * azErr, -settleCap, settleCap) * settleConf;
                _settleOn = true;
            }
            // HOVER REGIME (v0.43): fade the commanded bank to zero as _heliBlend->1. This neutralises the
            // eFine leveler base (eFine = t.right.y + sin(0) = t.right.y) and self-cancels the coordinating
            // pull (coordPull ∝ sin(tBankE) -> 0). NOTE: it does NOT by itself stop roll — the eAlign
            // roll-to-marker branch below is gated separately by _heliBlend (v0.46 fix); both gates are
            // needed for a true wings-level hover. Fixed-wing => _heliBlend==0 => tBankE unchanged (v0.42).
            tBankE *= (1f - _heliBlend);
            // SLEW LIMIT (v0.54). At 500 m/s the atan slope is ~44 deg of bank per degree of error, so
            // prediction ripple made this target BANG 0<->48-65 deg at ~1.5 Hz — and the roll servo below
            // faithfully chased it (outR tracks bankTR-bank at corr 0.79-0.96 in the v0.53 chatter files;
            // wings rocked ±14-30 deg from a 1-3 deg error). A target that can only move BankSlewRate
            // deg/s can't flap above the airframe's own roll response, and a slower-moving target also
            // shrinks the 15-20 deg servo overshoot that sustained the slow 0.5 Hz big-turn cycle.
            // Applied BEFORE coordPull so the pull sizes off the bank actually being commanded.
            float slewR = Cfg.BankSlewRate.Value;
            if (slewR > 0f) tBankE = Mathf.MoveTowards(_tBankSlewed, tBankE, slewR * dt);
            _tBankSlewed = tBankE;
            _tBankFlown  = tBankE; // recorder: the target this law's roll servo really flies

            // COORDINATING PULL — same structure as Legacy but sizes off tBankE (so the pull matches the
            // bank actually commanded, not the passed-in targetBank from the weakness-gated pre-compute).
            float pullTaper = Mathf.Clamp01(Mathf.Abs(azErr) / Mathf.Max(0.5f, Cfg.CoordPullReleaseAngle.Value));
            coordPull = Mathf.Clamp(
                Cfg.CoordPullGain.Value * Mathf.Abs(Mathf.Sin(tBankE * Mathf.Deg2Rad))
                * pullTaper * assist,
                0f, Cfg.CoordPullCap.Value);
            // v0.56 LOW-Q GAIN SCHEDULE — the takeoff-oscillation fix. Phase forensics on the v55
            // recordings (225034: period 1.77 s at 113 m/s; the mod's P-response to elevErr is instant,
            // +0.13 s, but the ACHIEVED pitch rate lags the command by >1 s — the low-q plant supplies
            // ~all the loop phase; iPitch was ±0.003, integrators irrelevant): the outer P gain, tuned
            // against the high-q plant, is too hot below corner speed, and the saturating loop ratchets
            // (bank 18->85 deg, g 0.7->7.5 over ~55 s of uninterrupted tracking — the takeoff departure).
            // Scale the DEMAND terms (error P + coordPull) by the game's own q clamp so the loop asks at
            // the same fraction of achievable at every speed; the rate-damping term and _iPitch stay
            // UNSCALED, so damping is relatively ~3x stronger exactly where the plant is slow. qSched==1
            // at/above corner speed (high-speed feel untouched); a big error still rails the stick, so
            // max-performance pulls keep full authority — only near-target hotness is removed. Applies in
            // BOTH assist regimes: one mechanism, no assist special case.
            // v0.58 HELO RATE NORMALIZATION — the rotorcraft wobble fix (v57 recs 123204/125639/224117).
            // The helo inner loop is a rate-command PID (decompiled HeloControlsFilter.HeloFlyByWire):
            // stick * maxAngularVel = commanded body rate, tracked with ~0.3 s effective lag (fitted from
            // the v57 CSVs, corr 0.88-0.97). The fixed-wing error->stick gains here (sens*fineGain, ~0.37
            // stick/deg with the old boost) made the OUTER loop gain 10-15 s^-1 around that lag — a
            // guaranteed ~1 Hz limit cycle (measured 0.88-1.16 Hz on both reported wobbles). Instead,
            // command a RATE and normalize by the airframe's probed authority:
            //   omegaDes = kHelo * errRad;  stick = omegaDes / omegaMaxAxis
            // pitch omegaMax = min(maxAngularVel.x, gLimit*9.81/max(V,10)) — the game's own clamp;
            // yaw omegaMax = maxAngularVel.y. kHelo = 2.0 s^-1: an integrator plant with ~0.3 s lag goes
            // unstable near pi/(2*0.3) ~ 5 s^-1; 2.0 leaves ~55 deg phase margin (crisp but calm) and is
            // plant-independent because the probe supplies each airframe's true authority (UH-90 back-
            // solves to gLimit~8, the modded RAH-72 to ~0.5 rad/s yaw — class defaults are NOT the truth).
            // HeliYawScale is deliberately NOT applied in this branch: normalization already delivers the
            // hover yaw authority that knob existed to patch (it still works when the probe is unresolved).
            // ponytail: kHelo fixed; promote to Cfg only if flight tuning demands it.
            float pErrTerm = -local.y * sens * fineGain * pullGate; // pre-0.58 direct P term (fixed-wing, or any heli the probe couldn't read)
            const float kHelo = 2.0f;                               // s^-1 desired-rate gain (see above)
            float wMaxP = 0f, wMaxY = 0f;
            if (_collective && _heloOk)
            {
                wMaxP = Mathf.Max(0.05f, Mathf.Min(_heloMaxAngVel.x, _heloGLimit * 9.81f / Mathf.Max(vMag, 10f)));
                wMaxY = Mathf.Max(0.05f, _heloMaxAngVel.y);
                pErrTerm = -Mathf.Clamp(kHelo * Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) / wMaxP, -1f, 1f) * pullGate;
            }
            // PLANT-AUTHORITY SCALING (v0.64; reversal-gated floor v0.65 C1) — the fix for the FS-12
            // 0.5 Hz departure limit cycle. _pitchEff is the measured achieved/commanded pitch ratio
            // (SIGNED: a reversed plant reads ~0). Scale the P demand by it so a mushing/reversed airframe
            // gets proportionally less command instead of pumping the cycle. v0.65: the 0.3 floor exists
            // for a healthy-but-noisy plant, but on a REVERSED plant (rec04/rec05: pEff median 0.04, 71%
            // of frames anti-phase) it clamped demand UP to 0.3 and kept feeding 30% pitch into a plant
            // moving the OPPOSITE way — the forcing that sustained the 14 s relay. Gate the floor behind
            // revThresh: signed _pitchEff sits near 0 ONLY on a reversed/lost plant; genuine low-q mush
            // reads a small POSITIVE ratio (~0.15-0.3). Below revThresh, DON'T floor — let demand collapse
            // to the measured (near-0) value so the law stops forcing a reversed plant. Healthy
            // (>=effFloor) and mush ([revThresh,effFloor)) cases are byte-identical to v0.64. Fixed-wing
            // only: _pitchEff is measured under a !_collective guard (see the estimator), so on rotorcraft
            // it is a stale hold, not a live reading — scaling by it there would apply a meaningless number.
            // Rotorcraft keep pre-v0.64 behaviour exactly.
            const float effFloor = 0.3f; // regime constant (revThresh is the shared PEffRevThresh field)
            if (!_collective) pErrTerm *= _pitchEff >= PEffRevThresh ? Mathf.Max(effFloor, _pitchEff) : _pitchEff;
            tgtP = Mathf.Clamp(((pErrTerm - coordPull) * qSched + _iPitch + pitchRate * pitchDamp) * Cfg.PitchGain.Value, -1f, 1f);

            // YAW — Legacy, plus the hover-regime authority boost (v0.43). With the wings held level by the
            // bank suppression above, local.x is ~the horizontal pointing error, so the existing yaw term
            // already points the nose at the marker; in hover it's the ONLY thing turning the aircraft, so
            // blend the scale up toward HeliYawScale as _heliBlend->1 to give the tail rotor the authority.
            // (When the v0.58 helo probe is resolved the direct term is replaced by the normalized rate
            // command above — see the HELO RATE NORMALIZATION comment.)
            yawScale = Mathf.Lerp(1f, Cfg.TurnYawScale.Value, bigTurn);
            yawScale = Mathf.Lerp(yawScale, Cfg.HeliYawScale.Value, _heliBlend);
            float yawWeakFade = 1f - Cfg.YawWeakFade.Value * assist;
            float yErrTerm = (_collective && _heloOk)
                ? Mathf.Clamp(kHelo * Mathf.Asin(Mathf.Clamp(local.x, -1f, 1f)) / wMaxY, -1f, 1f)
                : local.x * sens * fineGain * yawScale * yawWeakFade;
            tgtY = Mathf.Clamp((yErrTerm + _iYaw - yawRate * damp) * Cfg.YawGain.Value, -1f, 1f);

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
            // v0.61 — gate the anti-relay slew to the dead-astern WRAP region it was built for, and never let
            // near-boresight junk phi seed it (S1 counter-roll fix). Below the wrap gate phi is continuous
            // frame-to-frame, so the slew there only adds lag that ramps a reversing setpoint THROUGH zero,
            // holding the old sign for the crossing (~0.3 s at 3/s) = wrong-way roll at maneuver onset; pass
            // through instead. In the wrap region (|phi|>phiWrapGate) phi can flip ±180 in one tick — keep the
            // 3/s slew there (the v0.57 dead-astern relay fix, the ONE place the slew is genuinely needed).
            // The lateral gate zeroes eAlign where phi is ill-conditioned (both local.x/local.y ~0), so a
            // near-boresight HOLD frame can't seed the next real maneuver with saturated junk.
            // ponytail: phiWrapGate is the geometric atan2 wrap boundary and eAlignLatGate the atan2
            // conditioning floor (~sin FineAngle) — regime constants, not per-plane; promote to Cfg only if
            // flight tuning demands.
            const float phiWrapGate = 135f;    // deg; |phi|>135 is the only place phi is discontinuous (±180 wrap)
            const float eAlignLatGate = 0.10f; // ~sin(FineAngle 6°); below this |lateral| phi is meaningless
            float eAlignTgt = (lateral > eAlignLatGate) ? Mathf.Clamp(phi / 90f, -1.5f, 1.5f) : 0f;
            // Two-rate slew (single MoveTowards, so it's continuous at the gate — no exit snap if phi sweeps
            // back below 135° before the slow slew converged): 3/s inside the wrap region (the anti-relay
            // clamp — the ONE place phi is discontinuous), 30/s elsewhere (fast catch-up ≈ pass-through;
            // closes any ≤3.0 residual in ≤0.1 s, so the S1 wrong-way window stays ≲2 frames). Both rates are
            // regime constants (geometry-driven), not per-plane.
            float eAlignRate = (Mathf.Abs(phi) > phiWrapGate) ? 3f : 30f;
            _eAlignSlew = Mathf.MoveTowards(_eAlignSlew, eAlignTgt, eAlignRate * dt);
            float eAlign = _eAlignSlew;
            // 2b: lateral-error hold weight — stays > 0 while |azErr| > EvolvedAlignHoldDeg.
            //   v0.53: subtract FineBankDeadzone first (same guard the linear bank servo has at azBank).
            //   Near boresight phi snaps ±90° with the SIGN of a sub-degree azErr — eAlign is a full-scale
            //   directional relay there — so an unguarded |azErr|/alignHold weight injected ±0.2 roll stick
            //   per degree of raw error: a second proportional az→roll loop with no lead/deadzone that
            //   bypassed the v0.52-clamped bank pipeline. At 570+ m/s roll authority that self-sustained a
            //   ±30° wing-rock at ~1.2 Hz while targetBank sat at 0 (v52 KR67 recordings, HOLD phase).
            //   Inside the fine cone roll is now eFine only (wings-level + the braked/clamped tBankE).
            float azAl = Mathf.Max(0f, Mathf.Abs(azErr) - Cfg.FineBankDeadzone.Value);
            float lateralHold = Mathf.Clamp01(azAl / Mathf.Max(0.01f, Cfg.EvolvedAlignHoldDeg.Value));
            float blendWeight = Mathf.Max(bigTurn, lateralHold) * (1f - _heliBlend); // 2b + hover gate: kill roll-to-align in hover
            // v0.67 DOWN-HEMISPHERE PUSHOVER (rec24) — a target BELOW the nose gives phi≈±180, so eAlignTgt
            // saturates to ±1.5 and the half-committed roll-to-align finds a FALSE ~85° bank equilibrium in
            // the moderate-off band (rollErr=0 ⇒ sin(bank)=eAlignTgt·w/(1-w), which rails once w≳0.4); at 85°
            // the target is abeam, pitch (-local.y)→0, and yaw slowly wanders the nose down — the reported
            // "rolls to 90° then yaws the nose down". The UP hemisphere has no such trap (phi≈0 ⇒ eAlignTgt≈0,
            // pitch closes it directly). Restore the symmetry: SUPPRESS roll-to-align for a below-target in
            // the moderate band and let a BOUNDED pushover close it — pullGate already leaves ~0.4-0.55
            // nose-down authority here (maintainer-blessed bounded negative-g, and the aoaGateDn mirror caps
            // it), and with the wings held level local.y stays negative so that pushover actually points at
            // the target. Keyed to LIVE geometry only: Clamp01(-alignFrac)=belowness; (1-lateralHold) limits
            // it to the azErr≈0 hang (a genuine down-LATERAL with large azErr keeps its roll-and-pull); the
            // bigTurn taper hands full roll-and-pull back for large below-reorientations. No per-plane constant.
            const float downAlignTaper = 0.3f; // top fraction of the bigTurn ramp over which roll-to-align returns
            float belowSuppress = Mathf.Clamp01(-alignFrac) * (1f - lateralHold)
                                * Mathf.Clamp01((1f - bigTurn) / downAlignTaper);
            blendWeight *= (1f - belowSuppress);
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
        private void DetectAnomalies(Aircraft ac, float off, float bank, float targetBank, float bigTurn, float yawRate, float rollRate, float aoaNow, float azErr, float dt)
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
                _huntWinStart = _yawWagWinStart = _wobbleWinStart = _azlcWinStart = now;
                _flipsP = _flipsY = _yawWagFlips = _wobbleFlips = _azlcFlips = _azlcFlipsPrev = 0;
                _prevSignP = _prevSignY = _prevSignYW = _prevSignRW = 0f;
                _azlcPeak = _azlcPeakPrev = 0f; _azlcPrevAz = azErr;
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

            // AZ-LIMIT-CYCLE (v0.58) — a growing azimuth/roll limit cycle while basically on target:
            // azErr swings sign repeatedly with a RISING envelope and the roll stick actively chasing.
            // The v57 023018 signature (0.46 Hz, azErr pp 7→15 over 16 s, outR eventually railed) logged
            // ZERO lines — every detector above watches fast flapping (≥2 Hz windows) or big errors.
            // Two 3 s half-windows: both need ≥2 azErr sign flips (≥~0.33 Hz sustained — the clean v57
            // files flip ≤0.16/s, the cycle 0.84/s), the peak envelope must grow ≥25%, and outR must
            // span ≥0.5 so a quiet stick (e.g. phantom azErr swings near the vertical, harmless after
            // the v0.58 deprojection) never fires. Fine-cone only: honest big-turn reversals carry
            // off > FineAngle, so bigTurn > 0 resets the window; the ±90° flip bound skips ±180 wraps.
            if (bigTurn > 0.05f || Mathf.Abs(azErr) > 45f)
            {
                _azlcWinStart = now; _azlcFlips = _azlcFlipsPrev = 0;
                _azlcPeak = _azlcPeakPrev = 0f; _azlcOutRMin = _azlcOutRMax = _outR;
            }
            else
            {
                if (_azlcWinStart <= 0f) _azlcWinStart = now;
                if (azErr * _azlcPrevAz < 0f && Mathf.Abs(azErr - _azlcPrevAz) < 90f) _azlcFlips++;
                _azlcPeak = Mathf.Max(_azlcPeak, Mathf.Abs(azErr));
                _azlcOutRMin = Mathf.Min(_azlcOutRMin, _outR); _azlcOutRMax = Mathf.Max(_azlcOutRMax, _outR);
                if (now - _azlcWinStart >= 3f)
                {
                    if (_azlcFlipsPrev >= 2 && _azlcFlips >= 2 && _azlcPeak >= 3f
                        && _azlcPeakPrev >= 2f            // prev half already oscillating for real — sub-degree
                                                          // sign noise + one honest correction must not fire
                        && _azlcPeak >= 1.25f * _azlcPeakPrev
                        && _azlcOutRMax - _azlcOutRMin >= 0.5f)
                        Anomaly("az-limit-cycle",
                            $"pk {_azlcPeakPrev:0.0}->{_azlcPeak:0.0}deg flips={_azlcFlips}/3s outRpp={_azlcOutRMax - _azlcOutRMin:0.00}",
                            ref _anAzlcT, now, ac, off, bank);
                    _azlcFlipsPrev = _azlcFlips; _azlcPeakPrev = _azlcPeak;   // rotate half-windows
                    _azlcFlips = 0; _azlcPeak = 0f; _azlcOutRMin = _azlcOutRMax = _outR; _azlcWinStart = now;
                }
            }
            _azlcPrevAz = azErr;

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

            // OVERSTRESS (v0.57) — the mod flew the airframe past its own FBW limits for a sustained
            // stretch. The v56 assist-off recordings hit 9.7 g on a 6 g frame and 158% of the alpha
            // limiter with ZERO anomaly lines — every detector above watches stick patterns, none watch
            // the airframe. Uses the FBW probe's per-airframe numbers; silent when the probe isn't ok.
            // v0.58 ALPHA VALIDITY GATE — the v57 session logged 41 overstress lines, ALL false, all from
            // one unloaded departure/stall episode (g ≤ 2.1 throughout, aoa −176..−52 at 21 m/s, aoa 40–80
            // at 110–169 m/s). AoA past the limiter only means structural stress under LOAD: unloaded it's
            // stall/departure geometry (and below ~100 m/s / beyond ±90° the number is trajectory, not
            // wing loading). Genuine events (the v56 9.7 g @ 158% alpha) pass all three gates easily.
            // The g branch stays ungated — g IS the load. ponytail: fixed 2.5 g floor (the false cluster
            // peaked at 2.1 g); scale off _fbwGLimit if a low-limit airframe ever under-reports.
            if (_fbwEnabled && _fbwGLimit > 0.1f
                && (ac.gForce > 1.3f * _fbwGLimit
                    || (spdNow > 100f && ac.gForce > 2.5f && Mathf.Abs(aoaNow) <= 90f
                        && Mathf.Abs(aoaNow) > _fbwAlphaLimit)))
                _stressTimer += dt;
            else _stressTimer = 0f;
            if (_stressTimer > 0.5f)
            {
                Anomaly("overstress", $"g={ac.gForce:0.0}/{_fbwGLimit:0} aoa={aoaNow:0.0}/{_fbwAlphaLimit:0} for {_stressTimer:0.0}s", ref _anStressT, now, ac, off, bank);
                _stressTimer = 0f; // one line per sustained excursion (plus the per-type cooldown)
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
        private void Anomaly(string type, string detail, ref float lastStamp, float now, Aircraft ac, float off, float bank)
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
                $"law=EvolvedLegacy{(rec.Length > 0 ? $" rec={rec}" : "")}";
            WTMouseAimPlugin.Log.LogWarning(line);
            AnomalyLog.Write(line);
            if (Cfg.AnomalyContext.Value) DumpTrail(now);
        }

        // Dump the last ~20 ring-buffer frames as ONE compact [anomaly:trail] line so the lead-up to the
        // event is visible (how the wag built, how the bank blew past its target) without per-frame spam.
        // Throttled to once per second across all anomaly types so a multi-type frame doesn't repeat it.
        // Per frame: t | off / bank>tgtBank | P,R,Y outputs | yaw rate | spd. Oldest → newest, left → right.
        private void DumpTrail(float now)
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
        private void TrackManeuver(Aircraft ac, float off, float phi, float bank, float rollRate, float dt)
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
                string arch = "";
                try
                {
                    var cfilt = aircraft.GetControlsFilter();
                    if (cfilt != null) hasHover = cfilt.HasAutoHover();
                    // v0.58 archetype fingerprint — the "which rotorcraft is this, really" line the heli
                    // investigation had to fly blind without. Once per aircraft change, so the
                    // GetComponentInChildren sweeps are free.
                    arch = $" heloFilter={(cfilt is HeloControlsFilter ? 1 : 0)}" +
                           $" tiltwing={(aircraft.GetComponentInChildren<TiltWingController>() != null ? 1 : 0)}" +
                           $" swivelduct={(aircraft.GetComponentInChildren<SwivelDuctSystem>() != null ? 1 : 0)}" +
                           $" compound={(aircraft.GetComponentInChildren<CompoundHeloController>() != null ? 1 : 0)}";
                }
                catch { /* ignore — archetype info is best-effort */ }
                WTMouseAimPlugin.Log.LogInfo(
                    $"[seam] now flying '{name}' — fixedWing={fixedWing} collective={!fixedWing} hasAutoHover={hasHover}{arch} " +
                    $"(takeoffDistance={aircraft.GetAircraftParameters().takeoffDistance:0.##}); hover regime ramps {Cfg.HeliHoverSpeed.Value:0}..{Cfg.HeliForwardSpeed.Value:0} m/s fwd (collective aircraft only).");
            }

            // TEST-CARD DEMAND (M1). Runs HERE, in the prefix, so the scripted aim direction for this
            // fixed step is written BEFORE the postfix below calls Apply(), which reads
            // AimRig.AimForward — same PlayerAxisControls invocation, so zero-tick lag by
            // construction rather than by Harmony patch ordering or Unity's Update/FixedUpdate order
            // (plan §5.1 M-1). No-op (two field reads) when no card is running.
            ScenarioPlayer.Tick(aircraft);

            bool active = ChaseController.For(aircraft).BeginFrame(aircraft, fixedWing, pilotStrength);

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
            {
                ChaseController.For(aircraft).Apply(aircraft);
                // Throttle/brake ownership for a running card, on the FIXED step: this is the write
                // that is guaranteed to be in place when Aircraft.FilterInputs consumes the inputs
                // immediately after this postfix. PilotThrottlePatch below owns the Update-time half.
                // No-op when no card is running.
                ScenarioPlayer.OwnInputs(aircraft);
            }
        }

        internal static bool TryResolve(PilotPlayerState inst, out Aircraft aircraft, out bool fixedWing, out float pilotStrength)
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

    // v0.74 — THROTTLE OWNERSHIP AT THE UPDATE SEAM.
    //
    // PlayerThrottleAxis1Controls runs in Update (NOT FixedUpdate) and is the only place the player's
    // hardware throttle axis reaches ControlInputs.throttle. Owning throttle from the fixed-step
    // postfix alone was not enough, for a reason that only shows up in flight: Airbrake.Update reads
    // ControlInputs.throttle every RENDERED frame and opens the boards whenever it is exactly zero —
    //     openAmount += (throttle == 0f ? +openSpeed : -openSpeed) * Time.deltaTime;
    // With more rendered frames than fixed steps, every frame that native wrote the pilot's idle lever
    // before our next fixed-step write cracked the airbrakes open and bled energy off-script. The
    // pilot's own report: "the UX is showing me I'm modifying the throttle... there is a point at zero
    // percent where the game goes into airbraking mode."
    //
    // Skipping native outright (rather than overwriting after it) means the hardware axis never
    // reaches ControlInputs at all while a card runs, so the throttle HUD shows what the card is
    // actually flying instead of what the lever says. It also freezes customAxis1, which native writes
    // in the same method — deliberate: that axis is another uncontrolled input during a capture.
    // simulatedThrottle keeps its value while skipped and resumes from the lever when the card ends.
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerThrottleAxis1Controls")]
    internal static class PilotThrottlePatch
    {
        // Return false => skip native (the card owns the throttle this frame).
        private static bool Prefix(PilotPlayerState __instance)
        {
            if (!ScenarioPlayer.Playing) return true;                       // idle: two field reads
            if (!PilotPlayerStatePatch.TryResolve(__instance, out var aircraft, out _, out _)) return true;
            return !ScenarioPlayer.OwnInputs(aircraft);
        }
    }
}
