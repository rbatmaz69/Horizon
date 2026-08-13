using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// The paints the player can choose between.
    ///
    /// <para><b>One table, read twice.</b> The material asset and the swatch the player taps are both
    /// built from these rows. Two tables would be two tables that drift, and the failure is a menu that
    /// lies about what the car will look like — which nobody would notice until they had already
    /// driven away in the wrong colour.</para>
    ///
    /// <para><b>Index 0 is the existing <c>M_CarBody.mat</c>.</b> Not a new "Ember" asset alongside it:
    /// keeping the original path means the default car is byte-for-byte the car it was, the prefab's
    /// existing material reference still resolves, and nothing is orphaned for
    /// <c>HorizonAssetUtility.ReportOrphanedAssets</c> to complain about. Its colour is repeated here
    /// because the swatch needs it, and it is the one row where the value is a copy of something rather
    /// than the source of it — <c>LoadOrCreateMaterial</c> leaves an existing asset alone, so retinting
    /// the .mat by hand would leave this swatch behind. Worth knowing before wondering why.</para>
    ///
    /// <para>Eight, in two rows of four. Enough that the choice feels like a choice, few enough that
    /// every one could be given a smoothness and a metallic of its own — which is the entire reason
    /// these are materials and not a hue slider. Deliberately warmer and more saturated than the six
    /// ambient traffic colours in <c>PrototypeSetup.PrototypeMaterials</c>: those are muted on purpose so
    /// traffic does not pull the eye, and the player's car is the one thing on screen that should.</para>
    /// </summary>
    internal static class CarPaintPalette
    {
        private const string MaterialsFolder = "Assets/_Project/Art/Materials";

        internal readonly struct Paint
        {
            /// <summary>Shown under the swatch.</summary>
            public readonly string Name;

            public readonly string AssetPath;

            public readonly Color Colour;

            public readonly float Smoothness;

            public readonly float Metallic;

            public Paint(string name, string assetPath, Color colour, float smoothness, float metallic)
            {
                Name = name;
                AssetPath = assetPath;
                Colour = colour;
                Smoothness = smoothness;
                Metallic = metallic;
            }
        }

        internal static readonly Paint[] All =
        {
            // The car as it has always been. See the class note for why this row is the odd one.
            new Paint("Ember", MaterialsFolder + "/M_CarBody.mat",
                new Color(0.86f, 0.36f, 0.17f), 0.62f, 0.10f),

            // A deep metallic, and the one that shows the body's creases best — high smoothness with
            // real metallic content is what makes the fastback's shoulder line catch the sun.
            new Paint("Midnight", MaterialsFolder + "/M_CarPaint_Midnight.mat",
                new Color(0.10f, 0.13f, 0.24f), 0.78f, 0.45f),

            new Paint("Racing Green", MaterialsFolder + "/M_CarPaint_Racing.mat",
                new Color(0.09f, 0.24f, 0.16f), 0.72f, 0.30f),

            // Flat and chalky rather than glossy: a works-team white, and the one paint here that reads
            // as matte.
            new Paint("Chalk", MaterialsFolder + "/M_CarPaint_Chalk.mat",
                new Color(0.88f, 0.87f, 0.83f), 0.38f, 0.00f),

            new Paint("Sunflower", MaterialsFolder + "/M_CarPaint_Sunflower.mat",
                new Color(0.92f, 0.72f, 0.16f), 0.66f, 0.15f),

            new Paint("Signal Red", MaterialsFolder + "/M_CarPaint_Signal.mat",
                new Color(0.68f, 0.10f, 0.09f), 0.70f, 0.20f),

            // Cold and bright, so the palette is not eight warm colours. It is also the one that looks
            // most different at dusk, when the fog goes amber and everything else warms with it.
            new Paint("Ice Blue", MaterialsFolder + "/M_CarPaint_Ice.mat",
                new Color(0.52f, 0.70f, 0.80f), 0.74f, 0.25f),

            // Nearly black, high metallic, low colour — the closest thing to bare metal, and it makes the
            // car read as a silhouette at distance rather than as a shape with a colour.
            new Paint("Graphite", MaterialsFolder + "/M_CarPaint_Graphite.mat",
                new Color(0.20f, 0.21f, 0.23f), 0.80f, 0.60f),
        };

        /// <summary>Just the colours, in order, for building the swatch row.</summary>
        internal static Color[] Colours
        {
            get
            {
                var colours = new Color[All.Length];
                for (int i = 0; i < All.Length; i++)
                {
                    colours[i] = All[i].Colour;
                }

                return colours;
            }
        }

        /// <summary>Creates any paint asset that is missing and returns all eight, in table order.</summary>
        internal static Material[] LoadOrCreate()
        {
            var materials = new Material[All.Length];

            for (int i = 0; i < All.Length; i++)
            {
                Paint paint = All[i];
                materials[i] = HorizonAssetUtility.LoadOrCreateMaterial(
                    paint.AssetPath, System.IO.Path.GetFileNameWithoutExtension(paint.AssetPath),
                    paint.Colour, paint.Smoothness, paint.Metallic);
            }

            return materials;
        }
    }
}
