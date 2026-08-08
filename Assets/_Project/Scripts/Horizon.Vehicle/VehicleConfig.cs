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
        [Header("Body")]
        public float Mass = 1250f;

        [Tooltip("Local centre of mass. Keep it low — this is the main defence against rolling over.")]
        public Vector3 CenterOfMass = new Vector3(0f, -0.30f, 0.05f);

        public float LinearDamping = 0.06f;
        public float AngularDamping = 1.2f;

        [Header("Suspension")]
        public float WheelRadius = 0.34f;

        [Tooltip("Suspension travel in metres.")]
        public float SuspensionRestLength = 0.35f;

        [Tooltip("Spring rate in N per metre of compression.")]
        public float SuspensionStiffness = 42000f;

        [Tooltip("Damper rate in N per m/s of compression velocity.")]
        public float SuspensionDamping = 3800f;

        [Tooltip("Resists body roll by transferring load across an axle. Without this the car "
               + "flips on the first hairpin.")]
        public float AntiRollStiffness = 14000f;

        [Header("Drivetrain")]
        public DrivenAxle DrivenAxle = DrivenAxle.All;

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

        public float FinalDrive = 3.31f;

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
        [Tooltip("Steering angle at full lock, degrees.")]
        public float MaxSteerAngle = 32f;

        [Tooltip("Fraction of full lock available over normalized speed. Falling off with speed is "
               + "what makes fast driving calm instead of nervous.")]
        public AnimationCurve SteeringBySpeed = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(0.35f, 0.62f),
            new Keyframe(1f, 0.32f));

        [Tooltip("Degrees per second the steering angle can change.")]
        public float SteerRate = 160f;

        [Header("Grip")]
        [Tooltip("How completely a tyre kills sideways slide, over normalized speed. 1 is no slide, "
               + "lower lets the car drift.")]
        public AnimationCurve LateralGrip = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(0.5f, 0.9f),
            new Keyframe(1f, 0.78f));

        [Tooltip("Rear grip multiplier while the handbrake is held.")]
        [Range(0f, 1f)] public float HandbrakeGrip = 0.22f;

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
