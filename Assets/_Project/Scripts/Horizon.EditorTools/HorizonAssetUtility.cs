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
            created.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            created.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
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
