using System;
using System.IO;
using UnityEditor;
using UnityEngine;

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

        /// <summary>
        /// Loads a material, creating and saving it only if missing. Pass <paramref name="emission"/>
        /// to make it glow — applied on creation only, so a later retint is never overwritten.
        /// </summary>
        public static Material LoadOrCreateMaterial(
            string assetPath,
            string name,
            Color baseColor,
            float smoothness,
            float metallic = 0f,
            Color? emission = null)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            Material created = CreateLitMaterial(name, baseColor, smoothness, metallic);

            if (emission.HasValue)
            {
                // The keyword matters: without _EMISSION, URP Lit ignores _EmissionColor entirely, so
                // a property block trying to animate the glow at runtime would do nothing.
                created.EnableKeyword("_EMISSION");
                created.SetColor("_EmissionColor", emission.Value);
                created.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            AssetDatabase.CreateAsset(created, assetPath);
            return Reload(created, assetPath);
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
