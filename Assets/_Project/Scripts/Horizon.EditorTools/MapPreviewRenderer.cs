using System.IO;
using Horizon.Game;
using Horizon.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs the baked map: the whole world, each town, and a crop the size of the minimap.
    ///
    /// <para><b>Because the build cannot see any of this.</b> Every fault the last three features had
    /// was found in a picture and by nothing else — a sign painted with asphalt, a canopy with no
    /// underside, a bridge four kilometres past the far plane. A map is exactly that kind of artefact:
    /// a road drawn from the wrong path, a sea over a town, a hull that swallowed a hillside and a name
    /// on the wrong side of a strait all build cleanly and all show at a glance.</para>
    ///
    /// <para><b>It draws with the real <see cref="MapGraphic"/> rather than rasterising the arrays
    /// itself.</b> A preview with an opinion of its own agrees with the builder right up until one of
    /// them is wrong, which is the argument <c>ValidateBridgeSupport</c> already lost once. So this puts
    /// a genuine canvas in front of a camera and takes its picture.</para>
    /// </summary>
    public static class MapPreviewRenderer
    {
        private const int WideSize = 1400;
        private const int CropSize = 700;

        [MenuItem("Tools/Horizon/Render Map Preview", priority = 47)]
        public static void Render()
        {
            var map = AssetDatabase.LoadAssetAtPath<WorldMap>(PrototypeSetup.WorldMapPath);

            if (map == null)
            {
                Debug.LogWarning($"[Horizon] No map at {PrototypeSetup.WorldMapPath}. Run Rebuild Prototype Scene first.");
                return;
            }

            string directory = Directory.GetParent(Application.dataPath).FullName;

            Vector2 size = map.PlanSize;
            float fit = Mathf.Max(size.x / WideSize, size.y / WideSize) * 1.04f;

            Capture(map, WideSize, WideSize, map.PlanCentre, fit, 0f, true,
                Path.Combine(directory, "MapPreview_World.png"));

            // Each town, at a zoom where its streets are drawn. Named from the map itself rather than
            // from a list here, so a town added later photographs itself.
            for (int area = 0; area < map.AreaCount; area++)
            {
                if (map.AreaKindOf(area) != MapAreaKind.Town)
                {
                    continue;
                }

                string name = map.AreaNameOf(area);

                Capture(map, CropSize, CropSize, Centroid(map, area), 2.6f, 0f, true,
                    Path.Combine(directory, $"MapPreview_Town_{Safe(name)}.png"));
            }

            // The minimap's own view, at its own zoom, turned. Rendered at the widget's 300 units blown
            // up, because what has to be judged here is whether a hairpin stack is legible at that size
            // — which is not a question a plan view of the world can answer.
            Vector2 hairpin = SomewhereOn(map, MapLineKind.Trunk, 0.42f);

            Capture(map, CropSize, CropSize, hairpin, 340f / 300f, 35f, false,
                Path.Combine(directory, "MapPreview_Minimap.png"));

            Vector2 motorway = SomewhereOn(map, MapLineKind.Motorway, 0.5f);

            Capture(map, CropSize, CropSize, motorway, 340f / 300f, 0f, false,
                Path.Combine(directory, "MapPreview_Motorway.png"));

            Debug.Log($"[Horizon] Map preview written to {directory}/MapPreview_*.png");
        }

        /// <summary>A point some way along the first line of a kind, for a crop that is not the origin.</summary>
        private static Vector2 SomewhereOn(WorldMap map, MapLineKind kind, float fraction)
        {
            for (int line = 0; line < map.LineCount; line++)
            {
                if (map.KindOf(line) != kind)
                {
                    continue;
                }

                int from = map.LineStartAt(line);
                int to = map.LineEndAt(line);

                return map.PointAt(Mathf.Clamp(
                    from + Mathf.RoundToInt((to - from) * fraction), from, to - 1));
            }

            return map.PlanCentre;
        }

        private static Vector2 Centroid(WorldMap map, int area)
        {
            int from = map.AreaStartAt(area);
            int to = map.AreaEndAt(area);

            var sum = Vector2.zero;
            for (int p = from; p < to; p++)
            {
                sum += map.AreaPointAt(p);
            }

            return to > from ? sum / (to - from) : sum;
        }

        private static string Safe(string name)
        {
            var text = new System.Text.StringBuilder(name.Length);

            for (int i = 0; i < name.Length; i++)
            {
                text.Append(char.IsLetterOrDigit(name[i]) ? name[i] : '_');
            }

            return text.ToString();
        }

        /// <summary>
        /// One picture.
        ///
        /// <para>A world-space canvas rather than a screen-space one: a screen-space canvas takes its
        /// size from whatever the game view happens to be, and a preview whose framing depends on the
        /// window it was taken from is a preview two people cannot compare.</para>
        /// </summary>
        private static void Capture(
            WorldMap map, int width, int height, Vector2 centre, float metresPerUnit, float heading,
            bool labels, string filePath)
        {
            var cameraObject = new GameObject("MapPreviewCamera");
            var canvasObject = new GameObject("MapPreviewCanvas", typeof(RectTransform));

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f, 1f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.enabled = false;
                cameraObject.transform.position = new Vector3(0f, 0f, -20f);

                Canvas canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;

                var canvasRect = (RectTransform)canvasObject.transform;
                canvasRect.position = Vector3.zero;
                canvasRect.sizeDelta = new Vector2(width, height);

                var viewObject = new GameObject("View", typeof(RectTransform));
                viewObject.transform.SetParent(canvasRect, false);

                var view = (RectTransform)viewObject.transform;
                view.anchorMin = new Vector2(0.5f, 0.5f);
                view.anchorMax = new Vector2(0.5f, 0.5f);
                view.pivot = new Vector2(0.5f, 0.5f);
                view.sizeDelta = new Vector2(width, height);
                view.anchoredPosition = Vector2.zero;

                MapGraphic graphic = viewObject.AddComponent<MapGraphic>();
                graphic.SetMap(map);
                graphic.SetView(centre, metresPerUnit, heading);

                if (labels)
                {
                    AddLabels(map, graphic, view);
                }

                // The canvas has not been through an update since any of the above, and a camera render
                // does not run one. Without this the picture is an empty rectangle.
                Canvas.ForceUpdateCanvases();

                // What it drew, not merely that it ran. An empty frame is the one result this tool
                // cannot tell apart from a correct one by looking at the file size.
                Debug.Log($"[Horizon] {Path.GetFileName(filePath)}: {graphic.LastVertexCount} vertices, "
                          + $"{graphic.LastSegmentCount} segments, {graphic.LastAreaCount} areas at "
                          + $"{metresPerUnit:0.00} m/unit.");

                Shoot(camera, width, height, filePath);
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        /// <summary>
        /// The town and place names, so a picture says whether they landed where they belong.
        ///
        /// <para>Built here rather than borrowed from <c>MapScreen</c>, which owns a pool sized for a
        /// phone. This is a picture to be read at 1400 pixels; the two want different numbers of
        /// them.</para>
        /// </summary>
        private static void AddLabels(WorldMap map, MapGraphic graphic, RectTransform parent)
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            for (int area = 0; area < map.AreaCount; area++)
            {
                if (map.AreaKindOf(area) == MapAreaKind.Town)
                {
                    Write(font, parent, map.AreaNameOf(area),
                        graphic.LocalPointOf(Centroid(map, area)), 22, Color.white);
                }
            }

            for (int i = 0; i < map.MarkerCount; i++)
            {
                if (map.MarkerKindOf(i) != MapMarkerKind.Place)
                {
                    continue;
                }

                Write(font, parent, map.MarkerNameOf(i),
                    graphic.LocalPointOf(map.MarkerAt(i)) + new Vector2(0f, 14f), 17,
                    new Color(1f, 0.86f, 0.7f, 0.95f));
            }
        }

        private static void Write(
            Font font, RectTransform parent, string caption, Vector2 at, int fontSize, Color colour)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(340f, 30f);
            rect.anchoredPosition = at;

            Text text = go.AddComponent<Text>();
            text.text = caption;
            text.font = font;
            text.fontSize = fontSize;
            text.color = colour;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
        }

        /// <summary>
        /// Internal so the HUD preview shoots its frame exactly the way this one does.
        ///
        /// <para><b>Post is off, and that is the point rather than an omission.</b> Both this and the HUD
        /// preview photograph a canvas, and the game's canvas is <c>ScreenSpaceOverlay</c> — URP
        /// composites it after the post stack, so no tone map and no bloom ever touch it. A preview that
        /// ran post would be showing a HUD the player does not have.</para>
        /// </summary>
        /// <param name="msaa">
        /// Samples. One turns multisampling off, which the HUD shot needs.
        /// </param>
        internal static void Shoot(Camera camera, int width, int height, string filePath, int msaa = 4) =>
            PreviewCapture.Shoot(
                camera, width, height, filePath, msaa, post: false, fog: false, stencil: true);
    }
}
