using BepInEx.Configuration;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // Live-tunable config (BepInEx.ConfigurationManager can edit these in-game with F1).
    internal static class Cfg
    {
        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<KeyCode> ToggleKey; // master enable/disable hotkey (default F10)
        public static ConfigEntry<bool>  ShowOverlay;
        public static ConfigEntry<bool>  ShowDebugHud; // show the diagnostic text readouts (off = clean reticle-only HUD)
        public static ConfigEntry<bool>  DebugLogging; // periodic BepInEx-log dump of mouse/aim/chase state (verbose)
        public static ConfigEntry<bool>  AnomalyLogging; // event-only log: fires one line when a command misbehaves
        public static ConfigEntry<bool>  CursorLogging;  // focused [cursor] transition log: our cursor state vs the game's CursorManager cache
        public static ConfigEntry<float> MouseSensitivity; // degrees of aim offset per unit of mouse delta
        public static ConfigEntry<float> MouseSmoothing;   // 0..1 one-pole smoothing on the mouse delta
        public static ConfigEntry<float> MaxAimAngle;      // cone half-angle (deg) the marker is clamped within
        public static ConfigEntry<float> AimDistance;      // metres ahead the aim point is placed (projection only)
        public static ConfigEntry<bool>  InvertPitch;

        // --- Control-law A/B switch (v0.38). Pick which law turns the marker error into stick commands,
        // and a hotkey that cycles it in flight so Legacy vs the rearch laws can be compared back-to-back.
        public static ConfigEntry<ControlLawMode> ControlLawMode;  // active control law (default Legacy)
        public static ConfigEntry<KeyCode>        ControlLawKey;   // cycle the active law in-flight (default F9)

        // --- Chase law (writes flight controls). Per-axis gains may be negative to flip a sign.
        public static ConfigEntry<bool>  WriteControl;        // actually drive the stick (off = overlay only)
        public static ConfigEntry<bool>  ControlRotorcraft;   // fly collective aircraft (helis/hover-VTOLs) too, not just fixed-wing
        public static ConfigEntry<float> PitchYawSensitivity; // base chase gain on the body-frame aim direction
        public static ConfigEntry<float> ChaseDamping;        // derivative damping on the nose's rotation rate
        public static ConfigEntry<float> RollDamping;         // derivative damping on the roll rate (anti bank-wobble)
        public static ConfigEntry<float> RollRateSmoothing;   // sec: low-pass time constant on rollRate feeding the damping term (anti high-speed roll PIO)
        public static ConfigEntry<float> RollGain;            // roll output scale (negative flips roll direction)
        public static ConfigEntry<float> PitchGain;           // pitch output scale (negative flips)
        public static ConfigEntry<float> YawGain;             // yaw/rudder output scale (negative flips)
        public static ConfigEntry<float> OutputSlew;          // max stick units/sec (anti-jerk rate limit)
        public static ConfigEntry<float> MaxBankAngle;        // deg: cap on the bank-angle servo's commanded bank
        public static ConfigEntry<float> RollPitchCoordination;// 0..1 gate pitch pull by lift-vector alignment (roll-then-pull)
        public static ConfigEntry<float> AlignAngle;          // deg above which roll-to-align is full (knee of the bigTurn ramp)
        public static ConfigEntry<float> TurnYawScale;        // 0..1 yaw authority fraction during a big turn
        public static ConfigEntry<float> PitchBrake;          // extra rate damping near the target (anti-overshoot)
        public static ConfigEntry<float> AuthorityRamp;       // engage/disengage blend speed (1/sec)
        public static ConfigEntry<bool>  ManualOverride;      // let the stick/keyboard/pedals override the chase per-axis
        public static ConfigEntry<bool>  ManualReorients;     // manual input re-seeds the aim marker to the nose (release holds the NEW heading, not the old aim)
        public static ConfigEntry<float> ManualHandoffTime;   // >0: any input fully disables the instructor on ALL axes until this many sec after the last input (global hard handoff)
        public static ConfigEntry<float> ManualHandoffAimTime; // hold time used instead of ManualHandoffTime while AIMING (RMB/free-look NOT held): instructor resumes toward the untouched marker this many sec after release
        public static ConfigEntry<float> ManualReturnTime;    // sec for an axis to ease back to mouse-aim after release
        public static ConfigEntry<float> ManualDeadzone;      // how far a manual axis must move before it counts as input
        public static ConfigEntry<bool>  RightClickFreeze;    // hold RMB to freeze the reticle + free-look (both views)

        // --- Fine capture (the last few degrees). Log-confirmed v0.22: inside ~5deg the residual is
        // almost pure azimuth with the wings level — roll blends to wings-level, yaw P-term is ~0.05
        // stick, and the heading error decays with a ~6 s time constant ("never quite aligns").
        public static ConfigEntry<float> FineAngle;           // deg: cone inside which the fine-capture aids ramp in
        public static ConfigEntry<float> FineGainBoost;       // extra proportional gain at zero offset (0 = off)
        public static ConfigEntry<float> FineBankGain;        // deg of target bank per deg of azimuth error (the bank-servo gain)
        public static ConfigEntry<float> FineBankDeadzone;    // deg of azimuth error below which the bank servo commands ZERO bank (anti fine-cone roll wobble)

        // --- Fine integrator (kills the steady-state residual the FBW rate-command leaves; v0.24).
        // The game's FlyByWire reads our pitch/yaw as a commanded ANGULAR RATE and PID-tracks it, so a
        // pure-proportional outer loop asymptotes and parks a fraction of a degree short. A small leaky,
        // clamped integrator on the aim error (fine regime only) winds in the bias needed to land on it.
        public static ConfigEntry<float> FineIntegralGain;    // Ki: integrator wind-in rate (0 = off)
        public static ConfigEntry<float> FineIntegralLeak;    // 1/s: bleed toward zero (anti-windup)
        public static ConfigEntry<float> FineIntegralCap;     // max stick units the integrator may add per axis

        // --- Measured-reactive bank-and-pull assist (v0.35). When the rudder is empirically failing to
        // move the heading (low measured yaw effectiveness — happens at high speed / on draggy airframes),
        // the instructor shifts a side correction into bank-and-pull instead of leaning on the weak rudder.
        public static ConfigEntry<bool>  YawAssistEnabled;    // master on/off for the reactive assist
        public static ConfigEntry<float> YawAssistStrength;   // 0..1 overall scale of the assist
        public static ConfigEntry<float> YawAssistResponse;   // sec: low-pass time constant on the yaw-weakness estimate
        public static ConfigEntry<float> CoordPullGain;       // 0..1 coordinating-pull authority (the "pitch into the bank")
        public static ConfigEntry<float> CoordPullCap;        // 0..1 cap on the coordinating-pull stick
        public static ConfigEntry<float> BankAuthGain;        // bank-gain boost per unit assist (steep banks when yaw weak)
        public static ConfigEntry<float> YawWeakFade;         // 0..1 how far the rudder fades out as the assist rises
        public static ConfigEntry<float> AssistTurnRateGain;  // 1/s: turn-rate-targeted bank (self-scales the loaded bank with airspeed)
        public static ConfigEntry<float> CoordPullReleaseAngle;// deg: error cone inside which the coordinating pull eases off
        public static ConfigEntry<float> TurnLeadTime;        // s: anticipatory lead on the turn-rate bank command (v0.51 death-wobble fix)
        public static ConfigEntry<float> AssistOffPitchScale; // flat pitch scale while the game's flight-assist is OFF (v0.51 AoA-divergence guard)
        public static ConfigEntry<float> BankSlewRate;        // deg/s: rate limit on EvolvedLegacy's bank target (v0.54 anti-relay)

        // --- Bank-to-turn law (Phase 1, v0.39): physics-based unified law knobs that have no Legacy
        // equivalent. The rest of the BankToTurn law REUSES the existing Control gains (MaxBankAngle,
        // RollGain/PitchGain/YawGain, RollDamping, RollRateSmoothing, ChaseDamping, the integrator trio,
        // and AssistTurnRateGain as the turn-rate gain Kturn).
        public static ConfigEntry<float> BankToTurnVmin;      // m/s: airspeed floor in atan(omega*V/g) so low-speed/hover stays sane
        public static ConfigEntry<float> BankToTurnOmegaMax;  // rad/s: cap on the demanded turn rate (limits commanded bank + load factor)
        public static ConfigEntry<float> BankToTurnDeadband;  // deg: pointing-error deadband on the turn-rate demand (anti on-heading wobble)

        // --- EvolvedLegacy law (Phase 2, v0.40): knob for the final-leg align-hold that has no Legacy
        // equivalent. All other EvolvedLegacy gains reuse the existing Control/Assist binds.
        public static ConfigEntry<float> EvolvedAlignHoldDeg; // deg: |azErr| below which the align-hold releases (leveling is allowed)

        // --- Hover / "flown-like-a-helicopter" regime (v0.43, EvolvedLegacy only). On collective aircraft
        // (takeoffDistance==0) the bank-to-turn math degenerates at low forward speed, so a regime blend
        // (heliBlend) ramps the law from bank-to-turn -> yaw-to-point as forward speed drops: bank is
        // suppressed (roll becomes a wings-leveler) and yaw authority is raised so the tail rotor swings
        // the nose. Forced fully on while the game's AutoHover is engaged.
        public static ConfigEntry<float> HeliForwardSpeed; // m/s: at/above this forward speed a collective aircraft is full fixed-wing (heliBlend=0)
        public static ConfigEntry<float> HeliHoverSpeed;   // m/s: at/below this forward speed it's full yaw-to-point hover (heliBlend=1)
        public static ConfigEntry<float> HeliYawScale;     // yaw-authority multiplier blended in at full hover so yaw can point the nose

        // --- Maneuver recorder (v0.35): a hotkey dumps a bounded high-rate CSV of the control state so a
        // problem can be captured cleanly across aircraft and the assist calibrated against real data.
        public static ConfigEntry<KeyCode> RecordKey;         // start/stop the CSV capture (default F8)
        public static ConfigEntry<float>   RecordRateHz;      // samples per second written to the CSV

        // --- Fly Level autopilot (v0.24): toggle a key to hold wings-level + nose-on-horizon at the
        // heading captured when you pressed it. Ignores the reticle; a stick nudge or re-press releases it.
        public static ConfigEntry<bool>    FlyLevelEnabled;   // master on/off for the feature
        public static ConfigEntry<KeyCode> FlyLevelKey;       // toggle key (default F7)

        // --- Anomaly logging thresholds (v0.25): how far past "fine" a miss must go before the
        // event-only logger flags it. Sensitivity knobs so the log stays quiet in normal flight.
        public static ConfigEntry<float> AnomalyOvershootDeg; // deg the nose must rebound past its closest approach
        public static ConfigEntry<float> AnomalyOverRollDeg;  // deg actual bank may exceed the servo's target before flagging
        public static ConfigEntry<float> AnomalyLowSpeed;     // m/s below which yaw-wag (low-speed nose wander) is flagged
        public static ConfigEntry<float> AnomalyWobbleSpeed;  // m/s above which roll limit-cycle (high-speed bank wobble) is flagged
        public static ConfigEntry<bool>  AnomalyContext;      // dump a recent-frame trail line with each anomaly

        // --- Cockpit camera follow.
        public static ConfigEntry<bool>  CameraFollow;        // smoothly look toward the marker
        public static ConfigEntry<float> CameraFollowAmount;  // 0..1 fraction of the marker offset to look toward
        public static ConfigEntry<float> CameraFollowSmoothing;// seconds-ish smoothing (higher = lazier)
        public static ConfigEntry<float> CameraPitchOffset;   // 3p: deg the view is pitched down off the aim direction
        public static ConfigEntry<float> OrbitAimSmoothing;   // 3p: view-direction smoothing rate (1/s, higher = snappier)
        public static ConfigEntry<float> HorizonDeadzoneDeg;  // 3p: half-width of the no-horizon-level band at the pole (hysteresis)
        public static ConfigEntry<float> FreeLookReturnTime;  // 3p: seconds to ease the view back to the flight dir on release
        public static ConfigEntry<float> CameraDistanceOffset;// 3p: extra orbit distance (units of radius); + = farther back, - = closer/in front
        public static ConfigEntry<float> CameraHeightOffset;  // 3p: extra height vs plane (units of radius); + = higher, - = lower
        public static ConfigEntry<float> CameraSideOffset;    // 3p: lateral shift (units of radius); + = right of plane, - = left

        // --- "I broke it, fix it please" reset button (drawn as a real button in the F1 menu).
        public static ConfigEntry<bool>  ResetToDefaults;
        private static ConfigFile        _file;               // kept so the reset can walk every entry

        public static void Bind(ConfigFile cf)
        {
            Enabled          = cf.Bind("General", "Enabled", true,
                "Master ON/OFF for the whole mod. Off = stock game controls, no overlay, no camera follow.");
            ToggleKey        = cf.Bind("General", "ToggleKey", KeyCode.F10,
                "Key that enables/disables the whole mod in-flight (flips Enabled). Default F10. A brief on-screen toast confirms the change. Pick any single key.");
            ShowOverlay      = cf.Bind("HUD", "ShowOverlay", true,
                "Show the on-screen aim circle, boresight cross, and turn cone. Purely visual — no effect on handling.");
            ShowDebugHud     = cf.Bind("HUD", "ShowDebugHud", false,
                "Show the diagnostic text readouts in the top-left: mod status / nose-off-marker / cone, the instructor's live pitch/yaw/roll command, the ANOMALY flash, and the live PHASE. OFF by default so installers get a clean reticle-only HUD (the aim circle, airframe marker, FLY LEVEL banner, and G-LOC warning still show). Turn ON for tuning/debugging.");
            DebugLogging     = cf.Bind("HUD", "DebugLogging", false,
                "VERBOSE periodic trace: dumps mouse delta, marker-vs-nose angle, camera-vs-nose angle and chase outputs to the BepInEx log every ~0.1-0.2 s. Token-heavy — leave OFF normally and rely on AnomalyLogging. Flip it on for one run only when a problem feels wrong but the anomaly log stays quiet.");
            AnomalyLogging   = cf.Bind("HUD", "AnomalyLogging", true,
                "Event-only logging: stays silent until a command misbehaves, then writes ONE compact [anomaly] line — overshoot (nose crosses the marker), over-roll (banks past what the turn needs), hunt (output sign-flapping), or persistent-miss (saturated but not closing). Cheap to hand back, unlike the verbose DebugLogging trace. On by default.");
            CursorLogging    = cf.Bind("HUD", "CursorLogging", false,
                "Focused cursor instrument: writes one [cursor] line ONLY when the cursor regime changes (enter/exit mouse-aim, free-look, menu, mod toggle), showing our Cursor.visible/lockState next to the game CursorManager's cached visibility + flags. Use it to diagnose the OS pointer popping over the game on a mod toggle — when our visibility and the game's cache disagree, the game's Refresh() can't fix the pointer. Low-volume (transition-only); leave OFF normally.");

            MouseSensitivity = cf.Bind("Aim", "MouseSensitivity", 0.30f, new ConfigDescription(
                "Degrees the aim circle moves per unit of mouse motion. The raw Win32 delta is normalised to Unity's legacy axis scale (x0.1) so this number is read-backend independent. ~0.3 is a sane start; drag the slider for feel. Higher = the circle races with small hand movements; lower = finer, calmer aiming.",
                new AcceptableValueRange<float>(0.01f, 2.0f)));
            MouseSmoothing   = cf.Bind("Aim", "MouseSmoothing", 0.20f, new ConfigDescription(
                "Smooths raw mouse motion to kill jitter/stepping. 0 = raw (most responsive, can feel jumpy); ~0.2 = light; 0.5+ = very smooth but laggy.",
                new AcceptableValueRange<float>(0f, 0.9f)));
            MaxAimAngle      = cf.Bind("Aim", "MaxAimAngle", 180.0f, new ConfigDescription(
                "Half-angle (deg) the aim circle can sit off the nose. 180 = effectively unlimited (aim anywhere); lower it to cap how far off-boresight you can command. The cone ring is hidden above 89.",
                new AcceptableValueRange<float>(5f, 180f)));
            AimDistance      = cf.Bind("Aim", "AimDistance", 800.0f, new ConfigDescription(
                "How far ahead (m) the aim circle is drawn. Visual only — does not change handling. Set near your typical gun/engagement range.",
                new AcceptableValueRange<float>(100f, 3000f)));
            InvertPitch      = cf.Bind("Aim", "InvertPitch", false,
                "Flips vertical mouse so the circle (and the plane) aim up vs. down. Also flips the camera-follow tilt, since both follow the same circle.");

            ControlLawMode      = cf.Bind("Control", "ControlLawMode", NuclearOptionMouseAim.ControlLawMode.EvolvedLegacy,
                "Which control law turns the aim-marker error into stick commands (A/B switch, v0.38). EvolvedLegacy = the DEFAULT as of v0.42 (graduated): evolved Legacy with a universal atan(omega*V/g) bank at all speeds (not just when yaw-weak) + final-leg align-hold that keeps the roll-to-align contribution engaged until the target is genuinely close laterally (no early wings-level / park-short). Legacy = the accreted v0.37 law, kept as the pristine A/B reference (pick it to compare). BankToTurn = the abandoned Phase-1 experiment (not working) — HIDDEN from the F9 cycle as of v0.42; code retained but not reachable via the toggle. Cycle Legacy<->EvolvedLegacy in flight with ControlLawKey (default F9); an on-screen toast confirms the active law and the maneuver-recorder CSV tags each row with it.");
            ControlLawKey       = cf.Bind("Control", "ControlLawKey", KeyCode.F9,
                "Key that toggles the active control law in flight (Legacy <-> EvolvedLegacy as of v0.42; BankToTurn is excluded — abandoned), updating ControlLawMode. Default F9 (F7=Fly Level, F8=Record, F10=master toggle are taken). A brief on-screen toast names the law it switched to. Pick any single key.");
            WriteControl        = cf.Bind("Control", "WriteControl", true,
                "Let the mod actually fly the plane (drive the stick). Off = overlay/camera only, you keep manual control — handy for A/B comparing the feel.");
            ControlRotorcraft   = cf.Bind("Control", "ControlRotorcraft", true,
                "Also fly helicopters and hover-VTOLs (collective aircraft — the game flags them by takeoffDistance==0). They drive the SAME pitch/roll/yaw (cyclic + tail rotor); collective stays on your throttle, untouched. The chase law was tuned for forward flight, so the feel differs at low speed/hover — turn this OFF to leave rotorcraft on stock controls while keeping mouse-aim for fixed-wing.");
            PitchYawSensitivity = cf.Bind("Control", "PitchYawSensitivity", 3.0f, new ConfigDescription(
                "How hard the instructor pulls the nose toward the circle. Higher = snappier and closes faster, but can overshoot and wobble (raise ChaseDamping to compensate); lower = gentler, easier fine aiming. ~3 is balanced.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            ChaseDamping        = cf.Bind("Control", "ChaseDamping", 0.25f, new ConfigDescription(
                "Calms the inputs as the nose nears the circle so it eases in instead of overshooting — opposes the nose's own turn rate (the anti-wobble term). ~0.25 is a smooth default. 0 = off (snappy, but the rudder can hunt side-to-side); raise toward ~0.4 if it still oscillates around the aim direction, lower if it feels sluggish to close.",
                new AcceptableValueRange<float>(0f, 1f)));
            RollDamping         = cf.Bind("Control", "RollDamping", 0.1f, new ConfigDescription(
                "Anti-wobble damping for the ROLL axis — opposes the rolling RATE so the bank eases onto its target instead of blowing through it. Keep it SMALL: the rate feedback is delayed (one-frame finite difference + the RollRateSmoothing low-pass), so too much of it flips from damping to DRIVING a high-speed roll limit cycle — the ±roll-stick buzz/PIO felt at high dynamic pressure. ~0.1 takes the edge off the roll-out jitter without sustaining the cycle; 0 = off (a touch jittery on-heading); raising past ~0.3 brings the high-speed wobble back. Only opposes the rolling MOTION, so it won't fight a held bank.",
                new AcceptableValueRange<float>(0f, 2f)));
            RollRateSmoothing   = cf.Bind("Control", "RollRateSmoothing", 0.06f, new ConfigDescription(
                "Low-pass time constant (seconds) on the roll RATE that feeds RollDamping. The high-speed roll wobble is a derivative-feedback limit cycle: when level on-heading the roll command is essentially -rollRate*RollDamping, and rollRate is a one-frame finite difference (~60 Hz); at high dynamic pressure the airframe is responsive enough that this delayed rate feedback flips from damping to DRIVING at ~6-7 Hz (a fast roll-stick dither / PIO). Smoothing the rate before the damping term rolls off that high-frequency content so the damping only opposes real, low-frequency roll motion — killing the wobble while keeping turn damping. Higher = more smoothing (more wobble margin, but slightly laggier roll-out damping); 0 = off (raw rate, old behaviour). ~0.05-0.10 is the useful band.",
                new AcceptableValueRange<float>(0f, 0.3f)));
            RollGain            = cf.Bind("Control", "RollGain", 1.0f, new ConfigDescription(
                "Roll authority scale. Lower if it banks too eagerly, raise for crisper rolls (faster roll-in and roll-out). Negative flips roll direction — if the plane rolls AWAY from level when on-target, set this negative.",
                new AcceptableValueRange<float>(-2f, 2f)));
            PitchGain           = cf.Bind("Control", "PitchGain", 1.0f, new ConfigDescription(
                "Pitch authority scale. Negative flips pitch direction (nose chases the wrong way vertically).",
                new AcceptableValueRange<float>(-2f, 2f)));
            YawGain             = cf.Bind("Control", "YawGain", 1.0f, new ConfigDescription(
                "Yaw/rudder authority scale — the lever for fine horizontal alignment while the wings stay level. Raise it if small sideways corrections barely move the nose; lower it if the nose wags or feels twitchy. Negative flips.",
                new AcceptableValueRange<float>(-2f, 3f)));
            OutputSlew          = cf.Bind("Control", "OutputSlew", 6.0f, new ConfigDescription(
                "Max stick travel per second — the anti-jerk rate limit. Lower = silkier but laggier; higher = sharper and more immediate, but can feel jerky.",
                new AcceptableValueRange<float>(1f, 20f)));
            MaxBankAngle        = cf.Bind("Control", "MaxBankAngle", 72.0f, new ConfigDescription(
                "Cap (deg) on the bank the instructor will roll into for a turn. The bank-angle servo (v0.25) commands a bank proportional to the heading error (FineBankGain deg per deg) up to THIS limit, then holds it and rolls out as the turn completes — so a hard side command rolls to a firm, BOUNDED bank instead of slamming to full deflection and over-rolling. Lower for gentler max banks; raise toward 85 for very hard turns.",
                new AcceptableValueRange<float>(10f, 85f)));
            RollPitchCoordination = cf.Bind("Control", "RollPitchCoordination", 0.9f, new ConfigDescription(
                "Roll-then-pull coordination (body-frame law, v0.26). Gates the pitch PULL by how well the lift vector (body-up, the way a pull swings the nose) is aligned with the target during a big turn, so the plane rolls the lift vector ONTO the target first, then pulls up into it — the efficient line — instead of pulling across the wrong plane and bunting. The gate is signed and clamped at zero, so it NEVER pushes (no negative-G). 0 = off (pull immediately); 1 = no pull until the lift vector is on the target, never a push. ~0.9 is a firm roll-then-pull. Lower if turns feel hesitant to pull; raise toward 1 if it pulls before it has rolled. Only active above FineAngle (scaled by the big-turn ramp); inside the fine cone the pull is direct.",
                new AcceptableValueRange<float>(0f, 1f)));
            AlignAngle          = cf.Bind("Control", "AlignAngle", 25.0f, new ConfigDescription(
                "Off-angle (deg) at which the roll-to-align law is at FULL strength — the knee of the 'big turn' ramp that runs from FineAngle (direct nudge) up to here (roll the lift vector onto the target, pull gated). Below FineAngle the law is the fine wings-level/azimuth bank servo; between the two it blends. Lower = commits to the roll-then-pull line sooner (smaller direction changes count as big turns); raise = only the largest reorientations roll first.",
                new AcceptableValueRange<float>(8f, 90f)));
            TurnYawScale        = cf.Bind("Control", "TurnYawScale", 0.35f, new ConfigDescription(
                "Yaw authority fraction during a big turn (v0.26). At full big-turn strength the yaw/rudder command is scaled to this, so the bank + pull do the work and the rudder stops pinning to its stops and adding to the messy feel. 1 = full yaw always (old behaviour); ~0.35 leaves a little coordinating rudder; 0 = no rudder mid-turn. Full yaw authority always returns inside the fine cone for final alignment.",
                new AcceptableValueRange<float>(0f, 1f)));
            PitchBrake          = cf.Bind("Control", "PitchBrake", 0.35f, new ConfigDescription(
                "Pitch anti-overshoot brake. Adds extra rate damping that ramps in as the nose nears the target while it's still swinging, so a fast large-angle pull (e.g. takeoff climb-out) decelerates ONTO the marker instead of crossing it and settling back. Opposes rotation only, so it vanishes once the nose stops turning and never fights a held attitude. 0 = off; raise if it still overshoots in pitch, lower if it feels like it brakes too early.",
                new AcceptableValueRange<float>(0f, 1.5f)));
            AuthorityRamp       = cf.Bind("Control", "AuthorityRamp", 5.0f, new ConfigDescription(
                "How fast control blends back to the game when the mod disengages (per second). Higher = snappier handoff.",
                new AcceptableValueRange<float>(1f, 15f)));
            ManualOverride      = cf.Bind("Control", "ManualOverride", true,
                "Let your stick / keyboard / rudder pedals take over PER AXIS while mouse-aim flies. Push roll and you get roll (the mod stops leveling the wings); push rudder and you get rudder — the mouse keeps aiming whatever axis you're NOT touching. Release and that axis eases back to mouse-aim. Off = the mod fully owns the stick (manual inputs ignored while flying).");
            ManualReorients     = cf.Bind("Control", "ManualReorients", true,
                "Manual input REDEFINES the flight direction while free-looking (v0.48, scoped v0.50). While you hold RMB/free-look AND actively hold the stick / keyboard / pedals (any axis past ManualDeadzone), the aim marker is dragged onto the nose so the instructor stops flying you back toward where you last aimed — the fix for 'I free-look with RMB, fly somewhere manually, and the plane keeps pulling back to the old aim point'. Release and the marker stays parked on the heading you ended on. When you are NOT free-looking (the marker is live under your mouse), manual input never moves the marker — you make a correction, release, and the instructor resumes toward the target you were already aiming at (War Thunder style). Off = manual input never re-seeds the marker, even while free-looking. Requires ManualOverride.");
            ManualHandoffTime   = cf.Bind("Control", "ManualHandoffTime", 1.0f, new ConfigDescription(
                "Global hard handoff — 'my controls / your controls' (v0.49). When > 0, ANY manual input on ANY axis switches the WHOLE instructor OFF (you fly the plane directly on all three axes, not just the one you touched), and keeps it off until this many seconds after your LAST input — so a quick stick-stir won't let the chase grab back between nudges. This hold time applies while free-looking (RMB held), where your input also re-seeds the marker onto the nose (see ManualReorients); while AIMING the hold time is ManualHandoffAimTime instead and the marker stays put. Set to 0 to use the old PER-AXIS blend instead (touch one axis, mouse keeps the others). Requires ManualOverride.",
                new AcceptableValueRange<float>(0f, 5f)));
            ManualHandoffAimTime = cf.Bind("Control", "ManualHandoffAimTime", 0.0f, new ConfigDescription(
                "Hold time (seconds) after your last manual input before the instructor re-engages while AIMING — i.e. RMB/free-look NOT held, marker live under the mouse (v0.50). In this mode your input is a temporary correction: the marker never moves, and when you release, the instructor resumes flying toward the target you kept aimed. Default 0 = it starts steering back the moment you let go (it still eases in over ManualReturnTime). Raise it if you want a hands-off pause after a correction before the chase grabs back. Only used when ManualHandoffTime > 0.",
                new AcceptableValueRange<float>(0f, 5f)));
            ManualReturnTime    = cf.Bind("Control", "ManualReturnTime", 0.25f, new ConfigDescription(
                "How long (seconds) a released axis takes to ease back to mouse-aim after you let go of it. Lower = snaps back to the mouse fast; higher = hands the axis back gently. Manual takeover itself is always instant.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            ManualDeadzone      = cf.Bind("Control", "ManualDeadzone", 0.05f, new ConfigDescription(
                "How far a manual axis must move before it counts as you taking over (ignores stick noise / a centred gamepad). Raise if the mod hands you control from a twitchy stick at rest; keyboard is digital so this barely matters there.",
                new AcceptableValueRange<float>(0f, 0.3f)));
            FineAngle           = cf.Bind("Control", "FineAngle", 6.0f, new ConfigDescription(
                "Cone half-angle (deg) inside which the fine-capture aids (FineGainBoost / FineBankGain) ramp in. They are at full strength with the nose on the circle and fade to nothing at this angle, so the large-angle behaviour is untouched.",
                new AcceptableValueRange<float>(2f, 15f)));
            FineGainBoost       = cf.Bind("Control", "FineGainBoost", 2.5f, new ConfigDescription(
                "Extra pitch/yaw pull for the last few degrees: multiplies the proportional term by up to (1 + this) as the offset closes. Cures the 'never quite centres' residual where a ~1 deg error left only ~0.05 stick. 0 = off. Raise if the nose still parks short of the circle; lower if it hunts around it.",
                new AcceptableValueRange<float>(0f, 5f)));
            FineBankGain        = cf.Bind("Control", "FineBankGain", 3.0f, new ConfigDescription(
                "The bank-angle servo gain (v0.25): degrees of commanded bank per degree of heading (azimuth) error, capped at MaxBankAngle. This now drives the bank across the WHOLE range — a small error leans a few degrees, a big side command rolls to a firm bank and holds it — replacing the old proportional roll-rate slam that over-rolled. 0 = no banking (wings-level, leans on the weak rudder). Raise to bank harder/sooner; lower if it over-banks or wing-rocks.",
                new AcceptableValueRange<float>(0f, 10f)));
            FineBankDeadzone    = cf.Bind("Control", "FineBankDeadzone", 2.5f, new ConfigDescription(
                "Heading-error deadband (deg) for the bank servo. Below this azimuth error the commanded bank is ZERO — the wings stay level and the rudder/yaw does the final fine capture, instead of the bank servo amplifying a sub-few-degree heading hunt into a continuous roll-stick dither (the fine-cone roll wobble). Above it the bank ramps in smoothly (the error past the deadband still feeds FineBankGain) so genuine side commands bank to turn normally. 0 = off (old behaviour); raise if the wings still rock on-heading, lower if small corrections won't bank.",
                new AcceptableValueRange<float>(0f, 15f)));
            FineIntegralGain    = cf.Bind("Control", "FineIntegralGain", 0.8f, new ConfigDescription(
                "The piece that actually lands the nose ON the circle. The game's fly-by-wire treats our pitch/yaw as a turn-RATE request, not a position, so a plain proportional pull always parks a fraction of a degree short — this small integrator winds in the steady bias needed to close that last bit. Only active inside FineAngle. 0 = off (back to the v0.23 'gets close but never quite centres' behaviour). Raise if it still parks short; lower if it slowly drifts past and hunts.",
                new AcceptableValueRange<float>(0f, 3f)));
            FineIntegralLeak    = cf.Bind("Control", "FineIntegralLeak", 0.5f, new ConfigDescription(
                "How fast the fine integrator bleeds back toward zero (per second) — the anti-windup safety. Higher = forgets faster (less chance of overshoot/hunt, but may not fully close); lower = holds its bias longer (closes harder, slower to let go). ~0.5 is a calm default.",
                new AcceptableValueRange<float>(0f, 4f)));
            FineIntegralCap     = cf.Bind("Control", "FineIntegralCap", 0.12f, new ConfigDescription(
                "Hard limit on how much stick (per axis) the fine integrator may add. Keeps it from winding up into a lurch if the nose is held off-target. ~0.12 is enough to defeat the rate-command residual without being felt as a kick.",
                new AcceptableValueRange<float>(0f, 0.3f)));

            YawAssistEnabled    = cf.Bind("Control", "YawAssistEnabled", true,
                "Reactive bank-and-pull (v0.35). The honest way to move the flight-path heading is to BANK and PULL, not to push rudder — and rudder gets weaker the faster you go, which is why a small sideways nudge 'leaves yaw on the table' at high speed and the nose parks short of the reticle. When ON, the instructor continuously MEASURES how much heading change your yaw command is actually buying; when that's poor it automatically rolls a touch more into the correction AND adds a coordinating pull so the bank turns the nose, instead of leaning on the weak rudder. Self-adapting per airframe and speed — no tuning needed to work. OFF = the v0.34 rudder-led fine capture.");
            YawAssistStrength   = cf.Bind("Control", "YawAssistStrength", 0.7f, new ConfigDescription(
                "Overall scale of the reactive bank-and-pull assist (YawAssistEnabled). 0 = effectively off; 1 = full authority shift into bank-and-pull when the rudder is measured to be ineffective. ~0.7 is a firm assist that still leaves the rudder doing the final fine alignment. Lower if side corrections now over-bank or feel too eager to roll; raise if the nose still parks short on high-speed sideways nudges. Only acts in the small/mid correction regime — big turns already roll-and-pull, so it fades out there.",
                new AcceptableValueRange<float>(0f, 1f)));
            YawAssistResponse   = cf.Bind("Control", "YawAssistResponse", 0.8f, new ConfigDescription(
                "How quickly (seconds) the measured yaw-weakness estimate reacts. It's a low-pass so a momentary blip doesn't swing the assist — but it has MEMORY, so once it has learned the rudder is weak in this regime (e.g. high speed) the very next nudge already banks-and-pulls. Lower = snappier/twitchier adaptation; higher = smoother but slower to notice the rudder is failing. ~0.8 s is a calm, responsive default.",
                new AcceptableValueRange<float>(0.1f, 3f)));
            CoordPullGain       = cf.Bind("Control", "CoordPullGain", 0.8f, new ConfigDescription(
                "Coordinating-pull authority — the 'pitch INTO the bank' half of the assist, and the REAL driver of a high-speed correction. Once banked into a side nudge, a level turn needs back-pressure or gravity just drops the nose and the bank does nothing (without it the nose mushes at under 1g and the heading barely moves). This adds a nose-up pull proportional to the commanded bank so the bank becomes a loaded turn that actually slews the nose. Always a PULL (nose-up, positive-G) — clamped so it can never push/bunt, and capped by CoordPullCap. 0 = bank only (the nose sags); ~0.8 loads a firm coordinated turn. Raise if high-speed side nudges still creep, lower if they balloon high or feel grabby.",
                new AcceptableValueRange<float>(0f, 1f)));
            CoordPullCap        = cf.Bind("Control", "CoordPullCap", 0.85f, new ConfigDescription(
                "Cap on the coordinating-pull stick so the assist can load real G for a firm, quick correction without ever pinning the pitch axis (the rest of the pitch budget stays for the normal pull-to-target and the anti-overshoot brake). ~0.85 lets a high-speed nudge pull hard enough to slew the nose in ~1-2 s. Lower if banked corrections pull too hard / bleed too much energy; raise toward 1 if they still feel soft at the very highest speeds.",
                new AcceptableValueRange<float>(0f, 1f)));
            BankAuthGain        = cf.Bind("Control", "BankAuthGain", 5.0f, new ConfigDescription(
                "How much steeper the instructor banks per unit of measured yaw-weakness (the assist). At full assist the fine bank-servo gain is multiplied by (1 + this), so a small side nudge at high speed commands a real loaded-turn bank (~45-60 deg, still capped by MaxBankAngle) instead of the shallow ~20 deg that turns at well under 1 deg/s. Backed by the coordinating pull this becomes an efficient loaded turn. Lower if high-speed nudges over-bank/feel violent; raise if they still don't bank enough to turn.",
                new AcceptableValueRange<float>(0f, 10f)));
            YawWeakFade         = cf.Bind("Control", "YawWeakFade", 1.0f, new ConfigDescription(
                "How far the rudder is faded OUT as the assist rises. When the rudder is measured ineffective (high speed) it's just sideslip and drag doing nothing for the heading, so the law commits to bank-and-pull instead. At full assist the fine-regime yaw command is scaled by (1 - this), so 1.0 leaves ~30% rudder for a little coordination (never fully zero), 0 = keep full rudder alongside the bank+pull. Low speed (rudder still effective) is unaffected either way. Lower if you want some rudder retained when weak; raise to commit harder to bank+pull.",
                new AcceptableValueRange<float>(0f, 1f)));
            AssistTurnRateGain  = cf.Bind("Control", "AssistTurnRateGain", 0.9f, new ConfigDescription(
                "THE high-speed fix (v0.37). The old assist banked PROPORTIONAL to the heading error, so a small nudge commanded a shallow bank that turns fast at low speed but barely at all when fast (a 12 deg bank at 400 m/s slews the nose ~0.3 deg/s — the nose mushed the last few degrees). Instead the bank is now sized from a target TURN-RATE (proportional to error, this gain in 1/s) converted to the bank that physically holds it: phi = atan(omega*V/g). Because V is in there, the SAME error commands a steep loaded bank when fast and a gentle one when slow — automatically, no per-speed tuning. Blended in by measured yaw-weakness, so low speed / strong-yaw airframes keep the old gentle servo untouched. Higher = asks for a faster turn (steeper bank, snappier, more G) for a given error; lower = gentler. v0.42: default lowered 1.5 -> 0.9 — on the now-default EvolvedLegacy law (which uses this atan(omega*V/g) bank UNCONDITIONALLY at all speeds, not just when yaw-weak) 1.5 made a small high-speed nudge saturate the bank to ~72-85 deg, overshoot the heading, and rock back and forth; 0.9 keeps the bank proportionate so it settles on aim without the limit cycle. Raise toward 1.2 if the last few high-speed degrees mush; lower if fast nudges still feel violent.",
                new AcceptableValueRange<float>(0f, 4f)));
            CoordPullReleaseAngle = cf.Bind("Control", "CoordPullReleaseAngle", 2.0f, new ConfigDescription(
                "Heading-error cone (deg) inside which the coordinating pull eases back to zero. Outside it the pull stays at full strength so the loaded turn holds its G right down through the tail of the correction (the v0.36 pull tapered over the whole 6 deg fine cone, so it was already half-gone at 3 deg and the nose mushed); inside it the pull bleeds off so the bank+pull releases cleanly onto aim instead of overshooting. ~2 deg keeps the turn loaded until the nose is nearly on, then lets go. Raise if it overshoots / balloons past aim; lower if the very last degree still creeps.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            TurnLeadTime        = cf.Bind("Control", "TurnLeadTime", 0.65f, new ConfigDescription(
                "THE death-wobble fix (v0.51). Recordings from 8 wobble reports showed the achieved bank lags the atan(omega*V/g) turn-rate bank command by a constant ~0.68-0.71 s, and that command was pure-proportional in the heading error — at the observed 0.3-0.85 Hz oscillation that lag is ~90-180 deg of phase, so the loop self-sustained a limit cycle (bank rocking +/-80 deg from a +/-6 deg aim error, roll stick railed, at ALL speeds — worse the faster you fly). This subtracts noseHeadingRate*TurnLeadTime from the azimuth error BEFORE it becomes a bank target, so the bank rolls out EARLY, anticipating where the nose will be when the bank catches up — including a brief counter-bank that brakes the turn. 0 = exact old behaviour (the wobble). Default 0.65 deliberately sits just under the measured lag. Raise toward 0.7-0.8 if high-speed corrections still overshoot/rock; lower if turns roll out early and park short of the marker.",
                new AcceptableValueRange<float>(0f, 2f)));
            AssistOffPitchScale = cf.Bind("Control", "AssistOffPitchScale", 0.5f, new ConfigDescription(
                "Flat pitch-command scale applied while the game's own flight-assist (AoA limiter) is OFF (v0.51). With assist off at low-to-mid speed the game FBW's stick-to-pitch-rate gain roughly doubles-triples (decompiled: targetPitchAngVel becomes stick*maxPitchAngularVel instead of the G-limited formula), so the mod's fixed pitch gains overshoot violently — a recorded FS-12 run railed the elevator and swung AoA -29..+52 deg. ~0.5 is a rough compensating cut, NOT a physically-derived per-airframe match (that would need reflecting private FBW fields and reconstructing the game's dynamic-pressure blend — deferred). 1 = no change (old behaviour, can rail AoA with assist off). No effect while flight-assist is on.",
                new AcceptableValueRange<float>(0.2f, 1f)));
            BankSlewRate        = cf.Bind("Control", "BankSlewRate", 60f, new ConfigDescription(
                "EvolvedLegacy law only (v0.54). Rate limit (deg/s) on the atan(omega*V/g) bank target the roll servo chases. Nineteen v0.53 recordings (KR67 + AB4 Alcyon, 450-536 m/s) showed the brake-clamped lead rectifying heading-rate ripple into a bank target that BANGED 0<->48-65 deg at ~1.5 Hz from a 1-3 deg aim error — the roll servo faithfully chased it (corr 0.79-0.96) and the wings rocked +/-14-30 deg while station-keeping. A target that can only move this fast can't flap above the airframe's own roll response, and a slower-moving target also shrinks the 15-20 deg bank overshoot that sustained the slower 0.5 Hz cycle in big turns. 60 deg/s still reaches the full 72 deg bank in ~1.2 s (about what a real roll-in takes). 0 = off (instant target, old behaviour). Lower toward 40 if the wings still rock; raise toward 90-120 if turn entry feels lazy.",
                new AcceptableValueRange<float>(0f, 360f)));
            BankToTurnVmin      = cf.Bind("Control", "BankToTurnVmin", 50f, new ConfigDescription(
                "Bank-to-turn law (Phase 1) only. Airspeed FLOOR (m/s) used inside the speed-correct bank/load-factor physics phi=atan(omega*V/g) and n=sqrt(1+(omega*V/g)^2). The law banks and pulls in proportion to airspeed V; at very low speed / hover V->0 would collapse the commanded bank to nothing, so V is floored at this value to keep the maths sane (a gentle, sensible bank rather than zero). Has no effect above this speed. ~50 m/s is a safe floor; raise it if low-speed turns feel too weak, lower it toward true stall speed for more honest slow-flight banking.",
                new AcceptableValueRange<float>(10f, 150f)));
            BankToTurnOmegaMax  = cf.Bind("Control", "BankToTurnOmegaMax", 0.6f, new ConfigDescription(
                "Bank-to-turn law (Phase 1) only. CAP (rad/s) on the demanded turn rate omega. The law asks for a turn rate proportional to the pointing error (omega = Kturn*err, Kturn = AssistTurnRateGain), then sizes the bank phi=atan(omega*V/g) and the load factor n=sqrt(1+(omega*V/g)^2) to sustain it. This cap bounds how aggressive a large pointing error gets: it limits the steepest commanded bank and the hardest pull (more G the faster you go, since n scales with V). ~0.6 rad/s (~34 deg/s) is a firm but controlled slew. Lower for gentler maximum turns; raise if large reorientations feel sluggish (watch for over-G / energy bleed at high speed).",
                new AcceptableValueRange<float>(0.1f, 1.5f)));
            BankToTurnDeadband  = cf.Bind("Control", "BankToTurnDeadband", 0.5f, new ConfigDescription(
                "Bank-to-turn law (Phase 1) only. Pointing-error DEADBAND (deg) on the turn-rate demand — the BankToTurn replacement for FineBankDeadzone. Below this nose-to-marker error the demanded turn rate is ZERO (wings level, no pull), so the law doesn't chase sub-degree noise into an on-heading roll/pitch dither; the retained fine integrator still lands the nose on the marker. Above it the demand ramps in smoothly (error past the deadband feeds the turn rate). 0 = off; raise if the wings/nose still wobble when on-heading, lower if small corrections feel like they ignore the first fraction of a degree.",
                new AcceptableValueRange<float>(0f, 5f)));
            EvolvedAlignHoldDeg = cf.Bind("Control", "EvolvedAlignHoldDeg", 5.0f, new ConfigDescription(
                "EvolvedLegacy law only. When the total nose-off-marker angle (off) drops inside FineAngle, Legacy immediately snaps the roll blend to pure wings-level (bigTurn->0) even if the target is still a few degrees SIDEWAYS — it stops rolling to align early and the nose parks short. This knob keeps the roll-to-align contribution alive through the final degrees: the blend weight is MAX(bigTurn, lateralHold) where lateralHold = clamp01(|azErr|/EvolvedAlignHoldDeg), so as long as |azErr| exceeds this value the law keeps rolling to put the target at 12-o'clock instead of leveling early. When BOTH off and |azErr| are near zero the law settles wings-level on target — convergent, no limit cycle. ~5 deg is a reasonable start: raise if the nose still parks short or wings-level too early; lower toward 1-2 if it over-rolls past the target in the final stage.",
                new AcceptableValueRange<float>(0f, 15f)));

            HeliForwardSpeed    = cf.Bind("Control", "HeliForwardSpeed", 150f, new ConfigDescription(
                "EvolvedLegacy + collective aircraft (takeoffDistance==0: helicopters / hover-VTOLs) only. FORWARD airspeed (m/s, nose-direction component of velocity) at/above which the aircraft is flown as a normal fixed-wing: bank-to-turn at full strength, regime blend heliBlend=0. Between this and HeliHoverSpeed the law smoothly ramps from bank-to-turn toward yaw-to-point. Has no effect on fixed-wing airframes (they're always heliBlend=0). Default 150 m/s keeps yaw-to-point engaged across the whole normal heli envelope; raise if a fast-moving heli still feels like it's pedalling the nose around, lower if forward flight feels mushy/over-banked.",
                new AcceptableValueRange<float>(20f, 300f)));
            HeliHoverSpeed      = cf.Bind("Control", "HeliHoverSpeed", 40f, new ConfigDescription(
                "EvolvedLegacy + collective aircraft only. FORWARD airspeed (m/s) at/below which the aircraft is flown as a pure hover: bank fully suppressed (wings level, roll axis becomes a leveler), yaw authority raised by HeliYawScale so the tail rotor swings the nose onto the marker (heliBlend=1). Must be below HeliForwardSpeed. Also forced on whenever the game's AutoHover is engaged, regardless of speed. Default 40 m/s; raise toward HeliForwardSpeed for an earlier switch to yaw-pointing, lower toward 0 to keep banking down to a crawl.",
                new AcceptableValueRange<float>(0f, 150f)));
            HeliYawScale        = cf.Bind("Control", "HeliYawScale", 2.0f, new ConfigDescription(
                "EvolvedLegacy + collective aircraft only. Yaw-authority multiplier blended in (by heliBlend) at full hover so the tail rotor becomes the primary heading driver once the wings are held level. The existing yaw term already points the nose toward the marker; in hover it's the only thing turning the aircraft, so it usually needs more authority than coordinated forward flight. 1 = no boost (same yaw as fixed-wing); ~2 is a firm pedal-turn. Raise if the nose swings onto a side target too slowly; lower if the nose wags/overshoots in hover.",
                new AcceptableValueRange<float>(0.5f, 5f)));

            RecordKey           = cf.Bind("Recorder", "RecordKey", KeyCode.F8,
                "Key that starts/stops the maneuver recorder. Press once to begin capturing, fly the maneuver, press again to stop — each capture writes its own timestamped CSV (mouseaim-rec-<date-time>.csv) into the BepInEx folder next to LogOutput.log, one row per sample. A 'REC' marker shows on-screen while it's running. For diagnosing/tuning feel across different aircraft. Default F8.");
            RecordRateHz        = cf.Bind("Recorder", "RecordRateHz", 20f, new ConfigDescription(
                "How many samples per second the maneuver recorder writes to the CSV. Higher = finer time resolution (bigger files); 20/s resolves a normal correction well without bloating the file. Sampling runs on the physics step, so very high values are capped by the fixed-update rate.",
                new AcceptableValueRange<float>(5f, 60f)));

            FlyLevelEnabled     = cf.Bind("FlyLevel", "Enabled", true,
                "Enable the 'Fly Level' toggle key. When you press it, the instructor locks the current heading and holds TRUE level flight — wings level, zero climb rate (the velocity vector on the horizon, accounting for angle-of-attack), ignoring the aim circle. Press again (or nudge the stick) to return to mouse-aim.");
            FlyLevelKey         = cf.Bind("FlyLevel", "Key", KeyCode.F7,
                "Key that toggles Fly Level on/off. Default F7. Pick any single key (this is one key, not a chord).");
            AnomalyOvershootDeg = cf.Bind("HUD", "AnomalyOvershootDeg", 5.0f, new ConfigDescription(
                "Overshoot sensitivity: how far (deg) the nose must rebound past its closest approach to the marker before the anomaly log flags an overshoot. Only counts once the nose actually arrived (closest approach < 5deg), so it flags a genuine crossing of the marker rather than mid-manoeuvre wobble. Lower = flags smaller overshoots (noisier log); higher = only flags gross ones.",
                new AcceptableValueRange<float>(0.5f, 20f)));
            AnomalyOverRollDeg  = cf.Bind("HUD", "AnomalyOverRollDeg", 12.0f, new ConfigDescription(
                "Over-roll sensitivity: how far (deg) the actual bank may exceed the bank-angle servo's target before the anomaly log flags an over-roll. Lower = stricter; higher = only flags large bank overshoots.",
                new AcceptableValueRange<float>(2f, 45f)));
            AnomalyLowSpeed     = cf.Bind("HUD", "AnomalyLowSpeed", 70.0f, new ConfigDescription(
                "Speed (m/s) below which the anomaly log flags YAW-WAG — the nose wagging left/right on the takeoff roll / low-speed regime, where rudder authority is low and the cruise-tuned yaw loop over-corrects. Set near your typical rotation/climb-out speed; 0 disables the check in practice.",
                new AcceptableValueRange<float>(0f, 200f)));
            AnomalyWobbleSpeed  = cf.Bind("HUD", "AnomalyWobbleSpeed", 200.0f, new ConfigDescription(
                "Speed (m/s) ABOVE which the anomaly log flags ROLL-WOBBLE — a small roll limit-cycle (the bank rocking back and forth a little, the +/-0.1-ish roll output you see jittering in the top-left at high speed) that the cruise-tuned roll loop falls into at high dynamic pressure, where the ailerons are far more effective than at the tuning speed. Flagged only while roughly on-heading (small off) so a normal hard turn doesn't trip it. Set near the speed where you first feel the bank get twitchy; very high = effectively disables the check.",
                new AcceptableValueRange<float>(50f, 600f)));
            AnomalyContext      = cf.Bind("HUD", "AnomalyContext", true,
                "When an anomaly fires, also emit one compact [anomaly:trail] line with the last ~20 frames of state (off, bank vs target, P/R/Y outputs, yaw rate, speed) so the LEAD-UP to the event is visible without any continuous logging. Throttled to once per second. Turn off for the leanest possible log.");
            RightClickFreeze    = cf.Bind("Control", "RightClickFreeze", true,
                "Hold RIGHT MOUSE to freeze the aim reticle (cockpit AND 3rd-person): the plane keeps flying to the frozen point while the mouse looks the camera around, then the view eases back to the aim when you release. War Thunder–style free-look. Off = only the game's bound Free Look freezes the reticle.");

            CameraFollow          = cf.Bind("Camera", "CameraFollow", true,
                "Smoothly turn the cockpit view toward the aim circle, so you look where you're steering.");
            CameraFollowAmount    = cf.Bind("Camera", "CameraFollowAmount", 0.5f, new ConfigDescription(
                "How far the view leans toward the circle. 0 = view stays forward (circle moves freely on screen); 1 = looks fully at the circle (which then sits glued to screen-centre — you can't see it lead the nose). ~0.5 lets the circle visibly lead.",
                new AcceptableValueRange<float>(0f, 1f)));
            CameraFollowSmoothing = cf.Bind("Camera", "CameraFollowSmoothing", 0.3f, new ConfigDescription(
                "View-follow lag (seconds-ish). Higher = lazier, smoother camera; lower = snappier, can feel twitchy.",
                new AcceptableValueRange<float>(0.02f, 1f)));
            CameraPitchOffset     = cf.Bind("Camera", "CameraPitchOffset", 11f, new ConfigDescription(
                "3rd-person only: degrees the view is pitched DOWN off the aim direction, trading the aim circle's screen position against the airframe's. 0 = circle dead-centre (plane low in frame); ~11 = circle a little above centre, plane a little below — 'behind and slightly above, looking down at the airframe'; ~22 = plane centred (circle high).",
                new AcceptableValueRange<float>(0f, 22f)));
            OrbitAimSmoothing     = cf.Bind("Camera", "OrbitAimSmoothing", 8f, new ConfigDescription(
                "3rd-person only: how quickly the orbit camera's view direction chases the aim circle (per second, frame-rate independent). Higher = snappier tracking; lower = lazier, smoother swings. Position never lags the plane — this only smooths the direction.",
                new AcceptableValueRange<float>(1f, 20f)));
            HorizonDeadzoneDeg    = cf.Bind("Camera", "HorizonDeadzoneDeg", 5f, new ConfigDescription(
                "3rd-person only: half-width (deg) of the no-horizon-level band straight up/down. Inside it the camera stops re-leveling to the world horizon and holds its current up so it can't flip through the singularity; leveling eases back in over this band as you come out (hysteresis), so over-the-top loops roll to the new level side smoothly instead of snapping. Smaller keeps the horizon level closer to vertical; larger widens the no-level hold band.",
                new AcceptableValueRange<float>(0f, 20f)));
            FreeLookReturnTime    = cf.Bind("Camera", "FreeLookReturnTime", 0.5f, new ConfigDescription(
                "3rd-person only: seconds for the view to smoothly swing back to your flight direction when you RELEASE free-look (RMB / Free Look). Classic free-look — the plane keeps flying the heading it held; only the camera eases back (smoothstep). 0.5 is a gentle swing; smaller = snappier return, larger = lazier.",
                new AcceptableValueRange<float>(0.05f, 2f)));
            CameraDistanceOffset  = cf.Bind("Camera", "CameraDistanceOffset", 0f, new ConfigDescription(
                "3rd-person only: how far the camera sits from the plane, on top of the stock zoom-aware distance (measured in orbit-radius units, so it scales with zoom). 0 = stock framing. Positive pulls the camera FARTHER back; negative brings it CLOSER (large negatives can push it in front of the plane). Try +/-0.5 at a time.",
                new AcceptableValueRange<float>(-1.8f, 3f)));
            CameraHeightOffset    = cf.Bind("Camera", "CameraHeightOffset", 0f, new ConfigDescription(
                "3rd-person only: camera height relative to the plane, on top of the stock rise (orbit-radius units). 0 = stock. Positive raises the camera (look down more at the airframe); negative lowers it (look up). Try +/-0.3 at a time.",
                new AcceptableValueRange<float>(-1.5f, 2f)));
            CameraSideOffset      = cf.Bind("Camera", "CameraSideOffset", 0f, new ConfigDescription(
                "3rd-person only: shift the camera left/right of the plane (orbit-radius units), for an over-the-shoulder framing. 0 = centred behind. Positive shifts to the plane's RIGHT, negative to the LEFT. Try +/-0.3 at a time.",
                new AcceptableValueRange<float>(-2f, 2f)));

            // "I broke it, fix it please" — a real button in the F1 (ConfigurationManager) menu that
            // restores every MouseAim setting to its default. The custom drawer below replaces the usual
            // checkbox with the button; the bound bool is just a carrier (its value is never read).
            ResetToDefaults = cf.Bind("ZZZ - Panic Button", "I broke it, fix it please", false, new ConfigDescription(
                "Click to reset ALL of this mod's settings (camera, control law, keybinds, HUD — everything) back to their defaults. Use this if you've tuned yourself into a corner and want a clean slate.",
                null, new ConfigurationManagerAttributes { CustomDrawer = DrawResetButton, HideDefaultButton = true, HideSettingName = true }));
            _file = cf;

            // Config logging (v0.29): dump the full control law ONCE at startup, then log just the
            // changed entry whenever a value is edited live (F1 menu) — so the log always shows what
            // gains produced any [anomaly]/[maneuver] line, without bloating each event line.
            cf.SettingChanged += (_, e) =>
            {
                var s = e.ChangedSetting;
                WTMouseAimPlugin.Log.LogInfo($"[config] {s.Definition.Section}/{s.Definition.Key} = {s.BoxedValue}");
                // Mirror the change into any active recording so a mid-run tuning edit is inline with
                // the data (no-op when not recording). Lets the CSV alone explain a feel change.
                ManeuverRecorder.NoteConfigChange(s.Definition.Section, s.Definition.Key, s.BoxedValue);
            };
            LogSnapshot();
        }

        // One compact line with every control-law knob — the single source of truth for the gain dump,
        // reused by the startup/reset log line (LogSnapshot) AND the maneuver-recorder CSV header so a
        // recording is self-describing without cross-referencing the BepInEx log. Includes the active law.
        public static string SnapshotString()
        {
            return
                $"law={ControlLawMode.Value} sens={PitchYawSensitivity.Value:0.0} chaseDamp={ChaseDamping.Value:0.00} " +
                $"pitchG={PitchGain.Value:0.0} yawG={YawGain.Value:0.0} rollG={RollGain.Value:0.00} rollDamp={RollDamping.Value:0.00} rollSm={RollRateSmoothing.Value:0.00} " +
                $"bankGain={FineBankGain.Value:0.0} bankDz={FineBankDeadzone.Value:0.0} maxBank={MaxBankAngle.Value:0} " +
                $"fineAng={FineAngle.Value:0} fineBoost={FineGainBoost.Value:0.0} align={AlignAngle.Value:0} " +
                $"coord={RollPitchCoordination.Value:0.00} brake={PitchBrake.Value:0.00} yawSc={TurnYawScale.Value:0.00} slew={OutputSlew.Value:0.0} " +
                $"iGain={FineIntegralGain.Value:0.00} iLeak={FineIntegralLeak.Value:0.00} iCap={FineIntegralCap.Value:0.00} " +
                $"yawAssist={(YawAssistEnabled.Value ? 1 : 0)} yaStr={YawAssistStrength.Value:0.00} yaResp={YawAssistResponse.Value:0.00} " +
                $"coordPull={CoordPullGain.Value:0.00} coordCap={CoordPullCap.Value:0.00} bankAuth={BankAuthGain.Value:0.0} yawFade={YawWeakFade.Value:0.00} " +
                $"trGain={AssistTurnRateGain.Value:0.00} pullRel={CoordPullReleaseAngle.Value:0.0} alignHold={EvolvedAlignHoldDeg.Value:0.0} " +
                $"leadT={TurnLeadTime.Value:0.00} aOffP={AssistOffPitchScale.Value:0.00} bankSlew={BankSlewRate.Value:0} " +
                $"heliFwd={HeliForwardSpeed.Value:0} heliHover={HeliHoverSpeed.Value:0} heliYawSc={HeliYawScale.Value:0.00}";
        }

        // Emit the gain dump to the BepInEx log at startup/reset so the log is self-describing for tuning.
        // Live edits are logged per-entry via SettingChanged above.
        public static void LogSnapshot()
        {
            WTMouseAimPlugin.Log.LogInfo($"[config {SnapshotString()}]");
        }

        // Custom F1-menu widget for ResetToDefaults: a single button instead of a checkbox.
        private static void DrawResetButton(ConfigEntryBase _)
        {
            if (GUILayout.Button(new GUIContent("I broke it, fix it please",
                    "Reset every MouseAim setting back to its default."), GUILayout.ExpandWidth(true)))
                ResetAllToDefaults();
        }

        // Walk every bound entry and restore its default. Setting BoxedValue fires SettingChanged, so
        // each restored knob is logged via the hook in Bind(); we add one summary line + a fresh snapshot.
        private static void ResetAllToDefaults()
        {
            if (_file == null) return;
            var defs = new System.Collections.Generic.List<ConfigDefinition>(_file.Keys);
            foreach (var def in defs)
            {
                var entry = _file[def];
                if (entry == null || entry == ResetToDefaults) continue; // skip the panic button itself
                if (!Equals(entry.BoxedValue, entry.DefaultValue))
                    entry.BoxedValue = entry.DefaultValue;
            }
            WTMouseAimPlugin.Log.LogInfo("[config] ALL settings reset to defaults ('I broke it, fix it please').");
            LogSnapshot();
        }
    }

    // Minimal stand-in for BepInEx.ConfigurationManager's attributes class. ConfigurationManager finds
    // it in a ConfigDescription's tags by TYPE NAME via reflection (no hard assembly reference needed),
    // then reads these fields — so a local copy carrying just the bits we use drives the in-menu button.
    internal sealed class ConfigurationManagerAttributes
    {
        public System.Action<ConfigEntryBase> CustomDrawer;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
    }
}
