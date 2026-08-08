using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Raycast-wheel vehicle on a single <see cref="Rigidbody"/>. Deliberately not WheelCollider:
    /// this model is cheaper on mobile, far easier to tune, and does not fight us over grip.
    ///
    /// Wheel order is fixed: 0 front-left, 1 front-right, 2 rear-left, 3 rear-right.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class VehicleController : MonoBehaviour
    {
        [SerializeField] private VehicleConfig config;

        [Tooltip("Suspension attachment points, in order FL, FR, RL, RR.")]
        [SerializeField] private Transform[] wheelAnchors = new Transform[WheelCount];

        [Tooltip("Optional wheel meshes, same order. Purely cosmetic.")]
        [SerializeField] private Transform[] wheelVisuals = new Transform[WheelCount];

        [SerializeField] private LayerMask groundMask = ~0;

        private const int WheelCount = 4;
        private const int FrontLeft = 0;
        private const int FrontRight = 1;
        private const int RearLeft = 2;
        private const int RearRight = 3;

        private readonly WheelState[] wheels = new WheelState[WheelCount];

        private Rigidbody body;
        private IDriveInput explicitInput;
        private float steerAngle;
        private float forwardSpeed;

        private int gearIndex;
        private float shiftTimer;
        private float engineRpm;

        /// <summary>Per-wheel runtime state. Allocated once — nothing here may allocate per frame.</summary>
        private sealed class WheelState
        {
            public bool Grounded;
            public Vector3 ContactPoint;
            public float SpringLength;
            public float Compression01;
            public float SpinAngle;
        }

        /// <summary>Signed forward speed in m/s. Negative when reversing.</summary>
        public float ForwardSpeed => forwardSpeed;

        /// <summary>Speed in km/h, for the HUD.</summary>
        public float SpeedKmh => Mathf.Abs(forwardSpeed) * 3.6f;

        /// <summary>Speed as a 0..1 fraction of <see cref="VehicleConfig.TopSpeed"/>.</summary>
        public float SpeedNormalized =>
            config != null ? Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(1f, config.TopSpeed)) : 0f;

        /// <summary>Current steering angle in degrees.</summary>
        public float SteerAngle => steerAngle;

        /// <summary>Engine speed in rpm. Comes from the wheels through the gearbox, not from a guess.</summary>
        public float EngineRpm => engineRpm;

        /// <summary>Selected gear: 1-based forwards, 0 while reversing.</summary>
        public int Gear => reversing ? 0 : gearIndex + 1;

        /// <summary>True during the torque interruption of a shift.</summary>
        public bool IsShifting => shiftTimer > 0f;

        /// <summary>Engine speed as a fraction of the redline, for audio and instruments.</summary>
        public float RpmNormalized =>
            config != null ? Mathf.Clamp01(engineRpm / Mathf.Max(1f, config.RedlineRpm)) : 0f;

        private bool reversing;

        /// <summary>How many wheels are touching the ground.</summary>
        public int GroundedWheelCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < WheelCount; i++)
                {
                    if (wheels[i].Grounded)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public VehicleConfig Config => config;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            for (int i = 0; i < WheelCount; i++)
            {
                wheels[i] = new WheelState
                {
                    SpringLength = config != null ? config.SuspensionRestLength : 0.3f,
                };
            }

            ApplyConfigToBody();
        }

        /// <summary>
        /// Pushes body values from the config onto the Rigidbody. Public so the setup tool can call
        /// it at edit time and get a correctly configured prefab.
        /// </summary>
        public void ApplyConfigToBody()
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }

            if (config == null)
            {
                Debug.LogError("[Horizon] VehicleController has no VehicleConfig assigned.", this);
                return;
            }

            body.mass = config.Mass;
            body.linearDamping = config.LinearDamping;
            body.angularDamping = config.AngularDamping;
            body.centerOfMass = config.CenterOfMass;

            // The chase camera follows in LateUpdate, so without interpolation the car visibly
            // stutters at any frame rate that is not an exact multiple of the physics rate.
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        /// <summary>
        /// Overrides the input source. Leave unset and the vehicle follows
        /// <see cref="DriveInput.Current"/>, which the router publishes.
        /// </summary>
        public void SetInput(IDriveInput input)
        {
            explicitInput = input;
        }

        /// <summary>Teleports the vehicle and clears its momentum. For respawn / debug.</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            body.position = position;
            body.rotation = rotation;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(position, rotation);

            steerAngle = 0f;
            float restLength = config != null ? config.SuspensionRestLength : 0.3f;
            for (int i = 0; i < WheelCount; i++)
            {
                wheels[i].SpringLength = restLength;
                wheels[i].Compression01 = 0f;
            }
        }

        private void Start()
        {
            // Playing the world scene on its own leaves no input router alive, and the car just sits
            // there looking broken. Say so, rather than letting it be guessed at.
            if (explicitInput == null && DriveInput.Current is NullDriveInput)
            {
                Debug.LogWarning(
                    "[Horizon] No input source is active, so this vehicle will not respond. Press Play "
                    + "from Assets/_Project/Scenes/Bootstrap.unity — that scene owns the "
                    + "DriveInputRouter and loads the world additively.",
                    this);
            }
        }

        private void FixedUpdate()
        {
            if (config == null)
            {
                return;
            }

            IDriveInput drive = explicitInput ?? DriveInput.Current;
            float deltaTime = Time.fixedDeltaTime;

            Vector3 velocity = body.linearVelocity;
            forwardSpeed = Vector3.Dot(velocity, transform.forward);
            float speed01 = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(1f, config.TopSpeed));

            float targetSteer = drive.Steer * config.MaxSteerAngle * config.SteeringBySpeed.Evaluate(speed01);
            steerAngle = Mathf.MoveTowards(steerAngle, targetSteer, config.SteerRate * deltaTime);

            // Brake doubles as reverse once we are almost stopped — one pedal, no gear selection.
            float throttle = drive.Throttle;
            float brake = drive.Brake;
            float reverse = 0f;
            if (brake > 0f && forwardSpeed < 0.6f)
            {
                reverse = brake;
                brake = 0f;
            }

            float driveForcePerWheel = UpdateDrivetrain(deltaTime, throttle, reverse);

            for (int i = 0; i < WheelCount; i++)
            {
                UpdateWheel(i, deltaTime, speed01, driveForcePerWheel, brake, drive.Handbrake);
            }

            ApplyAntiRoll(FrontLeft, FrontRight);
            ApplyAntiRoll(RearLeft, RearRight);

            // Aerodynamic drag, on the body once — not per wheel. Applying drag four times over is how
            // the car ended up with a top speed of about 45 km/h.
            float speed = velocity.magnitude;
            if (speed > 0.1f)
            {
                body.AddForce(-velocity / speed * (config.AeroDrag * speed * speed));
            }

            // Downforce only while in contact: in the air it would make jumps feel like anchors.
            if (GroundedWheelCount > 0)
            {
                float downforce = config.Downforce * velocity.sqrMagnitude;
                body.AddForce(-transform.up * downforce);
            }
        }

        /// <summary>
        /// Runs the engine and gearbox, and returns the drive force each driven wheel should apply.
        ///
        /// Engine speed is derived from how fast the wheels are turning through the current gear, so the
        /// gearbox is a real part of the physics rather than a sound effect: the ratio multiplies torque,
        /// and the interruption during a shift is a genuine gap in thrust. That gap is what a gear change
        /// feels like, and it is the reason the car no longer accelerates like a single-speed electric.
        /// </summary>
        private float UpdateDrivetrain(float deltaTime, float throttle, float reverse)
        {
            if (shiftTimer > 0f)
            {
                shiftTimer -= deltaTime;
            }

            reversing = reverse > 0f;

            float ratio = reversing ? config.RatioForGear(-1) : config.RatioForGear(gearIndex);
            float driveRatio = Mathf.Abs(ratio) * config.FinalDrive;

            // Engine speed from road speed, through the gearbox.
            float wheelRevsPerSecond = Mathf.Abs(forwardSpeed) / (2f * Mathf.PI * config.WheelRadius);
            float geared = wheelRevsPerSecond * 60f * driveRatio;

            // Rolling away from a standstill the clutch or converter slips, so the engine can rev while
            // the wheels barely turn. Without this the car has no voice at all until it is moving.
            float command = Mathf.Max(throttle, reverse);
            float slipping = Mathf.Lerp(config.IdleRpm, config.RedlineRpm * 0.55f, command);
            float blend = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / 3.5f);

            engineRpm = Mathf.Clamp(
                Mathf.Max(geared, Mathf.Lerp(slipping, config.IdleRpm, blend)),
                config.IdleRpm,
                config.RedlineRpm);

            if (!reversing && shiftTimer <= 0f)
            {
                if (engineRpm >= config.UpshiftRpm && gearIndex < config.ForwardGearCount - 1)
                {
                    gearIndex++;
                    shiftTimer = config.ShiftTime;
                }
                else if (engineRpm <= config.DownshiftRpm && gearIndex > 0)
                {
                    gearIndex--;
                    shiftTimer = config.ShiftTime;
                }
            }

            if (reversing)
            {
                gearIndex = 0;
            }

            // No drive while the box is between gears.
            if (shiftTimer > 0f)
            {
                return 0f;
            }

            // Rev limiter. Without it, clamping the displayed rpm to the redline would hide the fact
            // that top gear still has thrust in reserve, and the car would quietly accelerate past the
            // speed its gearing allows. Cutting here is what makes top speed a real limit — and the
            // stutter it produces at 225 km/h is what a limiter sounds like.
            if (geared > config.RedlineRpm)
            {
                return 0f;
            }

            float torqueFraction = config.TorqueByRpm.Evaluate(RpmNormalized);
            float engineTorque = config.MaxTorqueNm * Mathf.Max(0f, torqueFraction) * command;

            if (engineTorque <= 0f)
            {
                return 0f;
            }

            float wheelForce = engineTorque * driveRatio * config.DrivetrainEfficiency / config.WheelRadius;

            if (reversing)
            {
                if (forwardSpeed <= -config.ReverseSpeed)
                {
                    return 0f;
                }

                wheelForce = -wheelForce;
            }

            return wheelForce / Mathf.Max(1, config.DrivenWheelCount);
        }

        private void UpdateWheel(
            int index,
            float deltaTime,
            float speed01,
            float driveForcePerWheel,
            float brake,
            bool handbrake)
        {
            Transform anchor = wheelAnchors[index];
            if (anchor == null)
            {
                return;
            }

            WheelState wheel = wheels[index];
            bool isFront = index < 2;
            float wheelSteer = isFront ? steerAngle : 0f;

            float maxDistance = config.SuspensionRestLength + config.WheelRadius;
            bool hitGround = Physics.Raycast(
                anchor.position,
                -transform.up,
                out RaycastHit hit,
                maxDistance,
                groundMask,
                QueryTriggerInteraction.Ignore);

            if (!hitGround)
            {
                wheel.Grounded = false;
                wheel.Compression01 = 0f;
                wheel.SpringLength = config.SuspensionRestLength;
                UpdateWheelVisual(index, config.SuspensionRestLength, wheelSteer, forwardSpeed, deltaTime);
                return;
            }

            wheel.Grounded = true;
            wheel.ContactPoint = hit.point;

            // --- Suspension: spring pushes out of compression, damper resists the rate of change.
            float springLength = Mathf.Clamp(hit.distance - config.WheelRadius, 0f, config.SuspensionRestLength);
            float compression = config.SuspensionRestLength - springLength;
            float compressionVelocity = (wheel.SpringLength - springLength) / deltaTime;
            wheel.SpringLength = springLength;
            wheel.Compression01 = compression / config.SuspensionRestLength;

            float suspensionForce = Mathf.Max(
                0f,
                compression * config.SuspensionStiffness + compressionVelocity * config.SuspensionDamping);
            body.AddForceAtPosition(transform.up * suspensionForce, hit.point);

            // --- Tyre frame, projected onto the surface so slopes behave.
            Vector3 steered = Quaternion.AngleAxis(wheelSteer, transform.up) * transform.forward;
            Vector3 forward = Vector3.ProjectOnPlane(steered, hit.normal).normalized;
            Vector3 right = Vector3.Cross(hit.normal, forward);

            Vector3 pointVelocity = body.GetPointVelocity(hit.point);
            float lateralVelocity = Vector3.Dot(pointVelocity, right);
            float longitudinalVelocity = Vector3.Dot(pointVelocity, forward);
            float wheelShareOfMass = config.Mass * 0.25f;

            // --- Lateral grip: cancel the sideways component. grip == 1 removes it entirely within
            // this step; anything lower leaves a proportional slide, which is the drift control.
            float grip = config.LateralGrip.Evaluate(speed01);
            if (handbrake && !isFront)
            {
                grip *= config.HandbrakeGrip;
            }

            float lateralAcceleration = -lateralVelocity * grip / deltaTime;
            body.AddForceAtPosition(right * (lateralAcceleration * wheelShareOfMass), hit.point);

            // --- Longitudinal: drive, then everything that opposes motion.
            float longitudinalForce = config.IsDriven(index) ? driveForcePerWheel : 0f;

            // Rolling resistance is a constant force opposing the direction of travel, not something
            // proportional to speed. The old speed-proportional version, applied to all four wheels,
            // reached 800 N per m/s and capped the car at walking pace.
            float rollingSign = longitudinalVelocity > 0f ? 1f : -1f;
            float resistive = rollingSign * config.RollingResistanceN;

            if (brake > 0f)
            {
                resistive += rollingSign * (brake * config.BrakeForce * 0.25f);
            }

            // Clamp so braking and rolling resistance can bring the wheel to a stop but never drag
            // it backwards through zero — that would make the car creep while fully braked.
            float maxResistive = Mathf.Abs(longitudinalVelocity) * wheelShareOfMass / deltaTime;
            resistive = Mathf.Clamp(resistive, -maxResistive, maxResistive);
            longitudinalForce -= resistive;

            body.AddForceAtPosition(forward * longitudinalForce, hit.point);

            UpdateWheelVisual(index, springLength, wheelSteer, longitudinalVelocity, deltaTime);
        }

        /// <summary>
        /// Anti-roll bar. Transfers load across an axle so the body resists leaning, which is what
        /// keeps the car on its wheels through a hairpin.
        /// </summary>
        private void ApplyAntiRoll(int leftIndex, int rightIndex)
        {
            WheelState left = wheels[leftIndex];
            WheelState right = wheels[rightIndex];
            if (!left.Grounded && !right.Grounded)
            {
                return;
            }

            // Positive when the left wheel is compressed more, i.e. the body leans left. Push the
            // compressed side up and the extended side down.
            float difference = left.Compression01 - right.Compression01;
            float force = difference * config.AntiRollStiffness;

            if (left.Grounded)
            {
                body.AddForceAtPosition(transform.up * force, left.ContactPoint);
            }

            if (right.Grounded)
            {
                body.AddForceAtPosition(-transform.up * force, right.ContactPoint);
            }
        }

        private void UpdateWheelVisual(
            int index,
            float springLength,
            float wheelSteer,
            float rollVelocity,
            float deltaTime)
        {
            Transform visual = wheelVisuals != null && index < wheelVisuals.Length
                ? wheelVisuals[index]
                : null;

            if (visual == null)
            {
                return;
            }

            WheelState wheel = wheels[index];
            float circumferenceFactor = Mathf.Rad2Deg / Mathf.Max(0.01f, config.WheelRadius);
            wheel.SpinAngle = Mathf.Repeat(wheel.SpinAngle + rollVelocity * circumferenceFactor * deltaTime, 360f);

            visual.position = wheelAnchors[index].position - transform.up * springLength;
            visual.rotation = transform.rotation * Quaternion.Euler(wheel.SpinAngle, wheelSteer, 0f);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (config == null || wheelAnchors == null)
            {
                return;
            }

            Gizmos.color = Color.cyan;
            for (int i = 0; i < wheelAnchors.Length; i++)
            {
                Transform anchor = wheelAnchors[i];
                if (anchor == null)
                {
                    continue;
                }

                Vector3 end = anchor.position - transform.up * config.SuspensionRestLength;
                Gizmos.DrawLine(anchor.position, end);
                Gizmos.DrawWireSphere(end, config.WheelRadius);
            }

            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(transform.TransformPoint(config.CenterOfMass), 0.12f);
        }
#endif
    }
}
