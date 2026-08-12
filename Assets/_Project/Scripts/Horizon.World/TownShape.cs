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

        [Tooltip("How far the basin reaches onto the valley floor, metres. A floor rather than a limit: "
               + "CoverLayout widens it until it covers every street the layout table asks for, so this "
               + "is how much flat ground the town gets *at least*.")]
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

        // --- The plots.
        //
        // What is left of a much longer list. The two hand-placed lanes and their junction throats —
        // FirstLaneAt, SecondLaneAt, LaneLength, LaneHalfWidth, JunctionGap, JunctionThroat — died with
        // the street network that replaced them, and PlotSpacing, PlotHalfWidth, PlotDepth and
        // PlotVacancy died with the per-quarter table in TownPlanner that decides those per street now.
        // Only PlotSetback survives, because it is the one number the parcelling still reads.

        [Tooltip("How far a house stands back from the centreline of its street, metres.")]
        public float PlotSetback;

        [Tooltip("Chance a plot has a car parked on it.")]
        public float ParkedCarChance;

        [Tooltip("Spacing of street lamps along the main road through the town, metres.")]
        public float LampSpacing;

        [Tooltip("Spacing of street lamps along a street in the old town or the market quarter, metres. "
               + "Lamps alternate sides, so a lamp every 30 m is one per side every 60 m.")]
        public float LampSpacingCore;

        [Tooltip("And everywhere else. The step between the two is one of the few things that says "
               + "'you have left the middle of town' without anything having to announce it.")]
        public float LampSpacingOuter;

        [Tooltip("Chance a plot carries a barn or a sawmill rather than a house.")]
        public float WorkingBuildingChance;

        [Tooltip("Margin around a *building* within which nothing wild grows at all, metres. Deliberately "
               + "small: it guards the walls, not the garden.")]
        public float PlotClearance;

        [Tooltip("Margin around a whole *plot* within which no tree or boulder is scattered, metres. Grass "
               + "and bushes carry on right up to the houses — a town with bare ground between its houses "
               + "looks abandoned.")]
        public float TreeKeepOut;

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

            // Measured from the *kerb* rather than from the centreline: TownPlanner adds the street's
            // own outer half-width, so a house on an alley and a house on the high street stand the same
            // distance back from the pavement. Only the quarter table's own setback is used for the
            // frontage itself; this is the floor the street-clearing pass measures against.
            PlotSetback = 14f,
            ParkedCarChance = 0.35f,

            LampSpacing = 42f,
            LampSpacingCore = 30f,
            LampSpacingOuter = 45f,
            WorkingBuildingChance = 0.16f,

            // 2.5 m around a wall, not 15 m around a whole plot. The old pair sterilised the place.
            PlotClearance = 2.5f,
            TreeKeepOut = 4f,

            TrunkHalfWidth = RoadShape.Default.OuterHalfWidth,
            TrunkLamps = true,
            Landmarks = true,
        };

        /// <summary>
        /// Half-width of the trunk road this town hangs on, shoulders included.
        ///
        /// <para>Was <c>RoadShape.Default.OuterHalfWidth</c> read straight out of the two places that
        /// needed it, which is correct for exactly one road. A town on a motorway measures its plot
        /// clearance and its lamp line against a carriageway twice as wide, and reading the pass's figure
        /// there puts buildings on the hard shoulder.</para>
        /// </summary>
        public float TrunkHalfWidth;

        /// <summary>
        /// Whether to line the trunk road through the town with street lamps.
        ///
        /// <para>True for a village the road runs through, false for a town hung off a motorway — the
        /// lamp loop is unconditional and would otherwise stand a row of village lanterns down the hard
        /// shoulder of a dual carriageway.</para>
        /// </summary>
        public bool TrunkLamps;

        /// <summary>
        /// Whether this town gets Talheim's set-piece buildings: the mosque on the high ground and the
        /// windmill on the first industrial frontage.
        ///
        /// <para>They are placed by rules written for a village — <c>AddMosque</c> searches a one-sided
        /// band of the basin for the highest ground — and a city wants its own landmarks rather than
        /// these ones dropped into it.</para>
        /// </summary>
        public bool Landmarks;

        /// <summary>
        /// Hochstadt: a city on both sides of a straight arterial.
        ///
        /// <para><b>Both sides, and that is the interesting difference.</b> Talheim sits entirely on one
        /// side of the pass because a village on a mountain road does, and because <c>TownSide</c> made
        /// that cheap. A city has the road through the middle of it — so the across range runs negative
        /// to positive, and <c>TownSide</c> is +1 only because the sign has to be something.</para>
        ///
        /// <para>Everything here that would fold on a bend is safe because
        /// <see cref="HochstadtCourse"/> is dead straight: <c>LimitAcross</c> caps a town at
        /// <c>0.65·R</c>, and on a straight <c>R</c> is infinite. That one property is what makes a
        /// kilometre-wide town possible at all, and it is why the city has its own arterial instead of
        /// hanging off the motorway.</para>
        /// </summary>
        public static TownShape Hochstadt => new TownShape
        {
            AlongStart = HochstadtCourse.CityStart,
            AlongEnd = HochstadtCourse.CityEnd,

            TownSide = 1f,
            AcrossInner = -HochstadtCourse.HalfWidth,
            AcrossOuter = HochstadtCourse.HalfWidth,

            SamplePitch = 28f,

            // Flatter than the village, and much flatter far out. A city floor that rose 4.5 m at its
            // edge the way Talheim's does would climb 13 m over a kilometre, and a grid of straight
            // streets on that reads as a fault line rather than as a hill.
            CrossFallNear = 0.006f,
            CrossFallFar = 0.004f,
            CrossFallBreak = 200f,

            // Half the village's dish over twice the wavelength: enough that the ground is not a table,
            // little enough that a straight boulevard stays straight.
            DishAmplitude = 0.4f,
            DishWavelength = 600f,

            SkirtFirstRise = 3f,
            SkirtSecondRise = 9f,

            CorridorMargin = 60f,
            MaxTrianglesPerTile = 30000,

            PlotSetback = 16f,
            ParkedCarChance = 0.35f,

            LampSpacing = 42f,
            LampSpacingCore = 30f,
            LampSpacingOuter = 45f,

            // No barns and no sawmills in a city centre.
            WorkingBuildingChance = 0f,

            PlotClearance = 2.5f,
            TreeKeepOut = 4f,

            // The arterial is a motorway-width boulevard, not a country road.
            TrunkHalfWidth = RoadShape.Autobahn.OuterHalfWidth,

            // Its own street lamps come from the street network; the arterial does not want a row of
            // village lanterns down it, and the city has no set-piece mosque or windmill.
            TrunkLamps = false,
            Landmarks = false,
        };

        /// <summary>Which way the town lies from the trunk road, as a clean ±1.</summary>
        public float Side => Mathf.Sign(TownSide == 0f ? -1f : TownSide);

        /// <summary>
        /// Widens the basin until it covers every piece of paving the layout table asks for.
        ///
        /// <para><b>Only the across-axis.</b> <see cref="AlongStart"/> and <see cref="AlongEnd"/> are not
        /// preferences — they come from <see cref="MountainPassCourse"/>, and the far one is the foot of
        /// the pass's first bend, past which town-local coordinates fold and the mapping stops meaning
        /// anything. Extending the basin over that bend would not level the ground there, it would pile
        /// several samples carrying different heights onto the same spot. So a street that reaches past it
        /// is reported and left alone: the answer is to move the street or to reshape the course, and only
        /// a person can pick. Across, there is nothing but valley floor and the basin may simply have
        /// more of it.</para>
        ///
        /// <para>The pads at the ends of the town need no allowance here — <see cref="BuildLevelSamples"/>
        /// already runs its grid 40 m past both ends, which is more than any junction reaches.</para>
        /// </summary>
        public static TownShape CoverLayout(TownShape shape, TownNetworkSpec spec, float shelfDrop)
        {
            if (spec == null)
            {
                return shape;
            }

            TownLayoutExtent extent = spec.MeasureExtent(shelfDrop);

            float inner = Mathf.Min(shape.AcrossInner, extent.AcrossMin);
            float outer = Mathf.Max(shape.AcrossOuter, extent.AcrossMax);

            if (inner < shape.AcrossInner || outer > shape.AcrossOuter)
            {
                Debug.Log($"[Horizon] Town basin: widened from {shape.AcrossInner:0} .. "
                          + $"{shape.AcrossOuter:0} m across to {inner:0} .. {outer:0} m, to cover the "
                          + "paving the layout table asks for.");
            }
            else
            {
                Debug.Log($"[Horizon] Town basin: {shape.AcrossInner:0} .. {shape.AcrossOuter:0} m across "
                          + $"covers a layout reaching {extent.AcrossMin:0} .. {extent.AcrossMax:0} m.");
            }

            shape.AcrossInner = inner;
            shape.AcrossOuter = outer;

            if (extent.CentreAlongMin < shape.AlongStart || extent.CentreAlongMax > shape.AlongEnd)
            {
                Debug.LogWarning(
                    $"[Horizon] Town basin: the layout's streets run from {extent.CentreAlongMin:0} to "
                    + $"{extent.CentreAlongMax:0} m along the trunk road, outside the town's extent of "
                    + $"{shape.AlongStart:0} .. {shape.AlongEnd:0} m. That end of the town is where the "
                    + "pass's first bend begins and town-local coordinates fold there, so the ground "
                    + "under those streets cannot be levelled. Move them back inside, or open the bend "
                    + "out in MountainPassCourse.");
            }

            return shape;
        }

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

        /// <summary>
        /// The least the along-axis may be squeezed before town-local coordinates stop meaning anything.
        ///
        /// A point <c>d</c> metres out on the inside of a bend of radius <c>r</c> has its along-axis
        /// compressed by <c>1 - d/r</c>. At zero the coordinate system has collapsed to a point; past it
        /// it is inside out. The course is shaped to keep the town well clear of that — see
        /// <see cref="MountainPassCourse"/> — and <c>ValidateTownMapping</c> says so in numbers, but
        /// <see cref="ToWorld"/> refuses to fold whatever it is handed, because the basin's own margins
        /// and skirt rings reach past the town's extent and would otherwise turn themselves inside out
        /// beyond the last street.
        /// </summary>
        public const float MinimumMappingScale = 0.35f;

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
            Vector3 point = centre + right * (LimitAcross(main, clamped, across, side) * side);

            return new Vector3(point.x, FloorHeight(main, shape, along, across), point.z);
        }

        /// <summary>
        /// Caps how far out a point may be placed, so the mapping never turns inside out.
        ///
        /// Where the trunk road bends away from the town the limit is infinite and this does nothing;
        /// where it bends towards it, points beyond the limit pile up on it rather than crossing through
        /// the centre of the arc. Piled-up level samples are harmless — they are a height field, and
        /// several samples in one place say the same thing once. A folded one is a spike of terrain.
        /// </summary>
        private static float LimitAcross(IRoadPath main, float along, float across, float side)
        {
            float curvature = main.GetSignedCurvatureAtDistance(along, 20f);
            float bendsTowardsTown = across * side * curvature;

            if (bendsTowardsTown <= 1f - MinimumMappingScale)
            {
                return across;
            }

            float limit = (1f - MinimumMappingScale) / (side * curvature);
            return across > 0f ? Mathf.Min(across, limit) : Mathf.Max(across, limit);
        }

        /// <summary>
        /// Whether a point is out past where town-local coordinates still mean what they say.
        ///
        /// <para>The basin's margins and its two skirt rings reach well past the last street, and past the
        /// end of the town the trunk road bends into the pass. <see cref="ToWorld"/> caps those points
        /// rather than folding them, but a capped point is a lie about where it is: several of them land
        /// on the same spot carrying different floor heights, the shelf averages the lot, and the result
        /// is a couple of metres of error that bleeds eighty metres back into ground that was fine.</para>
        ///
        /// <para>So the basin simply stops there. A valley that narrows into a pass is not somewhere a
        /// town extends to anyway.</para>
        /// </summary>
        public static bool IsBeyondFold(IRoadPath main, in TownShape shape, float along, float across)
        {
            if (main == null)
            {
                return false;
            }

            float clamped = Mathf.Clamp(along, 0f, main.Length);
            float side = Mathf.Sign(shape.TownSide == 0f ? -1f : shape.TownSide);

            return !Mathf.Approximately(LimitAcross(main, clamped, across, side), across);
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
                    AddSample(main, shape, along, across, 0f, samples);
                }

                // The outer edge exactly, whatever the pitch left over. The basin's own boundary is where
                // the skirt has to meet it, and a gap of up to a pitch there is a step.
                AddSample(main, shape, along, shape.AcrossOuter, 0f, samples);
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
                AddSample(main, shape, along, acrossMin, rise, samples);
                AddSample(main, shape, along, acrossMax, rise, samples);
            }

            for (float across = acrossMin; across <= acrossMax; across += pitch)
            {
                AddSample(main, shape, alongMin, across, rise, samples);
                AddSample(main, shape, alongMax, across, rise, samples);
            }
        }

        /// <summary>
        /// One level sample, unless it falls out past where town-local coordinates hold — see
        /// <see cref="IsBeyondFold"/>.
        /// </summary>
        private static void AddSample(
            IRoadPath main, in TownShape shape, float along, float across, float rise,
            List<Vector3> samples)
        {
            if (IsBeyondFold(main, shape, along, across))
            {
                return;
            }

            samples.Add(rise > 0f
                ? Raised(main, shape, along, across, rise)
                : ToWorld(main, shape, along, across));
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
