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
        private Vector3 previousVelocity;
        private float wheelbase = 2.7f;

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

            /// <summary>
            /// How much of the cornering force this tyre asked for it actually got, 0 to 1.
            ///
            /// 1 is a tyre holding on. Anything under it is the friction circle refusing, which is the
            /// same thing as sliding — so this is what the squeal, the drift state and the overlay all
            /// read rather than each deriving a slip angle of its own.
            /// </summary>
            public float GripUsed = 1f;

            /// <summary>Sideways speed at the contact patch, m/s.</summary>
            public float LateralSlip;

            /// <summary>
            /// How fast the tyre is spinning up or locking against the road, m/s at the contact patch.
            ///
            /// <para>The model carries no wheel angular velocity — <see cref="SpinAngle"/> is derived
            /// from road speed for the visual and nothing else — so there is no true longitudinal slip
            /// to read. This stands in for it, and it is not a fudge: when the friction circle refuses
            /// part of the drive or brake force, the refused part is exactly what would have been
            /// spinning the wheel up instead of pushing the car. Dividing it by the wheel's share of
            /// the mass gives the acceleration the patch slips at, and that settles to a slip speed.</para>
            /// </summary>
            public float SpinSlip;
        }

        /// <summary>
        /// Ceiling on the reported acceleration, m/s². Comfortably above what the tyres can produce
        /// (a traction-limited launch is around 8.5), so it only ever catches impact spikes.
        /// </summary>
        private const float MaxLongitudinalAcceleration = 15f;

        /// <summary>
        /// Seconds of overload that a steady spin slip settles to. Larger spins the tyres up more
        /// readily for the same excess force.
        /// </summary>
        private const float SpinSlipGain = 0.35f;

        /// <summary>How quickly spin slip rises and falls toward what the overload calls for, per second.</summary>
        private const float SpinSlipResponse = 8f;

        /// <summary>Ceiling on spin slip, m/s. Well past the point where the tyre is fully alight.</summary>
        private const float MaxSpinSlip = 25f;

        /// <summary>Filter rate for <see cref="LongitudinalAcceleration"/>, per second.</summary>
        private const float AccelerationSmoothing = 12f;

        /// <summary>Signed forward speed in m/s. Negative when reversing.</summary>
        public float ForwardSpeed => forwardSpeed;

        /// <summary>Speed in km/h, for the HUD.</summary>
        public float SpeedKmh => Mathf.Abs(forwardSpeed) * 3.6f;

        /// <summary>Speed as a 0..1 fraction of <see cref="VehicleConfig.TopSpeed"/>.</summary>
        public float SpeedNormalized =>
            config != null ? Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(1f, config.TopSpeed)) : 0f;

        /// <summary>
        /// Specific force along the car's own forward axis, m/s² — what an accelerometer bolted to the
        /// car would read. Positive is being pushed back into the seat, negative is braking.
        ///
        /// <para>Here because a speed readout cannot answer the question everything downstream actually
        /// asks. Speed changes smoothly, so a cue driven by it is invisible while it changes — which is
        /// the whole reason the car could gain 100 km/h without the moment ever registering. The events
        /// are real and already in the physics: the drivetrain drops to zero drive for the whole of
        /// <see cref="VehicleConfig.ShiftTime"/> at every shift, and again on the limiter. This is what
        /// lets the audio and the body movement show them.</para>
        ///
        /// <para>Filtered, and that is not tidiness. The raw difference carries every friction-circle
        /// clamp and suspension bump, which at 50 Hz is loud enough to swamp the signal — a consumer
        /// reading it unfiltered would shake rather than surge.</para>
        /// </summary>
        public float LongitudinalAcceleration { get; private set; }

        /// <summary>Current steering angle in degrees.</summary>
        public float SteerAngle => steerAngle;

        /// <summary>Engine speed in rpm. Comes from the wheels through the gearbox, not from a guess.</summary>
        public float EngineRpm => engineRpm;

        /// <summary>Selected gear: 1-based forwards, 0 while reversing.</summary>
        public int Gear => reversing ? 0 : gearIndex + 1;

        /// <summary>
        /// How hard the driver is actually braking, 0 to 1 — zero while the same pedal is being used
        /// to reverse. Not the same thing as <c>IDriveInput.Brake</c>, which cannot tell them apart.
        /// </summary>
        public float BrakeInput { get; private set; }

        /// <summary>True while the brake pedal is driving the car backwards rather than slowing it.</summary>
        public bool IsReversing => reversing;

        /// <summary>True during the torque interruption of a shift.</summary>
        public bool IsShifting => shiftTimer > 0f;

        /// <summary>Engine speed as a fraction of the redline, for audio and instruments.</summary>
        public float RpmNormalized =>
            config != null ? Mathf.Clamp01(engineRpm / Mathf.Max(1f, config.RedlineRpm)) : 0f;

        /// <summary>
        /// How far the car is travelling sideways to the way it is pointing, in degrees.
        ///
        /// Taken from the body's own velocity rather than from a tyre, so it is the angle you would
        /// measure from outside the car — which is the one that matters for whether this reads as a
        /// drift. Zero below walking pace, where the direction of travel is noise.
        /// </summary>
        public float SlipAngle { get; private set; }

        /// <summary>Sideways speed at the rear axle, m/s. What the tyre squeal is driven by.</summary>
        public float RearSlip { get; private set; }

        /// <summary>
        /// The worst grip shortfall on the rear axle, 0 to 1, where 1 is holding on.
        ///
        /// Rear rather than either axle because that is the end whose letting go is a drift; the front
        /// letting go is understeer, and it wants a different noise and a different camera.
        /// </summary>
        public float RearGrip { get; private set; } = 1f;

        /// <summary>
        /// A scale on every tyre's whole grip budget, 1 on tarmac.
        ///
        /// <para>The one hook the world has into the handling model, and it is deliberately a single
        /// number rather than a surface type. What a car in water needs is not different tyre physics,
        /// it is the same friction circle with almost nothing in it: drive, braking and cornering all
        /// go together, which is what ploughing to a halt actually is. Anything that wants gravel or ice
        /// later sets this too.</para>
        ///
        /// <para>Not serialised and not on the config: it is a state the world puts the car into for as
        /// long as the car is in it, not a property of the vehicle. Whatever sets it owns putting it
        /// back.</para>
        /// </summary>
        public float GripScale { get; set; } = 1f;

        /// <summary>
        /// True when the car is meaningfully sideways *and* the rear tyres are the reason.
        ///
        /// Both halves are needed. Slip angle alone counts a car sliding bodily down a wet camber,
        /// which is not a drift; lost rear grip alone counts a standstill wheelspin, which is not one
        /// either.
        /// </summary>
        public bool IsDrifting =>
            config != null && SlipAngle > config.DriftSlipAngle && RearGrip < 0.92f;

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

        /// <summary>Number of wheels an effect can ask about. Order is FL, FR, RL, RR.</summary>
        public static int WheelSlipCount => WheelCount;

        /// <summary>
        /// Where a wheel is touching the road and how hard it is sliding there, for effects that have
        /// to be drawn at the contact patch — tyre smoke above all.
        ///
        /// <para>Returns false for an airborne wheel, which is the whole reason this is a Try method:
        /// a caller that got a stale contact point would leave smoke hanging in the air over a crest.</para>
        ///
        /// <para><paramref name="slipSpeed"/> is metres per second of tyre sliding across tarmac,
        /// combining the sideways slide with the spin-up from <see cref="WheelState.SpinSlip"/>. The two
        /// are perpendicular, so they combine as a hypotenuse rather than a sum.</para>
        ///
        /// <para>The sideways half is weighted by what the friction circle <i>refused</i> rather than by
        /// the raw sideways speed. A tyre holding a steady corner still has sideways velocity at the
        /// patch — that is how it makes force at all — and billing that as sliding would put smoke
        /// under the car in every bend it ever took.</para>
        /// </summary>
        public bool TryGetWheelSlip(int index, out Vector3 contactPoint, out float slipSpeed)
        {
            contactPoint = Vector3.zero;
            slipSpeed = 0f;

            if (index < 0 || index >= WheelCount)
            {
                return false;
            }

            WheelState wheel = wheels[index];
            if (!wheel.Grounded)
            {
                return false;
            }

            contactPoint = wheel.ContactPoint;

            float sliding = wheel.LateralSlip * (1f - Mathf.Clamp01(wheel.GripUsed));
            slipSpeed = Mathf.Sqrt(sliding * sliding + wheel.SpinSlip * wheel.SpinSlip);
            return true;
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

            CacheWheelbase();
            ApplyConfigToBody();
        }

        /// <summary>
        /// Measures the wheelbase off the anchors once, because <see cref="ApplyTurnInAssist"/> needs it
        /// every physics step and reading two transforms in there would be a needless dependency on the
        /// anchors still being where they were. Falls back to the prototype's 2.70 m if the anchors are
        /// not wired, which is a bad prefab rather than a bad number.
        /// </summary>
        private void CacheWheelbase()
        {
            wheelbase = 2.7f;

            if (wheelAnchors == null
                || wheelAnchors.Length < WheelCount
                || wheelAnchors[FrontLeft] == null
                || wheelAnchors[RearLeft] == null)
            {
                return;
            }

            float measured = Mathf.Abs(
                wheelAnchors[FrontLeft].localPosition.z - wheelAnchors[RearLeft].localPosition.z);

            if (measured > 0.5f)
            {
                wheelbase = measured;
            }
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
        /// Swaps the handling asset and re-seats everything derived from it.
        ///
        /// <para><b>A method rather than a settable property, and that is the whole point.</b> A bare
        /// setter is a setter somebody assigns without calling <see cref="ApplyConfigToBody"/>, and the
        /// result is silent: the car wears a van's bodywork while the Rigidbody keeps the fastback's
        /// 1250 kg and its centre of mass, so it looks wrong to nobody and drives wrong to everybody.
        /// This reads as an operation with consequences, which it is — mass, damping, centre of mass and
        /// the suspension rest length all move.</para>
        ///
        /// <para><b>Only call this while the physics step is stopped.</b> Changing mass and centre of
        /// mass recomputes the inertia tensor of a live non-kinematic body; doing it mid-corner is a
        /// kick nobody asked for. Both callers pause first, and both follow with
        /// <see cref="Teleport"/> so the suspension is not left carrying the last car's compression.</para>
        /// </summary>
        public void SetConfig(VehicleConfig value)
        {
            if (value == null || value == config)
            {
                return;
            }

            config = value;

            for (int i = 0; i < WheelCount; i++)
            {
                wheels[i].SpringLength = config.SuspensionRestLength;
                wheels[i].Compression01 = 0f;
            }

            CacheWheelbase();
            ApplyConfigToBody();
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

            // Without this the next step differentiates the whole of the old speed across one frame and
            // reports a crash the car never had — which every consumer would then render.
            forwardSpeed = 0f;
            previousVelocity = Vector3.zero;
            LongitudinalAcceleration = 0f;

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

            UpdateLongitudinalAcceleration(deltaTime);

            // Lock available at this speed, plus whatever the slide has earned back. SteeringBySpeed
            // cuts to a third at speed to keep a straight calm, and that is the exact opposite of what
            // is needed with the car sideways — see ApplyDriftAssists.
            float lock01 = config.SteeringBySpeed.Evaluate(speed01);
            if (SlipAngle > config.DriftSlipAngle)
            {
                float past = Mathf.InverseLerp(config.DriftSlipAngle, config.DriftSlipAngle + 25f, SlipAngle);
                lock01 = Mathf.Lerp(lock01, 1f, past * config.CountersteerAuthority);
            }

            float targetSteer = drive.Steer * config.MaxSteerAngle * lock01;
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

            // Published because the pedal alone cannot tell the two apart, and anything downstream that
            // treats reversing as braking gets it wrong — brake lights being the obvious one.
            BrakeInput = brake;

            float driveForcePerWheel = UpdateDrivetrain(deltaTime, throttle, reverse);

            for (int i = 0; i < WheelCount; i++)
            {
                UpdateWheel(i, deltaTime, speed01, driveForcePerWheel, brake, drive.Handbrake);
            }

            ApplyAntiRoll(FrontLeft, FrontRight);
            ApplyAntiRoll(RearLeft, RearRight);

            ApplyAxisDamping();

            UpdateSlipState(velocity);
            ApplyTurnInAssist(speed01, drive.Handbrake);
            ApplyDriftAssists();

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
        /// Measures <see cref="LongitudinalAcceleration"/> for this step.
        ///
        /// <para>Runs at the top of the step, before any force is applied, so every consumer within the
        /// frame sees the same number — a value updated halfway through would mean the camera and the
        /// audio were reacting to different instants.</para>
        /// </summary>
        private void UpdateLongitudinalAcceleration(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            // Differentiating the *vector* and projecting the result, rather than differentiating the
            // already-projected forwardSpeed. The two are not the same thing and the difference is not
            // subtle: forwardSpeed is measured along transform.forward, so pitching the body re-projects
            // an unchanged velocity and manufactures acceleration out of nothing. Cresting a rise at
            // 10° is worth over 2 m/s² of pure fiction that way — and since PitchDamping now lets the
            // body move more, the error would feed itself.
            Vector3 velocity = body.linearVelocity;
            float raw = Vector3.Dot(velocity - previousVelocity, transform.forward) / deltaTime;
            previousVelocity = velocity;

            // Minus gravity's component along the same axis, which makes this specific force — what an
            // accelerometer bolted to the car reads, and what actually presses the driver into the seat.
            // On a game set on mountain passes that is the definition worth having: holding a steady
            // 60 km/h up a gradient now reads as the load it is, and coasting down one reads as the
            // free ride it is, neither of which a plain speed derivative can tell you.
            raw -= Vector3.Dot(Physics.gravity, transform.forward);

            // Clamped before the filter, not after. A single-frame spike from a kerb or a landing is
            // physically real but useless to a consumer, and letting it into the filter would leave the
            // tail of it in the signal for a tenth of a second after the bump was over.
            raw = Mathf.Clamp(raw, -MaxLongitudinalAcceleration, MaxLongitudinalAcceleration);

            LongitudinalAcceleration = Mathf.Lerp(
                LongitudinalAcceleration,
                raw,
                1f - Mathf.Exp(-AccelerationSmoothing * deltaTime));
        }

        /// <summary>
        /// Damps roll and pitch while leaving yaw alone.
        ///
        /// <para>Rigidbody.angularDamping applies to every axis at once, and a vehicle wants very
        /// different things from each: roll and pitch should be calmed so the body settles, while yaw is
        /// the axis the driver is actually commanding and should be decided by the tyres. Damping them
        /// together meant the damper was quietly fighting every corner — at the old 1.2, about 2600 Nm
        /// of yaw torque was going into the damper just to hold a steady turn, which the front tyres had
        /// to supply on top of the force that was turning the car. The result was a car that understeered
        /// by construction and steering that went numb the harder it was asked.</para>
        ///
        /// <para>Acceleration mode so the coefficient means rad/s² per rad/s regardless of the inertia
        /// tensor, and so the number in the config can be read as a time constant.</para>
        /// </summary>
        private void ApplyAxisDamping()
        {
            if (config.RollDamping <= 0f && config.PitchDamping <= 0f)
            {
                return;
            }

            Vector3 angular = body.angularVelocity;

            float roll = Vector3.Dot(angular, transform.forward);
            float pitch = Vector3.Dot(angular, transform.right);

            // A constant per axis, because the two are asked for by different things. Roll damping is
            // what keeps the car off its roof; pitch damping only ever cost us the squat and dive that
            // tell the player the car is accelerating. Sharing one number meant the anti-flip figure
            // silently set how much the nose was allowed to move.
            Vector3 damped = transform.forward * (roll * config.RollDamping)
                           + transform.right * (pitch * config.PitchDamping);

            body.AddTorque(-damped, ForceMode.Acceleration);
        }

        /// <summary>
        /// Pulls the car toward the yaw rate its steering angle is asking for.
        ///
        /// <para><b>Why an assist at all.</b> The tyre model builds cornering force the honest way: the
        /// car has to be travelling sideways at the contact patch before there is any sideways force to
        /// have. That delay is real and it is most of what a car feels like — but it is measured against
        /// a corner the driver could see coming, and on a phone the input arrives late and the corner is
        /// frequently over before the yaw has developed. This supplies the missing rotation at turn-in
        /// and then has nothing left to do, because once the car is actually rotating the error it works
        /// on is zero and the tyres are carrying the corner unaided.</para>
        ///
        /// <para><b>Why it cannot cheat.</b> The target comes from Ackermann geometry — the yaw rate that
        /// steering angle and wheelbase geometrically imply — and is then capped at
        /// <c>mu * g / speed</c>, the fastest the friction circle could turn the car at all. So the
        /// assist never asks for rotation the tyres could not have produced, and a corner taken too fast
        /// still runs wide. What it removes is the lag, not the limit.</para>
        ///
        /// <para><b>Why it stops at the drift line.</b> Past <see cref="VehicleConfig.DriftSlipAngle"/>
        /// the car belongs to <c>DriftYawDamping</c> and <c>CountersteerAuthority</c>. Leaving this one
        /// running there would put two controllers on the yaw axis with opposite ideas — one trying to
        /// reach a target rate, the other trying to bleed rate off — and a slide would neither hold nor
        /// catch. It fades out over the same band the drift assists fade in over, so nothing is fighting
        /// at the handover. The handbrake is a deliberate request to break traction, so it switches the
        /// assist off outright.</para>
        /// </summary>
        private void ApplyTurnInAssist(float speed01, bool handbrake)
        {
            if (config.TurnInAssist <= 0f || handbrake || GroundedWheelCount == 0)
            {
                return;
            }

            float speed = Mathf.Abs(forwardSpeed);
            if (speed < 1f)
            {
                // Below walking pace there is no meaningful yaw rate to aim at, and the division
                // below would ask for an enormous one.
                return;
            }

            float authority = GroundedWheelCount * 0.25f;
            if (SlipAngle > config.DriftSlipAngle)
            {
                authority *= 1f - Mathf.InverseLerp(
                    config.DriftSlipAngle, config.DriftSlipAngle + 25f, SlipAngle);
            }

            if (authority <= 0f)
            {
                return;
            }

            // What the front wheels are geometrically pointing at.
            float target = forwardSpeed * Mathf.Tan(steerAngle * Mathf.Deg2Rad) / wheelbase;

            // What the tyres could actually hold: a_lat = mu * g, and yaw rate = a_lat / v.
            float mu = config.LateralGrip.Evaluate(speed01);
            float ceiling = mu * Physics.gravity.magnitude / speed;
            target = Mathf.Clamp(target, -ceiling, ceiling);

            float error = target - Vector3.Dot(body.angularVelocity, transform.up);

            body.AddTorque(
                transform.up * (error * config.TurnInAssist * authority),
                ForceMode.Acceleration);
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
                // Shift points move with the pedal. A fixed threshold is a full-throttle threshold, and
                // applying it to a trailing throttle is what leaves the car pinned near the redline in a
                // low gear at town speeds — harmless with four long gears, unbearable with six short
                // ones. Interpolating on command is the whole of the fix: stamp on it and nothing
                // changes, lift off and the box short-shifts the way a driver would.
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
                wheel.LateralSlip = 0f;
                wheel.SpinSlip = 0f;
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

            // --- Longitudinal first, because the friction circle below has to know what this tyre is
            // already asking of the road before it can say how much is left for cornering.
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

            // The handbrake is a brake, not a switch on the grip.
            //
            // It used to multiply rear grip by a constant, which slid the car sideways without ever
            // rotating it — the back stepped out and the nose carried straight on. Locking the rear
            // wheels instead spends their whole grip budget on stopping, and the circle below then has
            // nothing left to give cornering, so the car comes round because it is being braked at one
            // end. That is what a handbrake turn actually is.
            if (handbrake && !isFront)
            {
                resistive += rollingSign * config.HandbrakeForceN;
            }

            // Clamp so braking and rolling resistance can bring the wheel to a stop but never drag
            // it backwards through zero — that would make the car creep while fully braked.
            float maxResistive = Mathf.Abs(longitudinalVelocity) * wheelShareOfMass / deltaTime;
            resistive = Mathf.Clamp(resistive, -maxResistive, maxResistive);
            longitudinalForce -= resistive;

            // --- The friction circle.
            //
            // <b>One tyre, one budget, shared between going and turning.</b> The model before this
            // cancelled a fraction of the sideways velocity and charged nothing for it, so a tyre could
            // put down full power and hold full cornering force at the same time — which is why the car
            // could not be made to oversteer by any amount of throttle, and why the handbrake had to be
            // a special case.
            //
            // The budget is the normal load times a grip coefficient, and the load is the suspension
            // force computed a few lines above rather than a quarter of the car's mass. That is what
            // brings load sensitivity for free: a wheel gone light over a crest or on the inside of a
            // hairpin loses grip on its own, and the anti-roll bar's load transfer starts to mean
            // something.
            float mu = config.LateralGrip.Evaluate(speed01) * GripScale;
            if (handbrake && !isFront)
            {
                mu *= config.HandbrakeGrip;
            }

            float budget = suspensionForce * mu;

            // The tyre cannot put down more than it has, in *either* direction. Clamping only the
            // cornering half would be the old bug wearing a circle: the car would accelerate as though
            // traction were free while its grip quietly went to nothing, so first gear would launch like
            // an electric motor and corner like it was on ice. Clamped here, asking for more drive than
            // the road can take costs acceleration — which is wheelspin, and is why a standing start in
            // first now has to be fed in rather than stamped on.
            float demandedLongitudinal = longitudinalForce;
            longitudinalForce = Mathf.Clamp(longitudinalForce, -budget, budget);

            // What the circle refused is what spins the tyre. Positive under power (wheelspin) and
            // under the handbrake or hard braking alike (a locked tyre slides just as far), because
            // the tyre does not care which end of the clamp it hit.
            float refused = Mathf.Abs(demandedLongitudinal) - budget;
            float spinTarget = refused > 0f
                ? Mathf.Min(MaxSpinSlip, refused / wheelShareOfMass * SpinSlipGain)
                : 0f;

            wheel.SpinSlip = Mathf.Lerp(
                wheel.SpinSlip, spinTarget, 1f - Mathf.Exp(-SpinSlipResponse * deltaTime));

            float spent = Mathf.Abs(longitudinalForce);
            float capacity = Mathf.Sqrt(Mathf.Max(0f, budget * budget - spent * spent));

            // What it would take to cancel the slide outright, which is what the old model always got.
            // Now it is a request, and the circle answers with what it can afford.
            float wanted = -lateralVelocity * wheelShareOfMass / deltaTime;
            float lateralForce = Mathf.Clamp(wanted, -capacity, capacity);

            body.AddForceAtPosition(right * lateralForce, hit.point);
            body.AddForceAtPosition(forward * longitudinalForce, hit.point);

            // How much of what the tyre wanted it actually got, for the slip readouts and the overlay.
            // Measured rather than inferred from the wheel's angle, because at very low speed a slip
            // angle is all noise while this stays meaningful.
            wheel.GripUsed = Mathf.Abs(wanted) > 1f ? Mathf.Clamp01(Mathf.Abs(lateralForce / wanted)) : 1f;
            wheel.LateralSlip = Mathf.Abs(lateralVelocity);

            UpdateWheelVisual(index, springLength, wheelSteer, longitudinalVelocity, deltaTime);
        }

        /// <summary>
        /// Works out how sideways the car is, once per step, from the body and the rear tyres.
        ///
        /// One place rather than four: the squeal, the flame, the overlay and the assists below all
        /// want the same numbers, and a slip angle each would be four chances to disagree.
        /// </summary>
        private void UpdateSlipState(Vector3 velocity)
        {
            Vector3 flat = velocity;
            flat.y = 0f;

            // Below walking pace the direction of travel is mostly noise, and an angle taken from it
            // would have the car reporting wild drifts while parking.
            if (flat.sqrMagnitude < 4f)
            {
                SlipAngle = 0f;
            }
            else
            {
                Vector3 heading = transform.forward;
                heading.y = 0f;

                // Unsigned: which way round a slide is going matters to the assists, which read the yaw
                // rate directly, but not to anything that only wants to know how sideways it is.
                SlipAngle = Vector3.Angle(heading, flat.normalized);

                // Reversing is not a 180° drift.
                if (SlipAngle > 90f)
                {
                    SlipAngle = 180f - SlipAngle;
                }
            }

            RearSlip = Mathf.Max(wheels[RearLeft].LateralSlip, wheels[RearRight].LateralSlip);
            RearGrip = Mathf.Min(wheels[RearLeft].GripUsed, wheels[RearRight].GripUsed);
        }

        /// <summary>
        /// The two things that make a slide catchable rather than a spin.
        ///
        /// <para>Both are assists and both go to nothing at zero, which is deliberate: the friction
        /// circle underneath has to be judgeable on its own, and an assist that cannot be switched off
        /// is a model you can never tune because you can never see it.</para>
        ///
        /// <para><b>Yaw damping resists the rate, not the angle.</b> A torque pulling the car back
        /// straight would fight the drift itself and the car would snap into line the moment you lifted;
        /// a torque opposing how fast it is rotating lets it sit at whatever angle you put it at and
        /// only bites when it starts to run away. That is the difference between a drift you hold and
        /// one you catch or lose.</para>
        ///
        /// <para><b>Countersteer authority</b> gives back the lock that <c>SteeringBySpeed</c> takes
        /// away. That curve cuts to a third at speed, which is right for stability on a straight and
        /// exactly wrong when you are sideways and need the wheel — so it is restored in proportion to
        /// how far sideways the car already is, which is when it can do no harm.</para>
        /// </summary>
        private void ApplyDriftAssists()
        {
            if (GroundedWheelCount == 0 || SlipAngle <= config.DriftSlipAngle)
            {
                return;
            }

            float past = Mathf.InverseLerp(config.DriftSlipAngle, config.DriftSlipAngle + 25f, SlipAngle);
            float yawRate = Vector3.Dot(body.angularVelocity, transform.up);

            // Force, not Impulse: a continuous torque for as long as the car is sideways. The first
            // version multiplied by deltaTime and passed Impulse, which is the same thing written twice
            // as confusingly.
            body.AddTorque(
                -transform.up * (yawRate * config.DriftYawDamping * past * config.Mass),
                ForceMode.Force);
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
