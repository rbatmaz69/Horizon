using System;
using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The town at the foot of the pass: how much of the valley floor belongs to it, what shape that
    /// floor is, and how densely it is built.
    ///
    /// It sits on the valley approach because that is one of only two stretches of the whole course under
    /// 3 % — the rest is 9.5 % legs, 20 m hairpins and a summit that drops 45 m within a hundred metres.
    /// The approach was extended by three quarters of a kilometre to make room for it; see
    /// <see cref="MountainPassCourse.ApproachLength"/>, which is where the extent below comes from rather
    /// than from literals, so reshaping the arrival road moves the town with it.
    ///
    /// <para>The basin is not decoration. <see cref="MountainField"/> lays a dead-flat shelf under every
    /// road and level sample it is given, and beyond that shelf the ground is Perlin noise with a median
    /// 22 % grade per cell — so a street on its own levels a 48 m ribbon and leaves a hillside either side
    /// of it. Saying plainly which *area* is meant to be buildable is the only thing that turns a row of
    /// houses into a place with depth.</para>
    /// </summary>
    [Serializable]
    public struct TownShape
    {
        [Tooltip("Where the town starts along the main course, metres.")]
        public float AlongStart;

        [Tooltip("Where it ends. Beyond this the road starts climbing in earnest.")]
        public float AlongEnd;

        [Tooltip("Which side of the main road the town spreads onto. -1 is left. The valley floor is flat "
               + "for 300 m on the left and climbs 25 m within 100 m on the right, so this is not free "
               + "choice — it is the only side there is room on.")]
        public float TownSide;

        [Tooltip("How far the basin reaches to the *uphill* side of the trunk road, metres. Negative, in "
               + "the same across-axis as everything else: enough for one row of frontage and no more.")]
        public float AcrossInner;

        [Tooltip("How far the basin reaches onto the valley floor, metres.")]
        public float AcrossOuter;

        [Tooltip("Grid pitch of the level samples, metres, in both axes. Must stay under twice "
               + "MountainField.Verge or the individual shelves do not merge and the floor comes out "
               + "corrugated.")]
        public float SamplePitch;

        [Tooltip("Cross-fall within CrossFallBreak of the trunk road, as a fraction: 0.012 is 1.2 %.")]
        public float CrossFallNear;

        [Tooltip("Cross-fall beyond it. Steeper, because that is where the valley starts turning into "
               + "hillside and a floor that stayed level would read as a table.")]
        public float CrossFallFar;

        [Tooltip("Where the cross-fall steepens, metres from the trunk road.")]
        public float CrossFallBreak;

        [Tooltip("Amplitude of the long dish, metres. A very low-frequency roll across the whole basin so "
               + "it is not a ruled surface. Small enough that the car never notices it.")]
        public float DishAmplitude;

        [Tooltip("Wavelength of the dish, metres.")]
        public float DishWavelength;

        [Tooltip("How far the first skirt ring stands above the basin floor, metres.")]
        public float SkirtFirstRise;

        [Tooltip("And the second. Together they step the shelf up into the hillside instead of leaving "
               + "TerrainShape.BlendDistance to do it all at once — a rim is what makes a mesa, a skirt "
               + "reads as fields rising around the town.")]
        public float SkirtSecondRise;

        [Tooltip("How far past the built area the local terrain corridor reaches, metres.")]
        public float CorridorMargin;

        [Tooltip("Warn if a single tile exceeds this many triangles. A warning, not a limit — a town core "
               + "tile is genuinely heavier than open hillside and is meant to be.")]
        public int MaxTrianglesPerTile;

        public static TownShape Default => new TownShape
        {
            AlongStart = MountainPassCourse.TownStartDistance,
            AlongEnd = MountainPassCourse.TownEndDistance,

            TownSide = -1f,
            AcrossInner = -90f,
            AcrossOuter = 260f,

            // 28 m against a 24 m verge: the shelves overlap, and the furthest any point in the basin
            // can be from a sample is 28/2 x sqrt(2) = 19.8 m, inside the verge, so the whole floor is
            // shelf rather than blend. That margin is the difference between a floor and a corrugation.
            SamplePitch = 28f,

            CrossFallNear = 0.012f,
            CrossFallFar = 0.025f,
            CrossFallBreak = 150f,

            DishAmplitude = 0.8f,
            DishWavelength = 300f,

            SkirtFirstRise = 3f,
            SkirtSecondRise = 8f,

            CorridorMargin = 60f,
            MaxTrianglesPerTile = 30000,
        };

        /// <summary>Half the basin's span across, for anything that needs a radius rather than an extent.</summary>
        public float AcrossSpan => AcrossOuter - AcrossInner;

        /// <summary>
        /// The one function that decides how high the ground under the town is.
        ///
        /// <b>Everything's Y comes from here</b> — street ribbons, level samples, junction pads, parcel
        /// seating. That is what guarantees the streets and the ground agree, and it is a single function
        /// rather than a convention precisely because the failure mode when the two drift apart is
        /// documented and unpleasant: a lane standing on a plinth with daylight under its edge, because
        /// the ground was levelled from the trunk road's height and the lane ran its own grade.
        ///
        /// <paramref name="along"/> is distance along the trunk road; <paramref name="across"/> is metres
        /// out from it, positive towards <see cref="TownSide"/>.
        /// </summary>
        public static float FloorHeight(IRoadPath main, in TownShape shape, float along, float across)
        {
            if (main == null)
            {
                return 0f;
            }

            float clamped = Mathf.Clamp(along, 0f, main.Length);
            float roadHeight = main.GetPositionAtDistance(clamped).y;

            return roadHeight + CrossFall(shape, across) + Dish(shape, along, across);
        }

        /// <summary>
        /// How much the floor rises away from the trunk road. About 4.5 m out at the far edge of the
        /// basin: visible from the pass above, far under any buildability limit, and enough that the town
        /// sits in a shallow bowl rather than on a plate.
        /// </summary>
        public static float CrossFall(in TownShape shape, float across)
        {
            float distance = Mathf.Abs(across);
            float near = Mathf.Min(distance, shape.CrossFallBreak) * shape.CrossFallNear;
            float far = Mathf.Max(0f, distance - shape.CrossFallBreak) * shape.CrossFallFar;
            return near + far;
        }

        /// <summary>
        /// A long, slow roll in both axes. Deterministic and completely smooth — this is not noise, and
        /// it must not be: noise at this amplitude would be felt through the wheels, while a 300 m
        /// wavelength at 0.8 m is a grade of half a percent that only the eye picks up.
        ///
        /// <para>Faded out over the first 80 m either side of the trunk road, and that is not cosmetic.
        /// The trunk road is the one thing in the basin whose height this function does <b>not</b> get to
        /// decide — it is where the height comes from. Rolling the floor up and down beside it would put
        /// the level samples up to 0.8 m out of agreement with the carriageway they were derived from,
        /// which is the plinth the shared floor function exists to prevent, in miniature and along the one
        /// stretch of ground the player is actually driving on.</para>
        /// </summary>
        public static float Dish(in TownShape shape, float along, float across)
        {
            float k = Mathf.PI * 2f / Mathf.Max(1f, shape.DishWavelength);
            float roll = Mathf.Sin(along * k) * 0.6f + Mathf.Sin(across * k * 0.8f + 1.7f) * 0.4f;

            return shape.DishAmplitude * roll * AwayFromTheRoad(across, 0f, 80f);
        }

        /// <summary>
        /// A 0-to-1 ramp on distance from the trunk road, for anything that must not touch it.
        ///
        /// The basin straddles the carriageway, so every ring, roll and terrace drawn across it crosses the
        /// road somewhere. Whatever is being raised has to be let down to nothing before it gets there, or
        /// it is a wall across the road rather than a shape in the landscape — the skirt ring's first
        /// version put 8 m of hillside squarely on the arrival road.
        /// </summary>
        public static float AwayFromTheRoad(float across, float from, float to)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, Mathf.Abs(across)));
        }

        /// <summary>World position of a point in town-local coordinates, floor height included.</summary>
        public static Vector3 ToWorld(IRoadPath main, in TownShape shape, float along, float across)
        {
            if (main == null)
            {
                return Vector3.zero;
            }

            float clamped = Mathf.Clamp(along, 0f, main.Length);
            Vector3 centre = main.GetPositionAtDistance(clamped);
            Vector3 right = main.GetRightAtDistance(clamped);

            float side = Mathf.Sign(shape.TownSide == 0f ? -1f : shape.TownSide);
            Vector3 point = centre + right * (across * side);

            return new Vector3(point.x, FloorHeight(main, shape, along, across), point.z);
        }

        /// <summary>
        /// The points the ground under the town has to be levelled to: a grid over the whole basin, plus
        /// two rings outside it that step up into the hillside.
        ///
        /// The rings are what stop the basin reading as a mesa. A dead-flat table blended into Perlin
        /// ridges over <see cref="TerrainShape.BlendDistance"/> has an edge, and an edge at this scale is
        /// a cliff seen from the pass above. Two rings at +3 m and +8 m turn that edge into two shallow
        /// terraces, which read as fields rising around the town.
        /// </summary>
        public static List<Vector3> BuildLevelSamples(IRoadPath main, in TownShape shape)
        {
            var samples = new List<Vector3>(512);
            if (main == null)
            {
                return samples;
            }

            float pitch = Mathf.Max(4f, shape.SamplePitch);

            for (float along = shape.AlongStart - 40f; along <= shape.AlongEnd + 40f; along += pitch)
            {
                for (float across = shape.AcrossInner; across <= shape.AcrossOuter; across += pitch)
                {
                    samples.Add(ToWorld(main, shape, along, across));
                }

                // The outer edge exactly, whatever the pitch left over. The basin's own boundary is where
                // the skirt has to meet it, and a gap of up to a pitch there is a step.
                samples.Add(ToWorld(main, shape, along, shape.AcrossOuter));
            }

            AddSkirtRing(main, shape, pitch, shape.SkirtFirstRise, samples);
            AddSkirtRing(main, shape, pitch * 2f, shape.SkirtSecondRise, samples);

            return samples;
        }

        /// <summary>One rectangular ring of level samples, <paramref name="expand"/> metres outside the basin.</summary>
        private static void AddSkirtRing(
            IRoadPath main,
            in TownShape shape,
            float expand,
            float rise,
            List<Vector3> samples)
        {
            float pitch = Mathf.Max(4f, shape.SamplePitch);

            float alongMin = shape.AlongStart - 40f - expand;
            float alongMax = shape.AlongEnd + 40f + expand;
            float acrossMin = shape.AcrossInner - expand;
            float acrossMax = shape.AcrossOuter + expand;

            for (float along = alongMin; along <= alongMax; along += pitch)
            {
                samples.Add(Raised(main, shape, along, acrossMin, rise));
                samples.Add(Raised(main, shape, along, acrossMax, rise));
            }

            for (float across = acrossMin; across <= acrossMax; across += pitch)
            {
                samples.Add(Raised(main, shape, alongMin, across, rise));
                samples.Add(Raised(main, shape, alongMax, across, rise));
            }
        }

        /// <summary>
        /// A skirt sample: the floor, lifted — except where the road runs through the ring, which is at
        /// both ends of the town and is exactly where the player drives in.
        /// </summary>
        private static Vector3 Raised(
            IRoadPath main, in TownShape shape, float along, float across, float rise)
        {
            Vector3 point = ToWorld(main, shape, along, across);
            point.y += rise * AwayFromTheRoad(across, 35f, 135f);
            return point;
        }

        /// <summary>
        /// Plan bounds of a set of level samples, inflated by a margin. Handed to
        /// <see cref="TerrainTileBuilder.ListTiles"/> so the terrain reaches the whole basin without the
        /// corridor being widened along the other five kilometres of pass.
        /// </summary>
        public static Bounds Footprint(IReadOnlyList<Vector3> samples, float margin)
        {
            if (samples == null || samples.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            var bounds = new Bounds(samples[0], Vector3.zero);
            for (int i = 1; i < samples.Count; i++)
            {
                bounds.Encapsulate(samples[i]);
            }

            bounds.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
            return bounds;
        }
    }
}
