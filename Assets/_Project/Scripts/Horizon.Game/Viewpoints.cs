using Horizon.Vehicle;
using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Knows where the world's viewpoints are, and notices when the car stops at one.
    ///
    /// <para><b>Twenty places were named, cleared of trees, given a lay-by and drawn on the map, and
    /// driving to one did nothing whatever.</b> <c>VegetationShape.ViewpointClearing</c> is 38 m, so the
    /// build has been cutting a hole in the forest at each of them since the day they were authored —
    /// which is to say the world already treated them as somewhere to look from, and the game did
    /// not.</para>
    ///
    /// <para><b>It reads the baked map rather than baking a list of its own, and that is the whole
    /// design.</b> <c>WorldMap</c> already holds every viewpoint as a marker with a name and a plan
    /// position, walked off the same <c>RoadFeature</c>s the courses carry — see
    /// <c>WorldMapBuilder.AddFeatures</c>. A second bake would be a second opinion about where a
    /// viewpoint is and what it is called, and the two would agree until somebody moved one. It also
    /// means the marker on the map and the place you stop at cannot be different places.</para>
    ///
    /// <para><b>No trigger volume</b>, for the reason <c>FillingStations</c> gives at length: there is
    /// not one <c>OnTriggerEnter</c> in this project, a volume would have to be authored beside every
    /// lay-by and kept in step with it, and twenty squared distances a physics step is not a cost worth
    /// building machinery to avoid.</para>
    ///
    /// <para><b>Measured in plan.</b> A viewpoint is a place on a road and the car is on that road, so
    /// height carries no information here — and the map's marker is a <c>Vector2</c>, which is the
    /// other half of the same fact. Including height would mean the one on the Weissjoch col rejecting
    /// a car nine metres below it for no reason a driver could see.</para>
    /// </summary>
    public sealed class Viewpoints : MonoBehaviour
    {
        [Tooltip("The baked map. Wired by the setup tool — it is the same asset the minimap draws.")]
        [SerializeField] private WorldMap map;

        [Tooltip("How near counts as being at one, metres. Comfortably inside the 38 m the vegetation "
               + "is cleared to, so a car parked in the lay-by is always inside it and one on the "
               + "carriageway going past is not.")]
        [SerializeField] private float reach = 30f;

        [Tooltip("Fast enough to be rolling to a stop, slow enough that nobody collects a view in "
               + "passing. FillingStations' own number, and for the same reason.")]
        [SerializeField] private float parkSpeedKmh = 4f;

        [Tooltip("How long the car must be standing before the place counts. Longer than the pump's "
               + "0.8 s: a view is something you stop to look at, and two seconds is the difference "
               + "between stopping and having stopped.")]
        [SerializeField] private float settleSeconds = 2f;

        private VehicleController vehicle;
        private float standing;

        /// <summary>Within reach of a viewpoint, at whatever speed.</summary>
        public bool IsAtViewpoint { get; private set; }

        /// <summary>At one and slow enough to be arriving rather than passing.</summary>
        public bool IsStopped { get; private set; }

        /// <summary>Its name, or null when there is none in reach.</summary>
        public string CurrentName { get; private set; }

        /// <summary>Whether the one in reach has been stood at before — this run or any earlier one.</summary>
        public bool CurrentIsVisited { get; private set; }

        /// <summary>
        /// True for a moment after a place is reached for the first time, and read by the notice line.
        ///
        /// <para>A latch rather than an event, in the shape <c>FillingStations.IsFilling</c> uses: the
        /// notice polls four times a second and an event fired inside a physics step would be missed by
        /// three frames in four. It is cleared when the car leaves.</para>
        /// </summary>
        public bool JustArrived { get; private set; }

        /// <summary>How many of them exist. Zero means the map carried none, which is worth reporting.</summary>
        public int Count { get; private set; }

        /// <summary>Hands the component the map. Called by the setup tool as it bakes the HUD.</summary>
        public void SetMap(WorldMap value)
        {
            map = value;
            Count = 0;
            indices = null;
        }

        /// <summary>
        /// Which markers are viewpoints, worked out once.
        ///
        /// <para>The map holds every kind of mark in one flat array and the viewpoints are a fifth of
        /// it, so walking the lot every physics step would be four fifths waste. Built on the first
        /// step rather than in <c>Awake</c> because the map is assigned by the setup tool and a
        /// serialized reference is not guaranteed to be there before the first frame.</para>
        /// </summary>
        private int[] indices;

        private void FixedUpdate()
        {
            if (map == null)
            {
                return;
            }

            if (indices == null && !Index())
            {
                return;
            }

            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();

                if (vehicle == null)
                {
                    Clear();
                    return;
                }
            }

            Vector3 world = vehicle.transform.position;
            var at = new Vector2(world.x, world.z);

            int best = -1;
            float bestSqr = reach * reach;

            for (int i = 0; i < indices.Length; i++)
            {
                float sqr = (map.MarkerAt(indices[i]) - at).sqrMagnitude;

                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = indices[i];
                }
            }

            if (best < 0)
            {
                Clear();
                return;
            }

            IsAtViewpoint = true;
            CurrentName = map.MarkerNameOf(best);
            CurrentIsVisited = PlayerChoices.HasVisited(CurrentName);
            IsStopped = vehicle.SpeedKmh <= parkSpeedKmh;

            standing = IsStopped ? standing + Time.fixedDeltaTime : 0f;

            if (standing < settleSeconds || CurrentIsVisited)
            {
                return;
            }

            // MarkVisited writes to disk and answers whether it was new, so the latch cannot be set
            // twice for one place even if the settle test somehow fires again.
            if (PlayerChoices.MarkVisited(CurrentName))
            {
                JustArrived = true;
                CurrentIsVisited = true;
            }
        }

        /// <summary>Everything false, so nothing is left standing from a step in which it was true.</summary>
        private void Clear()
        {
            IsAtViewpoint = false;
            IsStopped = false;
            JustArrived = false;
            CurrentIsVisited = false;
            CurrentName = null;
            standing = 0f;
        }

        private bool Index()
        {
            int count = 0;

            for (int i = 0; i < map.MarkerCount; i++)
            {
                if (map.MarkerKindOf(i) == MapMarkerKind.Viewpoint)
                {
                    count++;
                }
            }

            if (count == 0)
            {
                // Assigned rather than left null so this is not walked again every step for the life of
                // the run. A map with no viewpoints in it is a build fault and is reported as one by
                // the setup tool, not worked around here.
                indices = System.Array.Empty<int>();
                return false;
            }

            indices = new int[count];
            int next = 0;

            for (int i = 0; i < map.MarkerCount; i++)
            {
                if (map.MarkerKindOf(i) == MapMarkerKind.Viewpoint)
                {
                    indices[next++] = i;
                }
            }

            Count = count;
            return true;
        }
    }
}
