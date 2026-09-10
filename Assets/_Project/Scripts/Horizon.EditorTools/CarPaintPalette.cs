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
    /// <para><b>That paragraph had the danger the wrong way round, and the real one is worse.</b>
    /// <c>LoadOrCreateMaterial</c> returning an existing asset untouched does not merely mean a
    /// hand-retint survives — it means <b>this table stopped being read</b> the moment all eight .mat
    /// files existed on disk. Every number in it below was the seed of an asset, once, and has been
    /// decoration ever since: editing a smoothness here changed nothing, on any build, and nothing said
    /// so. <see cref="Apply"/> is what makes the table the source of truth it claims to be, and it is
    /// <see cref="VehicleConfigReset"/>'s shape exactly — a button nobody presses is not a guard.</para>
    ///
    /// <para><b>Deleting and recreating would have been the wrong fix</b>: new GUIDs, a broken material
    /// reference in the vehicle prefab, and eight assets for
    /// <c>HorizonAssetUtility.ReportOrphanedAssets</c> to name.</para>
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

        /// <summary>
        /// The eight paints. <b>The colours are exactly what they were; every smoothness and every
        /// metallic came down, and the relative order between them is kept.</b>
        ///
        /// <para><b>There are no reflection probes in this project</b>, by budget, so
        /// <c>unity_SpecCube0</c> is the skybox — which is what <i>The sky</i> already turns on its head
        /// when it explains that greying the dome was half the fix for a wet road reflecting blue in a
        /// rainstorm. Every one of these was above half. Above about half smoothness a surface stops
        /// being its own colour and becomes whatever hangs over it, which is the trap already recorded
        /// against <c>WetSurfaces</c> at 0.80 turning the Kehrtunnel's bore into a blue river. On a car
        /// it showed as the broad specular smear washing the fastback's deck from dark red to pale pink
        /// across one panel in <c>DriverPreview_1_AsShipped.png</c>.</para>
        ///
        /// <para><b>Metallic fell furthest and that is the less obvious half.</b> A metal has no diffuse
        /// term at all — it is nothing but its reflection — so Graphite at 0.60 with no probe in the
        /// world was a dark mirror of a gradient rather than a colour. Smoothness makes the reflection
        /// sharp; metallic makes it the only thing there is.</para>
        ///
        /// <para><b>The colours are deliberately not touched.</b> This is one curve applied to the whole
        /// palette, so the answer to it is the two numbers it is expressed in — the argument <i>The
        /// frame</i> makes about the tone map, where fifty recoloured tints, each of which looks right
        /// alone and none of which can be attributed, is the failure to avoid.</para>
        /// </summary>
        internal static readonly Paint[] All =
        {
            // The car as it has always been. See the class note for why this row is the odd one.
            new Paint("Ember", MaterialsFolder + "/M_CarBody.mat",
                new Color(0.86f, 0.36f, 0.17f), 0.26f, 0.03f),

            // A deep near-metallic, and still the glossiest thing in the row. Its comment used to say
            // that high smoothness "is what makes the fastback's shoulder line catch the sun" — which
            // was aspirational, because until the ring creases went in that car had no shoulder line to
            // catch anything. It has one now, and it is geometry, so it does not need a mirror finish to
            // be visible.
            new Paint("Midnight", MaterialsFolder + "/M_CarPaint_Midnight.mat",
                new Color(0.10f, 0.13f, 0.24f), 0.32f, 0.10f),

            new Paint("Racing Green", MaterialsFolder + "/M_CarPaint_Racing.mat",
                new Color(0.09f, 0.24f, 0.16f), 0.29f, 0.07f),

            // The flattest of the eight, as it always was: a works-team white. It used to be the only
            // paint here that read as matte and it is now the far end of a range rather than an
            // exception, which is why it came down least in absolute terms.
            new Paint("Chalk", MaterialsFolder + "/M_CarPaint_Chalk.mat",
                new Color(0.88f, 0.87f, 0.83f), 0.18f, 0.00f),

            new Paint("Sunflower", MaterialsFolder + "/M_CarPaint_Sunflower.mat",
                new Color(0.92f, 0.72f, 0.16f), 0.27f, 0.04f),

            new Paint("Signal Red", MaterialsFolder + "/M_CarPaint_Signal.mat",
                new Color(0.68f, 0.10f, 0.09f), 0.28f, 0.05f),

            // Cold and bright, so the palette is not eight warm colours. It is also the one that looks
            // most different at dusk, when the fog goes amber and everything else warms with it.
            new Paint("Ice Blue", MaterialsFolder + "/M_CarPaint_Ice.mat",
                new Color(0.52f, 0.70f, 0.80f), 0.30f, 0.06f),

            // Nearly black, low colour, and the closest thing here to bare metal — which is now a
            // matter of it being the darkest and the most reflective of eight, rather than of it being
            // an actual mirror. It still reads as a silhouette at distance, which is the point of it.
            new Paint("Graphite", MaterialsFolder + "/M_CarPaint_Graphite.mat",
                new Color(0.20f, 0.21f, 0.23f), 0.34f, 0.12f),
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

        /// <summary>
        /// Creates any paint asset that is missing, writes the table onto all eight, and returns them in
        /// table order.
        ///
        /// <para>The write is the point. See the class note: without it this table is a set of numbers
        /// that seeded eight assets once and have not been read since.</para>
        /// </summary>
        internal static Material[] LoadOrCreate()
        {
            var materials = new Material[All.Length];
            var report = new System.Text.StringBuilder();
            int moved = 0;

            for (int i = 0; i < All.Length; i++)
            {
                Paint paint = All[i];
                Material material = HorizonAssetUtility.LoadOrCreateMaterial(
                    paint.AssetPath, System.IO.Path.GetFileNameWithoutExtension(paint.AssetPath),
                    paint.Colour, paint.Smoothness, paint.Metallic);

                float wasSmoothness = material.HasProperty(SmoothnessId)
                    ? material.GetFloat(SmoothnessId) : paint.Smoothness;
                float wasMetallic = material.HasProperty(MetallicId)
                    ? material.GetFloat(MetallicId) : paint.Metallic;

                Apply(material, paint);
                materials[i] = material;

                if (Mathf.Abs(wasSmoothness - paint.Smoothness) > 0.001f
                    || Mathf.Abs(wasMetallic - paint.Metallic) > 0.001f)
                {
                    moved++;
                    report.Append($"\n  {paint.Name,-13} smoothness {wasSmoothness:0.00} -> "
                                  + $"{paint.Smoothness:0.00}, metallic {wasMetallic:0.00} -> "
                                  + $"{paint.Metallic:0.00}");
                }
            }

            // Only the rows that moved, and a count either way. A material edit that silently did
            // nothing is exactly the failure this method exists to fix, so the number has to be printed
            // every build — but this runs twice per rebuild, once per PrototypeMaterials, and a table of
            // eight arrows pointing at themselves on the second pass is a log line that means nothing.
            UnityEngine.Debug.Log(moved == 0
                ? $"[Horizon] {All.Length} car paints, all already at the table's values."
                : $"[Horizon] {All.Length} car paints written, {moved} moved:{report}");

            return materials;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");

        /// <summary>
        /// Writes one row of the table onto its material, and marks the asset dirty so the write lands.
        /// </summary>
        private static void Apply(Material material, in Paint paint)
        {
            if (material == null)
            {
                return;
            }

            material.SetColor(BaseColorId, paint.Colour);
            material.SetFloat(SmoothnessId, paint.Smoothness);
            material.SetFloat(MetallicId, paint.Metallic);

            UnityEditor.EditorUtility.SetDirty(material);
        }
    }
}
