using System;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Painted markings. Dash and gap lengths live here rather than next to the texture settings
    /// because the texture tiles once per cycle — if the two disagree, the dashes stop being evenly
    /// spaced in the world and nothing in the code would say why.
    /// </summary>
    [Serializable]
    public struct RoadMarkings
    {
        [Tooltip("Length of a painted dash, metres.")]
        public float DashLength;

        [Tooltip("Gap between dashes, metres.")]
        public float GapLength;

        [Tooltip("Width of the centre line, metres.")]
        public float CentreLineWidth;

        [Tooltip("Width of the solid edge lines, metres.")]
        public float EdgeLineWidth;

        [Tooltip("How far the edge line sits inside the edge of the asphalt, metres. Also serves as "
               + "the guard band that keeps the wrap seam free of paint.")]
        public float EdgeLineInset;

        /// <summary>Length of one dash-and-gap cycle. The texture covers exactly this along the road.</summary>
        public float CycleLength => DashLength + GapLength;

        public static RoadMarkings Default => new RoadMarkings
        {
            DashLength = 4f,
            GapLength = 8f,
            CentreLineWidth = 0.15f,
            EdgeLineWidth = 0.15f,
            EdgeLineInset = 0.15f,
        };
    }

    /// <summary>Cross-section of the road ribbon.</summary>
    [Serializable]
    public struct RoadShape
    {
        [Tooltip("Half the driveable width, metres.")]
        public float HalfWidth;

        [Tooltip("Width of the verge either side of the asphalt.")]
        public float ShoulderWidth;

        [Tooltip("How far the outer edge of the verge sits below the road surface.")]
        public float ShoulderDrop;

        [Tooltip("Distance between cross-sections. Smaller is smoother and heavier.")]
        public float StepLength;

        [Tooltip("Lifts the surface slightly so it never z-fights with the terrain below it.")]
        public float SurfaceLift;

        [Tooltip("How much higher the centre of the carriageway sits than its edges, metres. Real roads "
               + "are cambered to shed water, and without it the surface reads as a flat plate.")]
        public float Crown;

        public RoadMarkings Markings;

        [Tooltip("Camber at full lock, degrees. The carriageway rolls into a corner, which the car feels "
               + "through the wheel raycasts without any change to the vehicle.")]
        public float MaxBankDegrees;

        [Tooltip("Corner radius at which the camber reaches its maximum. Tighter corners get no more.")]
        public float FullBankRadius;

        [Tooltip("Below this corner radius the centre line becomes solid — no overtaking.")]
        public float SolidLineBelowRadius;

        [Tooltip("Above this radius it returns to dashed. The gap between the two thresholds is "
               + "hysteresis, so a corner hovering near the limit does not flicker between the two.")]
        public float DashedLineAboveRadius;

        /// <summary>Half the total width of the paved surface plus its shoulders.</summary>
        public float OuterHalfWidth => HalfWidth + ShoulderWidth;

        public static RoadShape Default => new RoadShape
        {
            // 10.5 m of asphalt as two 5.25 m lanes. Wider than a real pass, deliberately: the car is
            // 1.86 m across and tilt steering is not precise to the centimetre. Widening this is close
            // to free because everything that needs the number takes it from here — the ribbon and its
            // marking atlas, the tunnel arch, the guard rails, the trunk mouths, the spawn lane and the
            // clearance sweeps — but see ShoulderDrop for the one thing it eats into.
            HalfWidth = 5.25f,
            ShoulderWidth = 1.5f,

            // 0.5 m, and it has to be read together with TerrainShape.RoadShelfDrop and MaxBankDegrees.
            // The camber lowers the inner edge of the carriageway by HalfWidth × sin(bank); if the terrain
            // shelf is not below *that*, the hillside comes up through the asphalt on the inside of every
            // corner. At 4° that is 0.37 m, so the shelf at 0.45 m leaves 0.08 m of clearance — the
            // margin a wider carriageway is paid for out of, and the number to watch if this widens
            // again. ValidateRoadClearance measures it.
            ShoulderDrop = 0.5f,

            // 2.5 m, not 4 m: on a 20 m hairpin radius, 4 m steps are 11° apart and the corner
            // visibly facets. Hairpins are the whole point of the pass, so they set this number.
            StepLength = 2.5f,
            SurfaceLift = 0.08f,

            // About 2% of the half width.
            Crown = 0.09f,

            Markings = RoadMarkings.Default,

            // Held at 4°, not the 6° an alpine hairpin is really built with, because the camber lowers the
            // inner edge and every degree of it has to be paid for out of the terrain clearance above.
            MaxBankDegrees = 4f,
            FullBankRadius = 30f,

            // Hairpins are R=20 and the legs sweep at R=150, so both sit clearly on their own side.
            SolidLineBelowRadius = 60f,
            DashedLineAboveRadius = 90f,
        };

        /// <summary>
        /// One carriageway of the motorway: four lanes running the same way, with a hard shoulder.
        ///
        /// <para>Only ever used through <see cref="OffsetRoadPath"/>, twice — once either side of a
        /// centreline that is itself never paved. The median between them is the gap left by the two
        /// offsets, so the number that decides how far apart the carriageways sit lives in
        /// <c>AutobahnCourse</c>, not here.</para>
        ///
        /// <para>Note what is <i>not</i> different from <see cref="Default"/>: the ring
        /// <see cref="RoadMeshBuilder"/> extrudes and the way it lays out marking UVs are unchanged, and
        /// they do not need to be. That builder normalises u across the asphalt, so four lanes are a
        /// wider ribbon with a different atlas painted on it rather than different geometry. The lane
        /// lines come from <c>RoadTextureBuilder</c>.</para>
        /// </summary>
        public static RoadShape Autobahn => new RoadShape
        {
            // Four 3.75 m lanes.
            HalfWidth = 7.5f,

            // A hard shoulder, not a verge — wide enough to read as somewhere you could stop.
            ShoulderWidth = 2.5f,

            // Deeper than Default's 0.5, and required rather than chosen. Read the note on that field:
            // the camber drops the inner edge by HalfWidth × sin(bank), and HalfWidth here is half again
            // as large. At 3° that is 0.39 m against Default's 0.37 — so the shelf has to fall further
            // to keep the same clearance over a carriageway this wide.
            ShoulderDrop = 0.7f,

            // 8 m rather than 2.5. StepLength is set by the tightest radius a shape is used on, and
            // Default's 2.5 is set by 20 m hairpins. Nothing here is under 700 m, where 8 m steps are
            // 0.65° apart and invisible — and at 8 km × two carriageways the difference is about 1000
            // rings instead of 3200 each.
            StepLength = 8f,
            SurfaceLift = 0.08f,

            // Same ~2% cross-fall as Default, over a wider carriageway.
            Crown = 0.12f,

            Markings = RoadMarkings.Default,

            // Gentler than the pass and reaching much further out, because it is answering a different
            // question. On a hairpin the camber is what stops the car washing wide; here it is what
            // stops a 160 km/h sweeper feeling flat, and the radii are twenty times longer — at
            // Default's FullBankRadius of 30 m a 700 m bend would get essentially no bank at all.
            MaxBankDegrees = 3f,
            FullBankRadius = 400f,

            // Effectively never solid. On a one-way carriageway the interior lines are lane dividers,
            // not a centre line, and "no overtaking" is not a thing they can express — the atlas paints
            // them dashed in both variants, so these thresholds only matter to
            // RoadMeshBuilder.ResolveLineVariants, which must be kept off the solid variant entirely.
            SolidLineBelowRadius = 0f,
            DashedLineAboveRadius = 1f,
        };
    }

    /// <summary>
    /// Controls the terrain generated around a road: how finely it is meshed, how far it extends, and how
    /// the carriageway is cut into it.
    ///
    /// Note what is *not* here any more: there used to be settings for how far the uphill side rose and
    /// how deep the downhill side fell. That rule could not survive a road that doubles back on itself,
    /// and <see cref="MountainField"/> replaced it.
    /// </summary>
    [Serializable]
    public struct TerrainShape
    {
        [Tooltip("Grid cell size, metres. Larger cells give a more faceted look and fewer triangles.")]
        public float CellSize;

        [Tooltip("Flat ground either side of the road before the relief starts. Enforced to at least "
               + "two cells wide, so no terrain triangle can span from road level into full relief "
               + "and slice through the road surface.")]
        public float VergeWidth;

        [Tooltip("How far below the road centreline the flat shelf sits, metres.\n\n"
               + "This must roughly match where the outer edge of the verge ends up, or one of two "
               + "things goes wrong: too little and the terrain buries the verge and pokes up through "
               + "the asphalt wherever the coarse grid overshoots a climbing, curving road; too much "
               + "and the road appears to float on a plinth.")]
        public float RoadShelfDrop;

        [Tooltip("Distance over which the ground blends from the road's shelf into the open mountain. "
               + "Longer means the carriageway sits in a gentler cutting.")]
        public float BlendDistance;

        [Tooltip("Amplitude of the large rolling shapes, metres.")]
        public float RidgeAmplitude;

        [Tooltip("Frequency of the large shapes. Smaller means broader hills.")]
        public float RidgeScale;

        [Tooltip("Amplitude of the fine bumpiness, metres.")]
        public float DetailAmplitude;

        public float DetailScale;

        [Tooltip("Faces steeper than this are treated as rock rather than grass, degrees.")]
        public float RockSlopeThreshold;

        [Tooltip("Target side length of a terrain tile, metres. Rounded to a whole number of cells so "
               + "tile edges always land on the lattice.")]
        public float TileSize;

        [Tooltip("How far either side of the road terrain is generated at all. A folded pass leaves most "
               + "of its bounding box empty, so a corridor skips the work rather than the detail.")]
        public float CorridorWidth;

        public static TerrainShape Default => new TerrainShape
        {
            CellSize = 12f,
            VergeWidth = 24f,

            // Must clear the lowest point of the cambered carriageway, which is the inner edge in a
            // corner: HalfWidth 5.25 × sin(4°) = 0.37 m below the centreline. 0.45 leaves 0.08 m, and it
            // also lands close to where the outer edge of the verge falls to.
            RoadShelfDrop = 0.45f,
            // 70 m, not 30: this is now the width of the cutting the road sits in, and a short blend
            // makes the carriageway look like it is running along the top of a dyke.
            BlendDistance = 70f,
            RidgeAmplitude = 26f,
            RidgeScale = 0.0055f,
            DetailAmplitude = 5f,
            DetailScale = 0.03f,
            RockSlopeThreshold = 34f,

            // 168 m is fourteen 12 m cells: about 400 triangles a tile, and roughly 70 tiles for the
            // pass. Small enough to stream, large enough not to drown in draw calls.
            TileSize = 168f,
            CorridorWidth = 200f,
        };
    }
}
