using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>Which wheels receive drive torque.</summary>
    public enum DrivenAxle
    {
        Front = 0,
        Rear = 1,
        All = 2,
    }

    /// <summary>
    /// How the cylinders are arranged around the crankshaft, which is the whole of why one engine
    /// rumbles and another screams.
    /// </summary>
    public enum FiringLayout
    {
        /// <summary>One pipe, cylinders evenly spaced over the 720° cycle. Smooth by construction.</summary>
        Inline = 0,

        /// <summary>
        /// Two pipes. Evenly spaced at 90° overall, but each <i>bank</i> fires at 90/180/270/180 — and
        /// that unevenness, beating against the other bank, is the American V8 burble. Nothing else in
        /// this file produces it and no parameter asks for it.
        /// </summary>
        CrossPlaneV8 = 1,

        /// <summary>Two pipes, each evenly spaced at 180°. A racing V8: it screams rather than rumbles.</summary>
        FlatPlaneV8 = 2,
    }

    /// <summary>
    /// Every tunable of the handling model. Lives as an asset so a new vehicle is a new asset
    /// rather than new code, and so it can be edited during Play mode — that is the tuning loop.
    /// </summary>
    [CreateAssetMenu(menuName = "Horizon/Vehicle Config", fileName = "VehicleConfig")]
    public sealed class VehicleConfig : ScriptableObject
    {
        /// <summary>
        /// Bumped whenever a field in here changes <i>meaning</i> rather than merely value. An asset
        /// stamped below this is stale and gets rewritten from the code defaults — see
        /// <c>VehicleConfigReset</c>.
        ///
        /// <para><b>6: the exhaust resonances were wrong and the engine had no bottom end.</b>
        /// <c>ExhaustRing</c> was a damping in samples, which meant the pipe resonance died after
        /// <i>0.36 of one of its own cycles</i> — it never oscillated at all, so what came out was a
        /// train of clicks with 80% silence between them at idle. It is now a Q in cycles, and there is
        /// a fourth resonance on the firing order plus <see cref="ExhaustBoom"/> to weigh it. Same
        /// field name, completely different number.</para>
        ///
        /// <para><b>5: the engine became firing pulses instead of a harmonic stack.</b>
        /// <c>HalfOrderLevel</c> and <c>LopeDepth</c> are gone — they were hand-dialled stand-ins for a
        /// rumble that now falls out of <see cref="Layout"/> and <see cref="Cylinders"/> on its own, and
        /// <c>HarmonicRolloff</c> gave way to the exhaust's own resonances. Every one of those is a
        /// field whose meaning did not merely change but ceased, which is this counter's own
        /// definition.</para>
        ///
        /// <para><b>4: the engine got a voice and a turbocharger.</b> Strictly the existing fields all
        /// kept their meanings and only new ones arrived — but a new field lands in an existing asset as
        /// its <i>initialiser</i>, and the initialisers here are the fastback's. Every car would have
        /// come out naturally aspirated with a V8's pitch range, silently, because the value is present,
        /// in range and of the right type. That is the exact failure this counter exists for, so it
        /// moved. Checked before bumping: all ten assets held values identical to their code presets, so
        /// nothing hand-tuned was thrown away.</para>
        ///
        /// <para><b>5 and 6</b> are recorded in the git history rather than here; both were ordinary
        /// retunings of fields that already existed.</para>
        ///
        /// <para><b>7: the cars stopped sharing a wheel.</b> <see cref="WheelRadius"/> and
        /// <see cref="SuspensionRestLength"/> were the same on all ten and are now the shape's own,
        /// which moves <see cref="FinalDrive"/> on six of them to keep the gearing where it was tuned.
        /// Without the bump the assets keep the old radius against a body lofted around the new one, and
        /// the car sits with its wheels through its arches.</para>
        ///
        /// <para><b>21: every car got grip, and the throttle stopped being a drift request.</b> The
        /// spread was 1.47 to 3.14 and is 2.20 to 3.00 — a van that pulls 1.5 g next to a coupé that
        /// pulls 3 is not two characters, it is one good car and one bad one. Centres of mass and
        /// anti-roll bars move with it, and <see cref="PowerOversteer"/> goes to zero everywhere
        /// because a keyboard's throttle is 1 at all times and a signal that is always present is not
        /// a request.</para>
        ///
        /// <para><b>20: the turn-in assist became the stability controller.</b> A value change, and
        /// the assets hold the old one.</para>
        ///
        /// <para><b>19: the steering limit stopped being a curve.</b> <c>SteeringBySpeed</c> is gone
        /// and <see cref="SteeringMargin"/> replaces it — a field removed and a field added, so an
        /// asset left at 18 carries a dead curve and a margin of zero, which is a car that cannot
        /// steer at all above walking pace.</para>
        ///
        /// <para><b>18: sliding became something the driver asks for.</b> <c>HandbrakeGrip</c> is
        /// <see cref="DriftRearGrip"/> now — same number, same job, but reached by three entries
        /// instead of one — and <see cref="PowerOversteer"/> is new and would land in every asset as
        /// zero, which is the entry silently missing on all ten cars.</para>
        ///
        /// <para><b>17: the grip went to arcade levels.</b> <see cref="LateralGrip"/> is nearly
        /// trebled, <see cref="Downforce"/> halved again, and the steering curve opened back up
        /// because the usable angle rose with the grip. Values, not meanings — and the assets hold
        /// the old ones.</para>
        ///
        /// <para><b>16: the cars were given somewhere to put 2.8 g.</b> Every centre of mass moved
        /// down and every anti-roll bar moved with it. A value change, and the assets hold the old
        /// numbers — which in this case is not a matter of taste but of whether the car stays on its
        /// wheels.</para>
        ///
        /// <para><b>15: the cars were given a sporting character rather than a safe one.</b>
        /// <see cref="RearGripBias"/> is new and would otherwise land in every asset as 0 — no rear
        /// grip at all — which is the plainest case this counter exists for.
        /// <see cref="PeakSlipAngle"/>, <see cref="Downforce"/>, <see cref="TurnInAssist"/> and
        /// <c>SteeringBySpeed</c> all move with it, and they move as a set: each one alone is
        /// either useless or dangerous.</para>
        ///
        /// <para><b>14: <c>SteeringBySpeed</c> was rebuilt against the geometry.</b> Same
        /// reason as 13 — a value change whose whole point is that the assets hold the old one — but a
        /// much larger one, and it is the fix for a car that broke away on a small steering input at
        /// speed.</para>
        ///
        /// <para><b>13: <see cref="TurnInAssist"/> was halved.</b> A value change rather than a
        /// meaning change, which normally would not move this counter — but the number in every asset
        /// is the old one and the whole point of the change is that it should not be.</para>
        ///
        /// <para><b>12: traction control arrived, and a bool is the worst kind of new field.</b> An
        /// asset written at 11 carries no key for <see cref="TractionControl"/>, so it deserialises as
        /// <c>false</c> — not as the <c>true</c> in the source. Every car would have come out with the
        /// assist switched off, silently, and the only symptom would have been the powerful rear-drive
        /// cars sitting in their own smoke. A new field whose initialiser is universal is normally the
        /// safe case; a bool is the exception, because its "missing" value is also a legal one.</para>
        ///
        /// <para><b>11: the tyre became a tyre, and <see cref="LateralGrip"/> changed its axis.</b>
        /// That curve was looked up on road speed and is now looked up on wheel load, which is a
        /// different quantity with a different range — an asset carrying the old keys reads its whole
        /// curve off the first tenth of the new axis and comes out on ice above walking pace. It is
        /// the third time this exact field has moved underneath its own values, and the second time
        /// the meaning rather than the number changed; the first cost two rounds of handling
        /// complaints on tyres nobody had chosen. Every other field arriving here is new and would
        /// otherwise land in an existing asset carrying the fastback's initialiser, which is the
        /// failure this counter exists for.</para>
        ///
        /// <para><b>8: the cars stopped sitting on their bump stops.</b> Every road body gained three to
        /// four centimetres of <see cref="SuspensionRestLength"/> so there is daylight over the tyre and
        /// ground clearance under the sill, and <see cref="AntiRollStiffness"/> moved with it on all nine
        /// — the bar is normalised by the travel, so a longer spring silently softens it. Without the
        /// bump the assets keep the short travel and the soft bar together, which is the one combination
        /// that rolls.</para>
        /// </summary>
        public const int CurrentVersion = 21;

        /// <summary>
        /// Which set of meanings this asset's numbers were chosen under.
        ///
        /// <para>Deliberately <b>without</b> an initialiser, and that is the whole trick. An asset
        /// written before this field existed carries no key for it and therefore reads 0, which is
        /// below <see cref="CurrentVersion"/> and marks it stale — exactly the assets that need
        /// rewriting. An initialiser here would stamp every one of them as current and defeat the
        /// mechanism at the only moment it matters.</para>
        /// </summary>
        [HideInInspector] public int Version;

        [Header("Body")]
        public float Mass = 1250f;

        [Tooltip("Local centre of mass. Keep it low — this is the main defence against rolling over, "
               + "and with an arcade car it is a hard constraint rather than a preference.\n\n"
               + "A car tips when its lateral acceleration passes track / (2 × centre-of-mass height). "
               + "The track is 1.98 m and the wheel anchors sit at this transform's own height, so that "
               + "height is SuspensionRestLength + WheelRadius − static sag + this number. At −0.30 "
               + "that put the fastback's tipping point at 2.4 g — below the 2.7 g of grip it is now "
               + "asked to have, which is a car that lifts its inside wheels in the first fast corner "
               + "and falls over. Every road car here is set so the tipping point sits about 20 % above "
               + "its own grip.\n\n"
               + "The van and the offroader are deliberately left high. That is their character, and "
               + "the price of it is that they are the two vehicles which do not get arcade grip.")]
        public Vector3 CenterOfMass = new Vector3(0f, -0.42f, 0.05f);

        /// <summary>
        /// Rigidbody linear damping, and it is <b>zero on purpose</b>.
        ///
        /// <para>This car's resistance is meant to be two terms and is documented as two:
        /// <see cref="RollingResistanceN"/>, constant, which is what a tyre actually does, and
        /// <see cref="AeroDrag"/>, proportional to the square of speed, which is what air does. Unity's
        /// damping is a third — proportional to speed itself — and it is neither of those.</para>
        ///
        /// <para><b>At 0.06 it was not a detail, it was the dominant force.</b> It scales with mass, so
        /// at 50 m/s it was pulling three to five times as hard as the aerodynamic drag beside it, and
        /// every car in the fleet topped out far below what its gearing allowed: the coupé at 161 km/h
        /// against a geared 263, the liftback at 150 against 269. The note beside the aero force in
        /// <c>VehicleController</c> records that applying drag four times over once capped the car at
        /// 45 km/h; this was a fifth application that nobody had noticed.</para>
        ///
        /// <para><b>And it quietly disabled half of every speed-dependent curve.</b>
        /// <see cref="TopSpeed"/> is the redline in top gear, and <c>SpeedNormalized</c> divides by it to
        /// drive the speed-dependent curves of the day. A car that could only reach
        /// 61 % of its own top speed never read past 0.61 of either curve, so the fast end of both was
        /// authored, tuned and never once evaluated.</para>
        /// </summary>
        public float LinearDamping = 0f;

        [Tooltip("Rigidbody angular damping. Deliberately almost nothing — see RollDamping.\n\n"
               + "This was 1.2, and it is why the steering felt vague no matter how much lock was wound "
               + "on. Rigidbody damping cannot tell yaw from roll, so the figure that stopped a tall car "
               + "wallowing was also being applied to the axis the driver steers with. Holding a steady "
               + "corner at 1.2 rad/s took roughly 2600 Nm of yaw torque from the damper alone, which the "
               + "front tyres had to find on top of turning the car — a permanent bias toward understeer "
               + "that no amount of steering angle could answer.")]
        public float AngularDamping = 0.05f;

        [Tooltip("Damping applied to roll only, in rad/s per rad/s — a time constant of about 1/this.\n\n"
               + "Higher than the 1.2 it replaces, so the body is *steadier* in roll than before, while "
               + "yaw is left to the tyres and DriftYawDamping. Turning it down makes the car livelier "
               + "over crests and closer to flipping on a hairpin, which is the failure this and the "
               + "anti-roll bars exist to prevent.")]
        public float RollDamping = 2.5f;

        [Tooltip("Damping applied to pitch only, in rad/s per rad/s. Deliberately much lower than "
               + "RollDamping.\n\n"
               + "These used to be one number, and that was a mistake worth spelling out. The figure "
               + "exists to stop the car flipping, which is a *roll* problem — but applied to pitch it "
               + "also erased the squat and dive the tyre forces generate at the contact patch. That "
               + "movement is about a degree at a full-throttle launch, and it is most of how "
               + "acceleration is communicated to the player: with it damped away, the car gained speed "
               + "without ever looking like it was trying. Roll stability is unaffected by this number, "
               + "so it can be tuned on feel alone — down until the nose visibly takes a set, and no "
               + "further, because too little makes the car porpoise over crests.")]
        public float PitchDamping = 1f;

        [Header("Suspension")]
        /// <summary>
        /// Rolling radius. **Coupled to <see cref="FinalDrive"/>** — read the note there before changing
        /// it, because the radius is not only a visual dimension.
        ///
        /// <para><b>Written from the body, not tuned here.</b> <c>VehicleConfigPresets</c> copies this
        /// and <see cref="SuspensionRestLength"/> out of the car's <c>CarMeshBuilder.CarProfile</c>,
        /// because the mesh is lofted around them: the wheel arches are cut at a height that is
        /// arithmetic off exactly these two, and so is the ground plane the silhouette is measured
        /// against. Edit them on the profile and re-run the presets; editing them on the asset gives a
        /// car whose wheels no longer fit its own bodywork.</para>
        /// </summary>
        public float WheelRadius = 0.44f;

        [Tooltip("Suspension travel in metres, and with the wheel radius the car's ride height.\n\n"
               + "0.30 is the fastback's: static compression is only 7 cm, so 30 cm of travel is ample. "
               + "Like the radius this is the body's number rather than a tuning value — see the note on "
               + "WheelRadius.")]
        public float SuspensionRestLength = 0.34f;

        [Tooltip("Spring rate in N per metre of compression.")]
        public float SuspensionStiffness = 42000f;

        [Tooltip("Damper rate in N per m/s of compression velocity.")]
        public float SuspensionDamping = 3800f;

        [Tooltip("Resists body roll by transferring load across an axle. Without this the car "
               + "flips on the first hairpin.\n\n"
               + "Works on compression as a fraction of the travel, so it has to be rescaled whenever "
               + "SuspensionRestLength moves — see the note on VehicleConfigPresets. 15900 is 14000 "
               + "against the 0.30 m of travel this car used to have.")]
        public float AntiRollStiffness = 18100f;

        [Header("Drivetrain")]
        [Tooltip("Which wheels get drive.\n\n"
               + "Rear, and it is the single largest number in this file for how the car feels. Under "
               + "the friction circle a tyre shares one grip budget between driving and cornering, so "
               + "on all-wheel drive the throttle takes a quarter of the budget at each of four tyres "
               + "and the back never steps out; on rear drive it takes half at two, and the car will "
               + "oversteer on power the way the body it is wearing should. Set it back to All and the "
               + "handling goes quiet and safe again — useful for telling the model apart from the "
               + "layout.")]
        public DrivenAxle DrivenAxle = DrivenAxle.Rear;

        [Header("Engine")]
        [Tooltip("Peak crankshaft torque in newton-metres. A big lazy V8 makes its torque low down.")]
        public float MaxTorqueNm = 570f;

        [Tooltip("Torque as a fraction of peak, over rpm as a fraction of the redline. The shape of "
               + "this curve is the engine's character: this one peaks early and fades at the top.")]
        public AnimationCurve TorqueByRpm = new AnimationCurve(
            new Keyframe(0f, 0.55f),
            new Keyframe(0.18f, 0.82f),
            new Keyframe(0.42f, 1f),
            new Keyframe(0.70f, 0.95f),
            new Keyframe(0.90f, 0.78f),
            new Keyframe(1f, 0.62f));

        public float IdleRpm = 750f;

        public float RedlineRpm = 5800f;

        /// <summary>
        /// How much fuel the car carries, litres.
        ///
        /// <para><b>The only part of the fuel model that is per-car data.</b> Everything else
        /// <see cref="FuelTank"/> needs it already derives from fields that are here for the physics —
        /// the torque curve and the peak give it the work being done, and the burn falls out of that. A
        /// tank size does not: a van's is bigger than a hatchback's because it is a bigger vehicle, not
        /// because of anything else on this asset, so it is the one number that has to be written
        /// down.</para>
        ///
        /// <para>55 is an ordinary saloon's tank, and the fastback is the identity case every other
        /// body is a delta from — see <c>VehicleConfigPresets</c>.</para>
        /// </summary>
        public float FuelCapacityLitres = 55f;

        [Header("Engine voice")]

        /// <summary>
        /// How many times the engine fires per second at a playback pitch of 1. The clip is one second
        /// long, so this is also the number of firing pulses in it.
        ///
        /// <para><b>The loop constraint, which is arithmetic and not taste.</b> One engine cycle is two
        /// crank revolutions and contains <see cref="Cylinders"/> firings, so the cycle rate is
        /// <c>EngineFundamentalHz / (Cylinders / 2)</c>. For the clip to close, that rate must be a whole
        /// number <i>and</i> must divide the 44100 sample rate exactly — otherwise one cycle is a
        /// fractional number of samples and the seam ticks once a second, which is the failure CLAUDE.md
        /// warns about. <see cref="LoopsCleanly"/> checks it and the config reset logs anything that
        /// fails, because the symptom is quiet enough to ship.</para>
        /// </summary>
        public float EngineFundamentalHz = 48f;

        /// <summary>
        /// How many cylinders fire per cycle. Sets the pulse spacing together with <see cref="Layout"/>,
        /// and therefore how much of the noise is firing order and how much is rumble.
        /// </summary>
        [Range(2, 12)] public int Cylinders = 8;

        public FiringLayout Layout = FiringLayout.CrossPlaneV8;

        /// <summary>
        /// The exhaust's main resonance, in hertz — the pipe's own note, which every firing pulse rings.
        /// Low and long for a big silenced V8, higher and shorter for a small engine on a straight pipe.
        /// A second resonance sits at 2.5× this and does the mid honk.
        /// </summary>
        public float ExhaustPitchHz = 92f;

        /// <summary>
        /// The top resonance, where the hard edge and — on a diesel — the clatter lives. Explicit rather
        /// than a ratio of <see cref="ExhaustPitchHz"/> because that is exactly what separates a diesel
        /// from a petrol engine: both boom low, only one rattles at a kilohertz.
        /// </summary>
        public float ExhaustClatterHz = 600f;

        /// <summary>
        /// How long the pipe rings after each pulse, 0 to 1 — mapped to a Q of 2.5 to 10 <b>cycles of
        /// the resonance's own frequency</b>. High is boomy and joined-up; low is dry and separated, so
        /// you hear individual firings rather than a note.
        ///
        /// <para><b>Cycles, not milliseconds, and that distinction was a shipped bug.</b> This was once
        /// a fixed damping per sample, which sounds equivalent and is not: at 92 Hz it left the pipe
        /// ringing for 4 ms against a 10.9 ms period, so the resonance died a third of the way through
        /// its first swing. A resonator that cannot complete one oscillation is not a resonator, it is a
        /// click — and the engine correspondingly had no note at all, only a rattle with silence between
        /// the firings. Anything expressed here has to be relative to the frequency it applies to.</para>
        /// </summary>
        [Range(0f, 1f)] public float ExhaustRing = 0.6f;

        /// <summary>
        /// Weight of the resonance sitting on the firing order itself — the rumble, and the reason the
        /// engine has a bottom end.
        ///
        /// <para>An exhaust's fundamental <i>is</i> its firing rate; <see cref="ExhaustPitchHz"/> is the
        /// pipe's own colour on top of that. Tuning only the pipe and leaving the fundamental to chance
        /// is how this file ended up with 2.6% of its energy below 80 Hz where the model it replaced had
        /// 97% — measurably, and audibly, an engine with no engine in it.</para>
        /// </summary>
        [Range(0f, 2f)] public float ExhaustBoom = 0.8f;

        /// <summary>
        /// How much of the sound is the upper resonances rather than the low one, 0 to 1. This is the
        /// old <c>HarmonicRolloff</c>'s job done by the thing that actually does it in a real exhaust.
        /// </summary>
        [Range(0f, 1f)] public float ExhaustRasp = 0.2f;

        /// <summary>Roughness riding on each combustion pulse. Diesels are the noisy end of this.</summary>
        [Range(0f, 1f)] public float CombustionNoise = 0.45f;

        /// <summary>
        /// Soft-clip amount. Saturation, and where a loaded engine's growl comes from.
        ///
        /// <para>High, and it has to be. A pulse train is peaky, so at equal peak level it is far
        /// quieter than the saturated sine stack this model replaced — measured, 3 dB quieter, which is
        /// most of why the engine read as missing. Squashing it back up costs less than it sounds like
        /// it should: from drive 1.6 to 5.0 the RMS goes 0.46 to 0.57 while the octave balance moves by
        /// under a percent, so the firing pulses survive the compression intact.</para>
        /// </summary>
        public float ExhaustDrive = 3.1f;

        /// <summary>
        /// Level of the continuous exhaust layer, which is the same firing pulses through the low
        /// resonances only and nothing else.
        ///
        /// <para>It exists separately because an engine and its exhaust are two sounds in two places: one
        /// in front of the driver and one behind them, one with the intake and the mechanical noise in it
        /// and one that is only pipe. Folding them into a single layer is what made every car here sound
        /// like a speaker playing an engine rather than like a car.</para>
        /// </summary>
        [Range(0f, 1f)] public float ExhaustLevel = 0.55f;

        /// <summary>
        /// Playback pitch of the drone at idle, and at the redline.
        ///
        /// <para>These lived on <c>EngineAudio</c> as component fields, which meant one pitch range for
        /// every car in the game. That is wrong twice over: an engine that spins to 8000 sweeps a far
        /// wider range than a diesel that gives up at 4800, and the top of the sweep is most of what
        /// "revs" sounds like. A car whose redline is 38% higher than another's and sounds identical at
        /// it has no redline.</para>
        /// </summary>
        public float IdlePitch = 0.46f;

        public float RedlinePitch = 1.50f;

        /// <summary>Engine cycles per second at a playback pitch of 1 — the clip's own repeat rate.</summary>
        public float CycleHz => EngineFundamentalHz / Mathf.Max(1f, Cylinders * 0.5f);

        /// <summary>
        /// Whether the generated clip closes without a tick. See <see cref="EngineFundamentalHz"/> for
        /// what has to hold and why nothing at runtime can paper over it.
        /// </summary>
        public bool LoopsCleanly
        {
            get
            {
                float cycles = CycleHz;
                int rounded = Mathf.RoundToInt(cycles);
                return rounded > 0
                       && Mathf.Abs(cycles - rounded) < 0.0001f
                       && 44100 % rounded == 0;
            }
        }

        [Header("Forced induction")]

        /// <summary>
        /// Level of the turbo's whistle, 0 to 1. <b>Zero means naturally aspirated</b> and switches the
        /// whole layer off, which is what every car written before this field had.
        ///
        /// <para><b>Why this is not the wind layer this project deleted.</b> That one rose with the
        /// square of road speed, so every acceleration came with a whoosh over the engine, and there was
        /// no car and no moment where it was not there. This one is driven by <i>boost</i> — exhaust
        /// energy above <see cref="TurboSpoolRevs"/> multiplied by throttle — so it exists only on the
        /// cars that have a turbocharger, only when the driver is asking for something, and it collapses
        /// the instant they lift. It is also narrow-band and sits an octave above the drone's harmonics
        /// rather than across them. The level is applied squared, for the reason the tyre squeal is: a
        /// linear ramp gives a car that whistles gently everywhere, which is wallpaper.</para>
        /// </summary>
        [Range(0f, 1f)] public float TurboWhistle;

        /// <summary>
        /// Fraction of the redline below which there is not enough exhaust to spin the turbine. Small
        /// turbos come in early and big ones late — this is the number that decides whether the car has
        /// a hole at the bottom of the rev range you can hear as well as feel.
        /// </summary>
        [Range(0f, 0.8f)] public float TurboSpoolRevs = 0.30f;

        /// <summary>
        /// How fast boost builds, per second. Collapse is four times this — a turbo takes a moment to
        /// spool and no time at all to stop, and getting that asymmetry wrong is what makes synthesised
        /// boost sound like a volume envelope.
        /// </summary>
        public float TurboSpoolRate = 2.4f;

        /// <summary>
        /// Playback pitch of the whistle at full boost; it starts an octave below that and rises. Small
        /// twin turbos sing high, one big compressor sits lower and louder.
        /// </summary>
        public float TurboNotePitch = 1f;

        /// <summary>
        /// Level of the dump valve, 0 to 1, fired when the throttle shuts with boost still in the pipes.
        /// Zero for anything without a throttle plate to shut — which is every diesel here, however
        /// turbocharged.
        /// </summary>
        [Range(0f, 1f)] public float BlowOffLevel;

        /// <summary>
        /// Whether this engine is blown at all — <b>the one test</b>, so a gauge and a whistle cannot
        /// disagree about what car this is.
        ///
        /// <para>Not <see cref="TurboWhistle"/> above zero on its own. The Van and the Offroader are
        /// turbocharged diesels with no throttle plate to shut, so they carry a whistle and no valve;
        /// a car with a valve and no whistle would be as legitimate. <c>EngineAudio.UpdateTurbo</c>
        /// had this written inline and <c>BoostGauge</c> would have been a second copy of it — which
        /// is how six cars quietly lose an instrument, or four quietly gain one.</para>
        /// </summary>
        public bool IsTurbocharged => TurboWhistle > 0.001f || BlowOffLevel > 0.001f;

        [Header("Gearbox")]
        [Tooltip("Forward gear ratios, first to top. Top speed comes out of the last one — it is not "
               + "set directly anywhere.")]
        /// <summary>
        /// Six forward ratios, closely spaced, ending at exactly 1.00.
        ///
        /// <para><b>Why six and not four.</b> The old ladder was {2.78, 1.93, 1.36, 1.00}, and the
        /// number that condemns it is first gear: it ran to <b>78.8 km/h</b>. Town and country driving
        /// lives entirely under that, so across the whole speed range the game is actually played in,
        /// the gearbox never changed gear once. Every shift is a hole in the drive — the one honest
        /// event the drivetrain produces — and there were none of them where the player was.</para>
        ///
        /// <para>These end first at 52 km/h and put five shifts under 171 km/h. The steps are 1.45 then
        /// 1.35 down to 1.28, wide at the bottom and closing at the top, which is how a real box is cut:
        /// first has to launch the car, the upper gears only have to keep the engine in its band.</para>
        ///
        /// <para><b>The last ratio must stay 1.00.</b> <see cref="TopSpeed"/> is computed from it, and
        /// TopSpeed is the divisor for <c>SpeedNormalized</c>, which everything speed-shaped is read
        /// against. That used to include the steering limit and the grip curve, and both have since
        /// moved off it — the steering is derived from the car's own grip and the grip from wheel load
        /// — so shortening top gear no longer quietly retunes the handling of every car in the game.
        /// It still moves the camera, the atmosphere and the audio.</para>
        /// </summary>
        public float[] GearRatios = { 4.20f, 2.90f, 2.15f, 1.65f, 1.28f, 1f };

        public float ReverseRatio = 2.90f;

        /// <summary>
        /// Axle ratio — and the counterweight to <see cref="WheelRadius"/>.
        ///
        /// The radius enters three separate formulas: top speed scales with it, tractive force scales
        /// with its inverse, and engine rpm for a given road speed scales with its inverse. All three
        /// cancel exactly if this ratio is scaled by the same factor, which is why the two numbers move
        /// together. 4.09 is 3.31 × 0.42/0.34, from the wheels growing from 0.34 m to 0.42 m — so that
        /// change was purely visual and acceleration, top speed (~225 km/h) and the shift points are
        /// arithmetically unchanged.
        ///
        /// Change the radius on its own and you silently retune the whole car.
        /// </summary>
        public float FinalDrive = 4.09f;

        [Range(0.5f, 1f)] public float DrivetrainEfficiency = 0.9f;

        [Tooltip("Upshift above this engine speed at full throttle.")]
        public float UpshiftRpm = 5400f;

        [Tooltip("Downshift below this engine speed at full throttle. Must stay well under UpshiftRpm "
               + "divided by the ratio step, or the box hunts between two gears.")]
        public float DownshiftRpm = 2100f;

        [Tooltip("Upshift above this engine speed with the throttle closed; the real threshold is "
               + "interpolated between the two on pedal travel.\n\n"
               + "Without this the shift points are the full-throttle ones at every pedal position, "
               + "which with six gears is not a detail — it is the difference between cruising through "
               + "town in third at 2600 rpm and screaming through it in first at 5200. A driver lifting "
               + "off short-shifts, and so should the box.")]
        public float PartThrottleUpshiftRpm = 2800f;

        [Tooltip("Downshift below this engine speed with the throttle closed. Must clear "
               + "PartThrottleUpshiftRpm divided by the widest ratio step, or the box hunts on a "
               + "trailing throttle — which is exactly where it would be least forgivable.")]
        public float PartThrottleDownshiftRpm = 1400f;

        [Tooltip("Seconds of torque interruption per shift. This gap is the shift you actually feel.")]
        public float ShiftTime = 0.35f;

        [Header("Braking and drag")]
        [Tooltip("Total braking force in newtons.")]
        public float BrakeForce = 16000f;

        [Tooltip("Top speed in reverse, m/s.")]
        public float ReverseSpeed = 8f;

        [Tooltip("Constant rolling resistance per wheel, newtons. Roughly 1.5% of the weight on it — "
               + "and constant, not proportional to speed, which is what tyres actually do.")]
        public float RollingResistanceN = 46f;

        [Tooltip("Aerodynamic drag in newtons per (m/s)². Applied once to the body, not per wheel. "
               + "0.45 gives about 1.7 kN at 220 km/h.")]
        public float AeroDrag = 0.45f;

        /// <summary>
        /// Top speed the drivetrain can actually reach: the redline in top gear. Everything that wants
        /// a normalized speed uses this, so there is one source of truth rather than a number someone
        /// typed in that the car could never achieve.
        /// </summary>
        public float TopSpeed
        {
            get
            {
                float topRatio = GearRatios != null && GearRatios.Length > 0
                    ? GearRatios[GearRatios.Length - 1]
                    : 1f;

                float driveRatio = topRatio * FinalDrive;
                if (driveRatio < 0.01f)
                {
                    return 1f;
                }

                return RedlineRpm / 60f * 2f * Mathf.PI * WheelRadius / driveRatio;
            }
        }

        /// <summary>Gear ratio for a 0-based forward gear index, or reverse when negative.</summary>
        public float RatioForGear(int gearIndex)
        {
            if (gearIndex < 0)
            {
                return -ReverseRatio;
            }

            if (GearRatios == null || GearRatios.Length == 0)
            {
                return 1f;
            }

            return GearRatios[Mathf.Clamp(gearIndex, 0, GearRatios.Length - 1)];
        }

        /// <summary>Number of forward gears.</summary>
        public int ForwardGearCount => GearRatios != null ? GearRatios.Length : 1;

        [Header("Steering")]
        [Tooltip("Steering angle at full lock, degrees.\n\n"
               + "40° against a 2.70 m wheelbase is a 3.2 m turning radius — a hairpin or a U-turn taken "
               + "in one go rather than in three.\n\n"
               + "Worth knowing where it stops mattering: with LateralGrip as it stands, the friction "
               + "circle becomes the tighter of the two limits at about 27 km/h, and above that the car "
               + "runs out of grip long before it runs out of lock. So this number buys geometry at "
               + "manoeuvring speed and, above it, only the authority to reach the limit whenever the "
               + "driver asks — which is most of what 'direct' means on a phone.")]
        public float MaxSteerAngle = 40f;

        [Tooltip("How much of the grip full lock is allowed to ask for, 0 to 1.\n\n"
               + "There used to be a hand-drawn SteeringBySpeed curve here, and it was the wrong shape "
               + "by construction. A car can only sustain the yaw rate its grip pays for, so the "
               + "steering angle it can actually use is atan(a · wheelbase / v²) — which collapses "
               + "with the *square* of speed. The curve was handing out between 1.5 and 2.1 times that "
               + "everywhere above 90 km/h, and on a keyboard, where every input reaches its stop in a "
               + "sixth of a second, that is a request for nearly double what the car has arriving in "
               + "one flick.\n\n"
               + "The limit is derived from the grip the four tyres were actually handed, so it is "
               + "right for every car without a curve each, and stays right after any retune. This is "
               + "the only knob left, and it says how close to the edge full lock puts you.\n\n"
               + "0.92 rather than 1: exactly at the limit the tyre sits on the peak of its own curve "
               + "with the falloff on the far side, which is a knife edge to ask a driver to balance "
               + "on. Lower is safer and duller; above about 0.97 the car starts letting go on its own "
               + "again, which is the fault this replaced.")]
        [Range(0.5f, 1f)] public float SteeringMargin = 0.92f;

        [Tooltip("Degrees per second the steering angle can change.\n\n"
               + "300 takes full lock in about 0.13 s. At 160 the rack itself was a fifth of a second "
               + "behind the thumb, which reads as the car responding late rather than as slow steering — "
               + "the other half of what felt vague.")]
        public float SteerRate = 300f;

        [Header("Grip")]
        [Tooltip("Grip coefficient over wheel load — how much force a tyre can put down, as a multiple "
               + "of the load on it. The lookup key is this wheel's load divided by a quarter of the "
               + "car's weight, so 1 is the static figure, 2 is a wheel carrying twice its share and 0 "
               + "is one that has gone light.\n\n"
               + "The key used to be road speed, which made mu constant in load and therefore made "
               + "weight transfer free: an axle's total capacity did not move when load shifted across "
               + "it, so anti-roll bars, centre-of-mass height and downforce could not change the "
               + "balance of the car at all. A real tyre gives back less grip per newton the harder it "
               + "is pressed, and that one fact is what makes all three of those into tuning levers.\n\n"
               + "The fall with speed the old curve carried is not lost, it has moved to where it "
               + "belongs: Downforce presses the tyres harder as the car goes faster, and this curve "
               + "then charges for it.\n\n"
               + "1.7 at the static load is a grippy road tyre. These are not road tyres: this is an "
               + "arcade game, and the corners of this world say what the number has to be. A 20 m "
               + "hairpin at 85 km/h and a 70 m corner at 159 km/h both work out at about 2.8 g, so "
               + "that is what the fast cars are given and the rest are spread below it.\n\n"
               + "It cannot simply be raised, and the reason is in CenterOfMass: a car tips over at "
               + "track / (2 × centre-of-mass height), and each car here is held to about 80 % of its "
               + "own tipping point. The van and the offroader keep their high centres of mass and "
               + "therefore keep road-car grip — deliberately, because that is what they are.")]
        public AnimationCurve LateralGrip = new AnimationCurve(
            new Keyframe(0f, 3.59f),
            new Keyframe(1f, 3.11f),
            new Keyframe(2f, 2.58f));

        [Tooltip("What the rear tyres hold as a multiple of the fronts.\n\n"
               + "A sports car has wider rear tyres, and this is that, as one number. It is the "
               + "difference between a car you can throw at a corner and one that answers by swapping "
               + "ends: above 1 the front runs out of grip first, so overdriving the entry pushes wide "
               + "— which a driver can feel coming and correct — instead of rotating past the point of "
               + "return.\n\n"
               + "It is the only thing that makes a large steering angle safe to hand out. Without it "
               + "the two axles saturate together and every input past the limit is a coin toss.\n\n"
               + "Below 1 is a car with its tail hung out on purpose. The pickup is the one that wants "
               + "that.")]
        [Range(0.85f, 1.25f)] public float RearGripBias = 1.08f;

        [Tooltip("Slip angle at which the tyre makes its most cornering force, degrees.\n\n"
               + "Everything below this is the tyre building force as it is asked to, and it is the "
               + "part the model did not have: the old one demanded the whole sideways velocity be "
               + "cancelled every step, which saturated at about half a degree. Between gripping and "
               + "sliding there was nothing.\n\n"
               + "It is also the directness knob, and the most honest one there is. Cornering stiffness "
               + "— force per degree of slip — goes as 1/tan of this, so 5° is a tyre 1.6× stiffer than "
               + "8° and a car that answers the wheel that much sooner. A soft road tyre peaks around "
               + "8°, a fast road tyre around 6°, a track tyre nearer 5. That spread is most of what "
               + "separates the coupé from the van, and it costs nothing to have.\n\n"
               + "Lower is sharper and less forgiving; higher is a tyre that lets go slowly and tells "
               + "you it is doing it.")]
        public float PeakSlipAngle = 6f;

        [Tooltip("Slip ratio at which the tyre makes its most drive or brake force.\n\n"
               + "The longitudinal half of the same curve. 0.12 means a wheel turning 12 % faster than "
               + "the road is at the limit of what it can push with.")]
        public float PeakSlipRatio = 0.12f;

        [Tooltip("What is left of the grip once the tyre is well past its peak, as a fraction.\n\n"
               + "This is the whole of what makes a limit findable. A curve that rose to a plateau and "
               + "stayed there gives a car that is either holding on or is not, with nothing in "
               + "between and no penalty for overdriving it. 0.89 is a road tyre: enough of a drop that "
               + "asking for too much costs you, gentle enough that it does not snap.")]
        [Range(0.5f, 1f)] public float GripPastPeak = 0.89f;

        [Tooltip("How far the tyre has to roll before a change in slip has become a change in force, "
               + "metres.\n\n"
               + "A real tyre is a carcass that has to wind up before the tread can push, and half a "
               + "metre is about right for a road tyre. It is also, not by accident, what keeps this "
               + "model stable at a 50 Hz step: the force law gets stiffer as the car slows, and the "
               + "rate this implies — speed over length — gets slower in exactly the same proportion. "
               + "One number, doing the physical job and the numerical one, because they are the same "
               + "job.")]
        public float RelaxationLength = 0.5f;

        [Tooltip("Rotational inertia of one wheel and tyre, kg·m².\n\n"
               + "1.2 is about a 20 kg wheel at this radius. Larger blunts wheelspin and lock-up and "
               + "makes the car feel heavier to get going; smaller makes both snappier.")]
        public float WheelInertia = 1.2f;

        /// <summary>
        /// Stiffness factor of the tyre curve, derived so that the peak lands exactly at a normalised
        /// slip of 1 — which is what lets <see cref="PeakSlipAngle"/> and <see cref="PeakSlipRatio"/>
        /// mean what their names say.
        ///
        /// <para>The curve is <c>sin(C · atan(B · u))</c>, the magic formula with its curvature term
        /// dropped. Two shape numbers rather than five, and both are read off values a person can
        /// picture: where the peak is, and how much is left past it.</para>
        /// </summary>
        public float TyreShapeB => Mathf.Tan(Mathf.PI / (2f * TyreShapeC));

        /// <summary>
        /// Falloff factor of the tyre curve, derived from <see cref="GripPastPeak"/>.
        ///
        /// <para>The upper branch of the arcsine on purpose: <c>sin(C·π/2) = GripPastPeak</c> has two
        /// solutions and the small one is a curve that never reaches its peak at all.</para>
        /// </summary>
        public float TyreShapeC =>
            2f - 2f / Mathf.PI * Mathf.Asin(Mathf.Clamp(GripPastPeak, 0.5f, 1f));

        [Tooltip("What the rear tyres hold while the driver is asking to be sideways, as a fraction.\n\n"
               + "It was a handbrake-only multiplier, which made the handbrake the only way into a "
               + "slide. It is the same number doing the same job, asked for by DriftIntent — so "
               + "braking into a corner and standing on the throttle in a tight one reach it too, and "
               + "all three entries feel like one mechanism because they are one.\n\n"
               + "The handbrake still locks the wheels on top of this, and that lock is "
               + "HandbrakeForceN. It does most of the work; this is the rest.")]
        [Range(0f, 1f)] public float DriftRearGrip = 0.55f;

        [Tooltip("How much a bootful of throttle in a tight corner counts as asking for a slide, 0 to 1.\n\n"
               + "Zero on every car, and the reason is the keyboard. A key has no middle: the throttle "
               + "is either 0 or 1, and on a keyboard it is 1 essentially all of the time. A signal "
               + "that is always present carries no request, so this read every tight corner taken at "
               + "the only speed a keyboard can offer as a demand to be sideways — the liftback, at "
               + "1.0, went into a full drift in every hairpin on the pass simply by driving through "
               + "it.\n\n"
               + "Above zero it needs an analogue throttle to mean anything: a gamepad trigger or the "
               + "on-screen slider, where holding three quarters and holding all of it are different "
               + "acts. Left as a field rather than deleted for exactly that reason.\n\n"
               + "Front-driven cars ignore it by construction rather than by a number — there is no "
               + "such thing as power oversteer when the driven wheels are the ones steering. And "
               + "traction control has to stand aside for it: the assist that exists to prevent this "
               + "wheelspin is the assist DriftIntent switches off.")]
        [Range(0f, 1f)] public float PowerOversteer;

        [Tooltip("Braking force the handbrake puts through each rear wheel, newtons.\n\n"
               + "Large on purpose: it has to be able to take the whole of a rear tyre's grip budget, "
               + "because that is what leaves nothing for cornering and brings the car round. Too small "
               + "and the handbrake merely slows you down.")]
        public float HandbrakeForceN = 9000f;

        [Header("Drift")]
        [Tooltip("Slip angle in degrees past which the car counts as sideways. Below it the assists do "
               + "nothing at all and the model is on its own.")]
        public float DriftSlipAngle = 12f;

        [Tooltip("Torque opposing yaw *rate* once past the slip angle, in newton-metres per rad/s per "
               + "kilogram of car.\n\n"
               + "Rate, not angle: a torque pulling the car straight would fight the drift and snap it "
               + "into line the moment you lifted. This one lets the car sit at whatever angle you put "
               + "it at and only bites when the rotation starts running away, which is what makes a "
               + "slide holdable. Zero switches it off.\n\n"
               + "2.5 is about 1 rad/s² of correction against this car's yaw inertia — enough to catch a "
               + "slide, not enough to hide the model underneath. The first value tried here was 0.35, "
               + "which worked out at 0.15 rad/s² and would have taken seven seconds to arrest a spin.")]
        public float DriftYawDamping = 2.5f;

        [Tooltip("How much of the steering lock that the speed limit takes away is handed back while "
               + "sideways, 0 to 1. Full lock at speed is nervous on a straight and exactly what is "
               + "wanted when catching a slide. Zero switches it off.")]
        [Range(0f, 1f)] public float CountersteerAuthority = 0.75f;

        [Header("Assists")]
        [Tooltip("Whether the engine backs off when a driven wheel breaks traction.\n\n"
               + "It has to exist, and the reason is arithmetic rather than taste. First gear on this "
               + "car is 4.20 × 4.09, so 570 Nm at the crank is about 22 kN of tractive force against a "
               + "car that weighs 12 kN — twice what any tyre can hold. That was true before the wheels "
               + "had a speed of their own too; the difference is that the excess used to be thrown "
               + "silently away, and now it spins the wheel. A player holding full throttle with a "
               + "thumb — which is exactly what the Auto pedals do — would never leave the smoke.\n\n"
               + "It allows slip right up to the peak before it does anything, so wheelspin is still "
               + "there to see, hear and feel; what it removes is the runaway past it. Off is a car "
               + "that needs the throttle fed in.")]
        public bool TractionControl = true;

        [Tooltip("How hard the car is pulled toward the yaw rate its steering angle is asking for, in "
               + "1/s — roughly the reciprocal of the time it takes to get there. Zero switches it off.\n\n"
               + "This is what makes the car feel *direct* without touching the tyres. The tyre model "
               + "builds yaw the honest way, through a slip angle that has to develop before it makes "
               + "force, and on a phone the corner is frequently over before that has happened. The "
               + "assist supplies the missing yaw immediately and then gets out of the way.\n\n"
               + "It cannot make the car do anything the tyres could not: the target is capped at the "
               + "yaw rate the current friction circle would hold, so at the limit the car still runs "
               + "wide. It also fades out past DriftSlipAngle, because that region belongs to "
               + "DriftYawDamping and CountersteerAuthority and two controllers on one axis fight. "
               + "Halved when the tyre got a curve, for two reasons. The lag it was written against was "
               + "mostly the old model's, which built its whole cornering force inside half a degree "
               + "of slip and therefore had no lag at the tyre at all — what it had was yaw inertia, "
               + "and that is smaller. And its ceiling is a measured grip capacity now rather than a "
               + "coefficient that fell with speed, so at 200 km/h the same gain was being allowed to "
               + "command about a third more yaw than before. A car that rotates on this rather than on "
               + "its tyres is the failure to watch for; it should be doing nothing at all once the car "
               + "is actually turning.\n\n"
               + "3.0 for an arcade car, and this file's own warning that above 4 it 'starts to "
               + "rotate like it is on rails' is now the specification rather than the caution. It can "
               + "be pushed this hard because the target is safe by construction: the steering limit "
               + "is derived from the same grip the assist is capped at, so it is no longer the thing "
               + "standing between the driver and a spin. What the gain buys is how quickly the car "
               + "reaches the yaw it is allowed, which is the whole of what 'direct' means once the "
               + "amount is settled.\n\n"
               + "It is also the stabiliser. A signed controller on the yaw error adds rotation when "
               + "there is too little and takes it away when there is too much, so raising it makes "
               + "the car both sharper and calmer — which is exactly the combination a race car has "
               + "and a loose one does not.\n\n"
               + "Was 1.5 once PeakSlipAngle came down. That figure was chosen against a tyre "
               + "that needed eight degrees to reach its peak, where the assist was carrying most of "
               + "the turn-in on its own and it was right to take it away. A six-degree tyre builds "
               + "half again as much force per degree and builds it sooner, so the assist is back to "
               + "removing lag rather than supplying rotation — which is the only job it should ever "
               + "have.")]
        public float TurnInAssist = 3f;

        [Tooltip("Downforce in N per (m/s)². Presses the car onto the road as speed rises.\n\n"
               + "This is what buys a fast car its steering angle back. The usable steering angle goes "
               + "as the lateral grip over the square of speed, so without aero it collapses to nothing "
               + "and the car cannot be pointed at anything above 150 km/h. Downforce grows with the "
               + "square of speed as well, which is why an aero car still turns up there — the two "
               + "terms cancel.\n\n"
               + "2.2 is about 9 kN at this car's top speed, roughly three quarters of its weight. "
               + "LateralGrip is keyed on wheel load, so the curve does charge for it: the coefficient "
               + "falls as the tyre is pressed harder, and the net gain is real but well short of the "
               + "load itself. It was briefly cut to a third of this on the reasoning that nothing "
               + "cancelled it any more, which was true and was the wrong conclusion — what it "
               + "produced was a car that could not turn at speed at all.")]
        public float Downforce = 1.1f;

        /// <summary>True if the wheel at <paramref name="index"/> is driven. 0/1 front, 2/3 rear.</summary>
        public bool IsDriven(int index)
        {
            bool front = index < 2;
            switch (DrivenAxle)
            {
                case DrivenAxle.Front:
                    return front;
                case DrivenAxle.Rear:
                    return !front;
                default:
                    return true;
            }
        }

        /// <summary>Number of driven wheels, so total power splits evenly.</summary>
        public int DrivenWheelCount => DrivenAxle == DrivenAxle.All ? 4 : 2;
    }
}
