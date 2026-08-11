using System.IO;
using Horizon.Atmosphere;
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

            RoadPath path = FindTrunkRoad();
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

                // Through the town, from the driver's eye and then from above it. The first says whether
                // it reads as somewhere people live; the second whether the plots hang together as a place
                // rather than as houses dropped on a field.
                //
                // Absolute distances taken from the course, not fractions of its length: the town's
                // position is a published number and the course grew by three quarters of a kilometre in
                // front of it, which silently moved every fraction-based station somewhere else.
                float townStart = MountainPassCourse.TownStartDistance;
                float townEnd = MountainPassCourse.TownEndDistance;
                float townMiddle = (townStart + townEnd) * 0.5f;

                Vector3 townAt = path.GetPositionAtDistance(townMiddle);
                Vector3 townForward = path.GetDirectionAtDistance(townMiddle);
                camera.fieldOfView = 60f;
                camera.transform.position = townAt - townForward * 12f + Vector3.up * 3.5f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (townForward + Vector3.down * 0.08f).normalized, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Street.png"));

                Vector3 overAt = path.GetPositionAtDistance(townMiddle);
                Vector3 overRight = path.GetRightAtDistance(townMiddle);
                camera.fieldOfView = 55f;
                camera.transform.position = overAt - overRight * 130f + Vector3.up * 85f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (overAt - camera.transform.position), Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Above.png"));

                // Down onto one frontage from the far verge. This is the shot that shows how close the
                // garden boundaries actually come to the asphalt, which no view along the road reveals.
                Vector3 plotAt = path.GetPositionAtDistance(townStart + 90f);
                Vector3 plotRight = path.GetRightAtDistance(townStart + 90f);
                camera.fieldOfView = 50f;
                camera.transform.position = plotAt + plotRight * 34f + Vector3.up * 17f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (plotAt - plotRight * 24f) - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Plot.png"));

                // The arrival: standing on the road half a kilometre out, looking at the town you are
                // about to drive into. The whole reason the approach was lengthened is that a place should
                // be seen before it is entered, and this is the only shot that shows whether it is.
                float arrivalAt = Mathf.Max(0f, townStart - 330f);
                Vector3 arrival = path.GetPositionAtDistance(arrivalAt);
                Vector3 arrivalForward = path.GetDirectionAtDistance(arrivalAt);
                camera.fieldOfView = 55f;
                camera.transform.position = arrival - arrivalForward * 10f + Vector3.up * 4.5f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (arrivalForward + Vector3.down * 0.05f).normalized, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Arrival.png"));

                // Straight down over the town, fog off. This is the shot that answers whether the layout
                // table is a town or spaghetti, and the one to iterate the table against — nothing at eye
                // level tells you whether the blocks hang together.
                Vector3 gridAt = path.GetPositionAtDistance(townMiddle);
                Vector3 gridRight = path.GetRightAtDistance(townMiddle);

                bool gridFog = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 60f;
                    camera.transform.position = gridAt - gridRight * 120f + Vector3.up * 420f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Town_Grid.png"));
                }
                finally
                {
                    RenderSettings.fog = gridFog;
                }

                // The worst junction in the network — the one with the tightest angle between its
                // streets, marked in the scene by PrototypeSetup so this does not have to re-derive it.
                // Aiming a camera at the junction most likely to be wrong beats aiming it at a
                // representative one.
                GameObject worst = GameObject.Find("TownWorstJunction");
                if (worst != null)
                {
                    camera.fieldOfView = 55f;
                    camera.transform.position = worst.transform.position + new Vector3(0f, 26f, -18f);
                    camera.transform.rotation = Quaternion.LookRotation(
                        worst.transform.position - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Town_Junction.png"));
                }

                // The square from a standing eye inside it, not from above. A market place is a room, and
                // the only question about it — whether the buildings round it read as walls or as a row
                // of houses that happen to face the same way — cannot be asked from a helicopter.
                // Station and aim both come from the scene marker, which is the only thing that knows
                // which edge came out uphill.
                CaptureFromMarker(camera, "TownSquareView", 65f,
                    Path.Combine(directory, "WorldPreview_Town_Square.png"));

                // The mosque from the street, close enough that the two-stage spire, the dome and the
                // porch are separate things rather than one blob.
                GameObject landmark = GameObject.Find("TownLandmark");
                if (landmark != null)
                {
                    Vector3 at = landmark.transform.position;
                    camera.fieldOfView = 50f;
                    camera.transform.position = at + new Vector3(-46f, 12f, -38f);
                    camera.transform.rotation = Quaternion.LookRotation(
                        (at + Vector3.up * 14f) - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Town_Mosque.png"));
                }

                CaptureFromViewpoint(camera, path, directory);

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

        /// <summary>
        /// Hour to hold the clock at for the night shots.
        ///
        /// Not just "after sunset". <c>TimeOfDayProfile</c>'s sun intensity reaches zero at about 18.7 h,
        /// but its ambient and fog gradients only arrive at night by midnight — so half past nine gives a
        /// sunless sky over sunset-coloured fog, and the fogged shots come back washed pink with the town
        /// barely in them. Eleven is late enough that the whole palette has turned over.
        /// </summary>
        private const float NightHours = 23f;

        /// <summary>
        /// The town after dark.
        ///
        /// <para>A deliverable of the lighting work rather than a nicety, because until this existed there
        /// was <b>no way to render the world at night at all</b> — the lit glass, the two thresholds, the
        /// lamp pools and the always-lit minaret were four pieces of machinery none of which could be
        /// looked at. Anything that cannot be seen is not finished.</para>
        ///
        /// <para>Its own command rather than three more shots on the day pass: it moves the clock, and a
        /// preview run that leaves the scene at half past nine at night would be a surprising thing for a
        /// rebuild to do. The clock is put back in a finally block either way.</para>
        ///
        /// <para>There is deliberately no square shot yet. The market square is a node type the layout
        /// table does not use, and a file called <c>Town_Night_Square</c> containing a street corner is
        /// worse than no file; it arrives with the square.</para>
        /// </summary>
        [MenuItem("Tools/Horizon/Render World Preview (Night)", priority = 42)]
        public static void RenderNight()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath path = FindTrunkRoad();
            if (path == null)
            {
                Debug.LogError("[Horizon] No RoadPath in the world scene. Run Rebuild Prototype Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            if (clock == null)
            {
                Debug.LogError("[Horizon] No TimeOfDayController in the world scene, so there is no night "
                               + "to render. Run Rebuild Prototype Scene first.");
                return;
            }

            if (lights == null)
            {
                Debug.LogWarning("[Horizon] No TownLights in the world scene. The night shots will come "
                                 + "out with every window dark, which is the component not being wired "
                                 + "rather than the town being asleep.");
            }

            float hoursWere = clock.TimeOfDayHours;
            bool runningWas = clock.Running;

            var cameraObject = new GameObject("WorldNightPreviewCamera");

            try
            {
                clock.Running = false;
                clock.TimeOfDayHours = NightHours;
                clock.Apply();

                // The clock and the capture happen in the same frame with no Update in between, which is
                // exactly what Refresh is for — without it the town is photographed in its daylight
                // materials under a night sky, and that reads as broken lighting rather than a missing
                // call.
                if (lights != null)
                {
                    lights.Refresh();
                    Debug.Log($"[Horizon] Night: windows lit {lights.IsGroupLit(LitGroup.Windows)}, "
                              + $"lamps lit {lights.IsGroupLit(LitGroup.Lamps)}, sun intensity "
                              + $"{(RenderSettings.sun != null ? RenderSettings.sun.intensity : 0f):0.00}. "
                              + "Both false means the thresholds on TownLights never fired.");
                }

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 900f;
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;

                float townStart = MountainPassCourse.TownStartDistance;
                float townEnd = MountainPassCourse.TownEndDistance;
                float townMiddle = (townStart + townEnd) * 0.5f;

                // Down the street from the driver's eye. The one shot that says whether the pools of
                // light on the carriageway read as light rather than as grey hexagons.
                Vector3 at = path.GetPositionAtDistance(townMiddle);
                Vector3 forward = path.GetDirectionAtDistance(townMiddle);
                camera.fieldOfView = 60f;
                camera.transform.position = at - forward * 12f + Vector3.up * 3.5f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (forward + Vector3.down * 0.08f).normalized, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Night_Street.png"));

                // From above, which is where the lit-window fraction becomes visible as a pattern: a town
                // rolled at a flat half looks like static, and one lit per quarter has structure.
                Vector3 right = path.GetRightAtDistance(townMiddle);
                camera.fieldOfView = 55f;
                camera.transform.position = at - right * 130f + Vector3.up * 85f;
                camera.transform.rotation = Quaternion.LookRotation(at - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Night_Above.png"));

                // The square after dark: the stalls' awnings, the fountain, and whether the town hall's
                // clock still reads when everything around it has gone to two flat colours.
                CaptureFromMarker(camera, "TownSquareView", 65f,
                    Path.Combine(directory, "WorldPreview_Town_Night_Square.png"));

                CaptureNightFromViewpoint(camera, path, directory, townMiddle);

                Debug.Log($"[Horizon] Night preview written to {directory}/WorldPreview_Town_Night_*.png "
                          + $"at {NightHours:0.0}h.");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                clock.TimeOfDayHours = hoursWere;
                clock.Running = runningWas;
                clock.Apply();

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>
        /// The town at night from the pass above — the shot the whole lighting stage exists for.
        ///
        /// A minaret whose openings are always lit is supposed to be what makes the place readable as a
        /// town from a kilometre up the mountain. Either it is, in this frame, or the claim was wrong.
        /// Fog off for the same reason the daylight version turns it off: it is tuned to hide a 600 m draw
        /// distance from a car, and this camera is well past that.
        /// </summary>
        private static void CaptureNightFromViewpoint(
            Camera camera, RoadPath path, string directory, float townMiddle)
        {
            RoadCourse course = MountainPassCourse.Build();

            float viewpointAt = -1f;
            for (int i = 0; i < course.Features.Count; i++)
            {
                if (course.Features[i].Kind == RoadFeatureKind.Viewpoint
                    && course.Features[i].Name == "Talblick")
                {
                    viewpointAt = course.Features[i].StartDistance;
                    break;
                }
            }

            if (viewpointAt < 0f)
            {
                return;
            }

            Vector3 from = path.GetPositionAtDistance(Mathf.Min(viewpointAt, path.Length));
            Vector3 to = path.GetPositionAtDistance(townMiddle);

            bool fogWasOn = RenderSettings.fog;
            float farWas = camera.farClipPlane;
            RenderSettings.fog = false;

            try
            {
                camera.fieldOfView = 38f;
                camera.farClipPlane = Mathf.Max(farWas, Vector3.Distance(from, to) * 2.5f);
                camera.transform.position = from + Vector3.up * 30f;
                camera.transform.rotation = Quaternion.LookRotation(to - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_Night_FromThePass.png"));
            }
            finally
            {
                RenderSettings.fog = fogWasOn;
                camera.farClipPlane = farWas;
            }
        }

        /// <summary>
        /// The pass itself, out of however many <see cref="RoadPath"/> components the world scene holds.
        ///
        /// It is the longest by an order of magnitude — five kilometres against a couple of hundred metres
        /// of town street — and that is a far safer test than the first one the scene happens to return.
        /// Taking the first put every station on a village lane 176 m long: the "climb" previews were five
        /// shots of the same field, and nothing said so, because a foggy render of a field looks exactly
        /// like a foggy render of a field.
        /// </summary>
        private static RoadPath FindTrunkRoad()
        {
            RoadPath[] paths = Object.FindObjectsByType<RoadPath>(FindObjectsSortMode.None);

            RoadPath longest = null;
            for (int i = 0; i < paths.Length; i++)
            {
                if (longest == null || paths[i].Length > longest.Length)
                {
                    longest = paths[i];
                }
            }

            return longest;
        }

        /// <summary>
        /// The town seen from the pass above — from the <c>Talblick</c> viewpoint, which is the one place
        /// on the course the layout was designed to be read from.
        ///
        /// This is the acceptance shot for the basin: whether it reads as a bowl with the ground rising
        /// around it, or as a flat table cut into a hillside. Fog comes off, because the viewpoint is
        /// well over a kilometre from the town and the fog is tuned to hide the draw distance from a car —
        /// with it on, the answer is a wall of orange either way.
        /// </summary>
        private static void CaptureFromViewpoint(Camera camera, RoadPath path, string directory)
        {
            RoadCourse course = MountainPassCourse.Build();

            float viewpointAt = -1f;
            for (int i = 0; i < course.Features.Count; i++)
            {
                if (course.Features[i].Kind == RoadFeatureKind.Viewpoint
                    && course.Features[i].Name == "Talblick")
                {
                    viewpointAt = course.Features[i].StartDistance;
                    break;
                }
            }

            if (viewpointAt < 0f)
            {
                return;
            }

            float townMiddle = (MountainPassCourse.TownStartDistance
                                + MountainPassCourse.TownEndDistance) * 0.5f;

            Vector3 from = path.GetPositionAtDistance(Mathf.Min(viewpointAt, path.Length));
            Vector3 to = path.GetPositionAtDistance(townMiddle);

            bool fogWasOn = RenderSettings.fog;
            float farWas = camera.farClipPlane;
            RenderSettings.fog = false;

            try
            {
                // Lifted well above the viewpoint and narrowed, which is a compromise worth naming: from a
                // driver's eye at the kerb the near carriageway fills the lower half of the frame and the
                // town is forty pixels of it. This is a shot about the shape of the ground, so it is taken
                // from where the ground can be seen; the version at eye level belongs with the landmarks,
                // once there is a minaret in the frame to look at.
                camera.fieldOfView = 38f;
                camera.farClipPlane = Mathf.Max(farWas, Vector3.Distance(from, to) * 2.5f);
                camera.transform.position = from + Vector3.up * 30f;
                camera.transform.rotation = Quaternion.LookRotation(to - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Town_FromThePass.png"));
            }
            finally
            {
                RenderSettings.fog = fogWasOn;
                camera.farClipPlane = farWas;
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

        /// <summary>
        /// Takes a shot from a marker the build placed, using both its position and its rotation.
        ///
        /// The marker carries the aim as well as the station because the thing worth looking at is often
        /// decided by geometry only the builder has — which edge of a square came out uphill, which
        /// junction came out worst. A renderer that re-derived those would be a second opinion about them.
        /// </summary>
        private static void CaptureFromMarker(
            Camera camera, string markerName, float fieldOfView, string filePath)
        {
            GameObject marker = GameObject.Find(markerName);
            if (marker == null)
            {
                return;
            }

            camera.fieldOfView = fieldOfView;
            camera.transform.SetPositionAndRotation(
                marker.transform.position, marker.transform.rotation);
            Capture(camera, filePath);
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
