using System;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The village at the foot of the pass: how far it spreads, where its lanes run, and how densely it
    /// is built.
    ///
    /// <para><b>Transitional.</b> The ground it stands on is now <see cref="TownShape"/>'s basin, which is
    /// far larger than these two stub lanes can fill, and the street network that replaces them is the
    /// next stage. What is left here is the plot layout, pointed at the town's extent so the houses stand
    /// where the floor is, rather than 400 m back down the arrival road where the numbers used to put
    /// them.</para>
    /// </summary>
    [Serializable]
    public struct VillageShape
    {
        [Tooltip("Where the village starts along the main course, metres.")]
        public float AlongStart;

        [Tooltip("Where it ends. Beyond this the road starts climbing in earnest.")]
        public float AlongEnd;

        [Tooltip("Which side of the main road the lanes run off. -1 is left. The valley floor is flat for "
               + "300 m on the left and climbs 25 m within 100 m on the right, so this is not free choice.")]
        public float LaneSide;

        [Tooltip("Distance along the main course where the first lane branches off.")]
        public float FirstLaneAt;

        [Tooltip("Distance along the main course where the second lane branches off.")]
        public float SecondLaneAt;

        [Tooltip("How far the lanes reach away from the main road, metres.")]
        public float LaneLength;

        [Tooltip("Half-width of a lane's carriageway. Narrower than the pass — a village lane is not a "
               + "trunk road, and it carries no centre line.")]
        public float LaneHalfWidth;

        [Tooltip("Gap between the main carriageway edge and where a lane's ribbon starts, metres. The "
               + "throat below fills it.")]
        public float JunctionGap;

        [Tooltip("How far the junction mouth opens along the main road, and how far the blend runs along "
               + "the lane. The same number for both, which is what makes it a bell mouth rather than a "
               + "wedge.")]
        public float JunctionThroat;

        [Tooltip("Spacing of house plots along a street, metres.")]
        public float PlotSpacing;

        [Tooltip("How far a house stands back from the centreline of its street, metres.")]
        public float PlotSetback;

        [Tooltip("Half-width of a plot across the street, metres. Also the fence line.")]
        public float PlotHalfWidth;

        [Tooltip("Depth of a plot away from the street, metres.")]
        public float PlotDepth;

        [Tooltip("Chance a plot is left empty, so the place has gaps and does not read as a terrace.")]
        public float PlotVacancy;

        [Tooltip("Chance a plot has a car parked on it.")]
        public float ParkedCarChance;

        [Tooltip("Spacing of street lamps along the main road through the village, metres.")]
        public float LampSpacing;

        [Tooltip("Distance along the main course where the windmill stands. It is the one silhouette "
               + "visible from the pass road above, so it wants an open plot near the edge of the village.")]
        public float MillAt;

        [Tooltip("Chance a plot carries a barn or a sawmill rather than a house.")]
        public float WorkingBuildingChance;

        [Tooltip("Margin around a *building* within which nothing wild grows at all, metres. Deliberately "
               + "small: it guards the walls, not the garden.")]
        public float PlotClearance;

        [Tooltip("Margin around a whole *plot* within which no tree or boulder is scattered, metres. Grass "
               + "and bushes carry on right up to the houses — a village with bare ground between them "
               + "looks abandoned.")]
        public float TreeKeepOut;

        [Tooltip("Warn if a single tile exceeds this many triangles.")]
        public int MaxTrianglesPerTile;

        public static VillageShape Default => new VillageShape
        {
            // Taken from the course, not written down twice. The town's extent is published by
            // MountainPassCourse because everything about it shifts when the arrival road is reshaped.
            AlongStart = MountainPassCourse.TownStartDistance,
            AlongEnd = MountainPassCourse.TownEndDistance,

            LaneSide = -1f,
            FirstLaneAt = MountainPassCourse.TownStartDistance + 130f,
            SecondLaneAt = MountainPassCourse.TownStartDistance + 330f,
            LaneLength = 105f,

            // 3.2 m of half-width is 6.4 m of lane: two cars can pass, just.
            LaneHalfWidth = 3.2f,
            JunctionGap = 0.5f,
            JunctionThroat = 11f,

            PlotSpacing = 26f,

            // 24 m, not 17. The setback is measured to the *house*, but the garden boundary sits
            // PlotDepth/2 in front of it — at 17 m that put fences and hedges 7 m from the centreline,
            // a metre off the asphalt edge. The corridor check would never have caught it either: its
            // box is only 1.3 m wide.
            PlotSetback = 24f,
            PlotHalfWidth = 11f,
            PlotDepth = 20f,
            PlotVacancy = 0.18f,
            ParkedCarChance = 0.35f,

            LampSpacing = 42f,
            MillAt = MountainPassCourse.TownStartDistance + 470f,
            WorkingBuildingChance = 0.16f,

            // 2.5 m around a wall, not 15 m around a whole plot. The old pair sterilised the village.
            PlotClearance = 2.5f,
            TreeKeepOut = 4f,

            MaxTrianglesPerTile = 14000,
        };
    }
}
