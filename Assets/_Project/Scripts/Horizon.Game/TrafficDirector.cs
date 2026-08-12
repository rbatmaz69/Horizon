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

            /// <summary>The junction whose token this agent holds, or -1.</summary>
            public int HeldNode;

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

        [Tooltip("Stops this far short of whatever is in front of it.")]
        [SerializeField] private float stopGap = 6.5f;

        [Tooltip("How far off an agent's own path something can be and still count as being in the way. "
               + "Half a carriageway: wide enough to catch a car merging out of a junction, narrow "
               + "enough not to brake for oncoming traffic on the other side of the road.")]
        [SerializeField] private float lateralReach = 2.6f;

        [Header("Streaming")]
        [Tooltip("Renderers switch off past this. Matches WorldStreamer's own load radius — a car drawn "
               + "beyond the chunk it is standing on is a car floating in fog.")]
        [SerializeField] private float loadRadius = 650f;

        [Tooltip("Past this an agent is teleported to a lane near the viewer instead. It is cheaper to "
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

        private Agent[] agents;

        /// <summary>
        /// Which agent holds each junction, or -1. Claimed for the length of a connector.
        ///
        /// <para>Crude on purpose. It is one integer per junction, it allocates nothing, and it prevents
        /// the single failure that reads as broken rather than as busy — two cars passing through each
        /// other in the middle of an intersection. Everything subtler about giving way is invisible at
        /// the distance ambient traffic is watched from.</para>
        /// </summary>
        private int[] nodeToken;

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
        /// </summary>
        private Vector3[] agentAt;

        /// <summary>
        /// Cumulative length of the driven lanes, for picking one in proportion to its size.
        ///
        /// <para>Uniform over lane <i>indices</i> was the old rule, and it put as many cars on a forty
        /// metre alley as on five kilometres of pass — with eighty-odd town streets and two trunk lanes,
        /// almost the whole pool ended up in Talheim. Weighting by length makes "pick a lane" mean "pick
        /// a place on the road network", which is what every caller wanted it to mean.</para>
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
            nodeToken = new int[Mathf.Max(1, network.NodeCount)];

            BuildLaneWeights();

            for (int i = 0; i < nodeToken.Length; i++)
            {
                nodeToken[i] = -1;
            }

            for (int i = 0; i < agents.Length; i++)
            {
                agents[i].Random = (uint)(i * 2654435761u + 12345u);
                agents[i].HeldNode = -1;

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

            for (int i = 0; i < agents.Length; i++)
            {
                Advance(i, dt, eye);
            }
        }

        private void Advance(int index, float dt, Vector3 eye)
        {
            network.GetLane(agents[index].Lane, agents[index].Distance,
                out Vector3 position, out Vector3 forward);

            float gap = GapAhead(index, position, forward, eye);

            // And the junction ahead, if the lane runs out before the look-ahead does. An agent stops at
            // the end of its lane rather than in the middle of the junction, which is what a give-way
            // line is.
            float remaining = network.LengthOf(agents[index].Lane) - agents[index].Distance;
            if (network.NodeOf(agents[index].Lane) < 0 && remaining < lookAhead && !CanEnterNext(index))
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

            Recycle(index, position, eye);
        }

        /// <summary>
        /// Distance to the nearest thing in front of this agent — another car, or the player.
        ///
        /// <para>A forward cone rather than a projection onto the lane, and the difference is worth
        /// naming. Projecting everything onto every lane would be exact and would cost a search per
        /// agent per obstacle; a cone along the agent's own heading gets the same answer wherever the
        /// road is straighter than the cone is wide, which on a town street it always is. It also
        /// handles the case lane projection would miss entirely: a car nosing out of a junction on a
        /// different lane, which is exactly where the collisions would be.</para>
        /// </summary>
        private float GapAhead(int index, Vector3 position, Vector3 forward, Vector3 eye)
        {
            float nearest = float.MaxValue;

            for (int other = 0; other < agents.Length; other++)
            {
                if (other == index)
                {
                    continue;
                }

                nearest = Mathf.Min(nearest, Ahead(position, forward, agentAt[other]));
            }

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

        /// <summary>Whether the junction this agent's lane leads into is free, or already held by it.</summary>
        private bool CanEnterNext(int index)
        {
            int lane = agents[index].Lane;
            if (network.ExitCount(lane) == 0)
            {
                return true;
            }

            int connector = network.ExitAt(lane, 0);
            int node = network.NodeOf(connector);

            return node < 0 || nodeToken[node] < 0 || nodeToken[node] == index;
        }

        /// <summary>
        /// Moves an agent onto the next lane, carrying the overshoot so a handover costs no distance.
        ///
        /// Returns false when there is nowhere to go, which leaves the agent parked at the end of its
        /// lane rather than looping forever on a lane of zero length.
        /// </summary>
        private bool Handover(int index)
        {
            int lane = agents[index].Lane;
            int count = network.ExitCount(lane);

            if (count == 0)
            {
                agents[index].Distance = network.LengthOf(lane);
                agents[index].Speed = 0f;
                return false;
            }

            float overshoot = agents[index].Distance - network.LengthOf(lane);

            int chosen = network.ExitAt(lane, count == 1 ? 0 : (int)(NextFloat(ref agents[index].Random) * count) % count);

            ReleaseToken(index);

            int node = network.NodeOf(chosen);
            if (node >= 0)
            {
                // Taken even if somebody else holds it: CanEnterNext should have stopped this agent
                // short, and if it did not, driving on is better than deadlocking. The token's job is to
                // make the common case orderly, not to be a lock.
                nodeToken[node] = index;
                agents[index].HeldNode = node;
            }

            agents[index].Lane = chosen;
            agents[index].Distance = Mathf.Max(0f, overshoot);

            return network.LengthOf(chosen) > 0.01f;
        }

        private void ReleaseToken(int index)
        {
            int held = agents[index].HeldNode;
            if (held >= 0 && nodeToken[held] == index)
            {
                nodeToken[held] = -1;
            }

            agents[index].HeldNode = -1;
        }

        /// <summary>
        /// Hides an agent that has fallen outside the render radius, and moves one that has fallen well
        /// outside it to a lane the player might actually meet.
        ///
        /// <para>Deliberately <b>not</b> a <see cref="WorldChunk"/>. Chunk toggling flips
        /// <c>enabled</c> across cached renderer arrays gathered once at load, and was written for
        /// geometry that never leaves the tile it was built on. An object that migrates between chunks
        /// every few seconds is the one thing it cannot express.</para>
        /// </summary>
        private void Recycle(int index, Vector3 position, Vector3 eye)
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

            // Eight tries, each one a lane picked in proportion to its length and then *searched* for the
            // point on it nearest the viewer.
            //
            // The old rule tested a candidate lane at its own midpoint, which worked while every lane was
            // a town street forty metres long and failed completely once they were not: the pass is one
            // 5 km polyline whose midpoint is within range only when the player happens to be halfway up
            // the mountain, so recycling onto the road the player was actually driving on almost never
            // fired. Motorway lanes are eight kilometres and would never have fired at all.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int lane = DrivenLane(ref agents[index].Random);
                float nearest = NearestDistanceOnLane(lane, eye);
                float length = network.LengthOf(lane);

                // Behind or ahead of that point, far enough out to be invisible when it lands but inside
                // the radius that would recycle it straight back. Both directions are tried because near
                // an end of a lane only one of them exists.
                float reach = (loadRadius * 1.05f + recycleRadius * 0.95f) * 0.5f;
                float side = NextFloat(ref agents[index].Random) < 0.5f ? -1f : 1f;

                for (int flip = 0; flip < 2; flip++)
                {
                    float at = nearest + side * reach;
                    side = -side;

                    if (at < 0f || at > length)
                    {
                        continue;
                    }

                    network.GetLane(lane, at, out Vector3 candidate, out Vector3 _);
                    float range = Vector3.Distance(candidate, eye);

                    // The straight-line distance can be much shorter than the distance along a road that
                    // doubles back — a switchback stack puts two points 400 m apart on the tarmac
                    // forty metres apart in the air — so the result is checked rather than assumed.
                    if (range > loadRadius * 1.02f && range < recycleRadius * 0.95f)
                    {
                        ReleaseToken(index);
                        PlaceOnLane(index, lane, at / Mathf.Max(1f, length));
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Distance along a lane of the point closest to <paramref name="target"/>.
        ///
        /// <para>Coarse sweep then a local refinement, rather than every sample: an eight-kilometre lane
        /// holds three thousand of them, and this runs for a handful of cars a second at most. The coarse
        /// step is under the load radius, so the true nearest point cannot hide between two probes.</para>
        /// </summary>
        private float NearestDistanceOnLane(int lane, Vector3 target)
        {
            float length = network.LengthOf(lane);
            float coarse = Mathf.Max(25f, loadRadius * 0.5f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / coarse));

            float bestAt = 0f;
            float bestSqr = float.MaxValue;

            for (int i = 0; i <= steps; i++)
            {
                float at = length * i / steps;
                network.GetLane(lane, at, out Vector3 point, out Vector3 _);

                float sqr = (point - target).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestAt = at;
                }
            }

            for (float window = coarse * 0.5f; window > 4f; window *= 0.5f)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    float at = Mathf.Clamp(bestAt + window * side, 0f, length);
                    network.GetLane(lane, at, out Vector3 point, out Vector3 _);

                    float sqr = (point - target).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        bestAt = at;
                    }
                }
            }

            return bestAt;
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
        }

        /// <summary>This lane's speed limit, as the director is set to drive it.</summary>
        private float LimitOf(int lane)
        {
            return network.SpeedOf(lane) * speedScale;
        }

        /// <summary>
        /// A random lane that is a road rather than a turn connector.
        ///
        /// Starting or recycling a car inside a junction would have it claim a token it never took
        /// through the handover, and hold it until it happened to leave.
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
                // Connectors excluded: starting or recycling a car inside a junction would have it claim
                // a token it never took through the handover, and hold it until it happened to leave.
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
