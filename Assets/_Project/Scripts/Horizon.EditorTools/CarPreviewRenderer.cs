using System.IO;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Renders the vehicle prefab to PNGs next to the project folder, so the car can be reviewed
    /// without hunting for a camera angle in the scene view.
    ///
    /// Deliberately does not switch scenes: the rig is built far above the world, rendered, and torn
    /// down again, leaving whatever you had open untouched. Output goes outside <c>Assets/</c> so it
    /// never becomes an imported asset.
    /// </summary>
    public static class CarPreviewRenderer
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/Vehicles/Vehicle_Prototype.prefab";
        private const int Width = 900;
        private const int Height = 600;

        /// <summary>Somewhere nothing else exists, so the render only contains the car.</summary>
        private static readonly Vector3 StagePosition = new Vector3(0f, 5000f, 0f);

        [MenuItem("Tools/Horizon/Render Car Preview", priority = 40)]
        public static void Render()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[Horizon] No vehicle prefab at {PrefabPath}. Run Rebuild Prototype Scene first.");
                return;
            }

            GameObject car = Object.Instantiate(prefab, StagePosition, Quaternion.identity);
            var lightObject = new GameObject("PreviewLight");
            var cameraObject = new GameObject("PreviewCamera");

            try
            {
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.color = new Color(1f, 0.97f, 0.90f);
                lightObject.transform.rotation = Quaternion.Euler(38f, 145f, 0f);

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
                camera.fieldOfView = 34f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;

                // Front three-quarter and rear three-quarter show the hood, the windscreen rake, the
                // roofline and both sets of lights. The side view is the one that actually exposes
                // proportion — stance, wheel arches, where the roof sits over the wheelbase.
                RenderFrom(camera, car.transform, new Vector3(5.2f, 2.3f, 5.8f),
                    Path.Combine(directory, "CarPreview_Front.png"));
                RenderFrom(camera, car.transform, new Vector3(-5.0f, 2.1f, -5.9f),
                    Path.Combine(directory, "CarPreview_Rear.png"));
                RenderFrom(camera, car.transform, new Vector3(9.5f, 0.6f, 0f),
                    Path.Combine(directory, "CarPreview_Side.png"));

                Debug.Log($"[Horizon] Car preview written to {directory}/CarPreview_Front.png and _Rear.png");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(lightObject);
                Object.DestroyImmediate(car);
            }
        }

        private static void RenderFrom(Camera camera, Transform target, Vector3 offset, string filePath)
        {
            Vector3 focus = target.position + new Vector3(0f, 0.35f, 0f);
            camera.transform.position = focus + offset;
            camera.transform.rotation = Quaternion.LookRotation(focus - camera.transform.position, Vector3.up);

            var renderTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
            };

            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(filePath, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }
    }
}
