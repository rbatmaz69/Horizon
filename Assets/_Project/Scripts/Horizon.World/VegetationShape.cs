using System;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What grows on the mountain, how thickly, and where it refuses to.
    ///
    /// Every number here is a rejection rule rather than a count, because a count cannot survive the road
    /// changing shape. Density is expressed as the spacing of the candidate grid; what actually ends up on
    /// the hillside is whatever survives the clearances, the slope limits, the tree line and the clumping
    /// noise. That means the pass can be re-routed and the vegetation still lands somewhere sensible.
    ///
    /// The altitude fields are normalised against the course itself — 0 is the lowest point of the road, 1
    /// is the summit — so the tree line moves with the pass instead of being a hard-coded elevation.
    /// </summary>
    [Serializable]
    public struct VegetationShape
    {
        [Tooltip("Spacing of the tree candidate grid, metres. One candidate per cell, jittered inside it.")]
        public float TreeCellSize;

        [Tooltip("Spacing of the shrub candidate grid, metres.")]
        public float ShrubCellSize;

        [Tooltip("Spacing of the grass tuft candidate grid, metres.")]
        public float TuftCellSize;

        [Tooltip("Spacing of the boulder candidate grid, metres.")]
        public float BoulderCellSize;

        [Tooltip("Nothing taller than a shrub within this distance of the centreline, metres. Has to clear "
               + "the carriageway, the shoulder and the guard rail, with room for a canopy that leans.")]
        public float TreeClearance;

        [Tooltip("Shrubs may come almost to the rail — that band is what the driver looks at all the time, "
               + "and leaving it bare is most of what makes a road feel like it is on a grey plane.")]
        public float ShrubClearance;

        [Tooltip("Grass starts just past the shoulder.")]
        public float TuftClearance;

        public float BoulderClearance;

        [Tooltip("Grass is only worth its triangles where it can be seen close up, so it stops here.")]
        public float TuftMaxDistance;

        [Tooltip("Ground steeper than this grows no trees, degrees. Well below the terrain's own rock "
               + "threshold: a tree on a 34° face reads as stuck to a wall.")]
        public float TreeMaxSlopeDegrees;

        [Tooltip("Shrubs hang on rather further up a slope than trees do, degrees.")]
        public float ShrubMaxSlopeDegrees;

        [Tooltip("Grass wants the shallow ground, degrees.")]
        public float TuftMaxSlopeDegrees;

        [Tooltip("Boulders prefer steep ground and screes, degrees. Below this they are unlikely rather "
               + "than forbidden, so the odd erratic still sits in a meadow.")]
        public float BoulderMinSlopeDegrees;

        [Tooltip("Tree line as a fraction of the climb: 0 is the lowest point of the road, 1 the summit.")]
        public float TreeLineHeight;

        [Tooltip("How far the tree line wanders, in the same units. Without it the last row of trees "
               + "follows a contour line, which no mountain does.")]
        public float TreeLineJitter;

        [Tooltip("Below this fraction of the climb the forest is mostly broadleaf, above it mostly conifer.")]
        public float BroadleafBelow;

        [Tooltip("Frequency of the clumping noise. Smaller means broader stands and broader clearings.")]
        public float ClumpScale;

        [Tooltip("Clumping noise below this value grows nothing. This is what makes clearings, forest "
               + "edges and open shoulders instead of an even carpet.")]
        public float ClumpThreshold;

        [Tooltip("Density at the outer edge of the corridor relative to density at the road. The far field "
               + "is seen through fog and edge-on, so it buys almost nothing at full density.")]
        public float FarDensity;

        [Tooltip("Distance from the road at which the thinning starts, metres.")]
        public float FarDensityStart;

        [Tooltip("Distance from the road at which the thinning has reached FarDensity, metres.")]
        public float FarDensityEnd;

        [Tooltip("Keep-out radius around the centreline of a tunnel or gallery, metres. A tunnel is a solid "
               + "body standing on untouched ground and the height field knows nothing about it, so without "
               + "this, trees grow happily inside the rock. Must exceed TunnelBuilder.MoundHalfWidth.")]
        public float TunnelExclusion;

        [Tooltip("How far past each portal the keep-out runs, metres. Must exceed TunnelBuilder.EndOverhang.")]
        public float TunnelEndMargin;

        [Tooltip("Trees are cleared this far around a viewpoint, metres. A viewpoint with a spruce in front "
               + "of it is not a viewpoint.")]
        public float ViewpointClearing;

        [Tooltip("Above the tree line, the band in which dead snags appear, as a fraction of the climb.")]
        public float SnagBand;

        [Tooltip("Chance that a tree rejected for standing above the tree line becomes a snag instead.")]
        public float SnagChance;

        [Tooltip("Warn if a single tile exceeds this many triangles. Not a hard limit — a warning naming the "
               + "tile is more useful than silently thinned vegetation.")]
        public int MaxTrianglesPerTile;

        /// <summary>
        /// What the whole pass costs at the settings below, measured rather than estimated, so the next
        /// person changing a density knows what they are moving away from.
        ///
        /// 55 tiles, about 290k triangles of vegetation in the world, densest tile just under 10k. What is
        /// drawn is only what the frustum keeps: the camera's far plane is 600 m and a tile is 168 m, so
        /// roughly a dozen tiles are in view, around 60k triangles a frame. Chunk streaming barely helps
        /// here — the pass is about 1.4 km across and the load radius is 650 m, so nearly every tile is
        /// resident nearly all the time. Frustum culling is what does the work.
        /// </summary>
        public const int MeasuredWorldTriangles = 290000;

        public static VegetationShape Default => new VegetationShape
        {
            // 11 m between trees is a fairly open forest once the clumping mask has taken its third. Tighter
            // than this and the near field turns into a wall you cannot see the corners through, which
            // fights the whole point of a pass road.
            TreeCellSize = 11f,

            // 9 m, up from 7. At 7 the first build put fifteen thousand bushes against three thousand
            // trees — a heath with the odd spruce in it rather than a forest with undergrowth. Bushes
            // should read as the floor of the wood, so they want to be a few times the trees, not five.
            ShrubCellSize = 9f,

            // Grass is six triangles apiece and lives only in the band beside the road, so it can afford
            // to be dense — that band is what the eye tracks for the whole climb.
            TuftCellSize = 4f,
            BoulderCellSize = 20f,

            // RoadShape.OuterHalfWidth is 6 m and the rails stand a little outside that. 13 m leaves a
            // driveable-feeling corridor without a bare strip.
            TreeClearance = 13f,
            ShrubClearance = 9f,
            TuftClearance = 7.5f,
            BoulderClearance = 11f,
            TuftMaxDistance = 30f,

            TreeMaxSlopeDegrees = 30f,
            ShrubMaxSlopeDegrees = 40f,
            TuftMaxSlopeDegrees = 32f,
            BoulderMinSlopeDegrees = 26f,

            // 0.82 of a 180 m climb puts the tree line about 30 m below the summit — high enough that most
            // of the drive is through forest, low enough that the last hairpins are visibly above it.
            TreeLineHeight = 0.82f,
            TreeLineJitter = 0.09f,

            // 0.26, not 0.38. Most of the corridor's *area* sits low on the mountain — a pass climbs out of
            // a wide valley into a narrow summit — so a threshold placed by eye at the middle of the climb
            // put two broadleaves on the hill for every conifer, which is a lowland wood, not an alpine one.
            BroadleafBelow = 0.26f,

            // 0.012 gives stands and clearings roughly 80-150 m across, which is the scale a corner reveals.
            ClumpScale = 0.012f,
            ClumpThreshold = 0.42f,

            FarDensity = 0.5f,
            FarDensityStart = 100f,
            FarDensityEnd = 200f,

            // TunnelBuilder.MoundHalfWidth is 52 m and EndOverhang is 24 m; both get a margin.
            TunnelExclusion = 58f,
            TunnelEndMargin = 30f,
            ViewpointClearing = 38f,

            SnagBand = 0.12f,
            SnagChance = 0.12f,

            // 12000, against a densest tile of about 9700 at these settings. Set to catch a tile that has
            // genuinely run away — half again as heavy as the worst the design produces — rather than to
            // flag the design itself. An earlier 9000 was guessed before any of this had been built and did
            // nothing but warn about the intended density.
            MaxTrianglesPerTile = 12000,
        };
    }
}
