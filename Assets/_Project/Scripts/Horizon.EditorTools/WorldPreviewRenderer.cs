using System.IO;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Renders the pass from the driver's eye at a few points up the climb, plus one look down on the whole
    /// thing, to PNGs beside the project folder.
    ///
    /// The same reasoning as <see cref="CarPreviewRenderer"/>, applied to the world: judging scenery means
    /// looking at it, and hunting for the same camera angle in the scene view every time is how you end up
    /// not checking. It is also the only way to see the things that are invisible from above — plants
    /// floating off a slope, a bare corridor, a bore with a forest growing through it.
    ///
    /// The sample points are spread over the length of the course on purpose: the whole point of the tree
    /// line is that the bottom and the top should not look the same.
    /// </summary>
    public static class WorldPreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";
        private const int Width = 1280;
        private const int Height = 720;

        /// <summary>Where along the course to stand, as fractions of its length.</summary>
        private static readonly float[] Stations = { 0.06f, 0.30f, 0.55f, 0.78f, 0.95f };

        [MenuItem("Tools/Horizon/Render World Preview", priority = 41)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            var path = Object.FindFirstObjectByType<RoadPath>();
            if (path == null)
            {
                Debug.LogError("[Horizon] No RoadPath in the world scene. Run Rebuild Prototype Scene first.");
                return;
            }

            var cameraObject = new GameObject("WorldPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 900f;
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;
                float length = path.Length;

                for (int i = 0; i < Stations.Length; i++)
                {
                    float distance = length * Stations[i];
                    Vector3 position = path.GetPositionAtDistance(distance);
                    Vector3 forward = path.GetDirectionAtDistance(distance);

                    // Behind and above the road, looking along it and a little down — roughly where the
                    // chase camera sits, so what this shows is what the player will see.
                    camera.transform.position = position - forward * 9f + Vector3.up * 4f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        (forward + Vector3.down * 0.14f).normalized, Vector3.up);

                    Capture(camera, Path.Combine(directory, $"WorldPreview_{i + 1}_at{distance:0}m.png"));
                }

                // Every covered stretch twice: the approach a driver actually sees, and side-on, which is
                // the only view that shows how much of the hillside the massif is and whether its skirt
                // meets the ground or stops on a rim in mid-air.
                // Rebuilt rather than read off the scene: the course is deterministic and the scene's
                // RoadPath was generated from this same call, so the two cannot disagree unless someone
                // hand-edits the path — which the project's conventions say not to do.
                RoadCourse course = MountainPassCourse.Build();
                int covered = 0;

                for (int i = 0; i < course.Features.Count; i++)
                {
                    RoadFeature feature = course.Features[i];
                    if (feature.Kind != RoadFeatureKind.Tunnel && feature.Kind != RoadFeatureKind.Gallery)
                    {
                        continue;
                    }

                    covered++;
                    string label = $"{covered}_{feature.Name}";

                    float approachAt = Mathf.Max(0f, feature.StartDistance - 80f);
                    Vector3 approach = path.GetPositionAtDistance(approachAt);
                    Vector3 approachForward = path.GetDirectionAtDistance(approachAt);

                    camera.fieldOfView = 60f;
                    camera.transform.position = approach - approachForward * 8f + Vector3.up * 3.5f;
                    camera.transform.rotation = Quaternion.LookRotation(approachForward, Vector3.up);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Portal{label}_Approach.png"));

                    // From the side, far enough out to get the whole body in, and lifted so the skirt is
                    // visible rather than hidden behind its own bulge. Fog off — at 190 m it would grey out
                    // exactly the detail this shot exists to show.
                    float middleAt = (feature.StartDistance + feature.EndDistance) * 0.5f;
                    Vector3 middle = path.GetPositionAtDistance(middleAt);
                    Vector3 side = path.GetRightAtDistance(middleAt);

                    bool sideFog = RenderSettings.fog;
                    RenderSettings.fog = false;

                    try
                    {
                        Vector3 station = middle + side * 190f;

                        // Lifted off whatever is actually underneath rather than off the road. A fixed
                        // height put the gallery camera inside the hillside, and a camera inside terrain
                        // sees its backfaces — which renders as an empty brown field, not as an error.
                        float ground = station.y;
                        if (Physics.Raycast(station + Vector3.up * 600f, Vector3.down,
                                out RaycastHit ground_hit, 1200f))
                        {
                            ground = ground_hit.point.y;
                        }

                        camera.fieldOfView = 50f;
                        camera.transform.position = new Vector3(
                            station.x, Mathf.Max(middle.y + 70f, ground + 55f), station.z);
                        camera.transform.rotation = Quaternion.LookRotation(
                            middle - camera.transform.position, Vector3.up);
                        Capture(camera, Path.Combine(directory, $"WorldPreview_Portal{label}_Side.png"));
                    }
                    finally
                    {
                        RenderSettings.fog = sideFog;
                    }
                }

                // Through the village, from the driver's eye and then from above it. The first says whether
                // it reads as somewhere people live; the second whether the plots hang together as a place
                // rather than as houses dropped on a field.
                Vector3 villageAt = path.GetPositionAtDistance(length * 0.035f);
                Vector3 villageForward = path.GetDirectionAtDistance(length * 0.035f);
                camera.fieldOfView = 60f;
                camera.transform.position = villageAt - villageForward * 12f + Vector3.up * 3.5f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (villageForward + Vector3.down * 0.08f).normalized, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Village_Street.png"));

                Vector3 overAt = path.GetPositionAtDistance(length * 0.033f);
                Vector3 overRight = path.GetRightAtDistance(length * 0.033f);
                camera.fieldOfView = 55f;
                camera.transform.position = overAt - overRight * 130f + Vector3.up * 85f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (overAt - camera.transform.position), Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Village_Above.png"));

                // Down onto one frontage from the far verge. This is the shot that shows how close the
                // garden boundaries actually come to the asphalt, which no view along the road reveals.
                Vector3 plotAt = path.GetPositionAtDistance(length * 0.03f);
                Vector3 plotRight = path.GetRightAtDistance(length * 0.03f);
                camera.fieldOfView = 50f;
                camera.transform.position = plotAt + plotRight * 34f + Vector3.up * 17f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (plotAt - plotRight * 24f) - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Village_Plot.png"));

                // A close look across the verge at the roadside, which is the one angle that exposes plants
                // hovering off the ground or buried in it. Straight down the road hides it completely.
                // 0.88, not the middle of the course: the gallery sits around two thirds of the way along,
                // and a close-up of its pillars says nothing about how plants meet the ground.
                float inspectAt = length * 0.88f;
                Vector3 inspectPoint = path.GetPositionAtDistance(inspectAt);
                Vector3 inspectRight = path.GetRightAtDistance(inspectAt);
                camera.fieldOfView = 42f;
                camera.transform.position = inspectPoint + inspectRight * 34f + Vector3.up * 9f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (inspectPoint + Vector3.up * 1f) - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Verge.png"));

                // Obliquely from above, not straight down: a plan view of this terrain is a single flat
                // colour, because every top face catches the sun at the same angle and nothing casts a
                // silhouette. The tilt is what makes the tree line and the clearings visible at all.
                //
                // Fog has to come off for this one. It is tuned to hide the draw distance from a car, which
                // means the far wall sits a few hundred metres out — from a kilometre up, the entire pass is
                // behind it and the render is one flat orange field.
                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    Bounds bounds = WorldBounds();
                    float span = Mathf.Max(bounds.size.x, bounds.size.z);
                    camera.fieldOfView = 45f;
                    camera.farClipPlane = span * 5f;
                    camera.transform.position = bounds.center
                                                + new Vector3(0.7f, 0.75f, -0.7f).normalized * span * 1.35f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        bounds.center - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Overview.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }

                Debug.Log($"[Horizon] World preview written to {directory}/WorldPreview_*.png");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>Everything the world renders, so the overview frames the pass rather than the origin.</summary>
        private static Bounds WorldBounds()
        {
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one * 100f);
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private static void Capture(Camera camera, string filePath)
        {
            RenderTexture target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
            target.antiAliasing = 4;

            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);

            try
            {
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(filePath, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
