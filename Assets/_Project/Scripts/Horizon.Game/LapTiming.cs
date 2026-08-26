using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Counts laps of the Weissjochring and times them.
    ///
    /// <para><b>Without this the circuit is a wide road with kerbs on it.</b> Everything else built
    /// there — the ladder, the gantry, the grid — says "race track"; a lap time is the only thing that
    /// makes driving one mean anything, and it is the difference between a road the player passes
    /// through and one they go back to.</para>
    ///
    /// <para><b>It lives in the world scene, beside the geometry it is baked from</b>, exactly as
    /// <see cref="FillingStations"/> does — and for the same reason. What it needs to know is where a
    /// line is drawn on a road, which is a fact about the world and not about the HUD. The readout in
    /// Bootstrap finds it at run time; see <see cref="LapTimer"/>.</para>
    ///
    /// <para><b>A lap is a signed crossing, not a trigger volume.</b> The dot product of the car's
    /// offset against the line's own forward changes sign exactly once per crossing, in the right
    /// direction only, and costs nothing — where a collider would have to be authored, kept in the
    /// scene, and would fire on the way back through the pit exit as happily as on the way past the
    /// flag.</para>
    ///
    /// <para><b>A lap only counts if it was driven, and that is what the checkpoints are for.</b>
    /// Without them the fastest possible time is: cross the line, turn round, cross it again — four
    /// seconds, and it would sit at the top of the board forever. So the lap carries a handful of gates
    /// spaced round the circuit that have to be passed <b>in order</b> before the line will accept a
    /// time. In order, not merely all of them: any weaker rule can be satisfied by driving back and
    /// forth over one gate.</para>
    ///
    /// <para>Crossing the line always <i>restarts</i> the lap, whether or not it counted. That is the
    /// gentler of the two possible designs and the right one for this game — nothing is refused, the
    /// clock simply starts again, and an incomplete lap costs nothing but itself.</para>
    ///
    /// <para><b>Nothing here allocates.</b> The proximity test walks a fixed array four times a second
    /// and the crossing test is two dot products a frame; both write to fields rather than returning
    /// anything. That is the rule <c>CLAUDE.md</c> sets for driving code and this runs inside it.</para>
    /// </summary>
    public sealed class LapTiming : MonoBehaviour
    {
        [SerializeField] private string circuitName = string.Empty;

        [Tooltip("A point on the start/finish line, on the road's centreline.")]
        [SerializeField] private Vector3 linePoint;

        [Tooltip("Direction of travel across the line. Unit, and flat.")]
        [SerializeField] private Vector3 lineForward = Vector3.forward;

        [Tooltip("How far either side of the centreline still counts as crossing the line.")]
        [SerializeField] private float lineHalfWidth = 12f;

        [Tooltip("A sparse walk of the circuit, used only to tell whether the car is on it at all.")]
        [SerializeField] private Vector3[] track = new Vector3[0];

        [Tooltip("Gates that have to be passed in order for a lap to count. Points on the centreline.")]
        [SerializeField] private Vector3[] gatePoints = new Vector3[0];

        [Tooltip("Direction of travel through each gate. Unit, and flat.")]
        [SerializeField] private Vector3[] gateForwards = new Vector3[0];

        /// <summary>
        /// How near the circuit the car has to be for the clock to run, metres.
        ///
        /// <para>Generous. The samples are a coarse walk rather than the carriageway, so a corner cuts
        /// the chord by a few metres on its own, and a car that has run wide onto the gravel is still
        /// very much on a lap.</para>
        /// </summary>
        [SerializeField] private float reach = 70f;

        /// <summary>
        /// Shortest crossing-to-crossing time that counts as a lap, seconds.
        ///
        /// <para>Fifteen kilometres is minutes at any speed, so anything under half a minute is a car
        /// that has turned round on the straight or rolled back over the line — not a lap. Without this
        /// the best time converges on nought as soon as anybody parks on the line.</para>
        /// </summary>
        private const float ShortestLap = 30f;

        /// <summary>How often the "is the car on the circuit" walk runs, seconds.</summary>
        private const float PollSeconds = 0.25f;

        private VehicleController vehicle;
        private float previousSide;
        private bool hasPreviousSide;
        private bool onCircuit;
        private float pollTimer;
        private float previousGateSide;
        private bool hasPreviousGateSide;

        /// <summary>True while a lap is being timed — the car has crossed the line at least once.</summary>
        public bool Timing { get; private set; }

        /// <summary>True while the car is on the circuit at all. What the readout appears for.</summary>
        public bool OnCircuit => onCircuit;

        /// <summary>Time on the current lap, seconds.</summary>
        public float Current { get; private set; }

        /// <summary>The lap just finished, seconds. Zero until one has been.</summary>
        public float Last { get; private set; }

        /// <summary>Best of the session, seconds. Zero until a lap has been completed.</summary>
        public float Best { get; private set; }

        /// <summary>Completed laps this session.</summary>
        public int Laps { get; private set; }

        /// <summary>What the circuit is called, for the readout's heading.</summary>
        public string CircuitName => circuitName;

        /// <summary>How many gates a lap has to pass.</summary>
        public int GateCount => gatePoints != null ? gatePoints.Length : 0;

        /// <summary>How many of them this lap has passed, in order.</summary>
        public int GatesPassed { get; private set; }

        /// <summary>Baked by the rebuild alongside the line.</summary>
        public void SetGates(Vector3[] points, Vector3[] forwards)
        {
            gatePoints = points ?? new Vector3[0];
            gateForwards = forwards ?? new Vector3[0];

            if (gatePoints.Length != gateForwards.Length)
            {
                Debug.LogError(
                    $"[Horizon] The {circuitName} was given {gatePoints.Length} gate positions and "
                    + $"{gateForwards.Length} directions. They are one table in two arrays; a gate "
                    + "facing the way a different gate does is one that cannot be passed.");

                gatePoints = new Vector3[0];
                gateForwards = new Vector3[0];
                return;
            }

            // Flattened and normalised here, the way SetCircuit does it for the line — the tooltip
            // above has been claiming both for as long as this has existed and neither was true. A
            // course's tangent climbs, so what arrives is a unit vector with a y component of a few
            // hundredths, and the crossing test projects a flat offset onto it: the along-track
            // component comes out short by cos of the grade and the perpendicular one picks up a
            // vertical part that is not there. Small on this world's grades, and wrong at any of them.
            for (int i = 0; i < gateForwards.Length; i++)
            {
                Vector3 forward = gateForwards[i];
                forward.y = 0f;

                gateForwards[i] = forward.sqrMagnitude > 0.0001f
                    ? forward.normalized
                    : Vector3.forward;
            }
        }

        /// <summary>Baked by the rebuild. <paramref name="forward"/> need not be normalised.</summary>
        public void SetCircuit(
            string name, Vector3 point, Vector3 forward, float halfWidth, Vector3[] samples)
        {
            circuitName = name;
            linePoint = point;

            forward.y = 0f;
            lineForward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

            lineHalfWidth = halfWidth;
            track = samples ?? new Vector3[0];
        }

        /// <summary>How many samples the circuit was baked with. Read by the build's own report.</summary>
        public int SampleCount => track != null ? track.Length : 0;

        private void Update()
        {
            if (vehicle == null)
            {
                // Retried rather than resolved once: this component is in the world scene and so is the
                // car, but the car is instantiated after everything else in it.
                vehicle = FindFirstObjectByType<VehicleController>();
                if (vehicle == null)
                {
                    return;
                }
            }

            if (track == null || track.Length < 2)
            {
                return;
            }

            Vector3 at = vehicle.transform.position;

            pollTimer -= Time.deltaTime;
            if (pollTimer <= 0f)
            {
                pollTimer = PollSeconds;
                onCircuit = IsNearTrack(at);
            }

            if (!onCircuit)
            {
                // Left the circuit. The clock stops and the current lap is abandoned rather than paused:
                // a lap that includes a drive back down to the valley is not a lap time.
                Timing = false;
                Current = 0f;
                GatesPassed = 0;
                hasPreviousSide = false;
                hasPreviousGateSide = false;
                return;
            }

            UpdateGate(at);

            if (Timing)
            {
                Current += Time.deltaTime;
            }

            Vector3 offset = at - linePoint;
            offset.y = 0f;

            float side = Vector3.Dot(offset, lineForward);

            if (hasPreviousSide && previousSide < 0f && side >= 0f)
            {
                // Only where the car is actually on the road, not out on the far end of the plane the
                // line lies in — that plane is infinite and the circuit doubles back across it.
                Vector3 across = offset - lineForward * side;

                if (across.sqrMagnitude <= lineHalfWidth * lineHalfWidth)
                {
                    // A time is only taken when every gate has been passed, in order. Everything else
                    // about crossing the line happens either way — the clock always restarts, so an
                    // incomplete lap costs nothing but itself.
                    bool complete = GatesPassed >= GateCount;

                    if (Timing && complete && Current >= ShortestLap)
                    {
                        Last = Current;
                        Laps++;

                        if (Best <= 0f || Current < Best)
                        {
                            Best = Current;
                        }
                    }

                    Timing = true;
                    Current = 0f;
                    GatesPassed = 0;
                    hasPreviousGateSide = false;
                }
            }

            previousSide = side;
            hasPreviousSide = true;
        }

        /// <summary>
        /// Advances the gate counter if the car has just crossed the one it is waiting for.
        ///
        /// <para>Only ever the <i>next</i> gate is tested — two dot products a frame, and it is also
        /// what makes the order binding. A gate already passed does nothing if crossed again, and a gate
        /// further round does nothing until its turn.</para>
        /// </summary>
        private void UpdateGate(Vector3 at)
        {
            if (GatesPassed >= GateCount)
            {
                return;
            }

            Vector3 forward = gateForwards[GatesPassed];
            Vector3 offset = at - gatePoints[GatesPassed];
            offset.y = 0f;

            float side = Vector3.Dot(offset, forward);

            if (hasPreviousGateSide && previousGateSide < 0f && side >= 0f)
            {
                Vector3 across = offset - forward * side;

                if (across.sqrMagnitude <= lineHalfWidth * lineHalfWidth)
                {
                    GatesPassed++;
                    hasPreviousGateSide = false;
                    return;
                }
            }

            previousGateSide = side;
            hasPreviousGateSide = true;
        }

        /// <summary>Whether the car is within <see cref="reach"/> of any baked sample.</summary>
        private bool IsNearTrack(Vector3 at)
        {
            float limit = reach * reach;

            for (int i = 0; i < track.Length; i++)
            {
                float dx = track[i].x - at.x;
                float dz = track[i].z - at.z;

                if (dx * dx + dz * dz <= limit)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
