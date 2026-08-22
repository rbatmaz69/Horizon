using System;
using System.Collections.Generic;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Fills the tank when the car is stood at a pump, and says where the nearest one is when it is
    /// too late.
    ///
    /// <para><b>In Horizon.Game because it is the join</b>, in the same bind and for the same reason as
    /// <c>WaterHazard</c>: it has to know where the world put the pumps and it has to reach into the
    /// vehicle, and <c>Horizon.Vehicle</c> is not allowed to know the world exists. This is the leaf
    /// assembly, so it is the one place both are in scope.</para>
    ///
    /// <para><b>No trigger volumes, and none wanted.</b> There is not one <c>OnTriggerEnter</c> anywhere
    /// in this project, which is a position rather than an oversight — see <c>VehicleCover</c>, which
    /// argues it for the roof over a car. A volume would have to be authored beside every forecourt and
    /// kept in step with it, and it would only ever answer what a distance test answers directly. Six
    /// squared distances per physics step is not a cost worth building machinery to avoid.</para>
    ///
    /// <para>The stations are baked in by the setup tool rather than derived at run time, exactly as the
    /// water is: the courses that know where they go are build-time objects and do not exist in a
    /// player build at all.</para>
    /// </summary>
    public sealed class FillingStations : MonoBehaviour
    {
        /// <summary>One forecourt as the runtime needs it: a place, a reach and a name to say.</summary>
        [Serializable]
        public sealed class Station
        {
            public string Name;

            /// <summary>
            /// Centre of the pump islands — not of the forecourt.
            ///
            /// <para>You have to park at a pump, which is the whole of what makes a station somewhere you
            /// arrive at rather than a stretch of road that happens to refill you.</para>
            /// </summary>
            public Vector3 Pumps;

            public float Radius;

            /// <summary>
            /// Centre of the forecourt slab — not of the pumps — and the frame it lies in.
            ///
            /// <para><b>A rectangle, and it has to be.</b> "Is the car on a forecourt" is a much larger
            /// question than "is it at a pump", and the obvious answer — a bigger radius — is wrong in a
            /// way that would have been embarrassing: the slab is 52 m by 34, so a circle holding it is
            /// 31 m across, and 31 m from the pumps <i>along the road</i> is the carriageway. Every car
            /// that drove past a station at speed would have been told to pull up to a pump.</para>
            ///
            /// <para>Two dot products in plan against the slab's own axes answers it exactly, for eight
            /// multiplies a station a step. The class already argues that a direct test beats an
            /// authored volume; this is that argument one step further.</para>
            /// </summary>
            public Vector3 Centre;

            public Vector3 Forward;
            public Vector3 Outward;
            public float HalfLength;
            public float HalfDepth;
        }

        [SerializeField]
        private List<Station> stations = new List<Station>();

        [Tooltip("Fast enough to be rolling to a stop, slow enough that nobody fuels in passing.")]
        [SerializeField] private float parkSpeedKmh = 4f;

        [Tooltip("How long the car must be standing before the nozzle goes in. Short — it is the "
               + "difference between arriving and having arrived, not a queue.")]
        [SerializeField] private float settleSeconds = 0.8f;

        [Tooltip("Litres a second. Ten fills the biggest tank in the fleet from empty in eight seconds "
               + "and a reserve in two, which is long enough to watch the needle climb and short enough "
               + "that nobody goes looking for a way to skip it.")]
        [SerializeField] private float litresPerSecond = 10f;

        private VehicleController vehicle;
        private FuelTank tank;
        private float standing;

        /// <summary>Hands the component its pumps. Called by the setup tool as it bakes the scene.</summary>
        public void SetStations(IEnumerable<Station> pumps)
        {
            stations.Clear();

            if (pumps != null)
            {
                stations.AddRange(pumps);
            }
        }

        public int StationCount => stations.Count;

        /// <summary>True while fuel is actually going in. The gauge's needle is the other half of this.</summary>
        public bool IsFilling { get; private set; }

        /// <summary>Within reach of a station's pumps, at whatever speed.</summary>
        public bool IsAtPump { get; private set; }

        /// <summary>At a pump and slow enough to be arriving rather than passing.</summary>
        public bool IsStopped { get; private set; }

        /// <summary>Anywhere on a forecourt slab. See <see cref="Station.Centre"/> for why it is not a radius.</summary>
        public bool IsOnForecourt { get; private set; }

        /// <summary>
        /// The nearest station's name, and whether it is ahead of the car or behind it.
        ///
        /// <para>A bearing and a range, which is what a signpost gives you — not a route. Working out how
        /// far it is <i>along the road</i> would need the course tables, and those are build-time objects
        /// that a player build does not contain.</para>
        /// </summary>
        public bool TryNearest(Vector3 from, Vector3 facing, out string name, out float metres, out bool ahead)
        {
            name = null;
            metres = 0f;
            ahead = true;

            int best = -1;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < stations.Count; i++)
            {
                float sqr = (stations[i].Pumps - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i;
                }
            }

            if (best < 0)
            {
                return false;
            }

            Vector3 to = stations[best].Pumps - from;

            name = stations[best].Name;
            metres = Mathf.Sqrt(bestSqr);
            ahead = Vector3.Dot(to, facing) >= 0f;
            return true;
        }

        private void FixedUpdate()
        {
            if (stations.Count == 0)
            {
                return;
            }

            if (tank == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
                tank = vehicle != null ? vehicle.GetComponent<FuelTank>() : null;

                if (tank == null)
                {
                    Clear();
                    return;
                }
            }

            Vector3 at = vehicle.transform.position;

            // All three written every step and cleared on every early return, so none of them can be
            // left standing from a frame in which they were true.
            IsAtPump = WithinPumps(at);
            IsOnForecourt = IsAtPump || OnForecourt(at);
            IsStopped = IsAtPump && vehicle.SpeedKmh <= parkSpeedKmh;

            standing = IsStopped ? standing + Time.fixedDeltaTime : 0f;

            if (standing < settleSeconds || tank.Fraction01 >= 1f)
            {
                IsFilling = false;
                return;
            }

            IsFilling = true;
            tank.Fill(litresPerSecond * Time.fixedDeltaTime);
        }

        private void Clear()
        {
            IsFilling = false;
            IsAtPump = false;
            IsStopped = false;
            IsOnForecourt = false;
            standing = 0f;
        }

        /// <summary>Whether the car is standing on any station's slab.</summary>
        private bool OnForecourt(Vector3 at)
        {
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];

                if (station.HalfLength <= 0f)
                {
                    continue;
                }

                float dx = at.x - station.Centre.x;
                float dz = at.z - station.Centre.z;

                float along = dx * station.Forward.x + dz * station.Forward.z;
                float across = dx * station.Outward.x + dz * station.Outward.z;

                if (Mathf.Abs(along) <= station.HalfLength && Mathf.Abs(across) <= station.HalfDepth)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the car is standing within reach of any station's pumps.
        ///
        /// <para>Plan distance, ignoring height, so a forecourt is not missed because the slab sits a
        /// third of a metre above the road it was measured from.</para>
        /// </summary>
        private bool WithinPumps(Vector3 at)
        {
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];

                float dx = station.Pumps.x - at.x;
                float dz = station.Pumps.z - at.z;

                if (dx * dx + dz * dz <= station.Radius * station.Radius)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
