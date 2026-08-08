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

        [Tooltip("Below this corner radius the centre line becomes solid — no overtaking.")]
        public float SolidLineBelowRadius;

        [Tooltip("Above this radius it returns to dashed. The gap between the two thresholds is "
               + "hysteresis, so a corner hovering near the limit does not flicker between the two.")]
        public float DashedLineAboveRadius;

        /// <summary>Half the total width of the paved surface plus its shoulders.</summary>
        public float OuterHalfWidth => HalfWidth + ShoulderWidth;

        public static RoadShape Default => new RoadShape
        {
            // 9 m of asphalt as two 4.5 m lanes. Wider than a real pass, deliberately: the car is
            // 1.86 m across and tilt steering is not precise to the centimetre.
            HalfWidth = 4.5f,
            ShoulderWidth = 1.5f,
            ShoulderDrop = 0.3f,

            // 2.5 m, not 4 m: on a 20 m hairpin radius, 4 m steps are 11° apart and the corner
            // visibly facets. Hairpins are the whole point of the pass, so they set this number.
            StepLength = 2.5f,
            SurfaceLift = 0.08f,

            // About 2% of the half width.
            Crown = 0.09f,

            Markings = RoadMarkings.Default,

            // Hairpins are R=20 and the legs sweep at R=150, so both sit clearly on their own side.
            SolidLineBelowRadius = 60f,
            DashedLineAboveRadius = 90f,
        };
    }

    /// <summary>
    /// Controls the low-poly valley generated around a road. A mountain pass reads as one because
    /// the terrain rises on the inside of the curve and falls away on the outside — on a serpentine
    /// that alternates on its own as the road hairpins back.
    /// </summary>
    [Serializable]
    public struct TerrainShape
    {
        [Tooltip("Grid cell size, metres. Larger cells give a more faceted look and fewer triangles.")]
        public float CellSize;

        [Tooltip("How far the terrain extends beyond the road's bounding box, metres.")]
        public float Margin;

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

        [Tooltip("Distance over which terrain blends from road level into full relief.")]
        public float BlendDistance;

        [Tooltip("Rise per metre away from the road, on the uphill side.")]
        public float SlopeRise;

        [Tooltip("Cap on relief height, metres.")]
        public float MaxRelief;

        [Tooltip("How deep the downhill side drops, as a fraction of the uphill rise.")]
        public float ValleyDepth;

        [Tooltip("Amplitude of the large rolling shapes, metres.")]
        public float RidgeAmplitude;

        [Tooltip("Frequency of the large shapes. Smaller means broader hills.")]
        public float RidgeScale;

        [Tooltip("Amplitude of the fine bumpiness, metres.")]
        public float DetailAmplitude;

        public float DetailScale;

        [Tooltip("Faces steeper than this are treated as rock rather than grass, degrees.")]
        public float RockSlopeThreshold;

        public static TerrainShape Default => new TerrainShape
        {
            CellSize = 12f,
            Margin = 140f,
            VergeWidth = 24f,

            // Where the outer edge of the verge lands: SurfaceLift 0.08 minus ShoulderDrop 0.30, plus a
            // little margin so the asphalt is reliably proud of the terrain.
            RoadShelfDrop = 0.25f,
            BlendDistance = 30f,
            SlopeRise = 0.55f,
            MaxRelief = 85f,
            ValleyDepth = 0.85f,
            RidgeAmplitude = 26f,
            RidgeScale = 0.0055f,
            DetailAmplitude = 5f,
            DetailScale = 0.03f,
            RockSlopeThreshold = 34f,
        };
    }
}
