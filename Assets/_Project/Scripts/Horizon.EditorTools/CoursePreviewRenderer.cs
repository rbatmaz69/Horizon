using System.IO;
using Horizon.World;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Renders the whole pass to PNGs beside the project: a plan view and an elevation.
    ///
    /// Driving is the only way to judge whether a pass is *fun*, but it is a poor way to judge whether
    /// the layout is right — you cannot see a switchback stack from inside it. The plan view shows
    /// whether the hairpins stack sensibly and rotate around the mountain; the elevation shows the
    /// climb and descent as one shape.
    /// </summary>
    public static class CoursePreviewRenderer
    {
        private const int Width = 1100;
        private const int Height = 750;

        [MenuItem("Tools/Horizon/Render Course Overview", priority = 41)]
        public static void RenderFromOpenScene()
        {
            RoadPath path = Object.FindFirstObjectByType<RoadPath>();
            if (path == null)
            {
                Debug.LogWarning("[Horizon] No RoadPath in the open scenes. Open World_MountainPass first.");
                return;
            }

            Render(path);
        }

        /// <summary>Renders both views for a path that is present in the active scene.</summary>
        public static void Render(RoadPath path)
        {
            if (path == null || path.Length < 1f)
            {
                return;
            }

            ComputeBounds(path, out Bounds bounds);

            var cameraObject = new GameObject("CoursePreviewCamera");
            var lightObject = new GameObject("CoursePreviewLight");

            try
            {
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightObject.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.13f, 0.14f, 0.17f);
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;

                RenderPlanView(camera, bounds, Path.Combine(directory, "CoursePreview_Plan.png"));
                RenderElevationView(camera, bounds, Path.Combine(directory, "CoursePreview_Elevation.png"));
                RenderOnRoadViews(camera, path, directory);

                Debug.Log($"[Horizon] Course overview written to {directory}/CoursePreview_*.png");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(lightObject);
            }
        }

        private static void ComputeBounds(RoadPath path, out Bounds bounds)
        {
            float length = path.Length;
            bounds = new Bounds(path.GetPositionAtDistance(0f), Vector3.zero);

            for (float distance = 0f; distance < length; distance += 10f)
            {
                bounds.Encapsulate(path.GetPositionAtDistance(distance));
            }

            bounds.Encapsulate(path.GetPositionAtDistance(length));

            // Breathing room so the road never touches the frame edge.
            bounds.Expand(new Vector3(90f, 40f, 90f));
        }

        private static void RenderPlanView(Camera camera, Bounds bounds, string filePath)
        {
            camera.transform.position = new Vector3(bounds.center.x, bounds.max.y + 900f, bounds.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Vertical on screen is world Z when looking straight down with the default up vector.
            float aspect = Width / (float)Height;
            camera.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / aspect);
            camera.farClipPlane = 2500f;

            Capture(camera, filePath);
        }

        private static void RenderElevationView(Camera camera, Bounds bounds, string filePath)
        {
            camera.transform.position = new Vector3(bounds.max.x + 900f, bounds.center.y, bounds.center.z);
            camera.transform.rotation = Quaternion.Euler(0f, -90f, 0f);

            float aspect = Width / (float)Height;
            camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.z / aspect);
            camera.farClipPlane = 3000f;

            Capture(camera, filePath);
        }

        /// <summary>
        /// Two views from just above the carriageway: one on an open leg, one in the tightest hairpin.
        ///
        /// This is the only way to judge the markings — line width, dash rhythm, whether the centre line
        /// really goes solid in the corner, and whether the camber catches the light. None of that shows
        /// in a plan view from 900 m up.
        /// </summary>
        private static void RenderOnRoadViews(Camera camera, RoadPath path, string directory)
        {
            FindSampleDistances(path, out float straightAt, out float hairpinAt);

            bool wasOrthographic = camera.orthographic;
            camera.orthographic = false;
            camera.fieldOfView = 55f;
            camera.farClipPlane = 800f;

            try
            {
                RenderAlongRoad(camera, path, straightAt,
                    Path.Combine(directory, "CoursePreview_RoadStraight.png"));
                RenderAlongRoad(camera, path, hairpinAt,
                    Path.Combine(directory, "CoursePreview_RoadHairpin.png"));
            }
            finally
            {
                camera.orthographic = wasOrthographic;
            }
        }

        /// <summary>Picks an open stretch and the tightest corner on the course.</summary>
        private static void FindSampleDistances(RoadPath path, out float straightAt, out float hairpinAt)
        {
            float length = path.Length;
            straightAt = length * 0.05f;
            hairpinAt = length * 0.5f;

            float tightest = float.MaxValue;
            float openest = 0f;

            for (float distance = 20f; distance < length - 40f; distance += 5f)
            {
                float radius = path.GetRadiusAtDistance(distance);

                if (radius < tightest)
                {
                    tightest = radius;
                    hairpinAt = distance;
                }

                // Prefer an open stretch far enough in that the terrain around it is interesting.
                if (radius > 400f && distance > openest)
                {
                    openest = distance;
                    straightAt = distance;
                }
            }
        }

        private static void RenderAlongRoad(Camera camera, RoadPath path, float distance, string filePath)
        {
            Vector3 position = path.GetPositionAtDistance(distance);
            Vector3 forward = path.GetDirectionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            // In the right-hand lane, at about eye height in a car, aimed slightly down the road.
            camera.transform.position = position + right * 2.2f + Vector3.up * 2.0f;
            camera.transform.rotation = Quaternion.LookRotation(
                (forward * 6f - Vector3.up * 0.9f).normalized, Vector3.up);

            Capture(camera, filePath);
        }

        /// <summary>
        /// Fog off for the duration. An overview camera sits hundreds of metres back, and the scene's
        /// exponential-squared fog turns the entire frame into flat fog colour at that range — the first
        /// version of this tool produced a solid orange rectangle and nothing else. Post stays on: this
        /// is still a picture of the world.
        /// </summary>
        private static void Capture(Camera camera, string filePath) =>
            PreviewCapture.Shoot(camera, Width, Height, filePath, fog: false);
    }
}
