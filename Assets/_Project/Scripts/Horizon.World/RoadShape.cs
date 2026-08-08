using System;
using UnityEngine;

namespace Horizon.World
{
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

        [Tooltip("Metres of road covered by one tile of the texture along its length.")]
        public float TextureLength;

        public static RoadShape Default => new RoadShape
        {
            HalfWidth = 4f,
            ShoulderWidth = 1.3f,
            ShoulderDrop = 0.3f,
            StepLength = 4f,
            SurfaceLift = 0.08f,
            TextureLength = 12f,
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
