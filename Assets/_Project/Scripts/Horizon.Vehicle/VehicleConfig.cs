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
        /// </summary>
        public const int CurrentVersion = 2;

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

        [Tooltip("Local centre of mass. Keep it low — this is the main defence against rolling over.")]
        public Vector3 CenterOfMass = new Vector3(0f, -0.30f, 0.05f);

        public float LinearDamping = 0.06f;

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
        /// </summary>
        public float WheelRadius = 0.44f;

        [Tooltip("Suspension travel in metres.\n\n"
               + "0.30 rather than 0.35 because the wheels grew: ride height is rest length plus radius, "
               + "so without this the car would stand 8 cm taller and a muscle car on stilts is not the "
               + "look. Static compression is only 7 cm, so 30 cm of travel is still ample.")]
        public float SuspensionRestLength = 0.30f;

        [Tooltip("Spring rate in N per metre of compression.")]
        public float SuspensionStiffness = 42000f;

        [Tooltip("Damper rate in N per m/s of compression velocity.")]
        public float SuspensionDamping = 3800f;

        [Tooltip("Resists body roll by transferring load across an axle. Without this the car "
               + "flips on the first hairpin.")]
        public float AntiRollStiffness = 14000f;

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

        [Header("Gearbox")]
        [Tooltip("Forward gear ratios, first to top. Top speed comes out of the last one — it is not "
               + "set directly anywhere.")]
        public float[] GearRatios = { 2.78f, 1.93f, 1.36f, 1f };

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

        [Tooltip("Upshift above this engine speed.")]
        public float UpshiftRpm = 5400f;

        [Tooltip("Downshift below this engine speed. Must stay well under UpshiftRpm divided by the "
               + "ratio step, or the box hunts between two gears.")]
        public float DownshiftRpm = 2100f;

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

        [Tooltip("Fraction of full lock available over normalized speed. Falling off with speed is "
               + "what makes fast driving calm instead of nervous.\n\n"
               + "Opened up from 0.62/0.32 after the game was driven on a phone: at town speeds there "
               + "were only 25° of lock and by 80 km/h barely 20, which is fine with a keyboard that can "
               + "hold half a turn and not fine with a thumb, where the corner is over before the lock "
               + "arrives. This is the half of that problem belonging to the car; the other half was the "
               + "arrow buttons having no proportion at all, and lives in TouchSteer.")]
        public AnimationCurve SteeringBySpeed = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(0.35f, 0.80f),
            new Keyframe(1f, 0.50f));

        [Tooltip("Degrees per second the steering angle can change.\n\n"
               + "300 takes full lock in about 0.13 s. At 160 the rack itself was a fifth of a second "
               + "behind the thumb, which reads as the car responding late rather than as slow steering — "
               + "the other half of what felt vague.")]
        public float SteerRate = 300f;

        [Header("Grip")]
        [Tooltip("Grip coefficient over normalized speed — how much force a tyre can put down, as a "
               + "multiple of the load on it.\n\n"
               + "This used to mean 'fraction of the sideways slide removed', which charged nothing for "
               + "it: a tyre could put down full power and hold full cornering force at once. It is now "
               + "the radius of a friction circle that driving and braking spend from as well, so 1.6 "
               + "is a grippy road tyre and 1.0 is a car that will step out if you ask it to. The shape "
               + "still falls with speed, which is aerodynamic and tyre heat standing in for each "
               + "other.")]
        public AnimationCurve LateralGrip = new AnimationCurve(
            new Keyframe(0f, 1.70f),
            new Keyframe(0.5f, 1.52f),
            new Keyframe(1f, 1.32f));

        [Tooltip("What the handbrake does to rear grip on top of locking the wheels. The lock is "
               + "HandbrakeForceN and does most of the work; this is the rest.")]
        [Range(0f, 1f)] public float HandbrakeGrip = 0.55f;

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

        [Tooltip("How much of the steering lock that SteeringBySpeed takes away is handed back while "
               + "sideways, 0 to 1. Full lock at speed is nervous on a straight and exactly what is "
               + "wanted when catching a slide. Zero switches it off.")]
        [Range(0f, 1f)] public float CountersteerAuthority = 0.75f;

        [Header("Assists")]
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
               + "Above about 4 the car starts to rotate like it is on rails — 3 is the most that still "
               + "reads as a car.")]
        public float TurnInAssist = 3f;

        [Tooltip("Downforce in N per (m/s)². Presses the car onto the road as speed rises. Kept low "
               + "now that the car actually reaches 220 km/h — at 6 it would generate nearly twice the "
               + "car's weight up there and feel glued.")]
        public float Downforce = 2.5f;

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
