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

        [Tooltip("Total drive force at full throttle, in newtons.")]
        public float EnginePower = 11000f;

        [Tooltip("Drive force multiplier over normalized speed. Reaching zero at 1 sets top speed.")]
        public AnimationCurve PowerBySpeed = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

        [Tooltip("Speed in m/s that counts as 1 on the normalized speed axis. 45 m/s ≈ 162 km/h.")]
        public float TopSpeed = 45f;

        [Tooltip("Total braking force in newtons.")]
        public float BrakeForce = 16000f;

        [Tooltip("Top speed in reverse, m/s.")]
        public float ReverseSpeed = 8f;

        [Tooltip("Coast-down force per m/s. Higher means the car slows sooner off throttle.")]
        public float RollingResistance = 200f;

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

        [Tooltip("Downforce in N per (m/s)². Presses the car onto the road as speed rises.")]
        public float Downforce = 6f;

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
