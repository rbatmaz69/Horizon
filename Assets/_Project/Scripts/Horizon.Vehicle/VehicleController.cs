using Horizon.Core;
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

        [Tooltip("The tank this engine drinks from. Left empty the car simply never runs out, which is "
               + "what every scene that predates the tank does.")]
        [SerializeField] private FuelTank fuel;

        /// <summary>
        /// The fastest the suspension is allowed to be told it is moving, metres per second. See the
        /// note where it is used — it is a guard on a finite difference across a step in the road, not a
        /// tuning value.
        /// </summary>
        private const float MaxDamperSpeed = 4f;

        private const int WheelCount = 4;
        private const int FrontLeft = 0;
        private const int FrontRight = 1;
        private const int RearLeft = 2;
        private const int RearRight = 3;

        private readonly WheelState[] wheels = new WheelState[WheelCount];

        private Rigidbody body;
        private IDriveInput explicitInput;
        private float steerAngle;
        private float gripBudget;
        private float steeringCapacity;
        private float tractionCut = 1f;
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
            public Vector3 ContactNormal;
            public float SpringLength;
            public float Compression01;
            public float SpinAngle;

            /// <summary>
            /// What this tyre is being pressed onto the road with, newtons — the spring and damper,
            /// plus whatever the anti-roll bar has moved across the axle.
            ///
            /// <para>Separated from the spring force it starts as, because the bar runs <i>between</i>
            /// the two wheels of an axle and therefore cannot be known while either is being measured
            /// on its own. That ordering is the whole point of it existing: the friction budget below
            /// is this number times a grip coefficient, so a bar that never reached here could lean
            /// the body and change nothing about how much grip either end had.</para>
            /// </summary>
            public float NormalLoad;

            /// <summary>
            /// Where this tyre is on its own curve, 1 down to 0.
            ///
            /// <para>1 is a tyre inside its peak, holding everything it is asked for. It falls as the
            /// slip passes the peak and keeps falling past it, which is what makes it a genuine
            /// saturation figure — the old one was capacity over sideways speed and therefore said the
            /// least exactly when the tyre was sliding most.</para>
            ///
            /// <para>The squeal, the drift state and the overlay all read this rather than each
            /// deriving a slip figure of their own.</para>
            /// </summary>
            public float GripUsed = 1f;

            /// <summary>Sideways speed at the contact patch, m/s.</summary>
            public float LateralSlip;

            /// <summary>
            /// How fast this wheel is turning, rad/s. Positive is forwards.
            ///
            /// <para>The state the whole longitudinal half of the model hangs off. Without it there is
            /// no slip ratio, so there is no wheelspin, no lock-up, no differential and no engine
            /// braking — every one of those is a statement about the wheel turning at a different rate
            /// from the road, and until this existed the two were the same number by construction.</para>
            /// </summary>
            public float AngularVelocity;

            /// <summary>
            /// Longitudinal slip: how much faster the tread is moving than the road, as a fraction of
            /// road speed. Positive is spinning up, negative is locking.
            /// </summary>
            public float SlipRatio;

            /// <summary>Slip speed along the wheel's own plane, m/s at the contact patch.</summary>
            public float LongitudinalSlip;

            /// <summary>
            /// Where this tyre is on its curve: the length of its slip vector, with 1 the peak.
            /// </summary>
            public float Slip;

            /// <summary>
            /// The forces this tyre is actually making, newtons, in its own contact frame.
            ///
            /// <para>Held across steps because a tyre does not make its force instantly: the carcass
            /// has to wind up, over <see cref="VehicleConfig.RelaxationLength"/> of rolling. These are
            /// what gets applied; the curve computes what they are heading for.</para>
            /// </summary>
            public float Fx;

            /// <summary>The lateral half of <see cref="Fx"/>.</summary>
            public float Fy;

            /// <summary>What this wheel is standing on. <see cref="SurfaceKind.Asphalt"/> in the air.</summary>
            public SurfaceKind Surface = SurfaceKind.Asphalt;

            /// <summary>
            /// The collider the last surface answer came from, so the answer can be reused.
            ///
            /// <para>A wheel changes surface a handful of times a minute and asks four times a physics
            /// step, so this is a reference comparison standing in for a <c>GetComponent</c>. That is
            /// the difference between a lookup 240 times a second and one at the kerb.</para>
            /// </summary>
            public Collider SurfaceCollider;

            /// <summary>The tag found on <see cref="SurfaceCollider"/>, or null if it carries none.</summary>
            public GroundSurface SurfaceTag;
        }

        /// <summary>
        /// Ceiling on the reported acceleration, m/s². Comfortably above what the tyres can produce
        /// (a traction-limited launch is around 8.5), so it only ever catches impact spikes.
        /// </summary>
        private const float MaxLongitudinalAcceleration = 15f;

        /// <summary>
        /// Speed below which slip stops being measurable, m/s.
        ///
        /// <para>Slip ratio and slip angle are both a velocity divided by road speed, so both go to
        /// infinity at a standstill and a car parked on a hill would be asked for infinite grip. This
        /// is the floor on that divisor, and it decides two things against each other: how firmly the
        /// car stands still, and how stiff the force law gets at walking pace.</para>
        ///
        /// <para><b>It was one, and one stopped working when the grip went to arcade levels.</b> The
        /// tyre's stiffness against sideways velocity is the budget over this number, so nearly
        /// trebling the budget trebled the stiffness — and the body's lateral velocity is integrated
        /// once per physics step, outside the sub-stepped tyre solve, so there is nothing to catch it.
        /// Simulated across all ten cars at one, two and three times wheel load and from 0.3 to
        /// 65 m/s, a floor of 1 rings at low speed under load, and so does 1.5, 2 and 2.5 — the
        /// failure is a nonlinear limit cycle rather than a clean divergence, so the safe values are
        /// not even contiguous. From 3 upwards every relaxation length from 0.5 to 1.0 m is clean.
        /// Picked for the width of that region, not for its edge.</para>
        ///
        /// <para><b>And it is affordable now precisely because the grip is high.</b> Three was out of
        /// the question at 1.7 g — at five the car crept down the pass. At 2.8 g the tyre needs a
        /// three hundredth of its budget to hold station on a one-in-ten slope, which is about three
        /// millimetres a second of creep. The number that made the low-speed end expensive is the same
        /// number that paid for it.</para>
        /// </summary>
        private const float SlipReferenceSpeed = 3f;

        /// <summary>
        /// How many times the tyre and its wheel are advanced within one physics step.
        ///
        /// <para>Not accuracy for its own sake. The implicit term that keeps the wheel stable behaves as
        /// extra rotational inertia proportional to the sub-step, and at one step per frame that is
        /// about a hundred kilograms at the contact patch against a quarter car's three hundred — it
        /// cost 57 % of the braking force, and more as the car slowed. Eight brings it to 12 %.</para>
        ///
        /// <para>Cheap, because everything that varies with the body rather than the wheel — the tyre's
        /// stiffness, the damping factor, the relaxation blend — is computed once outside the loop, and
        /// what is left inside is one sine and one arctangent.</para>
        /// </summary>
        private const int SpinSubSteps = 8;

        /// <summary>
        /// How quickly the steering limit's idea of the car's grip follows the real one, per second.
        ///
        /// <para>Slow on purpose. The instantaneous capacity swings with every load transfer, and a
        /// steering stop that moved with it would read as the rack going light in the middle of a
        /// corner. This is meant to say what the car can do, not what this instant is doing.</para>
        /// </summary>
        private const float SteeringCapacityResponse = 1.5f;

        /// <summary>
        /// Floor on the grip the steering limit will assume, in g.
        ///
        /// <para>Covers the frames before any wheel has reported, and anything pathological. Not the
        /// airborne case — that is covered by holding the last grounded value, which keeps the lock the
        /// driver had rather than handing them a default in mid-air.</para>
        /// </summary>
        private const float MinSteeringCapacity = 0.6f;

        /// <summary>Speed below which no drift request is accepted, m/s. About 43 km/h.</summary>
        private const float DriftEntrySpeed = 12f;

        /// <summary>
        /// Corner radius at which a bootful of throttle counts fully as asking for a slide, metres.
        ///
        /// <para>Thirty is a hairpin or a tight serpentine; it fades to nothing by sixty. The world's
        /// own corners set the scale — the pass turns at 20 m, the Weissjoch at 26 to 36, the Steilufer
        /// at 38 to 70 — and a car at 180 km/h is driving a 90 m radius even at full lock, so the
        /// motorway and the fast sweepers are excluded by geometry rather than by a speed test.</para>
        /// </summary>
        private const float PowerDriftRadius = 30f;

        /// <summary>
        /// The same for braking, and deliberately more generous.
        ///
        /// <para>Braking hard into a medium-fast bend is a legitimate way to unstick a car, where
        /// standing on the throttle there is not. Only a straight has to be excluded, and a straight is
        /// an infinite radius.</para>
        /// </summary>
        private const float BrakeDriftRadius = 70f;

        /// <summary>How fast a drift request is taken up, per second. Under a fifth of a second.</summary>
        private const float DriftEntryRate = 6f;

        /// <summary>How fast it decays once the driver stops asking. Deliberately slower than entry.</summary>
        private const float DriftExitRate = 1.6f;

        /// <summary>How fast traction control takes torque away, per second. 50 ms to a full cut.</summary>
        private const float TractionCutRate = 20f;

        /// <summary>How fast it gives it back, per second. Four times slower than it takes it.</summary>
        private const float TractionRestoreRate = 5f;

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

        /// <summary>
        /// Distance between the front and rear axles, metres, as measured off the wheel anchors.
        ///
        /// <para>Published for the same reason as <see cref="SteerAngle"/>: it and the steer angle are
        /// the two halves of the Ackermann radius, and a reader that carried its own copy of either
        /// would be a second opinion about the shape of the car.</para>
        /// </summary>
        public float Wheelbase => wheelbase;

        /// <summary>Engine speed in rpm. Comes from the wheels through the gearbox, not from a guess.</summary>
        public float EngineRpm => engineRpm;

        /// <summary>
        /// Torque the engine is making right now, Nm at the crank — before the gearbox multiplies it.
        ///
        /// <para>Published rather than left inside <see cref="UpdateDrivetrain"/> because
        /// <see cref="FuelTank"/> needs exactly this number and a second copy of the expression would be
        /// a second thing to disagree. It is the torque curve, the peak and the pedal folded together,
        /// which is why the tank can burn fuel against load without reading the input at all.</para>
        ///
        /// <para><b>Zero on every path that makes no torque</b>, not merely on the one that computes it:
        /// mid-shift the drivetrain is disconnected and on the limiter the spark is cut. A value left
        /// standing from the frame before would have the tank burning fuel for work the engine is
        /// demonstrably not doing, at the exact moments the driver can hear that it is not.</para>
        /// </summary>
        public float EngineTorqueNm { get; private set; }

        /// <summary>
        /// True when there is a tank fitted and it is empty.
        ///
        /// <para>Read by <see cref="FixedUpdate"/> to cut the throttle, and worth stating plainly: this
        /// is the only thing in the vehicle that fuel does. It does not scale grip, or damp the body, or
        /// touch the brakes — an engine that has stopped simply stops pushing, and the car rolls exactly
        /// as it always did with the driver off the pedal.</para>
        /// </summary>
        public bool IsOutOfFuel => fuel != null && fuel.IsDry;

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
        /// How far the car is travelling sideways to the way it is pointing, in degrees, <b>signed</b>.
        ///
        /// <para>Positive means the car is travelling to the right of where its nose points, which is
        /// the tail having come round to the left. Taken from the body's own velocity rather than from
        /// a tyre, so it is the angle you would measure from outside the car — which is the one that
        /// matters for whether this reads as a drift. Zero below walking pace, where the direction of
        /// travel is noise.</para>
        ///
        /// <para>Readers that only want to know <i>how</i> sideways take the absolute value at the
        /// point of use. That is deliberate: a drift the driver is holding needs to know which way to
        /// point the wheels, and one property that is sometimes signed and sometimes not would be two
        /// numbers wearing one name.</para>
        /// </summary>
        public float SlipAngle { get; private set; }

        /// <summary>
        /// How hard the car is cornering right now, in g.
        ///
        /// <para>Centripetal, from the yaw rate the body actually has times its road speed — not from
        /// the steering geometry, which says what was asked for rather than what happened. Against
        /// <see cref="GripCapacityG"/> it is the only honest answer to "how close to the limit am I",
        /// and that ratio is what decides whether a throttle or brake input counts as asking for a
        /// slide.</para>
        /// </summary>
        public float LateralG { get; private set; }

        /// <summary>
        /// How much the driver is asking to be sideways, 0 to 1.
        ///
        /// <para><b>The one number that decides whether this car is on rails or loose</b>, and the
        /// reason a slide is now a choice rather than an accident. At zero the turn-in assist has full
        /// authority, the rear tyres have all their grip, traction control is sharp and the drift
        /// damper is asleep — the car goes where it is pointed. As it rises all four of those hand
        /// over together, so there is never a moment when two of them disagree about what the car is
        /// doing.</para>
        ///
        /// <para>Three things raise it, and all three are the driver saying so: the handbrake, braking
        /// hard while turning hard, and full throttle on a tight radius in a car with driven rear
        /// wheels. Nothing about the road, the speed or the tyres can raise it on their own.</para>
        ///
        /// <para>It rises faster than it falls. Being slow into a slide is a car that ignores the
        /// handbrake; being quick out of one is a car that snaps straight the instant you release, and
        /// the second is much the worse of the two to drive.</para>
        /// </summary>
        public float DriftIntent { get; private set; }

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
        /// A second world-level scale on every tyre, 1 in the dry. Owned by <c>WeatherDirector</c>.
        ///
        /// <para><b>A field of its own rather than a second writer of <see cref="GripScale"/>, and that
        /// is the rule stated above rather than a new one</b> — <i>whatever sets it owns putting it
        /// back</i>. Two owners cannot both honour that: <c>WaterHazard.Dry</c> writes 1 when the car
        /// leaves the water, and if rain had been sharing that number a car climbing out of a lake in a
        /// downpour would have got full grip back and kept it. Each factor has exactly one writer, and
        /// they multiply.</para>
        ///
        /// <para>Three factors now, and each answers a different question: what the world has done to
        /// the car (<see cref="GripScale"/>), what the sky is doing (this), and what this one wheel is
        /// standing on (<see cref="SurfaceGrip"/>, applied per wheel rather than car-wide).</para>
        /// </summary>
        public float WeatherGrip { get; set; } = 1f;

        /// <summary>
        /// The average grip multiplier the surfaces under the grounded wheels are asking for, 1 on
        /// tarmac. Read-only — this is measured, not set.
        ///
        /// <para><b>Published as an average and applied per wheel.</b> The force actually uses each
        /// wheel's own surface, which is what makes dropping the two right-hand wheels onto the verge
        /// pull the car towards it rather than simply making the whole car slippery. This figure exists
        /// for the readouts and for the rumble, where one number is the honest answer to "how much of
        /// the car is off the road".</para>
        /// </summary>
        public float SurfaceGrip { get; private set; } = 1f;

        /// <summary>
        /// How rough what the wheels are rolling over is, 0 on tarmac and 1 on a gravel verge.
        ///
        /// <para>Averaged over grounded wheels, so two wheels on the verge is half. Zero in the air,
        /// which matters: a car that has left the road entirely is not rumbling.</para>
        /// </summary>
        public float SurfaceRoughness { get; private set; }

        /// <summary>
        /// Which off-tarmac surface the car is on: 1 loose stone, 0 soft ground, blended where the wheels
        /// disagree. Held at its last value on tarmac, where the question does not arise.
        ///
        /// <para><b>Weighted by roughness rather than by wheel count, and that is the whole of it.</b>
        /// The number is spent crossfading two loops whose common level is <see cref="SurfaceRoughness"/>,
        /// so the share that matters is the share of the *noise* rather than the share of the wheels —
        /// otherwise a wheel resting on tarmac, contributing nothing to either clip, would still get a
        /// vote on what the other three sound like.</para>
        ///
        /// <para><b>Held rather than reset, because a reset is audible and this is not.</b> With four
        /// wheels back on tarmac the level is zero and the blend is inaudible, so putting it back to
        /// either end would be a decision nobody can hear — until the car returns to a verge, when a
        /// blend snapping from grass to gravel underneath a level that is already rising is exactly the
        /// wrong moment to move it.</para>
        /// </summary>
        public float SurfaceGrit { get; private set; }

        /// <summary>
        /// Raised when the body hits something, with a severity of 0 to 1 and where it was struck.
        ///
        /// <para>The shape <c>EngineAudio.Banged</c> already uses, and for the same reason: the sound
        /// and the camera kick are two consumers of one event, and a second opinion about how hard the
        /// car was hit would be a crash you can hear but not feel.</para>
        /// </summary>
        public event System.Action<float, Vector3> Impacted;

        /// <summary>
        /// How fast the body is sliding along whatever it is touching, m/s. Zero when touching nothing.
        ///
        /// <para>Its own value rather than part of <see cref="Impacted"/>, because a scrape is a state
        /// and an impact is an event. A car leaning on a barrier through a long corner is one continuous
        /// noise, and delivering it as a stream of events would be a stream of bangs.</para>
        /// </summary>
        public float ScrapeSpeed { get; private set; }

        /// <summary>
        /// True when the car is meaningfully sideways *and* the rear tyres are the reason.
        ///
        /// Both halves are needed. Slip angle alone counts a car sliding bodily down a wet camber,
        /// which is not a drift; lost rear grip alone counts a standstill wheelspin, which is not one
        /// either.
        /// </summary>
        public bool IsDrifting =>
            config != null && Mathf.Abs(SlipAngle) > config.DriftSlipAngle && RearGrip < 0.92f;

        private bool reversing;

        /// <summary>Scrape speed seen since the last physics step, m/s. Collected, then published.</summary>
        private float scrapeThisStep;

        /// <summary>No impact is reported before this time. See <see cref="Teleport"/>.</summary>
        private float impactsSuppressedUntil;

        /// <summary>How long a placement is given to settle before impacts count again, seconds.</summary>
        private const float PlacementSettleSeconds = 0.35f;

        /// <summary>Closing speed below which a contact is not an impact, m/s. Walking pace.</summary>
        private const float MinImpactSpeed = 1.6f;

        /// <summary>Closing speed that reports a severity of 1, m/s. About 65 km/h into a wall.</summary>
        private const float FullImpactSpeed = 18f;

        /// <summary>
        /// The most lateral acceleration all four tyres together could hold right now, in g.
        ///
        /// <para>Measured rather than looked up: it is the sum of the four friction budgets the tyres
        /// themselves were given this step, over the car's weight. That matters because the grip
        /// coefficient is a function of wheel load now, so the honest ceiling moves with weight
        /// transfer, with downforce, with the surface under each wheel and with the rain — and a reader
        /// that evaluated the grip curve for itself would get an answer for a car standing still and
        /// level.</para>
        /// </summary>
        public float GripCapacityG { get; private set; }

        /// <summary>
        /// One wheel's slip ratio: how much faster its tread is moving than the road, as a fraction.
        /// Positive is spinning up, negative is locking, zero is rolling. Order is FL, FR, RL, RR.
        /// </summary>
        public float SlipRatioAt(int index) =>
            index >= 0 && index < WheelCount ? wheels[index].SlipRatio : 0f;

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
        /// combining the sideways slide with <see cref="WheelState.LongitudinalSlip"/> — the difference
        /// between how fast the tread is moving and how fast the road is going under it. The two are
        /// perpendicular, so they combine as a hypotenuse rather than a sum.</para>
        ///
        /// <para>The sideways half is gated on how sideways the <i>car</i> is, not on the wheel's own
        /// sideways speed, and the difference is the whole of whether this effect is believable.</para>
        ///
        /// <para><b>What makes the wheel's own figure useless on its own.</b> A steered front wheel is
        /// dragged across its own plane by the mere act of turning: at 100 km/h and 30° of lock that is
        /// 14 m/s at the patch before the car has begun to yaw, which is more sideways speed than a
        /// genuine drift produces. Turning the wheel was enough to lay smoke.</para>
        ///
        /// <para><see cref="SlipAngle"/> is the honest gate: it measures the car's direction of travel
        /// against where it is pointing, so steering does not move it and only actually sliding does.</para>
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

            float sliding = wheel.LateralSlip * BodySlideFraction();
            slipSpeed = Mathf.Sqrt(sliding * sliding + wheel.LongitudinalSlip * wheel.LongitudinalSlip);
            return true;
        }

        /// <summary>
        /// How far the car is past cornering and into sliding, 0 to 1, from its body slip angle.
        ///
        /// <para>Ramped either side of <see cref="VehicleConfig.DriftSlipAngle"/> rather than switched
        /// at it, so smoke arrives with the slide instead of appearing whole the instant a threshold is
        /// crossed. Zero below half that angle, which is where ordinary hard cornering lives.</para>
        /// </summary>
        private float BodySlideFraction()
        {
            if (config == null)
            {
                return 0f;
            }

            float drift = Mathf.Max(1f, config.DriftSlipAngle);
            // Asking to be sideways lowers the bar; it does not clear it. Maxing the fraction with the
            // intent outright meant the smoke came on with the request rather than with the slide, so a
            // car that was merely being asked nicely smoked while still going perfectly straight. The
            // car still has to be sliding — it just counts sooner once the driver has committed, which
            // is what keeps a handbrake turn smoking from its first degree.
            float lower = Mathf.Lerp(drift * 0.5f, drift * 0.15f, DriftIntent);
            float upper = Mathf.Lerp(drift * 1.5f, drift * 0.6f, DriftIntent);

            return Mathf.Clamp01(Mathf.InverseLerp(lower, upper, Mathf.Abs(SlipAngle)));
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

                // The wheels have to be stopped too, and it is not tidiness. A car placed at a
                // standstill with its wheels still turning at the speed it was doing before is a car
                // whose tyres are asked for their whole budget on the first step, in whatever
                // direction it now happens to be pointing.
                wheels[i].AngularVelocity = 0f;
                wheels[i].SlipRatio = 0f;
                wheels[i].LongitudinalSlip = 0f;
                wheels[i].LateralSlip = 0f;
                wheels[i].Fx = 0f;
                wheels[i].Fy = 0f;
            }

            tractionCut = 1f;
            DriftIntent = 0f;

            // A car put down on a road is put down *inside* whatever it lands touching, and PhysX
            // reports the push-out as a contact. Every placement — the start screen, the pause menu's
            // Move to, a respawn out of the water — would otherwise open with a bang and a shaken
            // camera. The window is short because it only has to cover the settling, not the drive.
            impactsSuppressedUntil = Time.time + PlacementSettleSeconds;
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

            // Published here rather than in OnCollisionStay, because that runs *after* the step and may
            // run several times or not at all. Collecting into a field and reading it once a step gives
            // the scrape one value per step, which is what a continuous noise needs — sampled straight
            // from the callback it would drop to zero on every step with no contact in it and buzz.
            ScrapeSpeed = scrapeThisStep;
            scrapeThisStep = 0f;

            Vector3 velocity = body.linearVelocity;
            forwardSpeed = Vector3.Dot(velocity, transform.forward);

            UpdateLongitudinalAcceleration(deltaTime);

            LateralG = Mathf.Abs(Vector3.Dot(body.angularVelocity, transform.up) * forwardSpeed)
                       / Physics.gravity.magnitude;

            // Before anything reads it. Six things do, and they all have to see the same value in the
            // same step or the car will be half-loose and half-planted.
            UpdateDriftIntent(deltaTime, drive);

            // Lock available at this speed, plus whatever the slide has earned back.
            //
            // Two reasons to hand the lock back, and they are different questions. The slip term
            // catches a slide that has already started, which is a recovery; the intent term hands it
            // back the moment the driver asks, which is a request. Without the second, a handbrake turn
            // at speed begins with a fraction of the lock and the car cannot be pointed anywhere.
            float lock01 = AvailableLock();
            float slide = 0f;
            if (Mathf.Abs(SlipAngle) > config.DriftSlipAngle)
            {
                slide = Mathf.InverseLerp(
                    config.DriftSlipAngle, config.DriftSlipAngle + 25f, Mathf.Abs(SlipAngle));
            }

            float freed = Mathf.Max(slide, DriftIntent) * config.CountersteerAuthority;
            if (freed > 0f)
            {
                lock01 = Mathf.Lerp(lock01, 1f, freed);
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

            // A dry tank is a dead engine, and a dead engine is exactly a driver who cannot press the
            // pedal any more. Cutting the command here rather than zeroing the drive force further down
            // keeps that true all the way through: the gearbox still selects, the rev counter still
            // reads what the wheels are turning the engine at, and the brakes and steering are
            // untouched. What the player gets is a car that coasts, which is what running out of fuel
            // actually feels like.
            if (IsOutOfFuel)
            {
                throttle = 0f;
                reverse = 0f;
            }

            // Published because the pedal alone cannot tell the two apart, and anything downstream that
            // treats reversing as braking gets it wrong — brake lights being the obvious one.
            BrakeInput = brake;

            float driveTorquePerWheel = UpdateDrivetrain(deltaTime, throttle, reverse, brake);

            // Three passes, and the order is the anti-roll bar's whole reason for being here.
            //
            // The bar acts *between* the two wheels of an axle, so what either tyre is standing on
            // cannot be known until both springs have been measured. Measure all four, let the bars
            // move load across, and only then ask the tyres what they can hold: that is what makes a
            // bar a balance adjustment rather than a body-lean adjustment. It used to be a torque
            // couple applied after the tyres had already been paid, which leaned the car exactly as
            // it does now and left every friction budget in the car untouched.
            for (int i = 0; i < WheelCount; i++)
            {
                SuspendWheel(i, deltaTime);
            }

            TransferAntiRoll(FrontLeft, FrontRight);
            TransferAntiRoll(RearLeft, RearRight);

            gripBudget = 0f;
            for (int i = 0; i < WheelCount; i++)
            {
                DriveWheel(i, deltaTime, driveTorquePerWheel, brake, drive.Handbrake);
            }

            GripCapacityG = gripBudget / Mathf.Max(1f, config.Mass * Physics.gravity.magnitude);

            // What the steering limit reads. Held while airborne so the lock survives a crest, and
            // eased so it does not wander with the load transfer of the corner it is in.
            if (GroundedWheelCount > 0)
            {
                steeringCapacity = steeringCapacity <= 0f
                    ? GripCapacityG
                    : Mathf.Lerp(
                        steeringCapacity,
                        GripCapacityG,
                        1f - Mathf.Exp(-SteeringCapacityResponse * deltaTime));
            }

            UpdateSurfaceState();

            ApplyAxisDamping();

            UpdateSlipState(velocity);
            ApplyTurnInAssist();
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
        /// <see cref="GripCapacityG"/> times g over speed, the fastest the tyres could turn the car at
        /// all. That ceiling is measured from the four budgets the tyres were actually handed this
        /// step, not looked up: the grip coefficient is a function of wheel load, so a ceiling read off
        /// the curve would be a figure for a car standing still and level. So the assist never asks for
        /// rotation the tyres could not have produced, and a corner taken too fast still runs wide.
        /// What it removes is the lag, not the limit.</para>
        ///
        /// <para><b>Why it stops when the driver asks to slide.</b> While <see cref="DriftIntent"/> is
        /// up the car belongs to <c>DriftYawDamping</c> and <c>CountersteerAuthority</c>. Leaving this
        /// one running there would put two controllers on the yaw axis with opposite ideas — one trying
        /// to reach a target rate, the other trying to bleed rate off — and a slide would neither hold
        /// nor catch. The two fade against the same number and sum to one, so there is no moment when
        /// both are half in charge and none when neither is.</para>
        ///
        /// <para>It used to fade on the slip angle instead, which decided the handover by the symptom
        /// rather than by the cause: the assist switched itself off whenever the car got loose, for any
        /// reason, including the reasons an arcade car is not supposed to get loose for.</para>
        /// </summary>
        private void ApplyTurnInAssist()
        {
            if (config.TurnInAssist <= 0f || DriftIntent >= 1f || GroundedWheelCount == 0)
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

            // Hands over to the drift assists on the driver's request rather than on how sideways the
            // car has become. Fading on slip meant the assist switched itself off whenever the car got
            // loose for any reason — including the reasons an arcade car is not supposed to get loose
            // for — and the handover was decided by the symptom instead of the cause.
            float authority = GroundedWheelCount * 0.25f * (1f - DriftIntent);

            if (authority <= 0f)
            {
                return;
            }

            // What the front wheels are geometrically pointing at.
            float target = forwardSpeed * Mathf.Tan(steerAngle * Mathf.Deg2Rad) / wheelbase;

            // What the tyres could actually hold: a_lat = mu * g, and yaw rate = a_lat / v.
            //
            // From the budgets the tyres were actually given this step rather than from the grip curve.
            // The curve is keyed on wheel load now, so evaluating it here would mean picking a load to
            // evaluate it at — and the only honest answer is the one the four wheels have already
            // worked out between them, including the load the corner itself has moved across them.
            float ceiling = GripCapacityG * Physics.gravity.magnitude / speed;
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
        private float UpdateDrivetrain(float deltaTime, float throttle, float reverse, float brake)
        {
            // Cleared up front so that every early return below leaves it honest. There are three of
            // them — mid-shift, over the limiter, and no pedal at all — and each is a moment the engine
            // makes nothing; see the note on the property.
            EngineTorqueNm = 0f;

            if (shiftTimer > 0f)
            {
                shiftTimer -= deltaTime;
            }

            reversing = reverse > 0f;

            float ratio = reversing ? config.RatioForGear(-1) : config.RatioForGear(gearIndex);
            float driveRatio = Mathf.Abs(ratio) * config.FinalDrive;

            // Two wheel speeds, because the engine and the gearbox are asking different questions.
            //
            // The *engine* is turned by the driven wheels, and a wheel that is spinning is turning
            // faster than the car is moving. That is the flare, and it is not modelled — it is simply
            // not hidden any more, because the wheel really is going that fast.
            //
            // The *gearbox* must not read that number. It is choosing a ratio for the speed the car is
            // travelling at, and a spinning wheel says the car is doing 90 km/h when it is doing 15.
            // Fed the wheels, the box upshifted on the flare, found the road speed did not justify the
            // gear, kicked back down, span up again and hunted — the powerful rear-drive cars never got
            // out of second, because second is where a wheel that has broken traction always says it
            // wants to be. A gear is a statement about road speed, and this is where it comes from.
            float roadWheelRpm = Mathf.Abs(forwardSpeed) / (2f * Mathf.PI * config.WheelRadius) * 60f;
            float geared = roadWheelRpm * driveRatio;
            float spinning = DrivenWheelRpm() * driveRatio;

            // Rolling away from a standstill the clutch or converter slips, so the engine can rev while
            // the wheels barely turn. Without this the car has no voice at all until it is moving.
            float command = Mathf.Max(throttle, reverse);
            float slipping = Mathf.Lerp(config.IdleRpm, config.RedlineRpm * 0.55f, command);
            float blend = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / 3.5f);

            engineRpm = Mathf.Clamp(
                Mathf.Max(spinning, Mathf.Lerp(slipping, config.IdleRpm, blend)),
                config.IdleRpm,
                config.RedlineRpm);

            // A stopped engine is the one case where the idle floor above is wrong. Everywhere else it
            // is right — a running engine never falls below its idle speed, and clamping there is what
            // keeps the rev counter off zero and the engine note alive at a standstill. But with no fuel
            // there is no combustion holding it up: what is left is the wheels back-driving it through
            // the gearbox, so the needle follows `geared` down and reaches zero when the car does.
            // Without this the tacho sits at idle and the exhaust keeps burbling next to a car the
            // driver cannot move, which reads as a broken throttle rather than an empty tank.
            if (IsOutOfFuel)
            {
                engineRpm = Mathf.Min(engineRpm, spinning);
            }

            // A stalled engine is held out of the shift logic, and it is not a detail. The thresholds
            // below compare engine speed against a downshift rpm, and an engine falling towards zero is
            // under every one of them — so the box would walk itself down to first while the car coasts.
            // That is wrong on its own terms, and it also feeds back: `geared` is the ratio times the
            // wheel speed, so first gear at 100 km/h reads a very high number, and the needle that had
            // just fallen to zero would swing back up. A car that stalls holds the gear it stalled in.
            if (!IsOutOfFuel && !reversing && shiftTimer <= 0f)
            {
                // Shift points move with the pedal. A fixed threshold is a full-throttle threshold, and
                // applying it to a trailing throttle is what leaves the car pinned near the redline in a
                // low gear at town speeds — harmless with four long gears, unbearable with six short
                // ones. Interpolating on command is the whole of the fix: stamp on it and nothing
                // changes, lift off and the box short-shifts the way a driver would.
                // Braking counts as asking for a lower gear, exactly as the throttle does.
                //
                // It was left out, and the effect was the one thing a gearbox must never do: slow from
                // 100 to 50 and the box held sixth almost the whole way down, because with the throttle
                // shut it was reading the coasting threshold. The gear you needed was then chosen only
                // once you asked for drive — which is a second too late, because you are already asking.
                // A driver going for the brakes is a driver about to want a gear.
                float shiftDemand = Mathf.Max(command, brake);

                float upshiftRpm = Mathf.Lerp(
                    config.PartThrottleUpshiftRpm, config.UpshiftRpm, shiftDemand);
                float downshiftRpm = Mathf.Lerp(
                    config.PartThrottleDownshiftRpm, config.DownshiftRpm, shiftDemand);

                // Against `geared` rather than `engineRpm`: the second carries the flare, and a box
                // that upshifted on wheelspin would be choosing a gear for a road speed the car has
                // not reached.
                if (geared >= upshiftRpm && gearIndex < config.ForwardGearCount - 1)
                {
                    // Upshifts stay one at a time. Accelerating walks through the gears anyway, and
                    // skipping one would step over the engine speed that justified it.
                    gearIndex++;
                    shiftTimer = config.ShiftTime;
                }
                else if (command >= KickdownDemand && gearIndex > 0
                         && TryKickdown(roadWheelRpm, upshiftRpm, out int kicked))
                {
                    gearIndex = kicked;
                    shiftTimer = config.ShiftTime;
                }
                else if (geared <= downshiftRpm && gearIndex > 0)
                {
                    // Downshifts go straight to the right gear instead of walking down to it.
                    //
                    // One gear per shift cost ShiftTime of *zero drive* per step, so answering a pedal
                    // in sixth at town speed meant three quarters of a second of nothing while the box
                    // counted its way down — the pause being longest exactly when the driver had asked
                    // for the most. A real automatic kicks down two or three gears in one action for
                    // this reason. The search stops at the first gear that clears the threshold, so it
                    // can never drop further than a sequence of single steps would have.
                    int target = gearIndex;
                    while (target > 0
                           && roadWheelRpm * config.RatioForGear(target) * config.FinalDrive
                              <= downshiftRpm)
                    {
                        target--;
                    }

                    if (target != gearIndex)
                    {
                        gearIndex = target;
                        shiftTimer = config.ShiftTime;
                    }
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
            //
            // Against road speed, not against the driven wheels. An engine spun past its redline by a
            // wheel that has broken traction is real, but cutting the torque for it puts a 50 Hz square
            // wave through the drivetrain for as long as the wheelspin lasts — and every downstream
            // reader, the gearbox included, then sees a number swinging between over the redline and
            // under the downshift point. Wheelspin is traction control's job; this one is about the top
            // of the gearing.
            if (geared > config.RedlineRpm)
            {
                return 0f;
            }

            float torqueFraction = config.TorqueByRpm.Evaluate(RpmNormalized);
            float engineTorque = config.MaxTorqueNm * Mathf.Max(0f, torqueFraction) * command
                               * UpdateTractionControl(deltaTime);

            if (engineTorque <= 0f)
            {
                return 0f;
            }

            EngineTorqueNm = engineTorque;

            // Torque at the wheel, not force at the road. It used to be a force, which was the same
            // thing divided by the radius and handed straight to the tyre — and that quietly assumed
            // the wheel was always turning at exactly road speed, because there was nowhere else for
            // the torque to go. Now there is: it spins the wheel, the wheel slips against the road, and
            // the road decides how much of it becomes acceleration. That is the whole difference
            // between a car that cannot spin its wheels and one that can.
            float wheelTorque = engineTorque * driveRatio * config.DrivetrainEfficiency;

            if (reversing)
            {
                if (forwardSpeed <= -config.ReverseSpeed)
                {
                    return 0f;
                }

                wheelTorque = -wheelTorque;
            }

            return wheelTorque / Mathf.Max(1, config.DrivenWheelCount);
        }

        /// <summary>
        /// How hard the driver has to ask before the box will drop a gear it has no rpm reason to drop.
        ///
        /// <para>0.85 is the detent. A real automatic's kickdown is a switch under the end of the pedal
        /// travel, not a proportional thing, and that is the right model here: below this the box is
        /// left alone to short-shift and cruise, and past it the driver has unambiguously asked for
        /// everything the car has.</para>
        /// </summary>
        private const float KickdownDemand = 0.85f;

        /// <summary>
        /// How much more thrust a lower gear must offer before it is worth the shift.
        ///
        /// <para>Six per cent. Two adjacent gears are often within a percent or two of each other at a
        /// given road speed, and a box that took every one of those would shuffle continuously for
        /// nothing — while each shift costs <see cref="VehicleConfig.ShiftTime"/> of no drive at all,
        /// so a marginal downshift is a loss twice over.</para>
        /// </summary>
        private const float KickdownGain = 1.06f;

        /// <summary>
        /// How far below the upshift point a candidate gear has to land, as a fraction of it.
        ///
        /// <para>Nine tenths, and the tenth is not caution — it is the difference between the right gear
        /// and one that is right for half a second. Allowing a candidate all the way to the upshift point
        /// let the box answer a floored pedal at 129 km/h by dropping two gears into fourth at 5240 rpm,
        /// which pulls seven per cent harder than fifth and then hits the upshift 0.6 s later. Six
        /// hundred milliseconds of extra thrust, bought with another <see cref="VehicleConfig.ShiftTime"/>
        /// of none at all: measurably slower, and it feels like the box changing its mind. With the
        /// headroom it goes to fifth and stays there.</para>
        /// </summary>
        private const float KickdownHeadroom = 0.90f;

        /// <summary>
        /// Picks the gear that would actually pull hardest at this road speed, if it is not this one.
        ///
        /// <para><b>The gap this fills.</b> Every other rule in the box is about engine speed, and engine
        /// speed cannot tell a sixth gear that is right from one that is wrong. Run fourth to the redline
        /// at 133 km/h, lift, and the part-throttle rule short-shifts up through fifth into sixth — which
        /// is correct, and is what a driver lifting off wants. But it leaves the engine at 3272 rpm, and
        /// the downshift threshold is 2100. Get back on the throttle and nothing happens: the box has no
        /// rpm reason to move, so it sits in top making three quarters of the thrust fifth would give,
        /// and the car does not accelerate.</para>
        ///
        /// <para><b>Chosen by thrust, not by a shift map.</b> Wheel force is engine torque times the
        /// ratio, and the torque curve is already on the config — so the question "which gear pulls
        /// hardest here" has an actual answer rather than a table of speeds somebody tuned. The final
        /// drive and the wheel radius are common to every candidate and drop out of the comparison.</para>
        ///
        /// <para><b>The ceiling is what keeps it from hunting.</b> A gear is only a candidate if it lands
        /// a clear <see cref="KickdownHeadroom"/> below the same <paramref name="upshiftRpm"/> the rule
        /// above would upshift at; without that the box drops into gears it has to leave again almost
        /// immediately. Lower gears only ever rev higher, so the walk can stop at the first one that
        /// fails.</para>
        /// </summary>
        private bool TryKickdown(float wheelRpm, float upshiftRpm, out int gear)
        {
            gear = gearIndex;

            float best = ThrustInGear(gearIndex, wheelRpm) * KickdownGain;

            for (int candidate = gearIndex - 1; candidate >= 0; candidate--)
            {
                float rpm = wheelRpm * config.RatioForGear(candidate) * config.FinalDrive;
                if (rpm >= upshiftRpm * KickdownHeadroom)
                {
                    break;
                }

                float thrust = ThrustInGear(candidate, wheelRpm);
                if (thrust > best)
                {
                    best = thrust;
                    gear = candidate;
                }
            }

            return gear != gearIndex;
        }

        /// <summary>
        /// How much of the engine's torque the driven wheels can currently use, 0 to 1.
        ///
        /// <para><b>It exists because first gear asks for twice what the road can give.</b> 570 Nm
        /// through 4.20 × 4.09 on a 0.44 m wheel is about 22 kN of tractive force against a car that
        /// weighs 12 kN. That was equally true before the wheels had a speed of their own — the
        /// difference is only that the excess used to be clamped away in silence and now it spins the
        /// tyre. With the Auto pedals pinning the throttle to 1, a car left to it never stops
        /// spinning.</para>
        ///
        /// <para><b>It does nothing at all until the tyre is past its peak.</b> Wheelspin is one of the
        /// best things the new model has to show, so the cut only begins at
        /// <see cref="VehicleConfig.PeakSlipRatio"/> and is complete at three times it: the flare, the
        /// smoke and the noise all survive, and what goes is the runaway beyond them.</para>
        ///
        /// <para><b>Fast to cut and slow to restore</b>, because the two are different mistakes. Being
        /// late to cut is a car that is already sideways; being early to restore is a car that spins
        /// again immediately, and a cut that chattered at the step rate would be the stutter this was
        /// written to remove.</para>
        ///
        /// <para>Reads the slip the wheels reported last step, which is the same lag every consumer of
        /// the drivetrain already has and is a fifth of the time it takes to act.</para>
        /// </summary>
        private float UpdateTractionControl(float deltaTime)
        {
            if (!config.TractionControl)
            {
                tractionCut = 1f;
                return 1f;
            }

            float worst = 0f;
            for (int i = 0; i < WheelCount; i++)
            {
                if (config.IsDriven(i))
                {
                    worst = Mathf.Max(worst, wheels[i].SlipRatio);
                }
            }

            float peak = Mathf.Max(0.01f, config.PeakSlipRatio);
            float wanted = 1f - Mathf.InverseLerp(peak, peak * 3f, worst);

            // And it gets out of the way when the driver asks to be sideways. Power oversteer is the
            // one drift entry that this assist would otherwise make impossible — it exists to stop
            // exactly the wheelspin that entry is made of — so the request that opens the drift has to
            // be the request that stands it down.
            //
            // Only for a committed request, though. Scaled straight off the intent, half a request
            // handed back half the wheelspin, and a car merely near its limit in a fast corner smoked
            // its tyres for nothing. Nothing happens below a half.
            wanted = Mathf.Lerp(wanted, 1f, Mathf.InverseLerp(0.5f, 1f, DriftIntent));

            float rate = wanted < tractionCut ? TractionCutRate : TractionRestoreRate;
            tractionCut = Mathf.MoveTowards(tractionCut, wanted, rate * deltaTime);

            return tractionCut;
        }

        /// <summary>
        /// Mean speed of the driven wheels in rpm, ignoring direction.
        ///
        /// <para>Over the driven wheels only, because they are the ones the gearbox is connected to.
        /// A front-driven car with its front wheels alight is at the redline whatever the rears are
        /// doing, and averaging all four would quietly halve the flare.</para>
        ///
        /// <para>Airborne wheels are counted rather than skipped. A driven wheel with nothing under it
        /// is exactly the case where the engine should be screaming, and dropping it from the average
        /// would silence the one moment it has something to say.</para>
        /// </summary>
        private float DrivenWheelRpm()
        {
            float total = 0f;
            int counted = 0;

            for (int i = 0; i < WheelCount; i++)
            {
                if (!config.IsDriven(i))
                {
                    continue;
                }

                total += Mathf.Abs(wheels[i].AngularVelocity);
                counted++;
            }

            if (counted == 0)
            {
                return 0f;
            }

            return total / counted * (60f / (2f * Mathf.PI));
        }

        /// <summary>
        /// Wheel force a gear would make at this road speed, in units that only mean anything against
        /// each other.
        /// </summary>
        private float ThrustInGear(int gear, float wheelRpm)
        {
            float ratio = Mathf.Abs(config.RatioForGear(gear));
            float rpm = wheelRpm * ratio * config.FinalDrive;
            float fraction = Mathf.Clamp01(rpm / Mathf.Max(1f, config.RedlineRpm));

            return Mathf.Max(0f, config.TorqueByRpm.Evaluate(fraction)) * ratio;
        }

        /// <summary>
        /// Casts this wheel's ray and works out what it is standing on, without applying anything.
        ///
        /// <para>The first of the step's three wheel passes. Nothing here touches the rigidbody,
        /// because the anti-roll bars have still to move load across the axles and the tyre's whole
        /// friction budget is the number this leaves in <see cref="WheelState.NormalLoad"/>.</para>
        /// </summary>
        private void SuspendWheel(int index, float deltaTime)
        {
            Transform anchor = wheelAnchors[index];
            if (anchor == null)
            {
                return;
            }

            WheelState wheel = wheels[index];

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
                wheel.NormalLoad = 0f;
                return;
            }

            wheel.Grounded = true;
            wheel.ContactPoint = hit.point;
            wheel.ContactNormal = hit.normal;
            wheel.Surface = ResolveSurface(wheel, hit);

            // --- Suspension: spring pushes out of compression, damper resists the rate of change.
            float springLength = Mathf.Clamp(hit.distance - config.WheelRadius, 0f, config.SuspensionRestLength);
            float compression = config.SuspensionRestLength - springLength;

            // Clamped, and a step in the road is the whole reason. A raycast wheel does not roll up a
            // kerb, it arrives on top of one: the ray finds ground 14 cm higher between one step and the
            // next, and this finite difference reads that as seven metres a second of shaft speed. At
            // the damper rates these cars run that is tens of kilonewtons on one corner in one step,
            // which throws the car into the air rather than lifting a wheel over a kerb.
            //
            // Not a config field, deliberately. It is not a property of any car — it is the speed above
            // which this difference stops describing a damper and starts describing the size of the
            // timestep. Ordinary bumps at speed reach two to three metres a second, so nothing a road
            // does touches it.
            float compressionVelocity = Mathf.Clamp(
                (wheel.SpringLength - springLength) / deltaTime, -MaxDamperSpeed, MaxDamperSpeed);

            wheel.SpringLength = springLength;
            wheel.Compression01 = compression / config.SuspensionRestLength;

            wheel.NormalLoad = Mathf.Max(
                0f,
                compression * config.SuspensionStiffness + compressionVelocity * config.SuspensionDamping);
        }

        /// <summary>
        /// Applies what this wheel is standing on, and what its tyre makes of that.
        ///
        /// <para>Runs after every spring has been measured and both anti-roll bars have moved load
        /// across their axles, so <see cref="WheelState.NormalLoad"/> is the finished figure — which is
        /// why the vertical force and the friction budget below are the same number rather than two
        /// numbers that agree by construction.</para>
        /// </summary>
        private void DriveWheel(
            int index,
            float deltaTime,
            float driveTorquePerWheel,
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

            if (!wheel.Grounded)
            {
                // A wheel in the air still answers the throttle and the brake, and it is the one place
                // a driven wheel can run away with itself entirely. That is worth seeing rather than
                // hiding: a car that lands with its rear wheels spinning is telling the truth about
                // what the driver did over the crest.
                wheel.Fx = 0f;
                wheel.Fy = 0f;
                wheel.Slip = 0f;
                wheel.SlipRatio = 0f;
                wheel.LongitudinalSlip = 0f;

                // Zero budget, so the tyre makes nothing and only the drive and the brake reach the
                // wheel — which is the whole point of running this at all in the air.
                SolveTyre(index, wheel, deltaTime, driveTorquePerWheel, brake, handbrake, 0f, 0f, 0f);

                UpdateWheelVisual(
                    index,
                    config.SuspensionRestLength,
                    wheelSteer,
                    wheel.AngularVelocity * config.WheelRadius,
                    deltaTime);
                return;
            }

            // The bar's roll moment is not applied separately any more, and must not be: it is the
            // difference between the two wheels of an axle pushing at their own contact points, which
            // is what a real bar does and what this line now produces on its own. Adding the couple as
            // well would be the bar counted twice.
            float suspensionForce = wheel.NormalLoad;
            body.AddForceAtPosition(transform.up * suspensionForce, wheel.ContactPoint);

            // --- Tyre frame, projected onto the surface so slopes behave.
            Vector3 normal = wheel.ContactNormal;
            Vector3 steered = Quaternion.AngleAxis(wheelSteer, transform.up) * transform.forward;
            Vector3 forward = Vector3.ProjectOnPlane(steered, normal).normalized;
            Vector3 right = Vector3.Cross(normal, forward);

            Vector3 pointVelocity = body.GetPointVelocity(wheel.ContactPoint);
            float lateralVelocity = Vector3.Dot(pointVelocity, right);
            float longitudinalVelocity = Vector3.Dot(pointVelocity, forward);
            // --- How much this tyre has to spend.
            //
            // Two multipliers and they mean different things. GripScale is what the world has done to
            // the car and applies to all four wheels at once — water, and rain. The surface is what
            // this one wheel is standing on, so a car with two wheels on the verge is pulled towards
            // it rather than made uniformly slippery. Folding the two into one number was the first
            // version and it lost exactly that.
            //
            // The coefficient is looked up on this wheel's own load rather than on road speed, which
            // is what makes weight transfer cost something: an axle that takes load across itself ends
            // up with less total grip than one that did not, because the outer tyre gives back less
            // per newton than the inner one loses.
            float staticLoad = config.Mass * 0.25f * Physics.gravity.magnitude;
            float loadRatio = suspensionForce / Mathf.Max(1f, staticLoad);

            float mu = config.LateralGrip.Evaluate(loadRatio)
                * GripScale
                * WeatherGrip
                * GroundSurface.GripOf(wheel.Surface);

            // Wider tyres on the back. One number, and it is what decides which end of the car gives up
            // first — which is in turn what makes a big steering angle safe to hand a driver. With both
            // axles saturating together, every input past the limit was a coin toss between pushing
            // wide and swapping ends.
            if (!isFront)
            {
                mu *= config.RearGripBias;
            }

            // What the rear tyres give up while the driver is asking to be sideways. This used to be a
            // handbrake-only multiplier, which meant the handbrake was the only way in; it is the same
            // number doing the same job, asked for by the same request that moves everything else.
            if (!isFront && DriftIntent > 0f)
            {
                mu *= Mathf.Lerp(1f, config.DriftRearGrip, DriftIntent);
            }

            float budget = suspensionForce * mu;
            gripBudget += budget;

            // --- The tyre and the wheel, solved together.
            SolveTyre(
                index, wheel, deltaTime, driveTorquePerWheel, brake, handbrake,
                budget, longitudinalVelocity, lateralVelocity);

            body.AddForceAtPosition(forward * wheel.Fx + right * wheel.Fy, wheel.ContactPoint);

            // How much of its budget this tyre still has in hand, 1 down to 0. A genuine saturation
            // figure now: it is where the tyre is on its own curve, so it falls as the slip grows and
            // keeps falling past the peak.
            wheel.GripUsed = 1f - Mathf.InverseLerp(1f, 2.5f, wheel.Slip);

            UpdateWheelVisual(
                index, wheel.SpringLength, wheelSteer, wheel.AngularVelocity * config.WheelRadius, deltaTime);
        }

        /// <summary>
        /// Advances one tyre's forces and its wheel's rotation together, in sub-steps.
        ///
        /// <para><b>One slip vector, one curve evaluation.</b> The longitudinal and lateral slips are
        /// each normalised by the slip their own axis peaks at, so a length of 1 is a tyre at its limit
        /// whichever direction it got there. Evaluating the curve once on that length and splitting the
        /// answer along it is what produces the friction ellipse: the tyre spends one budget, and drive
        /// leaves less for cornering because the vector is already partly used — not because anything
        /// subtracted one from the other. The old model did subtract, longitudinal first and lateral out
        /// of the remainder, which is why braking used to cut the steering off a cliff rather than
        /// blunting it.</para>
        ///
        /// <para><b>The wheel is integrated semi-implicitly, and it has to be.</b> Near pure rolling the
        /// tyre is enormously stiff against the wheel's own surface speed — around 150 kN per metre per
        /// second at a road tyre's static load — and a plain forward step of that against a 1.2 kg·m²
        /// wheel over 20 ms overshoots by a factor of forty and diverges within a handful of steps.
        /// Dividing the increment by <c>1 + h·k·r²/J</c> is unconditionally stable, and what it does
        /// physically is what is wanted: it damps hardest where the tyre is gripping, which is a wheel
        /// locking onto rolling rather than hunting around it.</para>
        ///
        /// <para><b>And that damping is why there are sub-steps.</b> It behaves as extra rotational
        /// inertia of <c>h·k·r²</c>, which at one step per frame is about a hundred kilograms at the
        /// contact patch against a quarter car's three hundred — measured, it cost 57 % of the braking
        /// force and grew worse as the car slowed. Eight sub-steps cut it to 12 %, and the remainder is
        /// absorbed by tuning against the bench. It is cheap: <c>k</c>, the relaxation blend and the
        /// damping factor all depend on the body's speed rather than the wheel's, so they are computed
        /// once and only the curve is evaluated per sub-step.</para>
        ///
        /// <para>The brake and rolling resistance are clamped so they can bring a wheel to a stop and
        /// never drag it backwards through zero, which is what a brake does and a reversed torque does
        /// not. The clamp is measured against the damped increment rather than the raw one, or it binds
        /// permanently at road speed and the brakes fade away as the car slows.</para>
        /// </summary>
        private void SolveTyre(
            int index,
            WheelState wheel,
            float deltaTime,
            float driveTorquePerWheel,
            float brake,
            bool handbrake,
            float budget,
            float longitudinalVelocity,
            float lateralVelocity)
        {
            float radius = config.WheelRadius;
            float inertia = Mathf.Max(0.05f, config.WheelInertia);
            float peakRatio = Mathf.Max(0.01f, config.PeakSlipRatio);
            float peakTan = Mathf.Tan(Mathf.Max(0.5f, config.PeakSlipAngle) * Mathf.Deg2Rad);

            float reference = Mathf.Max(Mathf.Abs(longitudinalVelocity), SlipReferenceSpeed);
            float shapeB = config.TyreShapeB;
            float shapeC = config.TyreShapeC;

            // Everything that depends on the body rather than on the wheel, hoisted out of the loop.
            float step = deltaTime / SpinSubSteps;
            float stiffness = budget * shapeB * shapeC / (peakRatio * reference);
            float damping = 1f + step * stiffness * radius * radius / inertia;
            float blend = 1f - Mathf.Exp(-reference / Mathf.Max(0.05f, config.RelaxationLength) * step);

            float slipLat = -lateralVelocity / reference / peakTan;

            float driveTorque = config.IsDriven(index) ? driveTorquePerWheel : 0f;

            float resistive = config.RollingResistanceN * radius;
            if (brake > 0f)
            {
                resistive += brake * config.BrakeForce * 0.25f * radius;
            }

            // The handbrake is a brake, not a switch on the grip.
            //
            // It used to multiply rear grip by a constant, which slid the car sideways without ever
            // rotating it — the back stepped out and the nose carried straight on. Locking the rear
            // wheels instead spends their whole slip budget on stopping, and the ellipse then has
            // nothing left to give cornering, so the car comes round because it is being braked at one
            // end. That is what a handbrake turn actually is.
            if (handbrake && index >= 2)
            {
                resistive += config.HandbrakeForceN * radius;
            }

            float slip = 0f;

            for (int i = 0; i < SpinSubSteps; i++)
            {
                float rollSpeed = wheel.AngularVelocity * radius;
                float slipLong = (rollSpeed - longitudinalVelocity) / reference / peakRatio;

                slip = Mathf.Sqrt(slipLong * slipLong + slipLat * slipLat);

                // sin(C·atan(B·u)): the magic formula with its curvature term dropped. Rises to exactly
                // 1 at a normalised slip of 1 and falls to GripPastPeak beyond it, which is the part
                // that makes a limit something a driver can find rather than a wall they hit.
                float force = slip > 0.0001f
                    ? budget * Mathf.Sin(shapeC * Mathf.Atan(shapeB * slip)) / slip
                    : 0f;

                wheel.Fx = Mathf.Lerp(wheel.Fx, force * slipLong, blend);
                wheel.Fy = Mathf.Lerp(wheel.Fy, force * slipLat, blend);

                float torque = driveTorque - wheel.Fx * radius;

                float speed = wheel.AngularVelocity;

                // Which way the resistance acts. A turning wheel is opposed; a stopped one has no
                // direction of its own, so it opposes the torque instead — and the clamp below then
                // keeps it from doing more than cancelling it. Reading Sign(0) as forwards would have
                // rolling resistance driving a stationary wheel backwards.
                float against = Mathf.Abs(speed) > 0.0001f ? Mathf.Sign(speed) : Mathf.Sign(torque);

                float stopping = Mathf.Abs(speed) * inertia * damping / step;
                float applied = Mathf.Min(resistive, stopping + Mathf.Abs(torque));
                torque -= against * applied;

                wheel.AngularVelocity = speed + step * torque / inertia / damping;
            }

            wheel.Slip = slip;
            wheel.SlipRatio = (wheel.AngularVelocity * radius - longitudinalVelocity) / reference;
            wheel.LongitudinalSlip = Mathf.Abs(wheel.AngularVelocity * radius - longitudinalVelocity);
            wheel.LateralSlip = Mathf.Abs(lateralVelocity);
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

                // Signed, about the car's own up: positive means the car is travelling to the right of
                // where its nose is pointing, which is the tail having come round to the left.
                //
                // It was unsigned, on the reasoning that the assists read the yaw rate directly and
                // nothing else cared which way round a slide was. A drift the driver asks for cares:
                // holding one means knowing which way to point the wheels, and that is a question about
                // direction, not about magnitude. Everything that only wants "how sideways" takes the
                // absolute value at the point of use, which is one call and visible.
                SlipAngle = Vector3.SignedAngle(heading, flat.normalized, Vector3.up);

                // Reversing is not a 180° drift. Folded about ±90 with the sign kept.
                if (SlipAngle > 90f)
                {
                    SlipAngle = 180f - SlipAngle;
                }
                else if (SlipAngle < -90f)
                {
                    SlipAngle = -180f - SlipAngle;
                }
            }

            RearSlip = Mathf.Max(wheels[RearLeft].LateralSlip, wheels[RearRight].LateralSlip);
            RearGrip = Mathf.Min(wheels[RearLeft].GripUsed, wheels[RearRight].GripUsed);
        }

        /// <summary>
        /// The fraction of full lock the driver is allowed at this speed, 0 to 1.
        ///
        /// <para><b>Derived from what the tyres can hold, not drawn as a curve.</b> A car can only
        /// sustain the yaw rate its grip pays for, so the steering angle it can actually use is
        /// <c>atan(a · wheelbase / v²)</c> — which collapses with the <i>square</i> of speed. A
        /// hand-drawn curve cannot track that: the one this replaces was handing out between 1.5 and
        /// 2.1 times the usable angle everywhere above 90 km/h, and on a keyboard, where every input
        /// reaches its stop in a sixth of a second, that is a request for nearly double what the car
        /// has arriving in one flick. The car broke away on what felt like a small correction.</para>
        ///
        /// <para>Doing it this way means the limit is right for all ten cars without ten curves, and
        /// stays right after any future retune — the same argument the rev counter makes for building
        /// its face out of the car rather than printing it on.</para>
        ///
        /// <para><see cref="VehicleConfig.SteeringMargin"/> keeps full lock just under the limit rather
        /// than exactly on it. At exactly the limit the tyre sits on the peak of its own curve with the
        /// falloff on the far side, which is a knife edge to ask a driver to balance on.</para>
        ///
        /// <para><b>The capacity it reads is held and smoothed, and both matter.</b> Airborne, every
        /// wheel load is zero and so is the capacity — read raw, the steering would die over every
        /// crest, which is precisely the sort of fault no log would ever mention. And the capacity
        /// moves with load transfer through a corner, so read raw the lock would wander mid-bend and
        /// feel like the steering going light. The floor covers the first frames, before any wheel has
        /// reported.</para>
        /// </summary>
        private float AvailableLock()
        {
            float speed = Mathf.Abs(forwardSpeed);
            if (speed < 1f)
            {
                return 1f;
            }

            float capacity = Mathf.Max(MinSteeringCapacity, steeringCapacity);
            float usable = Mathf.Atan(
                config.SteeringMargin * capacity * Physics.gravity.magnitude * wheelbase
                / (speed * speed)) * Mathf.Rad2Deg;

            return Mathf.Clamp01(usable / Mathf.Max(1f, config.MaxSteerAngle));
        }

        /// <summary>
        /// Works out how much the driver is asking to be sideways, and eases
        /// <see cref="DriftIntent"/> toward it.
        ///
        /// <para><b>Three ways in, and every one of them is an input rather than an outcome.</b> That
        /// is the whole design: the car is planted until somebody asks it not to be, so nothing about
        /// the corner, the camber or the speed can start a slide on its own.</para>
        ///
        /// <list type="bullet">
        /// <item><b>The handbrake</b> asks outright, and asks for all of it.</item>
        /// <item><b>Braking hard while turning hard</b> is the entry a driver already knows. Both
        /// pedals have to be well past half, so it cannot happen while merely braking in a bend.</item>
        /// <item><b>Full throttle on a tight radius</b>, and only in a car with driven rear wheels.
        /// This is the one that needs traction control to stand aside, which is exactly what
        /// <see cref="UpdateTractionControl"/> does with this number — the assist that normally
        /// prevents power oversteer is the assist that has to yield for it.</item>
        /// </list>
        ///
        /// <para>All three are held below <see cref="DriftEntrySpeed"/>, because a car being parked is
        /// not asking for anything, and at walking pace the slip angle is noise anyway.</para>
        ///
        /// <para><b>Faster in than out</b>, and the asymmetry is the same one the turbo uses. Late into
        /// a slide is a car that ignores the handbrake; quick out of one is a car that snaps straight
        /// the instant the button is released, which is far the worse of the two — a drift wants to
        /// decay, not to end.</para>
        /// </summary>
        private void UpdateDriftIntent(float deltaTime, IDriveInput drive)
        {
            float wanted = 0f;

            if (drive.Handbrake)
            {
                wanted = 1f;
            }
            else if (Mathf.Abs(forwardSpeed) > DriftEntrySpeed)
            {
                // How tight a corner the car is actually driving, in metres. Not how hard it is
                // leaning on its tyres, and the difference is the whole of the bug this replaces.
                //
                // Lateral load was the second wrong quantity here. The first was the steering input,
                // which on a keyboard is always large. The second looked right — a car going straight
                // pulls no lateral g — but it closed a loop: the turn-in assist drives the yaw rate up
                // to GripCapacityG·g/v whenever the driver asks for more angle than the car has, which
                // makes LateralG/GripCapacityG exactly 1, which read as a committed corner, which took
                // grip off the rear axle. The car broke away in fast bends because an assist had put
                // it at its own limit and the drift trigger believed the reading.
                //
                // A radius cannot do that. It is v over the yaw rate, so it is infinite on a straight
                // by construction, and in a fast sweeper it is hundreds of metres however hard the
                // tyres are working. Only a genuinely tight corner is a small number, which is also
                // exactly what "tight corner" means in the request this exists to serve.
                float yawRate = Mathf.Abs(Vector3.Dot(body.angularVelocity, transform.up));
                float radius = yawRate > 0.01f ? Mathf.Abs(forwardSpeed) / yawRate : float.MaxValue;

                // Trail-braking in. Hard on the brake, and in a corner — a generous one, because
                // braking into a medium-fast bend is a legitimate way to unstick a car and only a
                // straight has to be excluded.
                float trail = Mathf.InverseLerp(0.45f, 0.9f, drive.Brake)
                            * (1f - Mathf.InverseLerp(BrakeDriftRadius, BrakeDriftRadius * 2f, radius));
                wanted = Mathf.Max(wanted, trail);

                // Power on, and only where a bootful of throttle really would step the tail out.
                // Front-driven cars are excluded by construction rather than by a number — there is no
                // such thing as power oversteer when the driven wheels are the ones steering.
                if (config.DrivenAxle != DrivenAxle.Front && config.PowerOversteer > 0f)
                {
                    float power = Mathf.InverseLerp(0.92f, 1f, drive.Throttle)
                                * (1f - Mathf.InverseLerp(
                                    PowerDriftRadius, PowerDriftRadius * 2f, radius));
                    wanted = Mathf.Max(wanted, power * config.PowerOversteer);
                }
            }

            float rate = wanted > DriftIntent ? DriftEntryRate : DriftExitRate;
            DriftIntent = Mathf.MoveTowards(DriftIntent, wanted, rate * deltaTime);
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
        /// <para><b>Countersteer authority</b> gives back the lock that the speed limit takes
        /// away. That curve cuts to a third at speed, which is right for stability on a straight and
        /// exactly wrong when you are sideways and need the wheel — so it is restored in proportion to
        /// how far sideways the car already is, which is when it can do no harm.</para>
        /// </summary>
        private void ApplyDriftAssists()
        {
            if (GroundedWheelCount == 0 || DriftIntent <= 0f)
            {
                return;
            }

            // On the request, not on the slip angle. This term damps yaw rate without caring which way
            // the car is rotating, so gated on slip it fought the turn-in of every corner taken past
            // twelve degrees just as hard as it caught a spin. Inside a drift that is exactly what is
            // wanted — the angle holds and only a runaway is arrested — and outside one it should not
            // be running at all.
            float past = DriftIntent;
            float yawRate = Vector3.Dot(body.angularVelocity, transform.up);

            // Force, not Impulse: a continuous torque for as long as the car is sideways. The first
            // version multiplied by deltaTime and passed Impulse, which is the same thing written twice
            // as confusingly.
            body.AddTorque(
                -transform.up * (yawRate * config.DriftYawDamping * past * config.Mass),
                ForceMode.Force);
        }

        /// <summary>
        /// Anti-roll bar. Moves load across an axle so the body resists leaning.
        ///
        /// <para><b>It moves load, and until now it did not.</b> This doc comment has said "transfers
        /// load across an axle" since the day it was written, and the code underneath applied a torque
        /// couple at the contact points <i>after</i> all four tyres had already been asked what they
        /// could hold. So the bar leaned the body and left every friction budget in the car exactly
        /// where it found it — which is to say the one classical balance adjustment a car has did not
        /// adjust the balance. A doc comment is not a test, and this file has already paid for that
        /// lesson once, on the guard rails.</para>
        ///
        /// <para>The couple has not gone anywhere: two wheels of one axle pushing up by
        /// <c>+transfer</c> and <c>-transfer</c> at their own contact points <i>is</i> the couple, and
        /// <see cref="DriveWheel"/> applies it as part of the load. Applying it here as well would be
        /// the bar counted twice.</para>
        ///
        /// <para><b>Clamped at zero, and that clamp is a feature.</b> A bar stiff enough to take more
        /// load off the inside wheel than it had lifts it, and a lifted wheel has no grip — which is
        /// exactly what the offroader's preset warns about in words and could not previously produce
        /// in fact.</para>
        /// </summary>
        private void TransferAntiRoll(int leftIndex, int rightIndex)
        {
            WheelState left = wheels[leftIndex];
            WheelState right = wheels[rightIndex];
            if (!left.Grounded && !right.Grounded)
            {
                return;
            }

            // Positive when the left wheel is compressed more, i.e. the body leans left. The compressed
            // side takes load and the extended side gives it up.
            float difference = left.Compression01 - right.Compression01;
            float transfer = difference * config.AntiRollStiffness;

            if (left.Grounded)
            {
                left.NormalLoad = Mathf.Max(0f, left.NormalLoad + transfer);
            }

            if (right.Grounded)
            {
                right.NormalLoad = Mathf.Max(0f, right.NormalLoad - transfer);
            }
        }

        /// <summary>
        /// What the wheel's raycast landed on, reusing the last answer while the collider has not
        /// changed.
        ///
        /// <para><see cref="RaycastHit.triangleIndex"/> is read every time even when the collider is
        /// cached, because the shoulder is a submesh of the same road mesh — the answer changes without
        /// the collider changing, which is the whole case this exists for. It is free: the hit already
        /// carries it.</para>
        /// </summary>
        private static SurfaceKind ResolveSurface(WheelState wheel, RaycastHit hit)
        {
            if (hit.collider != wheel.SurfaceCollider)
            {
                wheel.SurfaceCollider = hit.collider;
                wheel.SurfaceTag = hit.collider != null
                    ? hit.collider.GetComponent<GroundSurface>()
                    : null;
            }

            // Untagged geometry drives like a road. See the remarks on GroundSurface for why that is
            // the safe direction to be wrong in.
            return wheel.SurfaceTag != null
                ? wheel.SurfaceTag.KindAt(hit.triangleIndex)
                : SurfaceKind.Asphalt;
        }

        /// <summary>
        /// Averages what the grounded wheels are standing on into the two published figures.
        ///
        /// <para>Over grounded wheels only, and airborne is not counted as tarmac. A car mid-jump has
        /// no surface at all; averaging in a default would have it rumbling over the crest of every
        /// rise on the Stadtfeld.</para>
        /// </summary>
        private void UpdateSurfaceState()
        {
            float grip = 0f;
            float roughness = 0f;
            float grit = 0f;
            int grounded = 0;

            for (int i = 0; i < WheelCount; i++)
            {
                if (!wheels[i].Grounded)
                {
                    continue;
                }

                float rough = GroundSurface.RoughnessOf(wheels[i].Surface);

                grip += GroundSurface.GripOf(wheels[i].Surface);
                roughness += rough;

                // Weighted by how much noise this wheel is making, not by the wheel — see SurfaceGrit.
                grit += GroundSurface.GritOf(wheels[i].Surface) * rough;
                grounded++;
            }

            if (grounded == 0)
            {
                SurfaceGrip = 1f;
                SurfaceRoughness = 0f;
                return;
            }

            SurfaceGrip = grip / grounded;
            SurfaceRoughness = roughness / grounded;

            if (roughness > 0.0001f)
            {
                SurfaceGrit = grit / roughness;
            }
        }

        /// <summary>
        /// Turns a contact into a severity and raises <see cref="Impacted"/>.
        ///
        /// <para><b>The closing speed along the contact normal, not the speed of the car.</b> A car
        /// leaning into a barrier through a fast corner is doing 160 km/h and hitting nothing: almost
        /// all of that velocity is along the wall. Taking the magnitude would report every graze on the
        /// Meerenge parapet as the hardest crash in the game, which is the one reading that would make
        /// this feature worse than not having it.</para>
        ///
        /// <para>The wheels are raycasts, so the body's own collider does not touch the road in normal
        /// driving — a vertical contact means the car has bottomed out or landed, and that is a thud
        /// worth hearing. It is deliberately not filtered out.</para>
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (Impacted == null || Time.time < impactsSuppressedUntil)
            {
                return;
            }

            ContactPoint contact = collision.GetContact(0);
            float closing = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
            if (closing < MinImpactSpeed)
            {
                return;
            }

            float severity = Mathf.InverseLerp(MinImpactSpeed, FullImpactSpeed, closing);
            Impacted.Invoke(severity, contact.point);
        }

        /// <summary>
        /// How fast the body is sliding along whatever it is resting against.
        ///
        /// <para>From <see cref="Rigidbody.GetPointVelocity"/> projected onto the contact plane, not from
        /// <c>Collision.relativeVelocity</c>: that field is the velocity at the moment of the collision
        /// and is not meaningful for a contact that is being maintained, which is exactly the case a
        /// scrape is.</para>
        ///
        /// <para>The largest contact wins rather than the sum. A car pressed against a barrier reports
        /// several contacts along its flank, and adding them would make one wall sound like four.</para>
        /// </summary>
        private void OnCollisionStay(Collision collision)
        {
            int count = collision.contactCount;
            for (int i = 0; i < count; i++)
            {
                ContactPoint contact = collision.GetContact(i);

                Vector3 pointVelocity = body.GetPointVelocity(contact.point);
                Vector3 along = Vector3.ProjectOnPlane(pointVelocity, contact.normal);

                float speed = along.magnitude;
                if (speed > scrapeThisStep)
                {
                    scrapeThisStep = speed;
                }
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
