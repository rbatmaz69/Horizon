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

        // The line widths went up a quarter with the carriageways under them; the dash and the gap did
        // not. A painted line is read against the width of the road it is on, so a 0.15 m line on a
        // 13.2 m carriageway reads thinner than the same line on a 10.5 m one. A dash length is read
        // against the speed it goes past at, and no speed changed.
        public static RoadMarkings Default => new RoadMarkings
        {
            DashLength = 4f,
            GapLength = 8f,
            CentreLineWidth = 0.19f,
            EdgeLineWidth = 0.19f,
            EdgeLineInset = 0.19f,
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
            // 13.2 m of asphalt as two 6.6 m lanes. Wider than a real pass, deliberately: the widest car
            // is 2.92 m across its collider and 3.00 m across its tyres, and tilt steering is not precise
            // to the centimetre.
            //
            // <b>A quarter wider than it was, because the cars grew a quarter in plan.</b> What the
            // driver reads is not the width of the road, it is how much of it the car fills — so when
            // 5bd7396 scaled every car by 1.25 the whole cross-section had to follow or the world would
            // have quietly become a tighter game. Every shape here took the same factor.
            //
            // Widening this is close to free because almost everything that needs the number takes it
            // from here — the ribbon and its marking atlas, the tunnel arch, the guard rails, the trunk
            // mouths, the spawn lane and the clearance sweeps. The exceptions are the numbers that are
            // secretly a width and are written out as literals somewhere else, and two of those break
            // in silence: AutobahnCourse.CarriagewayOffset, which is a half-width plus a median, and
            // TerrainShape.RoadShelfDrop below. See ShoulderDrop for the one thing this eats into.
            HalfWidth = 6.6f,
            ShoulderWidth = 1.9f,

            // 0.63 m, and it has to be read together with TerrainShape.RoadShelfDrop and MaxBankDegrees.
            // The camber lowers the inner edge of the carriageway by HalfWidth × sin(bank); if the terrain
            // shelf is not below *that*, the hillside comes up through the asphalt on the inside of every
            // corner. At 4° that is 0.46 m, so the shelf at 0.57 m leaves 0.11 m of clearance — the
            // margin a wider carriageway is paid for out of, and the number to watch if this widens
            // again. ValidateRoadClearance measures it.
            ShoulderDrop = 0.63f,

            // 2.5 m, not 4 m: on a 20 m hairpin radius, 4 m steps are 11° apart and the corner
            // visibly facets. Hairpins are the whole point of the pass, so they set this number.
            StepLength = 2.5f,
            SurfaceLift = 0.08f,

            // About 2% of the half width.
            Crown = 0.11f,

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
            // Four 4.7 m lanes.
            HalfWidth = 9.4f,

            // A hard shoulder, not a verge — wide enough to read as somewhere you could stop.
            ShoulderWidth = 3.1f,

            // Deeper than Default's 0.63, and required rather than chosen. Read the note on that field:
            // the camber drops the inner edge by HalfWidth × sin(bank), and HalfWidth here is half again
            // as large. At 3° that is 0.49 m against Default's 0.46 — so the shelf has to fall further
            // to keep the same clearance over a carriageway this wide. This is the deepest of the three
            // and therefore the shape that sets TerrainShape.RoadShelfDrop: 0.08 m of margin, against
            // the pass's 0.11 and the circuit's 0.15.
            ShoulderDrop = 0.88f,

            // 8 m rather than 2.5. StepLength is set by the tightest radius a shape is used on, and
            // Default's 2.5 is set by 20 m hairpins. Nothing here is under 700 m, where 8 m steps are
            // 0.65° apart and invisible — and at 8 km × two carriageways the difference is about 1000
            // rings instead of 3200 each.
            StepLength = 8f,
            SurfaceLift = 0.08f,

            // Same ~2% cross-fall as Default, over a wider carriageway.
            Crown = 0.15f,

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

        /// <summary>
        /// The Weissjochring: one wide ribbon with no lanes marked on it, and a gravel run-off either
        /// side rather than a verge.
        ///
        /// <para><b>Sixteen metres of asphalt, a quarter more than the widest the Nordschleife ever
        /// gets.</b> Thirteen was that figure exactly; the quarter on top is the quarter the cars grew by
        /// in 5bd7396, and it is added here for the same reason it is added to every other shape — what
        /// is being held constant is how much of the road the car fills. The road shapes here are already
        /// generous — the widest car is 2.92 m across and tilt steering is not precise to the
        /// centimetre — but a racing line needs room to be got wrong, and the whole point of a circuit is
        /// that a corner can be taken more than one way.</para>
        ///
        /// <para><b>No centre line, and it costs nothing to say so.</b>
        /// <c>RoadTextureBuilder.BuildSurface</c> paints <c>laneCount − 1</c> interior lines, so the
        /// atlas for this shape is asked for with one lane: two edge lines and bare asphalt between
        /// them, which is what a race track is. A dashed line down the middle of a circuit would read as
        /// a country road that had been widened.</para>
        ///
        /// <para><b><see cref="MaxBankDegrees"/> is 3 and not the 6 an alpine hairpin is built with, and
        /// the arithmetic is the one <see cref="Default"/>'s <see cref="ShoulderDrop"/> records.</b> The
        /// camber lowers the inner edge of the carriageway by <c>HalfWidth × sin(bank)</c>, and the
        /// terrain shelf has to sit below <i>that</i> or the hillside comes up through the asphalt on the
        /// inside of every corner. This carriageway is a quarter wider than the pass's, so every degree
        /// costs a quarter more: 8.1 × sin 3° is 0.42 m against a 0.57 m shelf, which leaves 0.15 m —
        /// more margin than the pass has, and the number to watch if this ever widens again.
        /// <c>ValidateRoadClearance</c> measures it.</para>
        /// </summary>
        public static RoadShape Circuit => new RoadShape
        {
            HalfWidth = 8.1f,

            // Run-off, not a verge. Wide enough that going off is a moment rather than an event, and
            // it is the strip the kerbs are laid along.
            ShoulderWidth = 3.75f,

            // Between the pass's 0.63 and the motorway's 0.88, in proportion to the carriageway over it.
            ShoulderDrop = 0.69f,

            // 4 m rather than the pass's 2.5. StepLength is set by the tightest radius a shape is used
            // on, and nothing here is under 170 m, where 4 m steps are 1.3° apart.
            StepLength = 4f,
            SurfaceLift = 0.08f,

            // The same ~2 % cross-fall over a wider carriageway.
            Crown = 0.14f,

            Markings = RoadMarkings.Default,

            MaxBankDegrees = 3f,

            // Reaching much further out than the pass's 30 m, because the corners are. At 30 a 400 m
            // sweeper would get no bank at all, and a circuit that feels flat is a wide road.
            FullBankRadius = 150f,

            // Effectively never solid. There is no interior line on this atlas to be solid, so these
            // only matter to RoadMeshBuilder.ResolveLineVariants, which must be kept off the solid
            // variant entirely — the same setting the motorway uses and for the same reason.
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
            // corner. The deepest of the three shapes is the motorway: HalfWidth 9.4 × sin(3°) = 0.49 m
            // below the centreline, against the pass's 0.46 and the circuit's 0.42. 0.57 leaves 0.08 m
            // there, and it also lands close to where the outer edge of the verge falls to.
            //
            // <b>It moves with the road widths, and it is the one that breaks in silence.</b> Widen a
            // carriageway without widening this and the hillside comes up through the asphalt on the
            // inside of every corner in the world — which builds without a word and is only ever
            // reported by ValidateRoadClearance.
            RoadShelfDrop = 0.57f,
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
