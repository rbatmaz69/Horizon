using Horizon.Net;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Somebody else's car, drawn from the snapshots they send.
    ///
    /// <para><b>It has no <c>VehicleController</c>, and that is the single most important thing about
    /// it.</b> There are seventeen places in this project that resolve the player's car with
    /// <c>FindFirstObjectByType&lt;VehicleController&gt;()</c> — the instrument cluster, the fuel
    /// gauge, the minimap, the lap timer, the traffic recycler, the haptics — and one of them,
    /// <c>InstrumentCluster</c>, repeats the search every frame until it finds one. Put a second
    /// controller in the scene and every one of those becomes a coin toss whose losing side is a rev
    /// counter reading a friend's engine. A pool of dumb transforms leaves all seventeen exactly as
    /// they were.</para>
    ///
    /// <para><b>Nor does it simulate.</b> Feeding a remote car through
    /// <c>VehicleController.SetInput</c> with the sender's pedals is the tempting design, because that
    /// seam already exists — and two devices integrating the same car from the same inputs come apart
    /// within seconds. Different frame times, different traffic in the way (which is solid and is not
    /// synchronised), different surface under the wheels, and floating point that does not agree
    /// between an ARM phone and an x86 editor. What arrives here is where the car <i>is</i>, which is
    /// the one thing that cannot be wrong.</para>
    ///
    /// <para><b>No collider, on purpose.</b> A snapshot-driven body is kinematic and therefore wins
    /// every exchange: you would be shoved aside by a car that, on its owner's screen, never touched
    /// you. Worse, <c>VehicleController.Impacted</c> already has three subscribers — the thud, the
    /// camera kick and the vibration — so a phantom contact is felt three ways. The gentle mutual
    /// push-apart is a later change and a room setting; being a ghost is the correct first
    /// version.</para>
    /// </summary>
    public sealed class RemoteCar : MonoBehaviour
    {
        /// <summary>
        /// How far behind live this car is drawn.
        ///
        /// <para>Two ticks at <c>NetProtocol.SendRate</c>. The buffer has to hold at least one packet
        /// ahead of what is being drawn or there is nothing to interpolate towards, and two is the
        /// shortest delay that survives a single dropped packet without the car stopping. Longer would
        /// smooth more and put the car further into the past, which on a road you are driving beside
        /// is the thing you would notice.</para>
        /// </summary>
        public const float InterpolationDelay = 2f / NetProtocol.SendRate;

        /// <summary>
        /// How long the car will carry on under its own momentum when nothing arrives.
        ///
        /// <para>A quarter of a second, and then it stops rather than continuing. Extrapolating a car
        /// at two hundred kilometres an hour into a valley it never entered looks far worse than a car
        /// that pauses — and the pause is honest, because a peer that has been silent this long is
        /// usually about to be timed out anyway.</para>
        /// </summary>
        private const float MaxExtrapolation = 0.25f;

        /// <summary>Eight is half a second at the send rate: more history than the delay can ask for.</summary>
        private const int BufferSlots = 8;

        [Tooltip("The ten bodies and the paints. Wired with controller, lights, hull and engine audio "
               + "left null — this car wants the mesh and nothing else.")]
        [SerializeField] private VehicleBodySet bodySet;

        [Tooltip("The four wheel pivots, in the controller's order: front left, front right, rear "
               + "left, rear right. Only the first two steer.")]
        [SerializeField] private Transform[] wheelPivots = new Transform[0];

        [Tooltip("Where each wheel pivot hangs from, in chassis space, before the spring is subtracted. "
               + "The drop itself comes from the body, because an off-roader sits higher than a "
               + "hatchback and the pivots here are shared between all ten.")]
        [SerializeField] private Vector3[] wheelAnchorLocal = new Vector3[0];

        [Tooltip("Lamp material for a taillight that is merely lit at night.")]
        [SerializeField] private Material taillightNight;

        [Tooltip("Lamp material for a taillight under braking.")]
        [SerializeField] private Material taillightBraking;

        [Tooltip("Lamp material for a taillight in daylight, doing nothing.")]
        [SerializeField] private Material taillightDay;

        [Tooltip("Headlight lens, lit.")]
        [SerializeField] private Material headlightOn;

        [Tooltip("Headlight lens, dark.")]
        [SerializeField] private Material headlightOff;

        private readonly CarSnapshot[] buffer = new CarSnapshot[BufferSlots];
        private int count;
        private int next;

        private float spinAngle;
        private float steerAngle;

        /// <summary>
        /// The active body's material array, held so it is not read back every frame.
        ///
        /// <para><c>Renderer.sharedMaterials</c> allocates a fresh array on every read — the fact
        /// <c>TrafficDirector</c> caches around for ninety-six cars, and the reason this one is
        /// assigned only when a lamp actually changes.</para>
        /// </summary>
        private Material[] slots;
        private MeshRenderer slotsOwner;
        private int lampState = -1;

        /// <summary>
        /// Outside the streamed world, so not drawn.
        ///
        /// <para>Held rather than applied straight away, because <see cref="LateUpdate"/> shows the car
        /// on every frame it has a pose for — a cull applied directly would be undone by the next
        /// frame, and the car would flicker instead of disappearing.</para>
        /// </summary>
        private bool culled;

        /// <summary>Which peer this car belongs to, or <c>NetProtocol.NoPeerId</c> when it is free.</summary>
        public byte PeerId { get; private set; } = NetProtocol.NoPeerId;

        public bool InUse => PeerId != NetProtocol.NoPeerId;

        /// <summary>Where the car was drawn on the last frame. Read by the name tag and the map.</summary>
        public Vector3 DrawnPosition { get; private set; }

        /// <summary>Which way it was pointing. The map marker needs a heading, not just a place.</summary>
        public Quaternion DrawnRotation { get; private set; } = Quaternion.identity;

        /// <summary>Whether anything has been drawn yet. A bound car with an empty buffer has not.</summary>
        public bool HasPose { get; private set; }

        /// <summary>Metres a second, from the newest snapshot. The debug overlay prints it.</summary>
        public float Speed { get; private set; }

        /// <summary>Takes this car for a peer and clears everything the last one left behind.</summary>
        public void Bind(byte peerId)
        {
            PeerId = peerId;
            count = 0;
            next = 0;
            lampState = -1;
            HasPose = false;
            culled = false;
            Speed = 0f;
            spinAngle = 0f;
            steerAngle = 0f;
            SetVisible(false);
        }

        /// <summary>Gives the car back to the pool.</summary>
        public void Release()
        {
            PeerId = NetProtocol.NoPeerId;
            count = 0;
            next = 0;
            HasPose = false;
            SetVisible(false);
        }

        /// <summary>
        /// Adds one snapshot.
        ///
        /// <para>A snapshot carrying <see cref="CarFlags.Teleported"/> empties the buffer first.
        /// Respawning, every start place and every <c>PauseMenu.MoveTo</c> move a car by kilometres,
        /// and interpolating between two positions a valley apart slides it through the landscape at
        /// two hundred metres a second — which looks less like a placement than like the game having
        /// lost the car.</para>
        /// </summary>
        public void Push(in CarSnapshot snapshot)
        {
            if (snapshot.Has(CarFlags.Teleported))
            {
                count = 0;
                next = 0;
            }

            buffer[next] = snapshot;
            buffer[next].ReceivedAt = Time.time;
            next = (next + 1) % BufferSlots;

            if (count < BufferSlots)
            {
                count++;
            }

            Speed = snapshot.Velocity.magnitude;

            // The body may have changed — somebody stepping into the garage mid-session — and it rides
            // in every snapshot precisely so this does not have to wait for the next roster packet.
            if (bodySet != null
                && (bodySet.ActiveBody != snapshot.Body || bodySet.ActivePaint != snapshot.Paint))
            {
                bodySet.Select(snapshot.Body, snapshot.Paint);
                SeatWheels();

                // The renderer under the lamps is a different object now.
                slots = null;
                slotsOwner = null;
                lampState = -1;
            }
        }

        /// <summary>
        /// Draws the car at <c>Time.time - InterpolationDelay</c>.
        ///
        /// <para>In <c>LateUpdate</c> rather than <c>Update</c> so it lands after anything that might
        /// have moved the world, and after <c>ChaseCamera</c> has read the player's car — a remote car
        /// is drawn, never followed.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!InUse || count == 0)
            {
                return;
            }

            float renderTime = Time.time - InterpolationDelay;

            if (!Resolve(renderTime, out Vector3 position, out Quaternion rotation, out CarSnapshot state))
            {
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
            DrawnPosition = position;
            DrawnRotation = rotation;
            HasPose = true;

            SetVisible(!culled);
            UpdateWheels(state, Time.deltaTime);
            UpdateLamps(state);
        }

        /// <summary>
        /// Finds the pose for a moment in the past, interpolating between the two snapshots that
        /// bracket it, or carrying the newest one forward under its own velocity.
        /// </summary>
        private bool Resolve(
            float renderTime, out Vector3 position, out Quaternion rotation, out CarSnapshot state)
        {
            position = default;
            rotation = Quaternion.identity;
            state = default;

            int newest = (next - 1 + BufferSlots) % BufferSlots;
            state = buffer[newest];

            if (count == 1)
            {
                position = state.Position;
                rotation = state.Rotation;
                return true;
            }

            // Walk back from the newest until a pair brackets the render time.
            for (int step = 0; step < count - 1; step++)
            {
                int later = (newest - step + BufferSlots) % BufferSlots;
                int earlier = (later - 1 + BufferSlots) % BufferSlots;

                CarSnapshot a = buffer[earlier];
                CarSnapshot b = buffer[later];

                if (renderTime < a.ReceivedAt)
                {
                    continue;
                }

                float span = b.ReceivedAt - a.ReceivedAt;

                if (span <= 0.0001f)
                {
                    position = b.Position;
                    rotation = b.Rotation;
                    state = b;
                    return true;
                }

                float t = Mathf.Clamp01((renderTime - a.ReceivedAt) / span);
                position = Vector3.LerpUnclamped(a.Position, b.Position, t);
                rotation = Quaternion.SlerpUnclamped(a.Rotation, b.Rotation, t);
                state = t < 0.5f ? a : b;
                return true;
            }

            // Past the newest: nothing has arrived for a while. Carry on briefly, then hold.
            float ahead = renderTime - state.ReceivedAt;

            if (ahead > 0f)
            {
                position = state.Position + state.Velocity * Mathf.Min(ahead, MaxExtrapolation);
                rotation = state.Rotation;
                return true;
            }

            // Behind the oldest we hold: this is the first frame after a teleport cleared the buffer.
            int oldest = count < BufferSlots ? 0 : next;
            position = buffer[oldest].Position;
            rotation = buffer[oldest].Rotation;
            state = buffer[oldest];
            return true;
        }

        /// <summary>
        /// Drops the four wheels to the ride height of the body currently showing.
        ///
        /// <para>The pivots belong to the chassis and are shared by all ten shells — the same
        /// arrangement the player's car has, and there the controller rewrites their height every
        /// physics step from the spring it is solving. There is no spring here, so the height is taken
        /// once per body change from the config's rest length. Left at one body's figure, an
        /// off-roader would drive with its wheels inside its arches and a hatchback with its wheels
        /// hanging below the sills.</para>
        /// </summary>
        private void SeatWheels()
        {
            if (wheelPivots == null || wheelAnchorLocal == null || bodySet == null)
            {
                return;
            }

            float drop = bodySet.ActiveConfig != null ? bodySet.ActiveConfig.SuspensionRestLength : 0.35f;

            for (int i = 0; i < wheelPivots.Length && i < wheelAnchorLocal.Length; i++)
            {
                if (wheelPivots[i] != null)
                {
                    wheelPivots[i].localPosition = wheelAnchorLocal[i] - new Vector3(0f, drop, 0f);
                }
            }
        }

        /// <summary>
        /// Turns the front wheels and spins all four.
        ///
        /// <para>Spun from the car's own speed and its own wheel radius rather than from a shared
        /// constant, because an off-roader's tyre is half again a hatchback's and the same road speed
        /// is a different rate on each. The arithmetic is <c>VehicleController.UpdateWheelVisual</c>'s,
        /// which is also where the <c>Euler(spin, steer, 0)</c> order comes from — a pivot built by the
        /// same code has to be driven the same way round.</para>
        ///
        /// <para><b>Suspension travel is not replicated and the wheels sit at rest.</b> Static sag is
        /// 57 mm and the road relief moves a wheel by about three; neither is visible on somebody
        /// else's car, and both would cost a byte a wheel on every snapshot.</para>
        /// </summary>
        private void UpdateWheels(in CarSnapshot state, float deltaTime)
        {
            if (wheelPivots == null || wheelPivots.Length == 0)
            {
                return;
            }

            float radius = 0.35f;

            if (bodySet != null && bodySet.ActiveConfig != null)
            {
                radius = Mathf.Max(0.05f, bodySet.ActiveConfig.WheelRadius);
            }

            float forward = Vector3.Dot(state.Velocity, transform.forward);
            spinAngle = Mathf.Repeat(
                spinAngle + forward * (Mathf.Rad2Deg / radius) * deltaTime, 360f);

            // Eased rather than taken raw: the wire carries 0.35° steps at fifteen hertz, and stepping
            // a visible wheel between them reads as a twitch on a car being driven smoothly.
            steerAngle = Mathf.MoveTowards(
                steerAngle, state.SteerDegrees, 360f * deltaTime);

            for (int i = 0; i < wheelPivots.Length; i++)
            {
                Transform pivot = wheelPivots[i];

                if (pivot == null)
                {
                    continue;
                }

                float steer = i < 2 ? steerAngle : 0f;
                pivot.localRotation = Quaternion.Euler(spinAngle, steer, 0f);
            }
        }

        /// <summary>
        /// Lights the lamps.
        ///
        /// <para>Material swaps on the body's own submeshes — headlight is 2 and taillight is 3 in
        /// <c>CarMeshBuilder</c>'s constant order — and <b>no real <see cref="Light"/> components at
        /// all</b>. That is the decision that makes eight cars affordable: the budget allows realtime
        /// shadows from the sun and nothing else, and sixteen point lights following other players
        /// around a mountain is not a thing this renderer is set up for. The player's own car keeps
        /// its beams; everybody else gets lenses that glow.</para>
        ///
        /// <para>Braking is taken from the sender's brake pedal rather than from its deceleration, for
        /// the reason <c>TrafficDirector</c> records: a car easing off for a corner is not braking, and
        /// lamps that lit for every lift would be a road where everybody brakes forever.</para>
        /// </summary>
        private void UpdateLamps(in CarSnapshot state)
        {
            MeshRenderer renderer = bodySet != null ? bodySet.ActiveRenderer : null;

            if (renderer == null)
            {
                return;
            }

            if (slots == null || slotsOwner != renderer)
            {
                slots = renderer.sharedMaterials;
                slotsOwner = renderer;
                lampState = -1;
            }

            bool headlights = state.Has(CarFlags.Headlights);
            bool braking = state.Has(CarFlags.Braking);

            int wanted = (headlights ? 1 : 0) | (braking ? 2 : 0);

            if (wanted == lampState)
            {
                return;
            }

            lampState = wanted;

            if (slots.Length > HeadlightSubmesh && headlightOn != null && headlightOff != null)
            {
                slots[HeadlightSubmesh] = headlights ? headlightOn : headlightOff;
            }

            if (slots.Length > TaillightSubmesh && taillightDay != null)
            {
                slots[TaillightSubmesh] = braking
                    ? taillightBraking
                    : headlights ? taillightNight : taillightDay;
            }

            renderer.sharedMaterials = slots;
        }

        private void SetVisible(bool visible)
        {
            if (bodySet == null)
            {
                return;
            }

            MeshRenderer renderer = bodySet.ActiveRenderer;

            if (renderer != null && renderer.enabled != visible)
            {
                renderer.enabled = visible;
            }

            if (wheelPivots == null)
            {
                return;
            }

            for (int i = 0; i < wheelPivots.Length; i++)
            {
                if (wheelPivots[i] == null)
                {
                    continue;
                }

                var wheel = wheelPivots[i].GetComponent<MeshRenderer>();

                if (wheel != null && wheel.enabled != visible)
                {
                    wheel.enabled = visible;
                }
            }
        }

        /// <summary>Hides the car without giving it back — used when it is outside the streamed world.</summary>
        public void SetCulled(bool outsideTheWorld)
        {
            culled = outsideTheWorld;
        }

        /// <summary>
        /// Puts a body on the car before anything has been received.
        ///
        /// <para>In <c>Start</c> rather than <c>Awake</c> so <c>VehicleBodySet</c> has built its
        /// material slots first. Without it there is no active body at all, which means
        /// <see cref="SetVisible"/> has no renderer to turn off and an unused car in the pool would sit
        /// wherever the setup tool parked it, visible.</para>
        /// </summary>
        private void Start()
        {
            if (bodySet != null && bodySet.ActiveBody < 0)
            {
                bodySet.Select(0, 0);
                SeatWheels();
            }

            SetVisible(false);
        }

        /// <summary>
        /// How far a car's chassis sits above the ground it is standing on.
        ///
        /// <para>Spring plus tyre plus a centimetre of clearance, off the body currently showing —
        /// which for an off-roader is a quarter of a metre more than for a hatchback. The same
        /// expression <c>RoadRespawn.RideHeight</c> uses for the player's car, and the reason that one
        /// is a method rather than a constant.</para>
        /// </summary>
        public float RideHeight
        {
            get
            {
                VehicleConfig config = bodySet != null ? bodySet.ActiveConfig : null;

                return config != null
                    ? config.SuspensionRestLength + config.WheelRadius + 0.05f
                    : 0.75f;
            }
        }

        /// <summary>
        /// Puts the car on the ground somewhere, in a body, with its lamps in a state — with no
        /// snapshot, no buffer and no interpolation.
        ///
        /// <para><b>Public for the preview tool, and for the same reason <c>FuelGauge.LayOutFace</c>
        /// and <c>MapGraphic.SetView</c> are.</b> A preview photographs a saved scene in which no
        /// <c>Update</c> has ever run, so a car that only ever appears because a packet arrived is a
        /// car that appears in no picture this project can take. The alternative is the tool carrying
        /// its own copy of how a body is selected, where a wheel sits and which submesh a brake lamp
        /// is — four facts that already live here, and that would then agree with this class right up
        /// until one of them was changed.</para>
        /// </summary>
        public void ShowAt(
            Vector3 ground, Quaternion rotation, byte bodyIndex, byte paintIndex, CarFlags flags)
        {
            if (!InUse)
            {
                Bind(0);
            }

            if (bodySet != null)
            {
                bodySet.Select(bodyIndex, paintIndex);
                SeatWheels();
                slots = null;
                slotsOwner = null;
                lampState = -1;
            }

            // Lifted here rather than by the caller, and only after the body is chosen: ride height is
            // a property of the shell, and a tool that added its own figure would sink an off-roader
            // and float a hatchback. A real snapshot needs none of this — it carries the chassis
            // position its owner's own physics put it at.
            Vector3 position = ground + Vector3.up * RideHeight;

            transform.SetPositionAndRotation(position, rotation);
            DrawnPosition = position;
            DrawnRotation = rotation;
            HasPose = true;
            culled = false;

            SetVisible(true);
            UpdateLamps(new CarSnapshot { Flags = flags });
        }

        /// <summary>
        /// Wired by the setup tool, which is the only thing that may call it.
        ///
        /// <para>The lamp materials are handed in rather than loaded here for the reason every other
        /// runtime component in this project gives: a build-time asset path in a class that has to run
        /// on a phone is a path that fails silently in a player.</para>
        /// </summary>
        public void SetParts(
            VehicleBodySet bodies,
            Transform[] pivots,
            Vector3[] anchors,
            Material headOn,
            Material headOff,
            Material tailDay,
            Material tailNight,
            Material tailBraking)
        {
            bodySet = bodies;
            wheelPivots = pivots;
            wheelAnchorLocal = anchors;
            headlightOn = headOn;
            headlightOff = headOff;
            taillightDay = tailDay;
            taillightNight = tailNight;
            taillightBraking = tailBraking;
        }

        /// <summary><c>CarMeshBuilder</c>'s constant order. Duplicated for the reason that file gives.</summary>
        private const int HeadlightSubmesh = 2;

        private const int TaillightSubmesh = 3;
    }
}
