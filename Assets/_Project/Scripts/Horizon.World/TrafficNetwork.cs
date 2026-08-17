using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What a lane is, which decides who measures it against what.
    ///
    /// <para><c>NodeOf(lane) &lt; 0</c> used to answer this on its own, back when every lane was either a
    /// town street or a turn through a town junction. It cannot any more: a lane down the trunk road is
    /// no more a turn connector than a town street is, and a validator that measures it against the
    /// nearest street centreline reports six kilometres of correct road as six kilometres of car on the
    /// pavement.</para>
    /// </summary>
    public enum TrafficLaneKind : byte
    {
        /// <summary>One direction of one town street.</summary>
        Street = 0,

        /// <summary>One direction of one stretch of the trunk road, between two junctions.</summary>
        Trunk = 1,

        /// <summary>A turn through a junction, joining two of the above.</summary>
        Connector = 2,

        /// <summary>
        /// One of the four lanes of one carriageway of the motorway.
        ///
        /// <para>Distinct from <see cref="Trunk"/> for the same reason <see cref="Trunk"/> is distinct
        /// from <see cref="Street"/>: the validator measures a lane against the road it is supposed to
        /// be on, and a motorway lane is up to 5.6 m off its carriageway's centreline and 16 m off the
        /// median line the course was authored as. Measured against either of those as if it were a
        /// trunk lane, every metre of correct motorway reports as a car in a field.</para>
        /// </summary>
        Highway = 3,
    }

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

        [Tooltip("What each lane is — a town street, a stretch of trunk road, or a turn through a "
               + "junction. Stored as bytes because a TrafficLaneKind[] does not serialise.")]
        [SerializeField] private byte[] laneKind;

        [Tooltip("Speed limit of each lane, metres per second.\n\n"
               + "Per lane rather than one number on the director, and it is not a refinement: a town "
               + "street and a mountain pass are the same object here, and one cruise speed for both "
               + "means either the traffic races through Talheim or the player spends the whole descent "
               + "behind a car doing 40 km/h.")]
        [SerializeField] private float[] laneSpeed;

        [Tooltip("Prefix offsets into the exit array, parallel to the lane array.")]
        [SerializeField] private int[] exitStart;

        [Tooltip("Flattened lane indices: which lanes a lane leads to.")]
        [SerializeField] private int[] exits;

        [Tooltip("The signal group controlling the END of each lane, or NoSignal.\n\n"
               + "On the driven lane rather than on the connector, because the end of a driven lane is "
               + "already where the director holds a car back — a group on a connector would be a car "
               + "stopping in the middle of the junction, which is the one thing a give-way line "
               + "exists to prevent.")]
        [SerializeField] private byte[] laneSignal;

        [SerializeField] private int signalGroups;

        [Tooltip("Seconds for a full round of every phase.\n\n"
               + "Sixteen, and it is not a taste setting: Hochstadt's cross streets are 200 m apart and "
               + "the boulevard is driven at 12.5 m/s, so sixteen seconds is exactly how long it takes "
               + "to get from one junction to the next. With every junction in phase, a car that leaves "
               + "one on green reaches the next one cycle later — on green. The green wave is the cycle "
               + "length; there is deliberately no per-junction offset. See TrafficSignalPlan.")]
        [SerializeField] private float signalCycle = 16f;

        [SerializeField] private float signalGreen = 6f;

        [SerializeField] private float signalAmber = 1f;

        [SerializeField] private int nodeCount;

        /// <summary>A lane that no signal controls. 255 rather than -1 so the array can stay bytes.</summary>
        public const byte NoSignal = 255;

        /// <summary>How many phase groups the signals run on, or zero where there are none.</summary>
        public int SignalGroupCount => signalGroups;

        /// <summary>
        /// Seconds in a full round of every phase.
        ///
        /// Exposed because how long a car may legitimately be stationary is a property of this number:
        /// anything that gives up on a stopped car has to wait longer than the longest red plus the
        /// queue behind it, or it starts firing on ordinary traffic. See <c>TrafficDirector.Watchdog</c>.
        /// </summary>
        public float SignalCycle => signalCycle;

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

        public TrafficLaneKind KindOf(int lane)
        {
            return laneKind != null && lane < laneKind.Length
                ? (TrafficLaneKind)laneKind[lane]
                : TrafficLaneKind.Street;
        }

        /// <summary>
        /// This lane's speed limit, metres per second.
        ///
        /// Falls back to a town speed for a network baked before lanes carried one, so an asset from an
        /// older tool drives slowly rather than not at all.
        /// </summary>
        public float SpeedOf(int lane)
        {
            return laneSpeed != null && lane < laneSpeed.Length ? laneSpeed[lane] : 11f;
        }

        /// <summary>
        /// The signal group controlling the end of this lane, or -1.
        ///
        /// Falls back to "no signal" for a network baked before signals existed, so an old asset gives
        /// way at every junction the way it always did rather than failing to load — the same tolerance
        /// <see cref="SpeedOf"/> extends.
        /// </summary>
        public int SignalOf(int lane)
        {
            if (laneSignal == null || lane >= laneSignal.Length)
            {
                return -1;
            }

            byte group = laneSignal[lane];
            return group == NoSignal ? -1 : group;
        }

        /// <summary>
        /// What one group is showing at a given moment.
        ///
        /// <para><b>A pure function of the clock, with nothing integrated and nothing stored.</b> That
        /// is what makes a car teleported next to a junction see the right light on its first frame, and
        /// it is why the phase lives on the baked asset rather than on a component: the director and the
        /// thing that lights the lenses read the same arithmetic instead of one asking the other.</para>
        ///
        /// <para>Groups divide the cycle evenly and take their green at the start of their own share, so
        /// the leftover — a cycle half minus green minus amber — is the all-red clearance between them.
        /// At 16/6/1 that is a second and a half, which is what stops a car that went through on amber
        /// from meeting the first car off the line on the other axis.</para>
        /// </summary>
        public TrafficSignalState SignalStateOf(int group, float time)
        {
            if (signalGroups <= 0 || group < 0)
            {
                return TrafficSignalState.Green;
            }

            float share = signalCycle / signalGroups;
            float local = Mathf.Repeat(time - group * share, signalCycle);

            if (local < signalGreen)
            {
                return TrafficSignalState.Green;
            }

            return local < signalGreen + signalAmber
                ? TrafficSignalState.Amber
                : TrafficSignalState.Red;
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
            byte[] bakedLaneKind,
            float[] bakedLaneSpeed,
            int[] bakedExitStart,
            int[] bakedExits,
            int junctionCount)
        {
            points = bakedPoints;
            laneStart = bakedLaneStart;
            laneLength = bakedLaneLength;
            laneStep = bakedLaneStep;
            laneNode = bakedLaneNode;
            laneKind = bakedLaneKind;
            laneSpeed = bakedLaneSpeed;
            exitStart = bakedExitStart;
            exits = bakedExits;
            nodeCount = junctionCount;
        }

        /// <summary>
        /// Fills in the signals. Edit time only, and separate from <see cref="Fill"/> on purpose.
        ///
        /// <para>Growing <c>Fill</c> to fourteen positional parameters is how a bake ends up passing
        /// <c>laneKind</c> where <c>laneSignal</c> goes. It also keeps the two independent: a network
        /// baked without ever calling this is a valid network with no lights in it.</para>
        /// </summary>
        public void FillSignals(byte[] bakedLaneSignal, int groups, float cycle, float green, float amber)
        {
            laneSignal = bakedLaneSignal;
            signalGroups = groups;
            signalCycle = cycle;
            signalGreen = green;
            signalAmber = amber;
        }
    }
}
