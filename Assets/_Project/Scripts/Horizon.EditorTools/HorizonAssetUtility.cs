using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.EditorTools
{
    /// <summary>Small helpers shared by the setup tools. Editor-only.</summary>
    public static class HorizonAssetUtility
    {
        /// <summary>Creates every missing folder along an "Assets/a/b/c" path.</summary>
        public static void EnsureFolder(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath))
            {
                return;
            }

            string[] parts = assetFolderPath.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        /// <summary>
        /// Loads an asset, creating it from <paramref name="factory"/> only if it does not exist.
        /// Used for configs so re-running the setup tool never discards hand-tuned values.
        /// </summary>
        public static T LoadOrCreate<T>(string assetPath, Func<T> factory) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            T created = factory();
            AssetDatabase.CreateAsset(created, assetPath);
            return Reload(created, assetPath);
        }

        /// <summary>
        /// Flushes a just-created asset to disk and hands back the imported instance, so callers hold
        /// a reference to the asset rather than to the in-memory object it was built from.
        ///
        /// Note this is hygiene, not a fix for the scene-switch hazard — an asset reference still does
        /// not survive <c>EditorSceneManager.NewScene</c>. Assets must be re-loaded by path after any
        /// scene switch; see <c>PrototypeSetup.LoadVehicleConfig</c>.
        /// </summary>
        private static T Reload<T>(T created, string assetPath) where T : UnityEngine.Object
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            T imported = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (imported == null)
            {
                Debug.LogError($"[Horizon] Failed to import '{assetPath}' after creating it.");
                return created;
            }

            return imported;
        }

        /// <summary>
        /// Every derived asset path written since <see cref="BeginGeneratedRun"/>.
        ///
        /// Generated meshes are named after what produced them, so renaming a builder orphans its old
        /// output: the files stay on disk, keep their GUIDs, and are referenced by nothing. Nothing about
        /// a stale mesh asset announces itself — the world builds correctly and the folder quietly grows.
        /// </summary>
        private static readonly HashSet<string> WrittenAssets = new HashSet<string>();

        /// <summary>Starts recording which derived assets a build writes.</summary>
        public static void BeginGeneratedRun()
        {
            WrittenAssets.Clear();
        }

        /// <summary>
        /// Lists everything in a generated folder that this run did not write, so a rename leaves a line
        /// in the log rather than a file nobody will look at again.
        /// </summary>
        public static void ReportOrphanedAssets(string folder)
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { folder });
            var orphans = new List<string>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!WrittenAssets.Contains(path) && !AssetDatabase.IsValidFolder(path))
                {
                    orphans.Add(Path.GetFileName(path));
                }
            }

            if (orphans.Count == 0)
            {
                return;
            }

            orphans.Sort();
            Debug.LogWarning($"[Horizon] {orphans.Count} asset(s) in {folder} were not written by this "
                             + "build and are referenced by nothing: " + string.Join(", ", orphans)
                             + ". Derived output, so deleting them is safe.");
        }

        /// <summary>
        /// Replaces a derived asset outright and returns the imported instance. For meshes and other
        /// generated output. Use the return value, not the object you passed in — see <c>Reload</c>.
        /// </summary>
        public static T ReplaceAsset<T>(T asset, string assetPath) where T : UnityEngine.Object
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.CreateAsset(asset, assetPath);
            WrittenAssets.Add(assetPath);
            return Reload(asset, assetPath);
        }

        /// <summary>
        /// Writes private <c>[SerializeField]</c> values. This is the supported way to wire up
        /// components from editor code without widening their public API just for the setup tool.
        /// </summary>
        public static void Configure(UnityEngine.Object target, Action<SerializedObject> edit)
        {
            var serialized = new SerializedObject(target);
            edit(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Verifies an object-reference field actually got written. A null asset reference produces no
        /// error at wiring time and only surfaces as a broken scene much later, so the setup tool
        /// checks its own work.
        /// </summary>
        public static void AssertReferenceAssigned(UnityEngine.Object owner, string propertyName)
        {
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(propertyName);

            if (property == null)
            {
                Debug.LogError($"[Horizon] '{propertyName}' does not exist on {owner.GetType().Name}.");
                return;
            }

            if (property.objectReferenceValue == null)
            {
                Debug.LogError(
                    $"[Horizon] '{propertyName}' on {owner.GetType().Name} was not wired up. "
                    + "The prototype will not work. This usually means the referenced asset was not "
                    + "imported before it was assigned.");
            }
        }

        /// <summary>Fills an object-reference array property.</summary>
        public static void SetObjectArray(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object[] values)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogWarning($"[Horizon] Property '{propertyName}' not found on {serialized.targetObject}.");
                return;
            }

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        /// <summary>Creates a URP Lit material, falling back to the built-in shader if URP is absent.</summary>
        public static Material CreateLitMaterial(string name, Color baseColor, float smoothness, float metallic = 0f)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning(
                    "[Horizon] URP Lit shader not found — is the project using the Universal Render "
                    + "Pipeline? Falling back to Standard.");
                shader = Shader.Find("Standard");
            }

            var material = new Material(shader) { name = name };

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", baseColor);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", baseColor);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", smoothness);
            }
            else if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", smoothness);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", metallic);
            }

            return material;
        }

        /// <summary>Loads a material, creating and saving it only if missing.</summary>
        public static Material LoadOrCreateMaterial(
            string assetPath,
            string name,
            Color baseColor,
            float smoothness,
            float metallic = 0f,
            Texture2D baseMap = null)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Material created = CreateLitMaterial(name, baseColor, smoothness, metallic);

            if (baseMap != null)
            {
                created.SetTexture("_BaseMap", baseMap);

                // The tint multiplies the map, so it has to be white or the texture comes out darkened.
                created.SetColor("_BaseColor", Color.white);
            }

            AssetDatabase.CreateAsset(created, assetPath);
            return Reload(created, assetPath);
        }

        /// <summary>
        /// An unlit material, for surfaces that are meant to *be* a light rather than to catch one —
        /// lamp lenses above all.
        ///
        /// This replaces an emissive Lit material, and the reason is worth recording because it cost a
        /// long afternoon. URP Lit only renders emission when the <c>_EMISSION</c> shader keyword is on
        /// the material, and on this Unity version that keyword could not be made to survive being
        /// written to a .mat: <c>EnableKeyword</c>, the <c>LocalKeyword</c> overload, dirtying and
        /// saving, deleting the asset and regenerating it — every one reported success in memory and
        /// left <c>m_ValidKeywords: []</c> on disk, so every fresh load had emission compiled out. The
        /// lamps had therefore never once glowed since the project started.
        ///
        /// Unlit needs no keyword. <c>_BaseColor</c> is drawn at full brightness whatever the scene
        /// lighting does, which is exactly what a lamp lens should do, animates cleanly through a
        /// <see cref="MaterialPropertyBlock"/>, blooms when driven above 1, and costs less on a mobile
        /// GPU than lit shading did. For a game whose art direction is flat colour, it is also simply
        /// the more honest choice.
        /// </summary>
        /// <summary>
        /// The one material a whole palette rides on: <c>Horizon/VertexTintLit</c>, white, lit.
        ///
        /// <para>Its base colour is white on purpose — the colour comes from the mesh's vertices, so this
        /// material is a shading model rather than a shade. That is what lets the town's twelve façade
        /// materials be one, which is the difference between twelve draw calls a tile and three.</para>
        ///
        /// <para>Falls back to URP/Lit if the shader is missing, which renders the whole town white
        /// rather than magenta and says so. A magenta town is obvious; a white one with a warning is
        /// diagnosable.</para>
        /// </summary>
        public static Material LoadOrCreateTintMaterial(string assetPath, string name, float smoothness)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Horizon/VertexTintLit");
            if (shader == null)
            {
                Debug.LogWarning(
                    "[Horizon] Horizon/VertexTintLit not found, so the buildings fall back to URP/Lit and "
                    + "lose their palette — every façade will be plain white. The shader lives at "
                    + "Assets/_Project/Art/Shaders/HorizonVertexTint.shader; if it is present, it failed "
                    + "to compile and the console will say why.");

                shader = Shader.Find("Universal Render Pipeline/Lit");
            }

            var created = new Material(shader) { name = name };

            if (created.HasProperty("_BaseColor"))
            {
                created.SetColor("_BaseColor", Color.white);
            }

            if (created.HasProperty("_Smoothness"))
            {
                created.SetFloat("_Smoothness", smoothness);
            }

            if (created.HasProperty("_Metallic"))
            {
                created.SetFloat("_Metallic", 0f);
            }

            AssetDatabase.CreateAsset(created, assetPath);
            return created;
        }

        public static Material LoadOrCreateUnlitMaterial(string assetPath, string name, Color baseColor)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogWarning("[Horizon] URP Unlit shader not found. Falling back to Unlit/Color.");
                shader = Shader.Find("Unlit/Color");
            }

            var created = new Material(shader) { name = name };

            if (created.HasProperty("_BaseColor"))
            {
                created.SetColor("_BaseColor", baseColor);
            }

            if (created.HasProperty("_Color"))
            {
                created.SetColor("_Color", baseColor);
            }

            AssetDatabase.CreateAsset(created, assetPath);
            return Reload(created, assetPath);
        }

        /// <summary>
        /// Writes a generated texture out as a PNG asset and imports it, creating it only if missing.
        ///
        /// <paramref name="anisoLevel"/> matters for anything seen at a grazing angle — road markings
        /// above all, which is precisely the case anisotropic filtering exists for. It is also the first
        /// thing to turn down if mobile fill rate becomes a problem.
        /// </summary>
        public static Texture2D LoadOrCreateTexture(
            string assetPath,
            Func<Texture2D> factory,
            int anisoLevel = 4,
            bool wrap = true)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Texture2D generated = factory();
            File.WriteAllBytes(assetPath, generated.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(generated);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.mipmapEnabled = true;
                importer.wrapMode = wrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = anisoLevel;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// Generates a soft round particle sprite. Procedural so the project carries no binary art
        /// dependency for something this simple.
        /// </summary>
        public static Texture2D LoadOrCreateSoftCircleTexture(string assetPath, int size = 64)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                    float normalized = Mathf.Clamp01(distance / center);

                    // Squared falloff: a hard-edged disc reads as a bubble, this reads as smoke.
                    float alpha = 1f - normalized;
                    alpha *= alpha;

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            File.WriteAllBytes(assetPath, texture.EncodeToPNG());

            // Must be qualified: this file has both `using System` and `using UnityEngine`, so a bare
            // `Object` is ambiguous.
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// A white sprite: a rounded rectangle, or a ring if <paramref name="holeRadius"/> is set.
        ///
        /// <para>Generated rather than imported for the same reason every mesh in this project is: art
        /// nobody authored is art nobody has to keep, and a control that is a rounded box and a ring
        /// does not need an artist. It is white so the UI can tint it per widget — one texture serves
        /// every button, the wheel and the slider.</para>
        ///
        /// <para>Sprites, not textures, because uGUI's <c>Image</c> takes a sprite and generates a
        /// dreadful default if it does not get one.</para>
        /// </summary>
        /// <param name="holeRadius">
        /// Fraction of the half-size left transparent in the middle, for the steering wheel. Zero gives
        /// a solid rounded rectangle.
        /// </param>
        public static Sprite LoadOrCreateUiSprite(
            string assetPath, int size = 128, float cornerRadius = 0.25f, float holeRadius = 0f)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float half = size * 0.5f;
            float radius = cornerRadius * half;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance outside a rounded rectangle, the standard rounded-box field: clamp the
                    // point into the inner rectangle and measure from there.
                    float dx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float dy = Mathf.Abs(y + 0.5f - half) - (half - radius);

                    float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude
                                    + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;

                    // One pixel of feather, so the edge is not a staircase.
                    float alpha = Mathf.Clamp01(0.5f - outside);

                    if (holeRadius > 0f)
                    {
                        float centre = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude;
                        alpha = Mathf.Min(alpha, Mathf.Clamp01(centre - holeRadius * half - 0.5f));
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            File.WriteAllBytes(assetPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;

                // Single, explicitly. Setting textureType from code does *not* imply it the way the
                // inspector does — the importer defaults to Multiple, which means "this texture is an
                // atlas of named sub-sprites", and an atlas with no rects defined contains no Sprite at
                // all. LoadAssetAtPath<Sprite> then correctly returns null, every Image is assigned
                // null, and the UI draws as blank rectangles while every validator reports success.
                importer.spriteImportMode = SpriteImportMode.Single;

                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;

                // Nine-sliced from the corners, so one square texture stretches into a wide pedal or a
                // tall slider without the corners smearing.
                if (holeRadius <= 0f)
                {
                    importer.spriteBorder = new Vector4(radius, radius, radius, radius);
                }

                importer.SaveAndReimport();
            }

            return LoadFreshSprite(assetPath);
        }

        /// <summary>
        /// A steering wheel: a rim, three spokes and a hub, drawn rather than imported.
        ///
        /// <para>A bare ring does not read as a steering wheel — it reads as a ring, and worse, it gives
        /// no clue which way up it is, so a player cannot see how much lock they are holding. The spokes
        /// are what make the rotation legible, which is the entire job of this widget.</para>
        ///
        /// <para>Three spokes at the bottom and sides, leaving the top clear: that is the shape of most
        /// real wheels and it means the spokes are furthest from horizontal at rest, so the first few
        /// degrees of turn are the most visible.</para>
        /// </summary>
        public static Sprite LoadOrCreateWheelSprite(string assetPath, int size = 256)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float half = size * 0.5f;

            float outer = half * 0.96f;
            float inner = half * 0.74f;
            float hub = half * 0.26f;
            float spokeHalfWidth = half * 0.075f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    // The rim: a band, feathered a pixel at both edges.
                    float alpha = Mathf.Min(
                        Mathf.Clamp01(outer - r + 0.5f),
                        Mathf.Clamp01(r - inner + 0.5f));

                    // The hub.
                    alpha = Mathf.Max(alpha, Mathf.Clamp01(hub - r + 0.5f));

                    // Three spokes: down, and up-left and up-right at 30° above the horizontal. Each is
                    // a capsule from the hub out to the rim, so it meets both cleanly.
                    for (int s = 0; s < 3; s++)
                    {
                        float angle = (-90f + s * 120f) * Mathf.Deg2Rad;
                        float ax = Mathf.Cos(angle);
                        float ay = Mathf.Sin(angle);

                        // Distance from the segment running hub -> rim along this spoke.
                        float along = Mathf.Clamp(dx * ax + dy * ay, hub * 0.5f, inner + 1f);
                        float px = dx - ax * along;
                        float py = dy - ay * along;
                        float distance = Mathf.Sqrt(px * px + py * py);

                        alpha = Mathf.Max(alpha, Mathf.Clamp01(spokeHalfWidth - distance + 0.5f));
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            return SaveSprite(texture, assetPath, Vector4.zero);
        }

        /// <summary>
        /// A glyph for a control: an arrow, a pedal, a hand or a pause bar, drawn into a square.
        ///
        /// <para>Generated for the same reason the wheel is — a handful of shapes does not justify an
        /// art pipeline, and a caption reading "GAS" is a label rather than a control. A symbol is also
        /// the only version of this that works in any language.</para>
        /// </summary>
        public static Sprite LoadOrCreateGlyphSprite(string assetPath, string glyph, int size = 128)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Normalised to -1..1 with the origin in the middle, so the shapes below can be
                    // written in plain proportions rather than in pixels.
                    float u = (x + 0.5f - half) / half;
                    float v = (y + 0.5f - half) / half;

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, GlyphAlpha(glyph, u, v, 1f / half)));
                }
            }

            return SaveSprite(texture, assetPath, Vector4.zero);
        }

        private static float GlyphAlpha(string glyph, float u, float v, float pixel)
        {
            switch (glyph)
            {
                case "left":
                    return Triangle(-u, v, pixel);
                case "right":
                    return Triangle(u, v, pixel);

                // A pedal seen from the side, with the motion lines behind it that say "go".
                //
                // The lines are not decoration. The first pair of these glyphs was a bar leaning one way
                // for throttle and the same bar leaning the other way for brake, and that is one shape
                // shown twice: at thumb size, half covered by the thumb itself, nobody reads a fourteen
                // degree difference in lean. Throttle and brake have to differ in *silhouette*, which is
                // why the brake below is a disc rather than a third bar.
                case "throttle":
                {
                    float pedal = Bar(u - 0.30f, v, 0.17f, 0.60f, 14f, pixel);

                    float lines = 0f;
                    for (int i = 0; i < 3; i++)
                    {
                        lines = Mathf.Max(lines,
                            Bar(u + 0.10f + i * 0.24f, v, 0.055f, 0.34f - i * 0.08f, 0f, pixel));
                    }

                    return Mathf.Max(pedal, lines);
                }

                // A brake disc and its caliper. Round against the throttle's bars — the one difference
                // that survives being small.
                case "brake":
                {
                    float disc = Ring(u, v, 0.62f, 0.30f, pixel);
                    float hub = Mathf.Clamp01((0.13f - new Vector2(u, v).magnitude) / pixel);
                    float caliper = Bar(u - 0.46f, v - 0.30f, 0.15f, 0.27f, -35f, pixel);

                    return Mathf.Max(Mathf.Max(disc, hub), caliper);
                }

                // The handbrake: a lever, angled, with a knob on the end.
                case "handbrake":
                {
                    float lever = Bar(u + 0.1f, v, 0.11f, 0.55f, 32f, pixel);
                    float knob = Mathf.Clamp01((0.24f - new Vector2(u - 0.28f, v - 0.44f).magnitude) / pixel);
                    return Mathf.Max(lever, knob);
                }

                // Two bars: pause.
                case "pause":
                    return Mathf.Max(
                        Bar(u + 0.26f, v, 0.16f, 0.52f, 0f, pixel),
                        Bar(u - 0.26f, v, 0.16f, 0.52f, 0f, pixel));

                default:
                    return 0f;
            }
        }

        /// <summary>A solid triangle pointing right, for the arrows.</summary>
        private static float Triangle(float u, float v, float pixel)
        {
            // Inside when behind the sloping front faces and ahead of the back edge.
            float front = 0.62f - u - Mathf.Abs(v) * 1.15f;
            float back = u + 0.42f;

            return Mathf.Clamp01(Mathf.Min(front, back) / pixel);
        }

        /// <summary>A filled annulus — the brake disc.</summary>
        private static float Ring(float u, float v, float outerRadius, float innerRadius, float pixel)
        {
            float r = new Vector2(u, v).magnitude;

            return Mathf.Min(
                Mathf.Clamp01((outerRadius - r) / pixel),
                Mathf.Clamp01((r - innerRadius) / pixel));
        }

        /// <summary>A rounded bar of a given half-size, rotated by <paramref name="degrees"/>.</summary>
        private static float Bar(float u, float v, float halfWidth, float halfHeight, float degrees, float pixel)
        {
            float a = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(a);
            float s = Mathf.Sin(a);

            float ru = u * c + v * s;
            float rv = -u * s + v * c;

            float radius = halfWidth * 0.55f;
            float dx = Mathf.Abs(ru) - (halfWidth - radius);
            float dy = Mathf.Abs(rv) - (halfHeight - radius);

            float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude
                            + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;

            return Mathf.Clamp01(-outside / pixel);
        }

        /// <summary>
        /// Loads the sprite a generator has just written, and <b>refuses to return null</b>.
        ///
        /// <para>The importer does not always have the sprite ready on the call that follows
        /// <c>SaveAndReimport</c> — the texture is on disk and the <c>.meta</c> is correct, but
        /// <c>LoadAssetAtPath&lt;Sprite&gt;</c> comes back empty on the run that created the file, and
        /// only resolves on the next one. A caller that shrugs at that assigns null to every
        /// <c>Image</c>, and null is not a visible failure: an Image with no sprite draws a plain tinted
        /// quad. The result is a UI of blank rectangles that builds cleanly and passes every validator,
        /// which is exactly how a set of buttons shipped with no symbols on them.</para>
        ///
        /// <para>So: one forced re-import, and then an exception rather than a null. A rebuild that
        /// stops with a message is worth a great deal more than a scene that quietly comes out
        /// wrong.</para>
        /// </summary>
        private static Sprite LoadFreshSprite(string assetPath)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null)
            {
                return sprite;
            }

            AssetDatabase.ImportAsset(
                assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null)
            {
                return sprite;
            }

            throw new System.InvalidOperationException(
                $"[Horizon] Generated '{assetPath}' but the importer would not hand back a Sprite, even "
                + "after a forced re-import. Rebuilding again usually resolves it; letting it through "
                + "would silently produce a UI of blank rectangles.");
        }

        /// <summary>Writes a generated texture out as a sprite asset and imports it as one.</summary>
        private static Sprite SaveSprite(Texture2D texture, string assetPath, Vector4 border)
        {
            texture.Apply();
            File.WriteAllBytes(assetPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;

                // Single, explicitly. Setting textureType from code does *not* imply it the way the
                // inspector does — the importer defaults to Multiple, which means "this texture is an
                // atlas of named sub-sprites", and an atlas with no rects defined contains no Sprite at
                // all. LoadAssetAtPath<Sprite> then correctly returns null, every Image is assigned
                // null, and the UI draws as blank rectangles while every validator reports success.
                importer.spriteImportMode = SpriteImportMode.Single;

                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;

                if (border != Vector4.zero)
                {
                    importer.spriteBorder = border;
                }

                importer.SaveAndReimport();
            }

            return LoadFreshSprite(assetPath);
        }

        /// <summary>
        /// Creates an alpha-blended URP particle material. URP materials do not become transparent by
        /// setting a colour with alpha — the surface type, blend factors, keyword and render queue all
        /// have to be set together, which is what this wraps up.
        /// </summary>
        public static Material LoadOrCreateParticleMaterial(
            string assetPath,
            string name,
            Texture2D texture,
            Color baseColor)
        {
            return LoadOrCreateParticleMaterial(assetPath, name, texture, baseColor, false);
        }

        /// <param name="additive">
        /// Add the particle to what is behind it instead of blending over it.
        ///
        /// <para>What fire is. An alpha-blended flame can only ever be as bright as the texture it is
        /// drawn with, so against a bright sky it reads as a grey smudge and against a night road it
        /// reads as a sticker; adding means it brightens whatever it is over, which is why it glows at
        /// dusk and disappears into a noon sky. The only difference from the blended case is the
        /// destination blend factor — <c>One</c> rather than <c>OneMinusSrcAlpha</c> — but it has to be
        /// set alongside the surface type and the queue, which is what this whole method exists to keep
        /// together.</para>
        /// </param>
        public static Material LoadOrCreateParticleMaterial(
            string assetPath,
            string name,
            Texture2D texture,
            Color baseColor,
            bool additive)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
            {
                Debug.LogWarning("[Horizon] URP particle shader not found; smoke will not blend correctly.");
                shader = Shader.Find("Sprites/Default");
            }

            var created = new Material(shader) { name = name };

            created.SetFloat("_Surface", 1f);
            created.SetFloat("_Blend", 0f);
            created.SetFloat("_ZWrite", 0f);
            created.SetFloat("_AlphaClip", 0f);
            created.SetOverrideTag("RenderType", "Transparent");
            created.SetFloat("_BlendModePreserveSpecular", 0f);
            created.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            created.SetInt(
                "_DstBlend",
                additive
                    ? (int)UnityEngine.Rendering.BlendMode.One
                    : (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

            if (additive)
            {
                // URP's particle shader reads the blend *mode* as well as the factors, and leaving this
                // at its default quietly puts the alpha factors back on a material whose queue and
                // keywords all say additive.
                created.SetFloat("_Blend", 1f);
            }
            created.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            created.DisableKeyword("_ALPHATEST_ON");
            created.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (texture != null)
            {
                created.SetTexture("_BaseMap", texture);
            }

            created.SetColor("_BaseColor", baseColor);

            AssetDatabase.CreateAsset(created, assetPath);
            return Reload(created, assetPath);
        }

        /// <summary>Builds a gradient from evenly typed colour keys at explicit times.</summary>
        public static Gradient BuildGradient(params (float time, Color color)[] keys)
        {
            var gradient = new Gradient();
            var colorKeys = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                colorKeys[i] = new GradientColorKey(keys[i].color, keys[i].time);
            }

            gradient.SetKeys(colorKeys, new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            });

            return gradient;
        }
    }
}
