using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Drives a fixed pool of ambient cars along the baked <see cref="TrafficNetwork"/>.
    ///
    /// <para><b>The agents are kinematic followers, not vehicles.</b> No Rigidbody dynamics, no wheel
    /// raycasts, no engine — a lane index, a distance along it, and a speed, integrated and written
    /// straight to the transform. Fourteen cars each running the raycast-wheel model would cost more per
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
        [Tooltip("Free-running speed, metres per second. 11 is about 40 km/h — a town speed, and fast "
               + "enough that traffic reads as moving rather than as scenery that drifts.")]
        [SerializeField] private float cruiseSpeed = 11f;

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

        private void Awake()
        {
            if (network == null || cars == null || cars.Length == 0 || network.LaneCount == 0)
            {
                enabled = false;
                return;
            }

            agents = new Agent[cars.Length];
            nodeToken = new int[Mathf.Max(1, network.NodeCount)];

            for (int i = 0; i < nodeToken.Length; i++)
            {
                nodeToken[i] = -1;
            }

            for (int i = 0; i < agents.Length; i++)
            {
                agents[i].Random = (uint)(i * 2654435761u + 12345u);
                agents[i].HeldNode = -1;
                agents[i].Speed = cruiseSpeed;

                // Matches the state the renderers are actually in, so the first frame past the load
                // radius switches them off rather than agreeing with itself that it already had.
                agents[i].Visible = true;

                // Spread over the network rather than started together, or the whole pool leaves the
                // same junction in convoy on the first frame.
                PlaceOnLane(i, StreetLane(ref agents[i].Random),
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

            float target = cruiseSpeed;
            if (gap < stopGap)
            {
                target = 0f;
            }
            else if (gap < lookAhead)
            {
                target = cruiseSpeed * Mathf.InverseLerp(stopGap, lookAhead, gap);
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

                nearest = Mathf.Min(nearest, Ahead(position, forward, cars[other].position));
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

            // Rejected rather than searched: a handful of tries finds a lane near the viewer on a network
            // this size, and a failed attempt simply leaves the car where it was for another frame.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int lane = StreetLane(ref agents[index].Random);
                network.GetLane(lane, network.LengthOf(lane) * 0.5f, out Vector3 at, out Vector3 _);

                if (Vector3.Distance(at, eye) < loadRadius * 0.8f)
                {
                    ReleaseToken(index);
                    PlaceOnLane(index, lane, NextFloat(ref agents[index].Random));
                    return;
                }
            }
        }

        private void PlaceOnLane(int index, int lane, float fraction)
        {
            agents[index].Lane = lane;
            agents[index].Distance = network.LengthOf(lane) * Mathf.Clamp01(fraction);

            network.GetLane(lane, agents[index].Distance, out Vector3 position, out Vector3 forward);
            position.y += rideHeight;

            cars[index].SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
        }

        /// <summary>
        /// A random lane that is a street rather than a turn connector.
        ///
        /// Starting or recycling a car inside a junction would have it claim a token it never took
        /// through the handover, and hold it until it happened to leave.
        /// </summary>
        private int StreetLane(ref uint state)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int lane = (int)(NextFloat(ref state) * network.LaneCount) % network.LaneCount;
                if (network.NodeOf(lane) < 0 && network.LengthOf(lane) > 1f)
                {
                    return lane;
                }
            }

            return 0;
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

        private void ResolveViewer()
        {
            if (viewer != null)
            {
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
