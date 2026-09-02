using System.Collections.Generic;
using Horizon.Vehicle;
using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Drives a fixed pool of ambient cars along the baked <see cref="TrafficNetwork"/>.
    ///
    /// <para><b>The agents are kinematic followers, not vehicles.</b> No Rigidbody dynamics, no wheel
    /// raycasts, no engine — a lane index, a distance along it, and a speed, integrated and written
    /// straight to the transform. Two dozen cars each running the raycast-wheel model would cost more per
    /// frame than everything else in this world put together, and would buy nothing: what the player
    /// reads at thirty metres is that the traffic moves plausibly, not that it has suspension.</para>
    ///
    /// <para><b>In Horizon.Game, not Horizon.World.</b> It needs the player's transform, and
    /// <c>Horizon.World</c> must not depend upward. The lane maths lives down there where it belongs and
    /// this reaches down to it — the same shape as <c>DriveInput.Current</c>.</para>
    ///
    /// <para><b>No per-frame allocation.</b> State is a <c>struct[]</c> sized once, the pool is
    /// instantiated at build, and the whole update is arithmetic over those arrays. This runs in
    /// <c>Update</c> on a mobile budget, and a garbage collection in driving code is the thing this
    /// project's conventions spend the most effort forbidding.</para>
    /// </summary>
    public sealed class TrafficDirector : MonoBehaviour
    {
        /// <summary>One car: where it is on the network and what it is doing.</summary>
        private struct Agent
        {
            public int Lane;
            public float Distance;
            public float Speed;

            /// <summary>The junction whose capacity this agent is occupying, or -1.</summary>
            public int HeldNode;

            /// <summary>
            /// The connector this agent has decided to take, chosen once as it approaches the junction.
            ///
            /// Decided in advance rather than at the moment of handover so that the room-on-the-far-side
            /// check and the turn actually taken cannot disagree — checking one exit and then driving
            /// down another is how a car ends up stopped across a junction it was cleared to cross.
            /// </summary>
            public int NextLane;

            /// <summary>How long this agent has been stationary, seconds. The watchdog's input.</summary>
            public float Stalled;

            /// <summary>This agent's own xorshift state, so its turns do not depend on the frame rate.</summary>
            public uint Random;

            public bool Visible;
        }

        [SerializeField] private TrafficNetwork network;

        [Tooltip("The cars, instantiated at build. One agent per entry, and the array never changes size.")]
        [SerializeField] private Transform[] cars;

        [Tooltip("Renderers to switch off past the load radius, parallel to the car array.")]
        [SerializeField] private MeshRenderer[] renderers;

        [Header("Motion")]
        [Tooltip("Multiplies every lane's own speed limit. 1 drives them as baked.\n\n"
               + "The limits themselves live on the lanes, because a town street and a mountain pass are "
               + "the same object to this component and one number for both is wrong for one of them: at "
               + "a town speed the player spends the descent behind a car doing 40 km/h, and at a pass "
               + "speed the traffic races through Talheim. This is the knob for making all of it quicker "
               + "or slower at once.")]
        [SerializeField] private float speedScale = 1f;

        [SerializeField] private float acceleration = 3.5f;

        [Tooltip("Braking is harder than acceleration, as it is on a real car and as it needs to be for "
               + "the look-ahead below to be able to stop in time.")]
        [SerializeField] private float braking = 7f;

        [Tooltip("How far the car's transform sits above the lane, metres. The lane follows the road "
               + "surface; this is the ride height that puts the wheels on it. Written by the setup "
               + "tool from CarMeshBuilder.TrafficRideHeight, so the body and the number agree.")]
        [SerializeField] private float rideHeight = 0.74f;

        [Header("Look-ahead")]
        [Tooltip("How far ahead an agent watches for something to brake for, metres.")]
        [SerializeField] private float lookAhead = 22f;

        [Tooltip("Stops this far short of whatever is in front of it.\n\n"
               + "Scaled with the cars when they grew a quarter longer. It is a gap between positions, "
               + "so on a 4.74 m car 6.5 m was already close; on a 5.93 m one it would have been "
               + "bumper to bumper.")]
        [SerializeField] private float stopGap = 8.1f;

        [Tooltip("How far off an agent's own path something can be and still count as being in the way. "
               + "Half a carriageway: wide enough to catch a car merging out of a junction, narrow "
               + "enough not to brake for oncoming traffic on the other side of the road.\n\n"
               + "Scaled with the cars. At 2.6 it had become narrower than a car is wide, which is a "
               + "traffic agent that cannot see the one beside it.")]
        [SerializeField] private float lateralReach = 3.25f;

        [Header("Streaming")]
        [Tooltip("Renderers switch off past this. Matches WorldStreamer's own load radius — a car drawn "
               + "beyond the chunk it is standing on is a car floating in fog.")]
        [SerializeField] private float loadRadius = 650f;

        [Tooltip("Past this an agent is moved to a lane near the viewer instead. It is cheaper to "
               + "move a car than to keep simulating one nobody can reach.")]
        [SerializeField] private float recycleRadius = 900f;

        /// <summary>
        /// The baked routes, for anything that needs to know where a road is.
        ///
        /// <para>Exposed because it is the only description of the driveable world that exists at run
        /// time: every carriageway, street and slip road in the game is a polyline in here, already
        /// sampled and already in world space. The pause menu's "put the car back" reads it to find the
        /// nearest road, which beats each caller re-deriving the road network from the courses.</para>
        /// </summary>
        public TrafficNetwork Network => network;

        /// <summary>
        /// How many of the pooled agents are actually simulated and drawn.
        ///
        /// <para>The pool itself is baked into the scene and cannot shrink — ninety-six GameObjects with
        /// their meshes and renderers exist whatever this says. What this buys is the per-frame cost:
        /// the whole of <see cref="Advance"/>, which is the lane sampling, the gap search and the
        /// junction claim, plus one draw call each. That makes it the largest single lever available for
        /// a weak phone, and the reason <see cref="QualityDirector"/> reaches for it before anything
        /// else.</para>
        ///
        /// <para>Capping by index thins the traffic evenly rather than emptying one region: agents are
        /// seeded onto lanes picked in proportion to lane length by <c>Awake</c>, so index carries no
        /// information about where a car is.</para>
        ///
        /// <para><b>Out-of-budget cars are deactivated whole, not just hidden.</b> Every agent carries a
        /// <c>BoxCollider</c> — that is what makes traffic something you can run into — and switching off
        /// only the renderer leaves the collider exactly where the car stopped. Since an agent outside
        /// the budget is also no longer advanced, it stops dead and stays there: an invisible solid car
        /// parked on the carriageway, for the rest of the session. At the Balanced setting that is forty
        /// of them scattered over the map, which is precisely what "invisible walls on the road" is.
        /// <c>SetActive(false)</c> takes the mesh, the collider and the cost together.</para>
        ///
        /// <para><b>And their cached positions go with them.</b> An out-of-budget agent stops being
        /// advanced, so its entry in <see cref="agentAt"/> keeps whatever it held — a point on a lane, by
        /// construction, since that is where the car was when the budget shrank. <see cref="GapAhead"/>
        /// used to read every entry, so at the Balanced setting there were forty invisible cars parked on
        /// the road network that every real car queued behind and never passed. That, and not the
        /// junction logic, was the largest single cause of traffic that stops and does not move again.
        /// The loop is now bounded by the budget, and these are parked out of the world as well.</para>
        /// </summary>
        public int ActiveBudget
        {
            get => activeBudget;
            set
            {
                int count = cars != null ? cars.Length : 0;
                activeBudget = Mathf.Clamp(value, 0, count);

                for (int i = 0; i < count; i++)
                {
                    bool inBudget = i < activeBudget;

                    if (cars[i] != null && cars[i].gameObject.activeSelf != inBudget)
                    {
                        cars[i].gameObject.SetActive(inBudget);
                    }

                    // The runtime arrays are all built in Awake, and the budget can be applied before
                    // it — QualityDirector sets this the moment the world scene finishes loading.
                    if (agents == null || i >= agents.Length || nodeLoad == null || agentAt == null)
                    {
                        continue;
                    }

                    if (inBudget)
                    {
                        // Put the renderer and the bookkeeping back in step on the way in. Advance only
                        // touches the renderer when the two disagree, so a car returned to the budget
                        // with a stale flag would keep whatever visibility it was switched off with.
                        if (renderers != null && i < renderers.Length && renderers[i] != null)
                        {
                            renderers[i].enabled = true;
                        }

                        agents[i].Visible = true;
                        continue;
                    }

                    agents[i].Visible = false;

                    // Out of the way of the gap search as well as out of the frame. See the remarks.
                    agentAt[i] = Parked;

                    // Give up any junction it was holding, or a car parked out of budget in the
                    // middle of an intersection blocks it for the rest of the session.
                    ReleaseNode(i);
                }
            }
        }

        private int activeBudget = int.MaxValue;

        /// <summary>Where a car that is not being simulated is considered to be: nowhere near anything.</summary>
        private static readonly Vector3 Parked = new Vector3(0f, -10000f, 0f);

        /// <summary>
        /// How far out traffic is drawn and how far out it is recycled.
        ///
        /// <para>Kept together because the pair has to stay ordered: recycling inside the draw radius
        /// teleports cars while the player is looking at them.</para>
        /// </summary>
        public void SetRanges(float load, float recycle)
        {
            loadRadius = Mathf.Max(50f, load);
            recycleRadius = Mathf.Max(loadRadius + 50f, recycle);
        }

        private Agent[] agents;

        /// <summary>
        /// How many agents are inside each junction, and how many are allowed to be.
        ///
        /// <para>A count, and it was a single owner. One car at a time is right at an unmarked
        /// crossroads — it is the give-way rule, and it prevents the single failure that reads as broken
        /// rather than as busy, two cars passing through each other in the middle of a junction. It is
        /// exactly wrong at a signalised one: a green phase that admits one car every few seconds is
        /// slower than the red it replaced, and a queue that never clears in a green is a queue that
        /// never clears.</para>
        ///
        /// <para>So the capacity is three where there are lights and one where there are not. Three
        /// rather than four: the two through movements on a green axis do not cross, but a left turn
        /// crosses the oncoming through, and every extra car in the box is another chance to watch that
        /// happen. Paired with releasing the junction over the last few metres of the turn — see
        /// <see cref="Advance"/> — it roughly doubles what a green phase gets through without ever
        /// letting two cars want the same piece of tarmac.</para>
        /// </summary>
        private int[] nodeLoad;

        private byte[] nodeCapacity;

        private Transform viewer;

        /// <summary>
        /// Where each agent was put this frame, so <see cref="GapAhead"/> never touches a Transform.
        ///
        /// <para>The position is computed in <see cref="Advance"/> anyway and then written to the
        /// transform; reading it back out of the transform for every pair of agents was buying nothing
        /// and costing a managed-to-native call each time. The loop is still O(N²), and deliberately so —
        /// at sixty-four cars that is four thousand vector subtractions, which is nothing, while a
        /// spatial index would be real bookkeeping to maintain against agents that teleport. This is the
        /// change that makes the constant small enough for the quadratic not to matter.</para>
        ///
        /// <para>One frame stale for agents later in the array, which is correct rather than a tolerated
        /// error: an agent should see the world as it was when it started moving, not a mixture of before
        /// and after depending on its index.</para>
        ///
        /// <para><b>Written by <see cref="PlaceOnLane"/> as well as by Advance.</b> It used not to be,
        /// which left a freshly seeded or freshly moved car reporting its previous position for a frame —
        /// a phantom obstacle at the place it came from, and on the first frame of all, ninety-six of
        /// them at the world origin.</para>
        /// </summary>
        private Vector3[] agentAt;

        /// <summary>
        /// And which way each was pointing, so an agent can tell oncoming traffic from a queue.
        ///
        /// <para><see cref="Ahead"/> is a cone about the agent's own heading, and its width is a
        /// compromise: wide enough to catch a car nosing out of a junction, narrow enough to miss the
        /// oncoming lane. On a street that is nine metres kerb to kerb the two lanes are four metres
        /// apart and the cone is 2.6 wide, so it works — until the street bows, or until the two cars are
        /// on connectors curving past each other, at which point each is inside the other's cone and both
        /// brake to a stop. Nothing ever released them: they were each other's obstacle, permanently.</para>
        ///
        /// <para>A dot product against the other agent's heading settles it in one instruction, and it is
        /// tested first, so it costs less than the <see cref="Ahead"/> call it skips. Crossing traffic —
        /// a dot near zero — stays visible, which is the case that actually needs the cone.</para>
        /// </summary>
        private Vector3[] agentForward;

        /// <summary>
        /// Cumulative length of the driven lanes, for picking one in proportion to its size.
        ///
        /// <para>Uniform over lane <i>indices</i> was the old rule, and it put as many cars on a forty
        /// metre alley as on five kilometres of pass — with eighty-odd town streets and two trunk lanes,
        /// almost the whole pool ended up in Talheim. Weighting by length makes "pick a lane" mean "pick
        /// a place on the road network", which is what every caller wanted it to mean.</para>
        ///
        /// <para>Only used for the first seed now, before there is a viewer to be near. Everything after
        /// that goes through <see cref="FindSpot"/>, which picks by where the player is.</para>
        /// </summary>
        private float[] laneWeight;

        /// <summary>Which lane each entry of <see cref="laneWeight"/> refers to.</summary>
        private int[] drivenLane;

        private void Awake()
        {
            if (network == null || cars == null || cars.Length == 0 || network.LaneCount == 0)
            {
                enabled = false;
                return;
            }

            agents = new Agent[cars.Length];
            agentAt = new Vector3[cars.Length];
            agentForward = new Vector3[cars.Length];

            nodeLoad = new int[Mathf.Max(1, network.NodeCount)];
            nodeCapacity = new byte[nodeLoad.Length];

            BuildLaneWeights();
            BuildNodeCapacity();
            BuildTurnWeights();
            BuildLaneGrid();
            BuildWatchdogLimits();

            laneHead = new float[network.LaneCount];
            laneStamp = new int[network.LaneCount];

            for (int i = 0; i < agents.Length; i++)
            {
                agents[i].Random = (uint)(i * 2654435761u + 12345u);
                agents[i].HeldNode = -1;
                agents[i].NextLane = -1;

                // Matches the state the renderers are actually in, so the first frame past the load
                // radius switches them off rather than agreeing with itself that it already had.
                agents[i].Visible = true;

                // Spread over the network rather than started together, or the whole pool leaves the
                // same junction in convoy on the first frame.
                PlaceOnLane(i, DrivenLane(ref agents[i].Random),
                    NextFloat(ref agents[i].Random));
            }
        }

        private void Update()
        {
            ResolveViewer();

            float dt = Time.deltaTime;
            Vector3 eye = viewer != null ? viewer.position : Vector3.zero;
            Vector3 gaze = viewer != null ? viewer.forward : Vector3.forward;

            int count = Mathf.Min(agents.Length, activeBudget);

            RefreshSignals();
            RefreshLaneHeads(count);

            for (int i = 0; i < count; i++)
            {
                Advance(i, dt, eye, gaze, count);
            }

            Census(dt, eye, gaze, count);
        }

        private void Advance(int index, float dt, Vector3 eye, Vector3 gaze, int count)
        {
            network.GetLane(agents[index].Lane, agents[index].Distance,
                out Vector3 position, out Vector3 forward);

            float gap = GapAhead(index, position, forward, eye, count);

            bool onConnector = network.NodeOf(agents[index].Lane) >= 0;

            // And the junction ahead, if the lane runs out before the look-ahead does. An agent stops at
            // the end of its lane rather than in the middle of the junction, which is what a give-way
            // line — and now a stop line — is.
            float remaining = network.LengthOf(agents[index].Lane) - agents[index].Distance;
            bool atSignal = false;

            if (!onConnector && remaining < lookAhead && !CanEnterNext(index, remaining, out atSignal))
            {
                gap = Mathf.Min(gap, remaining + stopGap);
            }

            float limit = LimitOf(agents[index].Lane);
            float target = limit;

            if (gap < stopGap)
            {
                target = 0f;
            }
            else if (gap < lookAhead)
            {
                target = limit * Mathf.InverseLerp(stopGap, lookAhead, gap);
            }

            float rate = target > agents[index].Speed ? acceleration : braking;
            agents[index].Speed = Mathf.MoveTowards(agents[index].Speed, target, rate * dt);
            agents[index].Distance += agents[index].Speed * dt;

            // Out of the junction before the turn is finished. A car holds a junction from the moment it
            // enters the connector until it hands over at the far end, which counts the whole tail of the
            // turn — the part where it is already lined up with the street it is joining and is in
            // nobody's way. Giving the place back over the last few metres roughly doubles what a green
            // phase gets through, and costs one comparison.
            if (onConnector && agents[index].HeldNode >= 0
                && agents[index].Distance > network.LengthOf(agents[index].Lane) - JunctionClearance)
            {
                ReleaseNode(index);
            }

            // Bounded, not `while (true)`. A car crossing a five-metre connector at eleven metres a
            // second passes through at most one or two lanes in a frame, and a bound is what stops a
            // network with a zero-length lane in it hanging the editor inside Update.
            for (int hop = 0; hop < 4 && agents[index].Distance >= network.LengthOf(agents[index].Lane);
                 hop++)
            {
                if (!Handover(index))
                {
                    break;
                }
            }

            network.GetLane(agents[index].Lane, agents[index].Distance, out position, out forward);
            position.y += rideHeight;

            cars[index].SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
            agentAt[index] = position;
            agentForward[index] = forward;

            Watchdog(index, dt, atSignal, position, eye, gaze);
            Recycle(index, position, eye, gaze);
        }

        /// <summary>How far before the end of a connector an agent gives the junction back, metres.</summary>
        private const float JunctionClearance = 5f;

        /// <summary>
        /// Distance to the nearest thing in front of this agent — another car, or the player.
        ///
        /// <para>A forward cone rather than a projection onto the lane, and the difference is worth
        /// naming. Projecting everything onto every lane would be exact and would cost a search per
        /// agent per obstacle; a cone along the agent's own heading gets the same answer wherever the
        /// road is straighter than the cone is wide, which on a town street it always is. It also
        /// handles the case lane projection would miss entirely: a car nosing out of a junction on a
        /// different lane, which is exactly where the collisions would be.</para>
        ///
        /// <para>Bounded by the active budget, not by the pool. See <see cref="ActiveBudget"/>.</para>
        /// </summary>
        private float GapAhead(int index, Vector3 position, Vector3 forward, Vector3 eye, int count)
        {
            float nearest = float.MaxValue;

            for (int other = 0; other < count; other++)
            {
                if (other == index)
                {
                    continue;
                }

                // Oncoming traffic is not an obstacle, it is the other side of the road. Tested before
                // the cone because it is one instruction and the cone is a dozen.
                if (Vector3.Dot(forward, agentForward[other]) < -0.3f)
                {
                    continue;
                }

                nearest = Mathf.Min(nearest, Ahead(position, forward, agentAt[other]));
            }

            // The player is exempt from that rule: a car parked facing the wrong way up a street is
            // still something to stop for, and it is the one obstacle here that can be anywhere.
            if (viewer != null)
            {
                nearest = Mathf.Min(nearest, Ahead(position, forward, eye));
            }

            return nearest;
        }

        /// <summary>How far ahead a point is, or <see cref="float.MaxValue"/> if it is not in the way.</summary>
        private float Ahead(Vector3 from, Vector3 forward, Vector3 point)
        {
            Vector3 offset = point - from;
            offset.y = 0f;

            float along = Vector3.Dot(offset, forward);
            if (along <= 0f || along > lookAhead)
            {
                return float.MaxValue;
            }

            float lateral = (offset - forward * along).magnitude;
            return lateral <= lateralReach ? along : float.MaxValue;
        }

        // ---------------------------------------------------------------------------------------------
        // Signals
        // ---------------------------------------------------------------------------------------------

        /// <summary>What each phase group is showing, sampled once a frame rather than per agent.</summary>
        private TrafficSignalState[] groupState;

        private void RefreshSignals()
        {
            int groups = network.SignalGroupCount;
            if (groups <= 0)
            {
                return;
            }

            if (groupState == null || groupState.Length != groups)
            {
                groupState = new TrafficSignalState[groups];
            }

            float now = Time.time;
            for (int i = 0; i < groups; i++)
            {
                groupState[i] = network.SignalStateOf(i, now);
            }
        }

        /// <summary>
        /// Whether this agent may leave its lane and enter the junction at the end of it.
        ///
        /// <para>Four tests, cheapest first, and each of them is a different reason to wait: the light is
        /// against you, the junction is full, there is nowhere to go on the far side, or you are already
        /// in it. The order matters only for cost — a red light is one array read and settles nine cars
        /// out of ten at a controlled junction.</para>
        ///
        /// <para>It also picks the turn, and that is not a side effect but the point: the room-on-the-far-
        /// side test has to be asked about the lane the car is actually going to take, and
        /// <see cref="Handover"/> then takes that one rather than rolling again.</para>
        /// </summary>
        private bool CanEnterNext(int index, float remaining, out bool atSignal)
        {
            atSignal = false;

            int lane = agents[index].Lane;

            if (network.ExitCount(lane) == 0)
            {
                return true;
            }

            // Already inside: a car that has claimed the junction is not asked to claim it twice.
            int connector = ChooseExit(index);
            if (connector < 0)
            {
                return true;
            }

            int node = network.NodeOf(connector);
            if (node >= 0 && agents[index].HeldNode == node)
            {
                return true;
            }

            // Stage two of the watchdog waives the rules rather than let a car sit there — see Watchdog.
            bool impatient = agents[index].Stalled >= watchdogRelease;

            int signal = network.SignalOf(lane);
            if (signal >= 0 && groupState != null && signal < groupState.Length && !impatient)
            {
                TrafficSignalState state = groupState[signal];

                if (state == TrafficSignalState.Red)
                {
                    atSignal = true;
                    return false;
                }

                // Amber means stop if you still can. Derived from the braking rate rather than written
                // as a multiple of the stop gap, so it stays right if the braking is ever retuned: a car
                // nearer than its own stopping distance goes through, anything further back holds.
                if (state == TrafficSignalState.Amber && remaining > StoppingDistance(index))
                {
                    atSignal = true;
                    return false;
                }
            }

            if (node >= 0 && nodeLoad[node] >= nodeCapacity[node])
            {
                return false;
            }

            // Do not block the box. A junction a car cannot get out of the far side of is a junction it
            // must not enter — without this, one car stopped just past a green light is a jam that lasts
            // until the cross phase turns green and then never clears at all.
            if (!impatient)
            {
                int onward = network.ExitCount(connector) > 0 ? network.ExitAt(connector, 0) : -1;
                if (onward >= 0 && LaneHead(onward) < BoxLength)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>How much room a car needs on the far side of a junction before it enters, metres.</summary>
        private const float BoxLength = 7f;

        private float StoppingDistance(int index)
        {
            float speed = agents[index].Speed;
            return speed * speed / (2f * Mathf.Max(0.1f, braking)) + 1.5f;
        }

        // ---------------------------------------------------------------------------------------------
        // Lane occupancy, for the box check
        // ---------------------------------------------------------------------------------------------

        /// <summary>How far along its lane the rearmost active agent on it is.</summary>
        private float[] laneHead;

        /// <summary>The frame that entry was written, so nothing has to be cleared between frames.</summary>
        private int[] laneStamp;

        private int laneStampNow;

        private void RefreshLaneHeads(int count)
        {
            laneStampNow++;

            for (int i = 0; i < count; i++)
            {
                int lane = agents[i].Lane;

                if (laneStamp[lane] != laneStampNow)
                {
                    laneStamp[lane] = laneStampNow;
                    laneHead[lane] = agents[i].Distance;
                }
                else if (agents[i].Distance < laneHead[lane])
                {
                    laneHead[lane] = agents[i].Distance;
                }
            }
        }

        /// <summary>How far into a lane the nearest car on it has got, or infinity where it is empty.</summary>
        private float LaneHead(int lane)
        {
            return laneStamp[lane] == laneStampNow ? laneHead[lane] : float.MaxValue;
        }

        // ---------------------------------------------------------------------------------------------
        // Turning
        // ---------------------------------------------------------------------------------------------

        /// <summary>Cumulative turn weights, one run per lane, flattened. See <see cref="BuildTurnWeights"/>.</summary>
        private float[] exitWeight;

        /// <summary>Prefix offsets into <see cref="exitWeight"/>, one per lane plus a terminator.</summary>
        private int[] exitWeightStart;

        /// <summary>
        /// How likely each turn out of each lane is, worked out once.
        ///
        /// <para><b>Uniform over exits was the old rule and it is why the traffic looked aimless.</b> At
        /// a crossroads on the boulevard, turning into a residential stub was as likely as carrying
        /// straight on down the main road — so the main road emptied and the stubs filled, which is the
        /// exact opposite of what a city looks like from inside a car. Three things decide it now:</para>
        ///
        /// <para>Going straight on is what most traffic does, so the turn is weighted by how little it
        /// turns. Faster roads carry more traffic, which is what the per-kind speed limits baked by
        /// <c>TrafficNetworkBuilder</c> are for — with one speed for every street, as it used to be, this
        /// term did nothing at all. And a short lane is a stub rather than a route, so it is damped.</para>
        ///
        /// <para>Cumulative within each lane's run, so the pick is one random number and a scan of at
        /// most four. Built here rather than baked because it is derived from numbers the asset already
        /// carries — a second array to keep in step for something that costs a millisecond at load.</para>
        /// </summary>
        private void BuildTurnWeights()
        {
            int lanes = network.LaneCount;

            exitWeightStart = new int[lanes + 1];

            int total = 0;
            for (int lane = 0; lane < lanes; lane++)
            {
                exitWeightStart[lane] = total;
                total += network.ExitCount(lane);
            }

            exitWeightStart[lanes] = total;
            exitWeight = new float[total];

            for (int lane = 0; lane < lanes; lane++)
            {
                int count = network.ExitCount(lane);
                if (count == 0)
                {
                    continue;
                }

                network.GetLane(lane, network.LengthOf(lane), out Vector3 _, out Vector3 leaving);

                float running = 0f;

                for (int i = 0; i < count; i++)
                {
                    int connector = network.ExitAt(lane, i);
                    int target = network.ExitCount(connector) > 0
                        ? network.ExitAt(connector, 0)
                        : connector;

                    network.GetLane(target, 0f, out Vector3 _, out Vector3 joining);

                    // 1 straight on, about 0.35 at a right angle, near nothing for a U-turn.
                    float straight = Mathf.Clamp01(Vector3.Dot(leaving, joining) * 0.5f + 0.5f);
                    float weight = Mathf.Pow(Mathf.Max(0.05f, straight), 1.5f);

                    weight *= network.SpeedOf(target) / 11f;
                    weight *= Mathf.Clamp(network.LengthOf(target) / 120f, 0.4f, 1.6f);

                    running += Mathf.Max(0.01f, weight);
                    exitWeight[exitWeightStart[lane] + i] = running;
                }
            }
        }

        /// <summary>
        /// The connector this agent will take, decided once and remembered.
        ///
        /// Remembered because <see cref="CanEnterNext"/> asks about the far side of a particular turn
        /// every frame while the car creeps up to the line, and rolling a fresh one each time would mean
        /// testing one exit and taking another.
        /// </summary>
        private int ChooseExit(int index)
        {
            if (agents[index].NextLane >= 0)
            {
                return agents[index].NextLane;
            }

            int lane = agents[index].Lane;
            int count = network.ExitCount(lane);

            if (count == 0)
            {
                return -1;
            }

            int first = exitWeightStart[lane];
            float total = exitWeight[first + count - 1];

            int chosen = count - 1;

            if (total > 0f)
            {
                float pick = NextFloat(ref agents[index].Random) * total;

                for (int i = 0; i < count; i++)
                {
                    if (pick <= exitWeight[first + i])
                    {
                        chosen = i;
                        break;
                    }
                }
            }

            agents[index].NextLane = network.ExitAt(lane, chosen);
            return agents[index].NextLane;
        }

        /// <summary>
        /// Moves an agent onto the next lane, carrying the overshoot so a handover costs no distance.
        ///
        /// Returns false when there is nowhere to go, which hands the agent to the watchdog rather than
        /// leaving it parked forever on the last lane it reached.
        /// </summary>
        private bool Handover(int index)
        {
            int lane = agents[index].Lane;
            int count = network.ExitCount(lane);

            if (count == 0)
            {
                // Unreachable with the current bake — AddConnectors always admits the U-turn when there
                // is nothing else, so every lane has an exit, and the route validator checks it. Kept
                // because "unreachable" is a property of a bake rather than of this file, and the cost of
                // being wrong used to be a car parked on the carriageway for the rest of the session.
                agents[index].Distance = network.LengthOf(lane);
                agents[index].Speed = 0f;
                agents[index].Stalled = Mathf.Max(agents[index].Stalled, watchdogMove);
                return false;
            }

            float overshoot = agents[index].Distance - network.LengthOf(lane);

            int chosen = network.NodeOf(lane) >= 0
                ? network.ExitAt(lane, 0)
                : ChooseExit(index);

            ReleaseNode(index);
            agents[index].NextLane = -1;

            int node = network.NodeOf(chosen);
            if (node >= 0)
            {
                // Taken even if the junction is at capacity: CanEnterNext should have stopped this agent
                // short, and if it did not, driving on is better than deadlocking. The count's job is to
                // make the common case orderly, not to be a lock.
                nodeLoad[node]++;
                agents[index].HeldNode = node;
            }

            agents[index].Lane = chosen;
            agents[index].Distance = Mathf.Max(0f, overshoot);

            return network.LengthOf(chosen) > 0.01f;
        }

        /// <summary>
        /// Gives back the junction this agent was occupying.
        ///
        /// <para><b>Idempotent, and it has to be.</b> The old ownership token could be released twice
        /// harmlessly because the second release found somebody else's index in the slot and did
        /// nothing. A count cannot: two releases for one claim leak a place at the junction, and
        /// <see cref="ActiveBudget"/> calls this for every out-of-budget agent on every assignment —
        /// which <c>QualityDirector</c> makes on every preset change.</para>
        /// </summary>
        private void ReleaseNode(int index)
        {
            int held = agents[index].HeldNode;
            if (held < 0)
            {
                return;
            }

            agents[index].HeldNode = -1;

            if (nodeLoad[held] > 0)
            {
                nodeLoad[held]--;
            }
        }

        /// <summary>
        /// How many cars each junction may hold at once: three where it has lights, one where it has not.
        ///
        /// <para>Read off the lanes rather than off a second baked table. A junction has lights exactly
        /// when some driven lane leading into it stops at one, and that is what
        /// <c>TrafficNetwork.SignalOf</c> says — so the two cannot drift apart the way a parallel array
        /// would.</para>
        /// </summary>
        private void BuildNodeCapacity()
        {
            for (int i = 0; i < nodeCapacity.Length; i++)
            {
                nodeCapacity[i] = 1;
            }

            for (int lane = 0; lane < network.LaneCount; lane++)
            {
                if (network.NodeOf(lane) >= 0 || network.SignalOf(lane) < 0)
                {
                    continue;
                }

                for (int i = 0; i < network.ExitCount(lane); i++)
                {
                    int node = network.NodeOf(network.ExitAt(lane, i));
                    if (node >= 0 && node < nodeCapacity.Length)
                    {
                        nodeCapacity[node] = 3;
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // The watchdog
        // ---------------------------------------------------------------------------------------------

        /// <summary>Seconds stopped before an agent re-decides which way it was going to turn.</summary>
        private const float WatchdogRethink = 8f;

        /// <summary>
        /// And before it stops obeying the junction rules, and before it is simply moved elsewhere.
        ///
        /// <para><b>Derived from the signal cycle, not written down, and the first version of this file
        /// had them written down at 12 and 20 seconds — which is less than an ordinary wait at a red
        /// light.</b> One axis is red for nine seconds of every sixteen, a green phase clears about four
        /// cars, and the fifth car in a queue therefore waits a full extra cycle: twenty-five seconds,
        /// with nothing wrong anywhere. At twelve the watchdog would have waived that car's signal check
        /// and at twenty it would have teleported it out of the queue. A backstop that fires on correct
        /// behaviour is worse than no backstop, because it hides the thing it was built to catch.</para>
        /// </summary>
        private float watchdogRelease = 24f;

        private float watchdogMove = 40f;

        private void BuildWatchdogLimits()
        {
            float cycle = network.SignalCycle;
            if (cycle <= 0f || network.SignalGroupCount <= 0)
            {
                return;
            }

            // Long enough for a car to sit through a red, then a green it does not clear, then the next
            // red — which is the worst an agent can legitimately wait — with a cycle in hand.
            watchdogRelease = Mathf.Max(watchdogRelease, cycle * 1.5f);
            watchdogMove = Mathf.Max(watchdogMove, cycle * 2.5f);
        }

        /// <summary>
        /// The backstop: nothing may sit still forever.
        ///
        /// <para><b>Not a fix for anything, and that is the point of writing it down.</b> Every deadlock
        /// this was built alongside has its own correction elsewhere in this file — the phantom
        /// obstacles, the head-on stare, the blocked box. What this catches is the next one, the one
        /// nobody has thought of, and it turns "a car is parked on the boulevard for the rest of the
        /// session" into "a car waited a while and then went about its business". If the log shows this
        /// firing regularly, something above it is broken and this is hiding it.</para>
        ///
        /// <para>Three stages, because the cheap remedy fixes most of it: re-decide the turn (two cars
        /// each waiting for the other's box usually resolve when one of them changes its mind), then
        /// stop obeying the light or the junction count, then leave.</para>
        /// </summary>
        /// <param name="atSignal">
        /// Whether this agent is stopped because a light is against it. Time spent there is not counted
        /// at all: a car at the front of a queue would otherwise accumulate a full red every cycle and
        /// eventually waive the very check that is holding it — that is, drive through the red — which
        /// is the one outcome worse than sitting at it. Cars further back are held by the car in front
        /// rather than by the light, and they are covered by the limits being longer than two cycles.
        /// </param>
        private void Watchdog(
            int index, float dt, bool atSignal, Vector3 position, Vector3 eye, Vector3 gaze)
        {
            if (agents[index].Speed > 0.5f)
            {
                agents[index].Stalled = 0f;
                return;
            }

            if (atSignal)
            {
                return;
            }

            float before = agents[index].Stalled;
            agents[index].Stalled += dt;

            if (before < WatchdogRethink && agents[index].Stalled >= WatchdogRethink)
            {
                agents[index].NextLane = -1;
            }

            if (agents[index].Stalled < watchdogMove)
            {
                return;
            }

            // Only out of sight, and it keeps trying every frame until it is. A car vanishing from under
            // the player's bonnet is worse than a car that is still stuck.
            if (!OutOfSight(position, eye, gaze)
                || !FindSpot(index, eye, gaze, false, out int lane, out float at))
            {
                return;
            }

            ReleaseNode(index);
            agents[index].NextLane = -1;
            agents[index].Stalled = 0f;

            PlaceOnLane(index, lane, at / Mathf.Max(1f, network.LengthOf(lane)));
        }

        // ---------------------------------------------------------------------------------------------
        // Where the cars are
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Hides an agent that has fallen outside the render radius, and moves one that has fallen well
        /// outside it to a lane the player might actually meet.
        ///
        /// <para>Deliberately <b>not</b> a <see cref="WorldChunk"/>. Chunk toggling flips
        /// <c>enabled</c> across cached renderer arrays gathered once at load, and was written for
        /// geometry that never leaves the tile it was built on. An object that migrates between chunks
        /// every few seconds is the one thing it cannot express.</para>
        /// </summary>
        private void Recycle(int index, Vector3 position, Vector3 eye, Vector3 gaze)
        {
            if (viewer == null)
            {
                return;
            }

            float distance = Vector3.Distance(position, eye);

            bool visible = distance < loadRadius;
            if (visible != agents[index].Visible && renderers != null && index < renderers.Length
                && renderers[index] != null)
            {
                renderers[index].enabled = visible;
                agents[index].Visible = visible;
            }

            if (distance <= recycleRadius)
            {
                return;
            }

            Relocate(index, eye, gaze, false);
        }

        private bool Relocate(int index, Vector3 eye, Vector3 gaze, bool near)
        {
            if (!FindSpot(index, eye, gaze, near, out int lane, out float at))
            {
                return false;
            }

            ReleaseNode(index);
            agents[index].NextLane = -1;
            agents[index].Stalled = 0f;

            PlaceOnLane(index, lane, at / Mathf.Max(1f, network.LengthOf(lane)));
            return true;
        }

        private void PlaceOnLane(int index, int lane, float fraction)
        {
            agents[index].Lane = lane;
            agents[index].Distance = network.LengthOf(lane) * Mathf.Clamp01(fraction);

            // At the new lane's own limit rather than at whatever it was doing on the old one. A car
            // recycled from the pass onto a town street would otherwise arrive there at 70 km/h and brake
            // in full view, which is a stranger thing to watch than the teleport it is hiding.
            agents[index].Speed = LimitOf(lane);

            network.GetLane(lane, agents[index].Distance, out Vector3 position, out Vector3 forward);
            position.y += rideHeight;

            cars[index].SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));

            // The cached pair as well as the transform. Without this a car reports the place it came from
            // for one frame, which is a phantom obstacle at the far end of the map — and on the first
            // frame of all, before Advance has ever run, the whole pool reports the world origin.
            agentAt[index] = position;
            agentForward[index] = forward;
        }

        /// <summary>This lane's speed limit, as the director is set to drive it.</summary>
        private float LimitOf(int lane)
        {
            return network.SpeedOf(lane) * speedScale;
        }

        // ---------------------------------------------------------------------------------------------
        // The lane grid, and how full the world is
        // ---------------------------------------------------------------------------------------------

        /// <summary>Side of one grid cell, metres.</summary>
        private const float BucketSize = 128f;

        /// <summary>How far apart the sampled spots on a lane are, metres.</summary>
        private const float BucketStep = 25f;

        /// <summary>
        /// How near and how far a car may be dropped, per <see cref="TrafficLaneKind"/>, metres.
        ///
        /// <para><b>The old rule could not put a car on a town street at all, and this is the whole of
        /// why the city emptied.</b> There was one band for everything, centred on
        /// <c>(loadRadius * 1.05 + recycleRadius * 0.95) / 2</c> — 769 m at the Balanced setting — and a
        /// candidate that fell outside its lane's own length was discarded rather than clamped. A town
        /// street lane is about a hundred metres long. So the test could only ever succeed on the
        /// motorway and, marginally, the trunk road: every car that left the load radius was permanently
        /// transferred to the motorway, and nothing ever brought one back. Hochstadt did not feel empty,
        /// it drained, monotonically, for as long as you played.</para>
        ///
        /// <para>Per kind, the distances are what each road can hide a car at. Eighty metres is nothing
        /// on an open carriageway and is two blocks in a street grid, where the next building is the
        /// draw distance.</para>
        /// </summary>
        private static readonly float[] BandNear = { 90f, 200f, 0f, 420f };

        private static readonly float[] BandFar = { 260f, 560f, 0f, 880f };

        /// <summary>
        /// How many metres of lane there should be per car, per kind.
        ///
        /// <para>This is the density dial, and it is per kind rather than one number because the same
        /// spacing means different things at different speeds: a car every 140 m on a street is a busy
        /// city, and a car every 140 m on a motorway lane at 108 km/h is a traffic jam. Which streets
        /// inside a town get the cars is not decided here — that falls out of the turn weighting, which
        /// sends traffic down the boulevard and leaves the alleys quiet.</para>
        /// </summary>
        private static readonly float[] MetresPerCar = { 140f, 320f, 0f, 240f };

        /// <summary>Nearest a top-up may put a car, metres. Behind you, at this range, is behind a house.</summary>
        private const float TopUpNear = 80f;

        private int[] cellStart;
        private int[] itemLane;
        private float[] itemAt;
        private Vector3[] itemPoint;
        private byte[] itemKind;

        /// <summary>
        /// How many metres of lane each entry stands for.
        ///
        /// <para>Stored rather than assumed to be <see cref="BucketStep"/>. A lane is divided into a
        /// whole number of samples, so a forty-metre alley gets exactly one — which stands for forty
        /// metres, not twenty-five. Counting the nominal step instead undercounts a village of short
        /// streets by about a third, and no amount of tuning <see cref="MetresPerCar"/> can put that
        /// back, because the error is in the measurement rather than in the target.</para>
        /// </summary>
        private float[] itemSpan;

        private Vector2 gridOrigin;
        private int gridColumns;
        private int gridRows;

        /// <summary>
        /// Buckets every driven lane into a uniform grid, so "somewhere on a road near here" is a lookup.
        ///
        /// <para>Counts, prefix offsets, items — the shape <c>MountainField.BuildBuckets</c> and
        /// <c>StreetIndex</c> both use. About four thousand entries for the hundred kilometres of lane in
        /// this world, which is a hundred kilobytes and one pass at load.</para>
        ///
        /// <para>It replaces a search that was both expensive and unable to succeed: the old placement
        /// picked a lane at random and then swept it — up to thirty <c>GetLane</c> calls — for the point
        /// nearest the player, eight times per car per frame, and then usually rejected the result. The
        /// grid answers the same question by construction, and the point is stored so the test does not
        /// call <c>GetLane</c> at all.</para>
        /// </summary>
        private void BuildLaneGrid()
        {
            var lanes = new List<int>(1024);
            var ats = new List<float>(4096);
            var points = new List<Vector3>(4096);
            var kinds = new List<byte>(4096);
            var spans = new List<float>(4096);

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            for (int lane = 0; lane < network.LaneCount; lane++)
            {
                if (network.NodeOf(lane) >= 0 || network.LengthOf(lane) <= 8f)
                {
                    continue;
                }

                float length = network.LengthOf(lane);
                int steps = Mathf.Max(1, Mathf.RoundToInt(length / BucketStep));
                float span = length / steps;

                for (int i = 0; i < steps; i++)
                {
                    float at = (i + 0.5f) * span;
                    network.GetLane(lane, at, out Vector3 point, out Vector3 _);

                    lanes.Add(lane);
                    ats.Add(at);
                    points.Add(point);
                    kinds.Add((byte)network.KindOf(lane));
                    spans.Add(span);

                    min.x = Mathf.Min(min.x, point.x);
                    min.y = Mathf.Min(min.y, point.z);
                    max.x = Mathf.Max(max.x, point.x);
                    max.y = Mathf.Max(max.y, point.z);
                }
            }

            if (lanes.Count == 0)
            {
                cellStart = new int[1];
                itemLane = System.Array.Empty<int>();
                itemAt = System.Array.Empty<float>();
                itemPoint = System.Array.Empty<Vector3>();
                itemKind = System.Array.Empty<byte>();
                itemSpan = System.Array.Empty<float>();
                return;
            }

            gridOrigin = min - new Vector2(BucketSize, BucketSize);
            gridColumns = Mathf.Max(1, Mathf.CeilToInt((max.x - gridOrigin.x) / BucketSize) + 1);
            gridRows = Mathf.Max(1, Mathf.CeilToInt((max.y - gridOrigin.y) / BucketSize) + 1);

            int cells = gridColumns * gridRows;
            var counts = new int[cells];

            for (int i = 0; i < points.Count; i++)
            {
                counts[CellOf(points[i])]++;
            }

            cellStart = new int[cells + 1];
            for (int i = 0; i < cells; i++)
            {
                cellStart[i + 1] = cellStart[i] + counts[i];
            }

            itemLane = new int[points.Count];
            itemAt = new float[points.Count];
            itemPoint = new Vector3[points.Count];
            itemKind = new byte[points.Count];
            itemSpan = new float[points.Count];

            var cursor = new int[cells];

            for (int i = 0; i < points.Count; i++)
            {
                int cell = CellOf(points[i]);
                int slot = cellStart[cell] + cursor[cell]++;

                itemLane[slot] = lanes[i];
                itemAt[slot] = ats[i];
                itemPoint[slot] = points[i];
                itemKind[slot] = kinds[i];
                itemSpan[slot] = spans[i];
            }
        }

        private int CellOf(Vector3 point)
        {
            int column = Mathf.Clamp(
                Mathf.FloorToInt((point.x - gridOrigin.x) / BucketSize), 0, gridColumns - 1);
            int row = Mathf.Clamp(
                Mathf.FloorToInt((point.z - gridOrigin.y) / BucketSize), 0, gridRows - 1);

            return row * gridColumns + column;
        }

        /// <summary>
        /// A place to put a car: on a road, at a sensible distance for the kind of road it is, and where
        /// the player is not looking.
        ///
        /// <para>One pass over the cells near the viewer, keeping a uniformly random one of the entries
        /// that qualify — reservoir sampling, so it needs no list, no second pass and no allocation at
        /// all. Each entry is tested against <i>its own</i> kind's band, which is what lets a street, a
        /// trunk road and a motorway all be candidates in the same sweep and still be judged by the
        /// distance that suits them.</para>
        /// </summary>
        /// <param name="near">
        /// Whether this is the census topping the neighbourhood up rather than a car being moved in from
        /// far away. It matters for more than taste: a top-up that dropped a car at the motorway's own
        /// band — four hundred metres out — would leave it the farthest car again next census and move
        /// it straight back, four times a second, forever. Landing inside the radius it was called to
        /// fill is what makes the census settle.
        /// </param>
        private bool FindSpot(int index, Vector3 eye, Vector3 gaze, bool near, out int lane, out float at)
        {
            lane = -1;
            at = 0f;

            if (itemLane == null || itemLane.Length == 0)
            {
                return false;
            }

            float limit = near ? PopulateRadius : BandFar[3];
            int reach = Mathf.CeilToInt(limit / BucketSize);

            int centreColumn = Mathf.Clamp(
                Mathf.FloorToInt((eye.x - gridOrigin.x) / BucketSize), 0, gridColumns - 1);
            int centreRow = Mathf.Clamp(
                Mathf.FloorToInt((eye.z - gridOrigin.y) / BucketSize), 0, gridRows - 1);

            int seen = 0;

            for (int row = centreRow - reach; row <= centreRow + reach; row++)
            {
                if (row < 0 || row >= gridRows)
                {
                    continue;
                }

                for (int column = centreColumn - reach; column <= centreColumn + reach; column++)
                {
                    if (column < 0 || column >= gridColumns)
                    {
                        continue;
                    }

                    int cell = row * gridColumns + column;

                    for (int i = cellStart[cell]; i < cellStart[cell + 1]; i++)
                    {
                        if (!Suits(i, eye, gaze, near))
                        {
                            continue;
                        }

                        seen++;

                        if (NextFloat(ref agents[index].Random) * seen < 1f)
                        {
                            lane = itemLane[i];
                            at = itemAt[i];
                        }
                    }
                }
            }

            return lane >= 0;
        }

        /// <summary>Whether one grid entry is somewhere a car could appear without being seen to.</summary>
        private bool Suits(int item, Vector3 eye, Vector3 gaze, bool near)
        {
            int kind = itemKind[item];
            if (kind >= BandNear.Length || BandFar[kind] <= 0f)
            {
                return false;
            }

            // Topping the neighbourhood up uses one band for every kind, because the question is not
            // "how far can this road hide a car" but "is this inside the area I was asked to fill".
            float low = near ? TopUpNear : BandNear[kind];
            float high = near ? PopulateRadius : BandFar[kind];

            Vector3 offset = itemPoint[item] - eye;
            offset.y = 0f;

            float distance = offset.magnitude;
            if (distance < low || distance > high)
            {
                return false;
            }

            if (!OutOfSight(itemPoint[item], eye, gaze))
            {
                return false;
            }

            // And not on top of the car already leading that lane, which would be one appearing inside
            // another. Cheap because laneHead is this frame's, already gathered.
            int lane = itemLane[item];
            return Mathf.Abs(LaneHead(lane) - itemAt[item]) > 12f;
        }

        /// <summary>
        /// Whether a point is somewhere the player is not looking.
        ///
        /// <para>A cone rather than the camera's own frustum: this component follows the player's car,
        /// not the camera, and the two point the same way to within a few degrees. Anything behind the
        /// shoulder qualifies at any distance; anything ahead has to be past the draw radius, where the
        /// fog has it anyway.</para>
        /// </summary>
        private bool OutOfSight(Vector3 point, Vector3 eye, Vector3 gaze)
        {
            Vector3 offset = point - eye;
            offset.y = 0f;

            float distance = offset.magnitude;
            if (distance > loadRadius)
            {
                return true;
            }

            return distance > 0.01f && Vector3.Dot(offset / distance, gaze) < 0.35f;
        }

        // ---------------------------------------------------------------------------------------------
        // The census
        // ---------------------------------------------------------------------------------------------

        /// <summary>How far out the traffic is kept at the density the roads there deserve, metres.</summary>
        private const float PopulateRadius = 300f;

        /// <summary>Seconds between censuses. Four a second, the rate WorldStreamingDriver ticks at.</summary>
        private const float CensusInterval = 0.25f;

        private float censusTimer;
        private Vector3 censusEye = new Vector3(float.MaxValue, 0f, 0f);
        private readonly float[] nearMetres = new float[4];
        private bool populated;

        /// <summary>How many agents are inside the populate radius, and how many ought to be.</summary>
        public int NearCount { get; private set; }

        public int NearTarget { get; private set; }

        /// <summary>
        /// Keeps the number of cars around the player at what the roads there can justify.
        ///
        /// <para><b>This is what actually answers "no cars here, too many there".</b> Recycling alone
        /// only ever removed cars that had gone too far; nothing put one back where the player was, and
        /// on a street network nothing could — see <see cref="BandNear"/>. Counting what is near and
        /// topping it up from what is far turns the fixed pool into a local density, and because the
        /// target is metres of road divided by metres per car, a village of short lanes gets a village's
        /// worth and the boulevard gets a boulevard's.</para>
        ///
        /// <para>Four times a second, at most two cars moved per pass — a whole pool arriving at once is
        /// a thing you notice in the mirror. The first pass after the player exists is allowed eight, so
        /// the world is populated by the time anyone has driven anywhere.</para>
        /// </summary>
        private void Census(float dt, Vector3 eye, Vector3 gaze, int count)
        {
            if (viewer == null || count == 0)
            {
                return;
            }

            censusTimer -= dt;
            if (censusTimer > 0f)
            {
                return;
            }

            censusTimer = CensusInterval;

            // The road nearby only changes when the player moves, and measuring it is the expensive
            // half. Forty metres is a fifth of a city block.
            if ((eye - censusEye).sqrMagnitude > 40f * 40f)
            {
                censusEye = eye;
                MeasureNearbyRoad(eye);
            }

            float target = 0f;
            for (int kind = 0; kind < nearMetres.Length; kind++)
            {
                if (MetresPerCar[kind] > 0f)
                {
                    target += nearMetres[kind] / MetresPerCar[kind];
                }
            }

            // Never the whole pool: the rest has to stay out there, or the world behind you is empty the
            // moment you turn round.
            NearTarget = Mathf.Clamp(Mathf.RoundToInt(target), 4, Mathf.RoundToInt(count * 0.7f));

            int near = 0;
            int farthest = -1;
            float farthestAt = PopulateRadius;

            for (int i = 0; i < count; i++)
            {
                float distance = Vector3.Distance(agentAt[i], eye);

                if (distance <= PopulateRadius)
                {
                    near++;
                }
                else if (distance > farthestAt)
                {
                    farthestAt = distance;
                    farthest = i;
                }
            }

            NearCount = near;

            if (near >= NearTarget - 1 || farthest < 0)
            {
                populated = true;
                return;
            }

            int moves = populated ? 2 : 8;

            for (int move = 0; move < moves && near < NearTarget; move++)
            {
                if (farthest < 0 || !Relocate(farthest, eye, gaze, true))
                {
                    break;
                }

                near++;

                // Next farthest, found on the way rather than by sorting: this runs a handful of times a
                // second over at most ninety-six entries.
                farthest = -1;
                farthestAt = PopulateRadius;

                for (int i = 0; i < count; i++)
                {
                    float distance = Vector3.Distance(agentAt[i], eye);
                    if (distance > farthestAt)
                    {
                        farthestAt = distance;
                        farthest = i;
                    }
                }
            }

            populated = true;
            NearCount = near;
        }

        /// <summary>How many metres of each kind of lane lie within the populate radius.</summary>
        private void MeasureNearbyRoad(Vector3 eye)
        {
            for (int i = 0; i < nearMetres.Length; i++)
            {
                nearMetres[i] = 0f;
            }

            if (itemLane == null || itemLane.Length == 0)
            {
                return;
            }

            int reach = Mathf.CeilToInt(PopulateRadius / BucketSize);

            int centreColumn = Mathf.Clamp(
                Mathf.FloorToInt((eye.x - gridOrigin.x) / BucketSize), 0, gridColumns - 1);
            int centreRow = Mathf.Clamp(
                Mathf.FloorToInt((eye.z - gridOrigin.y) / BucketSize), 0, gridRows - 1);

            float squared = PopulateRadius * PopulateRadius;

            for (int row = centreRow - reach; row <= centreRow + reach; row++)
            {
                if (row < 0 || row >= gridRows)
                {
                    continue;
                }

                for (int column = centreColumn - reach; column <= centreColumn + reach; column++)
                {
                    if (column < 0 || column >= gridColumns)
                    {
                        continue;
                    }

                    int cell = row * gridColumns + column;

                    for (int i = cellStart[cell]; i < cellStart[cell + 1]; i++)
                    {
                        Vector3 offset = itemPoint[i] - eye;
                        offset.y = 0f;

                        if (offset.sqrMagnitude <= squared)
                        {
                            // Each entry stands for the stretch of lane it was sampled from, which is
                            // its own span rather than the nominal step. See itemSpan.
                            nearMetres[itemKind[i]] += itemSpan[i];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A random lane that is a road rather than a turn connector.
        ///
        /// Starting a car inside a junction would have it occupy a place it never claimed through the
        /// handover, and hold it until it happened to leave.
        /// </summary>
        private int DrivenLane(ref uint state)
        {
            if (laneWeight == null || laneWeight.Length == 0)
            {
                return 0;
            }

            float total = laneWeight[laneWeight.Length - 1];
            if (total <= 0f)
            {
                return 0;
            }

            float pick = NextFloat(ref state) * total;

            // Binary search over the cumulative lengths. Uniform over metres of road, which is what
            // makes the pool spread across the world in proportion to how much road is there.
            int low = 0;
            int high = laneWeight.Length - 1;

            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (laneWeight[middle] < pick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return drivenLane[low];
        }

        /// <summary>Cumulative lengths and the lanes they belong to. See <see cref="laneWeight"/>.</summary>
        private void BuildLaneWeights()
        {
            var lanes = new List<int>(network.LaneCount);
            var weights = new List<float>(network.LaneCount);
            float running = 0f;

            for (int lane = 0; lane < network.LaneCount; lane++)
            {
                // Connectors excluded: starting a car inside a junction would have it occupy a place it
                // never claimed through the handover, and hold it until it happened to leave.
                if (network.NodeOf(lane) >= 0 || network.LengthOf(lane) <= 1f)
                {
                    continue;
                }

                running += network.LengthOf(lane);
                lanes.Add(lane);
                weights.Add(running);
            }

            drivenLane = lanes.ToArray();
            laneWeight = weights.ToArray();
        }

        /// <summary>
        /// Xorshift, per agent. Not <c>UnityEngine.Random</c>, whose state is global — with that, which
        /// way a car turns would depend on how many other things had rolled a die that frame.
        /// </summary>
        private static float NextFloat(ref uint state)
        {
            if (state == 0u)
            {
                state = 0x9E3779B9u;
            }

            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            return (state >> 8) * (1f / 16777216f);
        }

        /// <summary>
        /// What the traffic drives around, and what it is kept near.
        ///
        /// <para>The player's <b>car</b>, not the camera, and the distinction is not pedantry. This
        /// transform is used for two things: deciding which cars to move somewhere useful, where either
        /// would do, and as an obstacle in <see cref="GapAhead"/>, where they are metres apart.
        /// <c>ChaseCamera</c> trails the car by 6.5 m, so traffic ahead of the player was braking 6.5 m
        /// later than it should, and traffic behind was braking for a point in mid-air that no car was
        /// occupying. On a motorway where the whole game is threading between moving cars, that is the
        /// difference between traffic that reacts to you and traffic that reacts to where you were.</para>
        ///
        /// <para>Falls back to the camera, because the world scene opened on its own has no vehicle in
        /// it and traffic that piles up at the origin is a worse debugging experience than traffic that
        /// follows the editor camera.</para>
        /// </summary>
        private void ResolveViewer()
        {
            if (viewer != null)
            {
                return;
            }

            VehicleController vehicle = FindFirstObjectByType<VehicleController>();
            if (vehicle != null)
            {
                viewer = vehicle.transform;
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = FindFirstObjectByType<Camera>();
            }

            if (camera != null)
            {
                viewer = camera.transform;
            }
        }
    }
}
