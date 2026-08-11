using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The routes ambient traffic drives on, baked at edit time into flat arrays.
    ///
    /// <para><b>Everything is a lane.</b> A street contributes two directed lanes, one each way, offset
    /// half a half-width from its centreline; a junction contributes one lane per legal turn through it.
    /// Both are the same kind of object here — a polyline with a length and a set of lanes it leads to —
    /// which is why an agent needs no notion of "am I on a street or in a junction". It has a lane and a
    /// distance along it, and when it runs off the end it picks one of the exits.</para>
    ///
    /// <para><b>Flat arrays, and no per-frame allocation anywhere.</b> Same reasoning as
    /// <c>MountainField.BuildBuckets</c> and <c>StreetIndex</c>: counts, prefix offsets, items.
    /// <see cref="GetLane"/> is a divide, an index and a lerp, called once per agent per frame with
    /// nothing constructed. A <c>ScriptableObject</c> rather than a scene component because the routes
    /// are tuning data derived from the layout — the same convention <c>VehicleConfig</c> and
    /// <c>TimeOfDayProfile</c> follow.</para>
    ///
    /// <para>Samples are spaced evenly <i>within</i> each lane rather than at one global step, so a
    /// distance maps to an index by a single divide. A global step would leave a short remainder at the
    /// end of every lane and turn that divide into a search.</para>
    /// </summary>
    public sealed class TrafficNetwork : ScriptableObject
    {
        [SerializeField] private Vector3[] points;

        [Tooltip("Prefix offsets into the point array: lane i owns points[laneStart[i]] up to "
               + "laneStart[i + 1]. One entry longer than the lane count.")]
        [SerializeField] private int[] laneStart;

        [SerializeField] private float[] laneLength;

        [Tooltip("Spacing between this lane's own samples, metres. Per lane, so distance to index is a "
               + "divide rather than a search.")]
        [SerializeField] private float[] laneStep;

        [Tooltip("The junction a lane passes through, or -1 for a street lane. This is what the "
               + "director's occupancy tokens are keyed on.")]
        [SerializeField] private int[] laneNode;

        [Tooltip("Prefix offsets into the exit array, parallel to the lane array.")]
        [SerializeField] private int[] exitStart;

        [Tooltip("Flattened lane indices: which lanes a lane leads to.")]
        [SerializeField] private int[] exits;

        [SerializeField] private int nodeCount;

        /// <summary>How many lanes there are, street lanes and turn connectors together.</summary>
        public int LaneCount => laneLength != null ? laneLength.Length : 0;

        /// <summary>How many junctions the routes pass through, which sizes the occupancy table.</summary>
        public int NodeCount => nodeCount;

        public float LengthOf(int lane)
        {
            return laneLength[lane];
        }

        /// <summary>The junction a lane passes through, or -1 where it is an ordinary street lane.</summary>
        public int NodeOf(int lane)
        {
            return laneNode[lane];
        }

        public int ExitCount(int lane)
        {
            return exitStart[lane + 1] - exitStart[lane];
        }

        public int ExitAt(int lane, int index)
        {
            return exits[exitStart[lane] + index];
        }

        /// <summary>
        /// Where a lane is at a distance along it, and which way it points there.
        ///
        /// Clamped at both ends rather than wrapping or throwing: an agent that has run past the end of
        /// its lane is about to be handed to the next one, and one frame of it sitting on the last sample
        /// is invisible. Direction comes from the segment the point falls in, which at a two-metre sample
        /// spacing turns by about a degree a step.
        /// </summary>
        public void GetLane(int lane, float distance, out Vector3 position, out Vector3 direction)
        {
            int first = laneStart[lane];
            int count = laneStart[lane + 1] - first;

            if (count < 2)
            {
                position = count == 1 ? points[first] : Vector3.zero;
                direction = Vector3.forward;
                return;
            }

            float step = Mathf.Max(0.0001f, laneStep[lane]);
            float t = Mathf.Clamp(distance / step, 0f, count - 1);

            int index = Mathf.Min((int)t, count - 2);
            float fraction = t - index;

            Vector3 a = points[first + index];
            Vector3 b = points[first + index + 1];

            position = Vector3.Lerp(a, b, fraction);

            Vector3 along = b - a;
            direction = along.sqrMagnitude > 0.000001f ? along.normalized : Vector3.forward;
        }

        /// <summary>One baked sample, for the route validator. Not for the driving path.</summary>
        public Vector3 SampleAt(int lane, int index)
        {
            return points[laneStart[lane] + index];
        }

        /// <summary>How many samples a lane holds.</summary>
        public int SampleCount(int lane)
        {
            return laneStart[lane + 1] - laneStart[lane];
        }

        /// <summary>Fills the network in. Edit time only — see <see cref="TrafficNetworkBuilder"/>.</summary>
        public void Fill(
            Vector3[] bakedPoints,
            int[] bakedLaneStart,
            float[] bakedLaneLength,
            float[] bakedLaneStep,
            int[] bakedLaneNode,
            int[] bakedExitStart,
            int[] bakedExits,
            int junctionCount)
        {
            points = bakedPoints;
            laneStart = bakedLaneStart;
            laneLength = bakedLaneLength;
            laneStep = bakedLaneStep;
            laneNode = bakedLaneNode;
            exitStart = bakedExitStart;
            exits = bakedExits;
            nodeCount = junctionCount;
        }
    }
}
