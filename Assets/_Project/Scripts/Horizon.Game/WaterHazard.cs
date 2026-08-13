using System;
using System.Collections.Generic;
using Horizon.Vehicle;
using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Makes water something that happens to the car rather than something it drives across.
    ///
    /// <para><b>In Horizon.Game because it is the join.</b> It has to know where the world's water is
    /// and it has to reach into the vehicle, and <c>Horizon.Vehicle</c> is not allowed to know the world
    /// exists. This is the leaf assembly, so it is the one place both are in scope.</para>
    ///
    /// <para><b>No colliders, and none wanted.</b> The water surfaces are drawn without one on purpose —
    /// a mesh collider on every lake would be a sheet of glass to drive over. What is under the car is
    /// the terrain of the basin, which it already collides with; this is a test against baked geometry,
    /// costing a rectangle rejection per body per physics step.</para>
    ///
    /// <para>The bodies are baked in by the setup tool rather than read from a <c>MountainField</c>: the
    /// field is a build-time object that costs a second to construct and does not exist in a player
    /// build at all.</para>
    /// </summary>
    public sealed class WaterHazard : MonoBehaviour
    {
        /// <summary>
        /// One body of water as the runtime needs it: a centreline, a width and a surface height.
        ///
        /// <para>A copy of what <c>WaterBody</c> holds rather than the thing itself, because that is a
        /// readonly struct and Unity serialises neither readonly fields nor structs it does not own.
        /// The shape of the copy is the same on purpose — the two must agree about what is wet.</para>
        /// </summary>
        [Serializable]
        public sealed class Patch
        {
            public string Name;

            /// <summary>Lake centre, or river centreline, in plan.</summary>
            public Vector2[] Spine;

            /// <summary>Lake radius, or half the river channel.</summary>
            public float HalfWidth;

            public float SurfaceY;
        }

        [SerializeField]
        private List<Patch> patches = new List<Patch>();

        /// <summary>
        /// What is left of the tyres' grip while the car is in the water.
        ///
        /// <para>Not zero. At zero the car keeps every bit of the speed it arrived with and glides
        /// across the lake like a puck, because nothing is left to slow it down — the friction circle is
        /// what rolling resistance and braking are spent out of. A sixth of the grip ploughs.</para>
        /// </summary>
        [SerializeField]
        private float wetGrip = 0.15f;

        /// <summary>Damping added to the body while it is under, which is the water pushing back.</summary>
        [SerializeField]
        private float wetDamping = 2.5f;

        /// <summary>
        /// How long the car may be under before it is put back on the road.
        ///
        /// <para>Long enough to be a moment rather than a punishment — the whole point of driving into a
        /// lake is to see what happens — and short enough that the player is not left steering a brick
        /// on the bottom of it.</para>
        /// </summary>
        [SerializeField]
        private float patienceSeconds = 1.6f;

        private VehicleController vehicle;
        private Rigidbody body;
        private TrafficNetwork routes;

        private float dryDamping;
        private float dryAngularDamping;
        private bool wet;
        private float underFor;

        /// <summary>Hands the hazard its water. Called by the setup tool as it bakes the scene.</summary>
        public void SetPatches(IEnumerable<Patch> bodies)
        {
            patches.Clear();

            if (bodies != null)
            {
                patches.AddRange(bodies);
            }
        }

        public int PatchCount => patches.Count;

        private void FixedUpdate()
        {
            if (patches.Count == 0)
            {
                return;
            }

            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
                body = vehicle != null ? vehicle.GetComponent<Rigidbody>() : null;

                if (body == null)
                {
                    return;
                }

                dryDamping = body.linearDamping;
                dryAngularDamping = body.angularDamping;
            }

            Vector3 at = vehicle.transform.position;
            bool under = IsUnder(at);

            if (under && !wet)
            {
                // Read on the way in rather than cached at startup: the config can be edited in play,
                // and putting back a value from before the edit would quietly undo it.
                dryDamping = body.linearDamping;
                dryAngularDamping = body.angularDamping;
            }

            if (under)
            {
                wet = true;
                underFor += Time.fixedDeltaTime;

                vehicle.GripScale = wetGrip;
                body.linearDamping = wetDamping;
                body.angularDamping = Mathf.Max(dryAngularDamping, wetDamping);

                if (underFor >= patienceSeconds)
                {
                    Recover();
                }

                return;
            }

            if (wet)
            {
                Dry();
            }
        }

        /// <summary>Whether the car's own position is inside a body and below its surface.</summary>
        private bool IsUnder(Vector3 at)
        {
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];

                if (patch.Spine == null || patch.Spine.Length == 0 || at.y >= patch.SurfaceY)
                {
                    continue;
                }

                if (DistanceToSpine(patch, at) <= patch.HalfWidth)
                {
                    return true;
                }
            }

            return false;
        }

        private static float DistanceToSpine(Patch patch, Vector3 at)
        {
            var point = new Vector2(at.x, at.z);

            if (patch.Spine.Length == 1)
            {
                return Vector2.Distance(point, patch.Spine[0]);
            }

            float best = float.MaxValue;

            for (int i = 1; i < patch.Spine.Length; i++)
            {
                Vector2 a = patch.Spine[i - 1];
                Vector2 b = patch.Spine[i];

                Vector2 along = b - a;
                float lengthSqr = along.sqrMagnitude;

                float distance = lengthSqr < 0.0001f
                    ? Vector2.Distance(point, a)
                    : Vector2.Distance(point, a + along * Mathf.Clamp01(Vector2.Dot(point - a, along) / lengthSqr));

                best = Mathf.Min(best, distance);
            }

            return best;
        }

        /// <summary>Back onto the nearest road, facing the way it runs.</summary>
        private void Recover()
        {
            if (routes == null)
            {
                TrafficDirector director = FindFirstObjectByType<TrafficDirector>();
                routes = director != null ? director.Network : null;
            }

            // Dried off first, and in this order. Teleport leaves the car standing on tarmac, and a car
            // put down with a sixth of its grip and the damping of a lake would spend the next second
            // wallowing on dry land.
            Dry();

            if (RoadRespawn.TryNearest(routes, vehicle.transform.position,
                    RoadRespawn.RideHeight(vehicle), out Vector3 position, out Quaternion rotation))
            {
                vehicle.Teleport(position, rotation);
            }
        }

        private void Dry()
        {
            wet = false;
            underFor = 0f;

            if (vehicle != null)
            {
                vehicle.GripScale = 1f;
            }

            if (body != null)
            {
                body.linearDamping = dryDamping;
                body.angularDamping = dryAngularDamping;
            }
        }
    }
}
