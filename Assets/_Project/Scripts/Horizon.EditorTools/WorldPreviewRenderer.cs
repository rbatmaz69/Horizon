using System.Collections.Generic;
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

                // Down a town street with an ambient car in it. The routes are checked in numbers by
                // Validate Traffic Routes; this is the shot that says whether a car baked onto its lane
                // sits on the road the way a car sits on a road.
                CaptureFromMarker(camera, "TrafficView", 55f,
                    Path.Combine(directory, "WorldPreview_Town_Traffic.png"));

                // A city junction from the stop line: the mast and head, the bar the traffic stops at,
                // the crossing beyond it and the lane lines going solid on the approach. All of that is
                // paint on a merged mesh, so a shot is the only thing that says it landed on the road
                // rather than under it.
                // The lenses do not tick outside Play mode, so they are told to evaluate once here.
                // Without it every signal in every shot is a head with three dark lenses, which is what
                // a broken material swap also looks like.
                Object.FindFirstObjectByType<TrafficSignals>()?.Refresh();

                CaptureFromMarker(camera, "SignalView", 42f,
                    Path.Combine(directory, "WorldPreview_City_Signal.png"));

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

                // --- Seeburg. Its own stations, because every other settlement has some and the newest
                // one is the one nobody has looked at. Found by name rather than rebuilt: the axis is a
                // RoadPath in the scene like the pass and the arterial.
                GameObject axisObject = GameObject.Find("SeeburgAxis");
                RoadPath seeburgAxis = axisObject != null
                    ? axisObject.GetComponent<RoadPath>()
                    : null;

                if (seeburgAxis != null)
                {
                    // Along the front from the old-town end, which is the view the promenade is for — and
                    // the one that shows whether the rail runs along the kerb or across the junctions.
                    float frontFrom = SeeburgCourse.CityStart + 60f;
                    Vector3 frontAt = seeburgAxis.GetPositionAtDistance(frontFrom);
                    Vector3 frontForward = seeburgAxis.GetDirectionAtDistance(frontFrom);
                    Vector3 frontRight = seeburgAxis.GetRightAtDistance(frontFrom);

                    camera.fieldOfView = 60f;
                    camera.farClipPlane = 900f;
                    camera.transform.position = frontAt - frontRight * 9f + Vector3.up * 3.5f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        (frontForward + Vector3.down * 0.05f).normalized, Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Seeburg_Promenade.png"));

                    // Across the basin at the moles and the light, from the quay.
                    Vector3 quayAt = seeburgAxis.GetPositionAtDistance(SeeburgCourse.BasinAlong);
                    Vector3 seawardAt = -seeburgAxis.GetRightAtDistance(SeeburgCourse.BasinAlong);

                    camera.transform.position = quayAt - seawardAt * 6f + Vector3.up * 6f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        (seawardAt + Vector3.down * 0.06f).normalized, Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Seeburg_Harbour.png"));

                    // The mosque, from the water side of the boulevard, so it is seen the way the town
                    // sees it rather than from its own back garden.
                    Vector3 mosqueAt = seeburgAxis.GetPositionAtDistance(515f);
                    Vector3 mosqueSeaward = -seeburgAxis.GetRightAtDistance(515f);

                    camera.fieldOfView = 55f;
                    camera.transform.position = mosqueAt + mosqueSeaward * 55f + Vector3.up * 8f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        (mosqueAt - mosqueSeaward * 48f + Vector3.up * 22f) - camera.transform.position,
                        Vector3.up);
                    Capture(camera, Path.Combine(directory, "WorldPreview_Seeburg_Mosque.png"));

                    // And straight down over the waterfront with the fog off — the shot that answers
                    // whether the rail, the quay and the junction pads agree with one another, which
                    // nothing at eye level shows.
                    Vector3 planAt = seeburgAxis.GetPositionAtDistance(
                        (SeeburgCourse.CityStart + SeeburgCourse.CityEnd) * 0.5f);

                    bool seeburgFog = RenderSettings.fog;
                    RenderSettings.fog = false;

                    try
                    {
                        camera.fieldOfView = 60f;
                        camera.transform.position = planAt + Vector3.up * 430f;
                        camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                        Capture(camera, Path.Combine(directory, "WorldPreview_Seeburg_Plan.png"));
                    }
                    finally
                    {
                        RenderSettings.fog = seeburgFog;
                    }
                }

                CaptureEbental(camera, directory);

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

                // The one shot that says whether ambient traffic has lights at all. It has no Light
                // components by design, so its lamps are two material swaps and nothing else — and a
                // swap that never fired looks exactly like a car parked in the dark.
                CaptureFromMarker(camera, "TrafficView", 55f,
                    Path.Combine(directory, "WorldPreview_Town_Night_Traffic.png"));

                // And the signal after dark, which is the only test of the lens materials there is: the
                // three lenses are one mesh with a material swapped per phase, so a swap that never
                // fired looks exactly like a head with no bulbs in it.
                // The lenses do not tick outside Play mode, so they are told to evaluate once here.
                // Without it every signal in every shot is a head with three dark lenses, which is what
                // a broken material swap also looks like.
                Object.FindFirstObjectByType<TrafficSignals>()?.Refresh();

                CaptureFromMarker(camera, "SignalView", 42f,
                    Path.Combine(directory, "WorldPreview_City_Night_Signal.png"));

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
        /// <summary>
        /// The Ebental's own stations: along the road, out of the avenue, and the two that answer
        /// whether the region reads as its own place.
        ///
        /// <para>Silently does nothing before the country road exists, so this can be merged ahead of
        /// the landscape it exists to photograph.</para>
        /// </summary>
        private static void CaptureEbental(Camera camera, string directory)
        {
            RoadPath road = FindEbentalRoad();
            if (road == null)
            {
                return;
            }

            float length = road.Length;

            for (int i = 0; i < Stations.Length; i++)
            {
                float distance = length * Stations[i];
                Vector3 position = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.transform.position = position - forward * 9f + Vector3.up * 4f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (forward + Vector3.down * 0.14f).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Ebental_{i + 1}_at{distance:0}m.png"));
            }

            // Rebuilt rather than read off the scene, the same way the pass's viewpoint shot does it: the
            // course is deterministic and the scene's RoadPath came from this same call.
            RoadCourse course = EbentalCourse.Build();

            float crestAt = -1f;
            for (int i = 0; i < course.Features.Count; i++)
            {
                if (course.Features[i].Kind == RoadFeatureKind.Viewpoint
                    && course.Features[i].Name == "Hochwiese")
                {
                    crestAt = Mathf.Min(course.Features[i].StartDistance, length);
                    break;
                }
            }

            if (crestAt < 0f)
            {
                return;
            }

            // Out of the avenue, in the direction of travel, on the long rising straight. This is the
            // shot the poplars exist for: a row of them only works if it draws the eye down the road,
            // and no view from above can answer that.
            float avenueAt = length * 0.33f;
            Vector3 avenue = road.GetPositionAtDistance(avenueAt);
            Vector3 avenueForward = road.GetDirectionAtDistance(avenueAt);

            camera.fieldOfView = 55f;
            camera.transform.position = avenue - avenueForward * 11f + Vector3.up * 2.6f;
            camera.transform.rotation = Quaternion.LookRotation(
                (avenueForward + Vector3.down * 0.05f).normalized, Vector3.up);
            Capture(camera, Path.Combine(directory, "WorldPreview_Ebental_Avenue.png"));

            // From the crest back down the valley it just climbed out of, fog off. The acceptance shot
            // for the whole region: at 900 m the individual trees are gone and what is left is field
            // colour and the line of the avenue, which is exactly what has to carry the place.
            Vector3 from = road.GetPositionAtDistance(crestAt);
            Vector3 to = road.GetPositionAtDistance(Mathf.Max(0f, crestAt - 900f));

            bool fogWasOn = RenderSettings.fog;
            float farWas = camera.farClipPlane;
            RenderSettings.fog = false;

            try
            {
                camera.fieldOfView = 48f;
                camera.farClipPlane = Mathf.Max(farWas, Vector3.Distance(from, to) * 3f);
                camera.transform.position = from + Vector3.up * 40f;
                camera.transform.rotation = Quaternion.LookRotation(
                    to - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, "WorldPreview_Ebental_FromTheCrest.png"));

                // And straight down over the middle of the region. A plan view of *relief* is a flat
                // colour and worth nothing — see the overview below — but the fields here are colour
                // rather than relief, so this is the one place a plan view is the right instrument.
                Vector3 planAt = road.GetPositionAtDistance(length * 0.5f);

                camera.fieldOfView = 60f;
                camera.transform.position = planAt + Vector3.up * 620f;
                camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                Capture(camera, Path.Combine(directory, "WorldPreview_Ebental_Plan.png"));
            }
            finally
            {
                RenderSettings.fog = fogWasOn;
                camera.farClipPlane = farWas;
            }
        }

        /// <summary>
        /// The pass, by the name <c>PrototypeSetup</c> gives its GameObject.
        ///
        /// <para><b>By name, and it has to be.</b> This used to take the longest <see cref="RoadPath"/>
        /// in the scene, which was the pass for exactly as long as the pass was the only road. The
        /// motorway is 8,515 m against its 5,990, so every shot below has been standing on the motorway
        /// since the day that was built — including the ones that seat a camera at
        /// <c>MountainPassCourse.TownStartDistance</c>, a distance that means nothing on it. A picture
        /// of the wrong road is worse than no picture, because nobody checks a photograph's caption.</para>
        /// </summary>
        private static RoadPath FindTrunkRoad()
        {
            return FindRoad("RoadPath") ?? LongestRoad();
        }

        /// <summary>The country road out of the Ebental, or null before it has been built.</summary>
        private static RoadPath FindEbentalRoad()
        {
            return FindRoad("EbentalRoadPath");
        }

        /// <summary>The road over the Kalkgrat, or null before it has been built.</summary>
        private static RoadPath FindKalkgratRoad()
        {
            return FindRoad("KalkgratRoadPath");
        }

        /// <summary>The coast road along the Meerenge and over it.</summary>
        private static RoadPath FindMeerengeRoad()
        {
            return FindRoad("MeerengeRoadPath");
        }

        /// <summary>The road round the eastern cape to Yalıköy and up into the hills behind it.</summary>
        private static RoadPath FindYalikoyRoad()
        {
            return FindRoad("YalikoyRoadPath");
        }

        /// <summary>The road from Hochstadt back up to the Ebental, or null before it has been built.</summary>
        private static RoadPath FindStadtfeldRoad()
        {
            return FindRoad("StadtfeldRoadPath");
        }

        /// <summary>The road up to the Weissjoch, or null before it has been built.</summary>
        private static RoadPath FindWeissjochRoad()
        {
            return FindRoad("WeissjochRoadPath");
        }

        private static RoadPath FindRoad(string objectName)
        {
            RoadPath[] paths = Object.FindObjectsByType<RoadPath>(FindObjectsSortMode.None);

            for (int i = 0; i < paths.Length; i++)
            {
                if (paths[i].name == objectName)
                {
                    return paths[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The fallback: whatever road is longest. Only reached in a scene whose objects are not the
        /// ones the rebuild tool makes, where any road at all beats an error.
        /// </summary>
        private static RoadPath LongestRoad()
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

        /// <summary>
        /// Every body of water, twice: from above it and from its own bank.
        ///
        /// <para>Its own command because water is the one thing in this world that cannot be judged from
        /// the road. The rivers are under viaducts the driver crosses at 130 km/h, the tarn is off the
        /// pass entirely, and the two questions worth asking about any of them — does the surface float
        /// over a hole it does not cover, and does the shore meet the ground or stop in mid-air — are
        /// invisible from every station the day pass already shoots.</para>
        ///
        /// <para>The bodies are found by walking the scene for the surfaces themselves rather than by
        /// rebuilding <c>WaterShape</c>: a surface mesh only exists where the tile builder actually laid
        /// one, so what this frames is what shipped, not what was planned. Tiles are clustered because a
        /// body spans several of them and one shot per tile would be sixteen pictures of the same river.</para>
        /// </summary>
        /// <summary>
        /// Every filling station, photographed from the road that serves it — once in daylight and once
        /// at night.
        ///
        /// <para><b>The night pass is the half that matters.</b> A station's canopy soffit, shop glazing
        /// and sign face are registered with <c>TownLights</c>, which swaps their material after dusk.
        /// Nothing in the build reports that wiring being wrong: get the lit submesh index from its
        /// constant instead of from what <c>ToMesh</c> kept and the sign is simply the wrong colour
        /// after dark and unremarkable before it. This is the only place that failure is visible.</para>
        ///
        /// <para>The camera is put on the road side by finding the nearest point on any carriageway in
        /// the scene and standing there. That is the view being designed for — a driver arriving — and
        /// not an architectural elevation of a thing nobody sees from that angle.</para>
        /// </summary>
        /// <summary>
        /// The Kalkgrat, the Steilufer and the crossing, day and night.
        ///
        /// <para><b>Nine framings, and the first one is the whole feature.</b> Everything on that leg is
        /// arranged so that the sea, the coast and the towers arrive together in the frame at the exit
        /// portal of the Kalkgrattunnel. If that shot does not read, the span is wrong or the fog is —
        /// and no number in the build log can tell the difference, because both produce a structure that
        /// measures perfectly and cannot be seen.</para>
        ///
        /// <para>The rest are the questions a suspension bridge raises that a viaduct does not: does the
        /// deck hold both towers at once, does a tower read as standing <i>in</i> water rather than on
        /// it, do the hangers reach the parapet, and does the silhouette hold from the far shore. The
        /// profile shot turns the fog off, because it is taken from further away than any driver ever
        /// stands and with it on the answer is a wall of orange either way.</para>
        ///
        /// <para>The night pass is not decoration. The beacons and the cable beads are the one thing in
        /// this structure on an always-bright material, and the failure they are guarding against — a
        /// lit part that comes out painted road-asphalt in daylight — shows in the <i>day</i> shots, not
        /// the night ones. Both passes have to be looked at together.</para>
        /// </summary>
        [MenuItem("Tools/Horizon/Render Strait Preview", priority = 45)]
        public static void RenderStrait()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath kalkgrat = FindKalkgratRoad();
            RoadPath meerenge = FindMeerengeRoad();

            if (kalkgrat == null || meerenge == null)
            {
                Debug.LogError("[Horizon] No Kalkgrat or Meerenge road in the world scene. Run Rebuild "
                               + "Prototype Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("StraitPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        // Stopped and applied in the same frame as the capture: no Update runs in edit
                        // mode, so a clock left running is a clock that never ticks.
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureStrait(camera, kalkgrat, meerenge, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Strait preview written beside the project. Look for: the descent "
                      + "falling away in _Portal; a gallery with an underside in _Gallery; water out of "
                      + "the right-hand window in _Corniche, which is the shot this leg lives or dies "
                      + "by; the towers arriving over the trees in _Approach; both of them in one frame "
                      + "from _Deck; a tower standing in the strait rather than beside it in _Tower; "
                      + "hangers that reach the parapet in _Profile; the full width of the road between "
                      + "the anchor blocks in _Entrance and _Exit; something holding the deck up in "
                      + "_SideSpan; and in the day shots, nothing on the structure painted the colour "
                      + "of the road.");
        }

        /// <summary>
        /// Anadolu: the cape, the bay, the village and the hills over it, day and night.
        ///
        /// <para>Its own menu item rather than more shots on the strait's, for the reason that one is
        /// separate from the world preview: a leg gets a tool when the questions it raises are its own.
        /// The strait's are about a structure — is there water under it, is the shape of it readable.
        /// These are about a place: does the bay arrive when it is meant to, is the village on its water
        /// rather than beside it, and does the harbour read as a harbour from the road.</para>
        /// </summary>
        [MenuItem("Tools/Horizon/Render Anadolu Preview", priority = 46)]
        public static void RenderAnadolu()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath yalikoy = FindYalikoyRoad();

            if (yalikoy == null)
            {
                Debug.LogError("[Horizon] No Yalıköy road in the world scene. Run Rebuild Prototype "
                               + "Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("AnadoluPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureAnadolu(camera, yalikoy, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Anadolu preview written beside the project. Look for: dry hills and no "
                      + "water at all in _Cape; the bay arriving whole on the corner in _Bay; the village "
                      + "ahead rather than beside you in _Arrival; moles, a light and boats out of the "
                      + "right-hand window in _Harbour; a square with buildings facing it in _Square; the "
                      + "road turning back on itself in _Hairpins; the harbour still readable through "
                      + "the driver's own fog in _Lookback; and in the night shots, a lit quay and a "
                      + "lighthouse rather than a dark bay with a road in it.");
        }

        private static void CaptureAnadolu(
            Camera camera, RoadPath yalikoy, string directory, string suffix)
        {
            RoadCourse course = YalikoyCourse.Build();

            // Yaw, which the strait's shots do not have and this leg needs.
            //
            // <b>Because the thing being checked is beside the road rather than down it.</b> A harbour
            // 160 m off the driver's left is 90° from a heading a 60° lens is pointing along: the first
            // pass of this tool came back with a picture of a seafront that had no harbour in it, and
            // the harbour was there all along. Positive turns towards the passenger's window.
            void FromRoad(float at, float back, float lift, float pitch, float yaw, string name)
            {
                float distance = Mathf.Clamp(at, 0f, yalikoy.Length);
                Vector3 on = yalikoy.GetPositionAtDistance(distance);
                Vector3 forward = yalikoy.GetDirectionAtDistance(distance);

                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Anadolu_{name}{suffix}.png"));
            }

            // 1. Coming up on the cape bore. Nothing but dry hillside — the strait is behind the left
            // shoulder by now and the bay has not arrived, and both of those are the point.
            FromRoad(FeatureStart(course, RoadFeatureKind.Tunnel, YalikoyCourse.CapeName) - 90f,
                9f, 2.4f, 0f, 0f, "1_Cape");

            // 2. The corner out of the bore, which is where the water arrives. If this shot has no sea
            // in it the whole leg is a lane through scrub — the failure the corniche had twice.
            FromRoad(ViewpointOn(course, YalikoyCourse.BayViewName), 9f, 2.4f, -0.02f, -22f, "2_Bay");

            // 3. The arrival, from the last corner before the front. The village has to be ahead rather
            // than beside you: it is the reason for the bridge, and a place you are already in when you
            // notice it is a place you drove past.
            FromRoad(YalikoyCourse.CityStart - 120f, 9f, 2.4f, 0f, 0f, "3_Arrival");

            // 4. On the seafront at the harbour, water out of the right-hand window.
            FromRoad(YalikoyCourse.BasinAlong, 0f, 2.4f, -0.02f, -75f, "4_Harbour");

            // 5. The harbour from the water, low down and outside its own mouth — the one view that
            // answers whether the moles are arms or an atoll with a nick in it. Seaward is the road's
            // left, so the camera stands out along it.
            {
                float at = Mathf.Clamp(YalikoyCourse.BasinAlong, 0f, yalikoy.Length);
                Vector3 on = yalikoy.GetPositionAtDistance(at);
                Vector3 seaward = -yalikoy.GetRightAtDistance(at);
                Vector3 quay = on + seaward * -YalikoyCourse.BasinAcross;

                camera.fieldOfView = 55f;
                camera.farClipPlane = 1200f;
                camera.transform.position = quay + seaward * (YalikoyCourse.BasinRadius + 90f)
                                            + Vector3.up * 14f;
                camera.transform.rotation = Quaternion.LookRotation(
                    quay - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, $"WorldPreview_Anadolu_5_Mole{suffix}.png"));
            }

            // 6. The square, from the village street below it.
            FromRoad(YalikoyCourse.CityStart + 330f, 6f, 2.4f, 0.05f, 62f, "6_Square");

            // 7. Into the hairpins, which is where the road stops being a seafront.
            FromRoad(YalikoyCourse.CityEnd + 640f, 9f, 2.4f, 0.04f, 0f, "7_Hairpins");

            // 8. From the layby at the end of the front, back down the village. <b>Fog on</b>, unlike
            // every other long shot in this file, and that is the whole point of where the viewpoint
            // stands: if the harbour is not readable here through the fog the driver actually has, the
            // viewpoint is in the wrong place rather than the fog being wrong.
            FromRoad(ViewpointOn(course, YalikoyCourse.LookbackName), 0f, 2.4f, -0.02f, 196f,
                "8_Lookback");

            // 9. The whole leg from above, so the village, the bay and the shore can be read against one
            // another. Fog off, because this is taken from four times higher than any driver stands.
            {
                Vector3 front = yalikoy.GetPositionAtDistance(
                    Mathf.Clamp(YalikoyCourse.Waterfront, 0f, yalikoy.Length));

                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 60f;
                    camera.farClipPlane = 4000f;
                    camera.transform.position = front + Vector3.up * 900f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Anadolu_9_Above{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }
        }

        [MenuItem("Tools/Horizon/Render Stadtfeld Preview", priority = 47)]
        public static void RenderStadtfeld()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath stadtfeld = FindStadtfeldRoad();
            RoadPath ebental = FindEbentalRoad();

            if (stadtfeld == null || ebental == null)
            {
                Debug.LogError("[Horizon] No Stadtfeld road or no Ebental road in the world scene. Run "
                               + "Rebuild Prototype Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("StadtfeldPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureStadtfeld(camera, stadtfeld, ebental, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Stadtfeld preview written beside the project. Look for: a boulevard "
                      + "that becomes a country road without a step or a change of width in _Gate; a "
                      + "corner you cannot see the exit of in _Crest, because if you can, the profile is "
                      + "the whole design and it is not working; a mouth that reads as a choice rather "
                      + "than as a widening in _ForkFromEbental and _ForkFromBranch; asphalt that is "
                      + "continuous and flat across the throat in _ForkThroat rather than two surfaces "
                      + "at slightly different heights; and above all, no ridge standing between the two "
                      + "branches in _ForkPlan — that is MountainField averaging two shelves, it is what "
                      + "AutobahnCourse.MergeOffset records five metres of, and no other frame shows it.");
        }

        /// <summary>
        /// The road out of Hochstadt, its three crests, and both sides of the fork it ends at.
        ///
        /// <para>The course is rebuilt here rather than read off the scene, the way
        /// <see cref="CaptureStrait"/> does it: the distances below are the course's own, and a
        /// <c>RoadPath</c> only knows arc length along a smoothed spline.</para>
        /// </summary>
        private static void CaptureStadtfeld(
            Camera camera, RoadPath stadtfeld, RoadPath ebental, string directory, string suffix)
        {
            void FromRoad(RoadPath road, float at, float back, float lift, float pitch, float yaw,
                string name)
            {
                float distance = Mathf.Clamp(at, 0f, road.Length);
                Vector3 on = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);

                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Stadtfeld_{name}{suffix}.png"));
            }

            float fork = EbentalCourse.ForkAlong;

            // 1. Sixty metres out of the city, looking back at the gate. The one frame that answers
            // whether a 13.5 m country ribbon meets a boulevard without a step or a jump in width — the
            // handover this leg exists to make, and the reason it is grafted onto the boulevard's last
            // node rather than onto the end of an axis nobody paves.
            FromRoad(stadtfeld, 60f, 0f, 2.4f, -0.02f, 180f, "1_Gate");

            // 2. Leaving, from the same place facing out. Open country should start immediately: this is
            // also where the LandRegion question is settled, because a road that came up on the pass's
            // grey rather than the Ebental's farmland is a road with no region on it.
            FromRoad(stadtfeld, 240f, 0f, 2.4f, 0f, 0f, "2_Leaving");

            // 3. On the first crest, 25 m above the city. The corner beyond it must not be in frame; if
            // it is, the profile is doing nothing and this road is a bend in a field.
            FromRoad(stadtfeld, 1170f, 0f, 2.4f, -0.01f, 0f, "3_Crest");

            // 4. In the deeper of the two hollows, looking up at the climb out. The opposite check: from
            // down here the road ahead should be readable all the way, because a dip that hides as much
            // as a crest is fog rather than shape.
            FromRoad(stadtfeld, 1609f, 0f, 2.4f, 0.02f, 0f, "4_Hollow");

            // 5. The last crest, with the fork 359 m ahead and three metres below. The junction has to be
            // in sight from here — it is the one place on this road where hiding what is coming would be
            // a fault rather than the feature.
            FromRoad(stadtfeld, 3307f, 0f, 2.4f, -0.02f, 0f, "5_LastCrest");

            // 6. Arriving at the mouth. Does the branch read as joining a road, or as running into one?
            //
            // Thirty metres and not the fifty-five it was first taken from. The corner paving reaches
            // about forty metres up the branch, so a camera outside that photographs two roads running
            // near each other with grass between — which is a true picture of a place that is not the
            // junction, and it cost a rebuild to work out that it was the camera and not the geometry.
            FromRoad(stadtfeld, stadtfeld.Length - 30f, 0f, 2.4f, -0.02f, 0f, "6_ForkFromBranch");

            // 7. The fork from the Ebental, driven the way most of the world reaches it — down off the
            // pass. A mouth that reads as a wide bit of verge here is a fork nobody will take.
            FromRoad(ebental, fork - 120f, 0f, 2.4f, 0f, 0f, "7_ForkFromEbental");

            // 8. And from the other side, coming back off the Kalkgrat. Yawed rather than driven,
            // because the Ebental has no lane running that way; what is being checked is the geometry,
            // not the traffic.
            FromRoad(ebental, fork + 120f, 0f, 2.4f, 0f, 180f, "8_ForkFromKalkgrat");

            // 9. The throat itself, low and close. Two roads' paving and a laid-on wedge in one frame:
            // if the surfaces fight for the depth buffer or step against one another, it is here.
            FromRoad(ebental, fork + 8f, 0f, 1.1f, -0.16f, 118f, "9_ForkThroat");

            // 10. The fork from directly above, fog off. <b>The acceptance shot for the whole change.</b>
            // MountainField derives the ground from the roads, so two carriageways diverging slowly are
            // two shelves being averaged into one — and what that produces is a ridge standing between
            // them. It builds without a word and no other frame in this file would show it.
            {
                Vector3 mouth = ebental.GetPositionAtDistance(Mathf.Clamp(fork, 0f, ebental.Length));

                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 60f;
                    camera.farClipPlane = 1200f;
                    camera.transform.position = mouth + Vector3.up * 260f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Stadtfeld_10_ForkPlan{suffix}.png"));

                    // 11. And the whole leg, so the ring can be read as one shape: the city at one end,
                    // the fork at the other, and the road curving clear of Hochstadt's corner between
                    // them rather than cutting across it.
                    Vector3 middle = stadtfeld.GetPositionAtDistance(stadtfeld.Length * 0.5f);

                    camera.farClipPlane = 6000f;
                    camera.transform.position = middle + Vector3.up * 2600f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Stadtfeld_11_Above{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }
        }

        [MenuItem("Tools/Horizon/Render Weissjoch Preview", priority = 47)]
        public static void RenderWeissjoch()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath weissjoch = FindWeissjochRoad();
            RoadPath motorway = FindRoad("MotorwayPath");

            if (weissjoch == null || motorway == null)
            {
                Debug.LogError("[Horizon] No Weissjoch road or no motorway in the world scene. Run "
                               + "Rebuild Prototype Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("WeissjochPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureWeissjoch(camera, weissjoch, motorway, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Weissjoch preview written beside the project. Look for: a mountain "
                      + "behind the exit in _Merge rather than a slip road into fog — that frame is the "
                      + "whole argument for which way the stack was turned; forest that ENDS in "
                      + "_TreeLine rather than thinning to nothing; snow that arrives as a line with "
                      + "drifts in it rather than as a painted contour in _SnowLine; bare rock on the "
                      + "steep flanks with snow lying either side of them, because uniform white above "
                      + "a height is a cake; and above all a mountainside rather than a staircase of "
                      + "flat shelves in _Face and _Above — MountainField builds the second one just as "
                      + "quietly as the first.");
        }

        /// <summary>
        /// The climb, its three altitude bands, and the two frames that say whether the mountain is a
        /// mountain.
        /// </summary>
        private static void CaptureWeissjoch(
            Camera camera, RoadPath weissjoch, RoadPath motorway, string directory, string suffix)
        {
            RoadCourse course = WeissjochCourse.Build();

            void FromRoad(RoadPath road, float at, float back, float lift, float pitch, float yaw,
                string name)
            {
                float distance = Mathf.Clamp(at, 0f, road.Length);
                Vector3 on = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);
                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Weissjoch_{name}{suffix}.png"));
            }

            // 1. From the westbound carriageway, a few hundred metres before the ramp. The question this
            // frame exists for: is there a mountain behind the exit? The stack was turned so its face
            // looks south at this road, and if that paid, it paid here and nowhere else.
            {
                float at = Mathf.Clamp(AutobahnCourse.WeissjochExitDistance - 380f, 0f, motorway.Length);
                Vector3 on = motorway.GetPositionAtDistance(at);
                Vector3 forward = motorway.GetDirectionAtDistance(at);
                Vector3 right = motorway.GetRightAtDistance(at);

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                // Yawed to the north, at the mountain, and not along the road. Looking forward down the
                // carriageway put the massif entirely off the left of the frame and came back as a
                // picture of flat forest — a true photograph of somewhere that is not the subject. What
                // this frame is for is whether a driver on the way to Seeburg can see what the exit
                // leads to, and that question is asked out of the side window.
                camera.transform.position = on - right * 10.5f + Vector3.up * 2.4f;
                camera.transform.rotation = Quaternion.LookRotation(
                    (Quaternion.Euler(0f, -70f, 0f) * forward + Vector3.up * 0.10f).normalized,
                    Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Weissjoch_1_Merge{suffix}.png"));
            }

            // 2. On the valley floor by the last pump, looking at what is to be climbed.
            FromRoad(weissjoch, 1457f, 0f, 2.4f, 0.08f, 0f, "2_Valley");

            // 3. Mid stage B, inside the forest and on the switchbacks.
            FromRoad(weissjoch, 5400f, 0f, 2.4f, 0.02f, 0f, "3_Forest");

            // 4. The tree line, from the Waldkanzel. The forest has to END here — if it thins to nothing
            // over half a kilometre the band is not landing where the numbers say it does, and the three
            // stages stop being three places.
            FromRoad(weissjoch, ViewpointOn(course, "Waldkanzel"), 0f, 2.4f, -0.04f, 150f, "4_TreeLine");

            // 5. The bore's mouth, on its own traverse between two hairpin groups.
            FromRoad(weissjoch,
                FeatureStart(course, RoadFeatureKind.Tunnel, "Graugrattunnel") - 70f,
                0f, 2.4f, 0.02f, 0f, "5_Tunnel");

            // 6. The snow line, at the top of the rock band.
            FromRoad(weissjoch, 9520f, 0f, 2.4f, 0.04f, 0f, "6_SnowLine");

            // 7. Out of the avalanche gallery at 663 m, which is the first structure in this world with
            // snow on both sides of it.
            FromRoad(weissjoch,
                FeatureStart(course, RoadFeatureKind.Gallery, "Lawinengalerie") + 90f,
                0f, 2.4f, 0f, 0f, "7_Gallery");

            // 8. Across the stack from a leg high in the snow, looking back down at the legs below.
            // <b>The acceptance shot for MountainField.</b> The ground between two stacked legs is an
            // inverse-fifth-power blend and it is meant to read as a face; the failure it hides is a
            // staircase of flat shelves, and that is only visible across the stack, never along it.
            {
                // Which way is downhill has to be measured, not guessed. The stack advances north, so the
                // legs below are always to the south — but a leg heads east or west depending on which
                // hairpin it came out of, and the first version of this frame guessed wrong and came
                // back as a photograph of the cutting on the uphill side, with one boulder in it.
                const float at = 11800f;
                Vector3 right = weissjoch.GetRightAtDistance(Mathf.Clamp(at, 0f, weissjoch.Length));
                float faceYaw = right.z > 0f ? -100f : 100f;

                // Pitched hard down, and the number comes from the stack rather than from taste: the
                // next leg below is 52 m away in plan and 26 m under, which is 27° — a shallower angle
                // photographs the horizon over the top of it, which is what the first two attempts did.
                FromRoad(weissjoch, at, 0f, 4f, -0.62f, faceYaw, "8_Face");
            }

            // 9. The col. The valley is nine hundred metres below and far outside the far plane, so what
            // has to be here is the road: the last hairpins under the parapet, and somewhere that reads
            // as arriving.
            FromRoad(weissjoch, ViewpointOn(course, WeissjochCourse.ColName), 0f, 2.4f, -0.06f, 168f,
                "9_Col");

            // 10. The whole massif from above, fog off. Is it a mountain or a ramp? This is also where a
            // wrong choice of stacking direction shows — the shoulders either side of the stack are flat
            // by construction, and from up here you can see which way the grain runs.
            {
                Vector3 middle = weissjoch.GetPositionAtDistance(weissjoch.Length * 0.6f);

                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 60f;
                    camera.farClipPlane = 9000f;
                    camera.transform.position = middle + Vector3.up * 2600f - Vector3.forward * 1800f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        (Vector3.down * 2f + Vector3.forward).normalized, Vector3.up);
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Weissjoch_10_Above{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }
        }

        private static void CaptureStrait(
            Camera camera, RoadPath kalkgrat, RoadPath meerenge, string directory, string suffix)
        {
            // Rebuilt rather than read off the scene, the way the Ebental's shots do it: the courses are
            // deterministic and the scene's paths came from these same calls.
            RoadCourse meerengeCourse = MeerengeCourse.Build();
            RoadCourse kalkgratCourse = KalkgratCourse.Build();

            // Standing on the carriageway looking sideways is the one view a driver never has, so every
            // road shot here is taken from behind and a little above the bonnet line — the same framing
            // TryRoadSide argues for at the filling stations.
            void FromRoad(RoadPath road, float at, float back, float lift, float pitch, string name)
            {
                float distance = Mathf.Clamp(at, 0f, road.Length);
                Vector3 on = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (forward + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_{name}{suffix}.png"));
            }

            // 1. Out of the Kalkgrattunnel, driver's eye, looking down the road.
            //
            // This shot was framed as the reveal of the strait, and it cannot be: the crossing is five
            // kilometres from here in a straight line against a 600 m far plane with a fog wall inside
            // it. Nothing in this world is ever revealed from more than about half a kilometre away.
            // What the portal actually opens onto is the top of the descent, which is what to look for.
            FromRoad(kalkgrat, KalkgratCourse.RevealDistance + 12f, 9f, 2.4f, -0.03f, "1_Portal");

            // 2. The gallery, from just short of it. The shot that catches a roof with no underside.
            float galleryAt = FeatureStart(kalkgratCourse, RoadFeatureKind.Gallery, "Klippengalerie");
            FromRoad(kalkgrat, galleryAt, 55f, 2.4f, 0f, "2_Gallery");

            // 3. The descent from above, so the hairpin stack can be read as a stack. Fog off: at two
            // kilometres it is tuned to hide the draw distance from a car, and with it on this shot
            // came back a wall of orange whatever the road underneath was doing.
            {
                Vector3 top = kalkgrat.GetPositionAtDistance(
                    Mathf.Min(KalkgratCourse.RevealDistance + 60f, kalkgrat.Length));
                Vector3 foot = kalkgrat.GetPositionAtDistance(kalkgrat.Length * 0.95f);

                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 55f;
                    camera.farClipPlane = 4000f;
                    camera.transform.position = top + Vector3.up * 260f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        foot - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_3_Descent{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }

            // 4. The corniche, at the bay. The water is on the right of this road for its whole length,
            // so this is the shot that says whether it is a coast road or a lane with a rumour of sea.
            FromRoad(meerenge, ViewpointOn(meerengeCourse, "Steilbucht") - 140f, 9f, 2.4f, -0.02f,
                "4_Corniche");

            // 5. The approach, from the last corner before the deck.
            FromRoad(meerenge, MeerengeCourse.CrossingStart - 260f, 9f, 2.4f, 0.02f, "5_Approach");

            // 6. On the deck, a third of the way over: near tower overhead, far tower ahead.
            FromRoad(meerenge, MeerengeCourse.CrossingStart + MeerengeCourse.StructureLength * 0.33f,
                9f, 2.4f, 0.06f, "6_Deck");

            // 7. The western tower from out on the water, low down. Whether a tower stands *in* the
            // strait or merely beside it is the one question no shot from the deck can answer — from up
            // there its own foot is directly below and out of frame, which is what the first attempt at
            // this framing came back as: a shaft and a lot of sky.
            {
                float at = MeerengeCourse.CrossingStart + MeerengeCourse.SideSpan;
                Vector3 tower = meerenge.GetPositionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 alongChannel = meerenge.GetRightAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 acrossChannel = meerenge.GetDirectionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));

                // Out along the water and a little towards mid-channel, so the shaft is seen against the
                // strait rather than against the bank it is nearest to.
                camera.fieldOfView = 55f;
                camera.farClipPlane = 1200f;
                camera.transform.position = tower
                                            + alongChannel * 330f
                                            + acrossChannel * 190f
                                            + Vector3.down * (tower.y - 12f);
                camera.transform.rotation = Quaternion.LookRotation(
                    tower - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_7_Tower{suffix}.png"));
            }

            // 8. From the far shore's viewpoint, back at the whole thing.
            {
                float at = ViewpointOn(meerengeCourse, "Köprü Bakışı");
                Vector3 on = meerenge.GetPositionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 middle = meerenge.GetPositionAtDistance(
                    Mathf.Clamp(MeerengeCourse.CrossingMiddle, 0f, meerenge.Length));

                camera.fieldOfView = 55f;
                camera.farClipPlane = 3000f;
                camera.transform.position = on + Vector3.up * 6f;
                camera.transform.rotation = Quaternion.LookRotation(
                    middle - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_8_FarShore{suffix}.png"));
            }

            // 11. The entrance, from where a driver meets it: forty metres back from the abutment, on
            // the carriageway, looking at the gap. The one thing no other shot on this list can show is
            // how much road there is between the anchor blocks — the structure used to leave 6.3 m of a
            // 13.5 m road, as two seven-metre concrete walls, and every picture here was taken from
            // somewhere that framing does not reach.
            FromRoad(meerenge, MeerengeCourse.CrossingStart - 40f, 9f, 2.4f, 0.01f, "11_Entrance");

            // 12. The exit, the same shot the other way round. Both ends, because the anchor blocks and
            // the towers are placed from opposite ends of the feature and a sign error shows on one of
            // them only.
            FromRoad(meerenge, MeerengeCourse.CrossingStart + MeerengeCourse.StructureLength - 60f,
                9f, 2.4f, 0.01f, "12_Exit");

            // 13. Under the western side span, from the bank, looking along it at the tower. A hundred
            // and fifty metres of deck that was carried by nothing at all: BridgeBuilder only ever took
            // RoadFeatureKind.Bridge, the hangers only ever hung between the towers, and MountainField
            // had taken the ground out from under the lot. From the deck it looks like a road.
            {
                float at = MeerengeCourse.CrossingStart + MeerengeCourse.SideSpan * 0.5f;
                Vector3 deck = meerenge.GetPositionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 across = meerenge.GetRightAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 along = meerenge.GetDirectionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));

                camera.fieldOfView = 55f;
                camera.farClipPlane = 900f;
                camera.transform.position = deck + across * 120f - along * 90f + Vector3.down * 34f;
                camera.transform.rotation = Quaternion.LookRotation(
                    deck + along * 120f - camera.transform.position, Vector3.up);
                Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_13_SideSpan{suffix}.png"));
            }

            // 9 and 10. The silhouette, square on from over the water, and the strait from above. Fog
            // off for both: they are taken from further out than any driver stands, and with it on the
            // answer is a wall of orange whatever the structure looks like.
            {
                float at = MeerengeCourse.CrossingMiddle;
                Vector3 middle = meerenge.GetPositionAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));
                Vector3 alongChannel = meerenge.GetRightAtDistance(Mathf.Clamp(at, 0f, meerenge.Length));

                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 42f;
                    camera.farClipPlane = 6000f;
                    camera.transform.position = middle + alongChannel * 1450f + Vector3.up * 30f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        middle - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_9_Profile{suffix}.png"));

                    camera.fieldOfView = 60f;
                    camera.transform.position = middle + Vector3.up * 1500f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Strait_10_Above{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }
        }

        /// <summary>Where a named feature of a kind begins, or zero if the course has no such thing.</summary>
        private static float FeatureStart(RoadCourse course, RoadFeatureKind kind, string name)
        {
            for (int i = 0; i < course.Features.Count; i++)
            {
                if (course.Features[i].Kind == kind && course.Features[i].Name == name)
                {
                    return course.Features[i].StartDistance;
                }
            }

            return 0f;
        }

        /// <summary>Where a named viewpoint stands on a course.</summary>
        private static float ViewpointOn(RoadCourse course, string name)
        {
            return FeatureStart(course, RoadFeatureKind.Viewpoint, name);
        }

        [MenuItem("Tools/Horizon/Render Weissjochring Preview", priority = 48)]
        public static void RenderWeissjochring()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath ring = FindRoad("WeissjochringPath");
            RoadPath access = FindRoad("WeissjochringAccessPath");

            if (ring == null || access == null)
            {
                Debug.LogError("[Horizon] No Weissjochring in the world scene. Run Rebuild Prototype "
                               + "Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("WeissjochringPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureWeissjochring(camera, ring, access, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Weissjochring preview written beside the project. The two frames that "
                      + "carry it: _2_Line, where the lap closes on itself — anything other than "
                      + "unbroken tarmac under the gantry is a closure that missed, and it is the "
                      + "fastest point on the circuit; and _8_Infield, which looks across the middle of "
                      + "the ladder — terrain that stops in mid-air there is the hole the corridor width "
                      + "makes, and no other frame in this project would show it. Then: kerbs that read "
                      + "as kerbs rather than as stripes in _7_Gratkehre; forest that ENDS where 700 m "
                      + "says it does between _5_TreeLine and _6_Kessel, with snow still lying under it; "
                      + "a mountainside rather than a staircase of flat shelves in _9_Face. The pit "
                      + "mouth is no longer this tool's job — _3_Fork here has never been able to frame "
                      + "it, and Render Junction Preview photographs every mouth in the world from "
                      + "three sides and from above.");
        }

        /// <summary>
        /// The circuit, its three altitude bands, and the two frames that say whether it is a circuit at
        /// all.
        /// </summary>
        private static void CaptureWeissjochring(
            Camera camera, RoadPath ring, RoadPath access, string directory, string suffix)
        {
            RoadCourse course = WeissjochringCourse.Build();

            void FromRoad(RoadPath road, float at, float back, float lift, float pitch, float yaw,
                string name)
            {
                float distance = road.IsLoop
                    ? Mathf.Repeat(at, road.Length)
                    : Mathf.Clamp(at, 0f, road.Length);

                Vector3 on = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);
                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Ring_{name}{suffix}.png"));
            }

            float lap = ring.Length;

            // 1. Coming down the access road. The question: does the place arrive? A circuit reached by
            // a lane that simply stops at it is a wide road with a gantry over it.
            FromRoad(access, access.Length - 190f, 0f, 2.4f, -0.04f, 0f, "1_Approach");

            // 2. THE acceptance frame. Standing 130 m short of the line on the main straight, looking
            // through it. The lap closes at distance zero, so this frame contains the seam — and a
            // closure that missed by a metre, or arrived a few degrees off, shows here and in no build
            // log anywhere.
            FromRoad(ring, WeissjochringCourse.LineDistance - 130f, 0f, 2.0f, 0.02f, 0f, "2_Line");

            // 2b. The grid, from behind the back row and low. Its own frame because everything painted
            // on a road is invisible from anywhere else: the start line is 0.9 m of white seen edge-on
            // and a grid box is two 16 cm rails, so at any ordinary camera height and distance they are
            // a pixel each. This is also the only frame that says whether the boxes stagger.
            FromRoad(ring, WeissjochringCourse.LineDistance - 108f, 0f, 1.1f, -0.05f, 0f, "2b_Grid");

            // 3. The pit mouth, from the straight and pointing at it. Yawed 55° off the road rather
            // than 26: at 26 the camera looked past the mouth and down the forecourt frontage, so the
            // frame came back as a photograph of the filling station and the one question it exists for
            // — is there a ridge standing between the branch and the trunk — went unanswered for a while.
            FromRoad(ring, WeissjochringCourse.LineDistance - 480f, 0f, 2.4f, -0.06f, 55f, "3_Fork");

            // 4. Off the top of the descent, looking down and across at the rung below. This is the
            // frame the whole ladder shape exists for: if the circuit reads as flat, it reads as flat
            // here.
            FromRoad(ring, lap * 0.20f, 0f, 3.0f, -0.10f, 62f, "4_Descent");

            // 5. Through the tree line at 700 m. The wood has to END rather than thin away over half a
            // kilometre — if it thins, the band is not landing where the table says it is.
            FromRoad(ring, lap * 0.36f, 0f, 2.4f, -0.02f, 0f, "5_TreeLine");

            // 6. The bottom, from the layby there. Under the tree line and above the snow line for most
            // of it: dark spruce standing on white ground is the one picture a winter region is for.
            FromRoad(ring, ViewpointOn(course, WeissjochringCourse.BottomName), 0f, 2.4f, 0f, -30f,
                "6_Kessel");

            // 7. The Gratkehre, from just before it. The kerbs are in every frame on this circuit, and
            // this is the one where they are the subject.
            FromRoad(ring, ViewpointOn(course, WeissjochringCourse.SummitName) - 60f, 0f, 1.6f, -0.05f,
                0f, "7_Gratkehre");

            // 8. THE other acceptance frame. From the climb out, looking square across the middle of the
            // ladder. Terrain exists only 200 m from a road, so a lap folded too loosely has a hole in
            // the middle of it — and a hole in an infield is near no road at all, which is exactly why
            // every other check in this build is blind to it.
            FromRoad(ring, lap * 0.78f, 0f, 3.0f, -0.06f, 90f, "8_Infield");

            // 9. The face between two stacked rungs, seen along it. MountainField will build a
            // staircase of flat shelves just as quietly as it builds a mountainside, and the difference
            // is not in any number the build prints.
            FromRoad(ring, lap * 0.83f, 0f, 4.0f, -0.14f, 118f, "9_Face");
        }

        [MenuItem("Tools/Horizon/Render Bahçe Ring Preview", priority = 49)]
        public static void RenderBahceRing()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath ring = FindRoad("BahceRingPath");
            RoadPath access = FindRoad("BahceRingAccessPath");

            if (ring == null || access == null)
            {
                Debug.LogError("[Horizon] No Bahçe Ring in the world scene. Run Rebuild Prototype "
                               + "Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("BahceRingPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    CaptureBahceRing(camera, ring, access, directory, suffix);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Bahçe Ring preview written beside the project. The three frames that "
                      + "carry it: _2_Line, which contains the closure seam at the fastest point on the "
                      + "lap — anything but unbroken tarmac under the gantry is a closure that missed, "
                      + "and it appears in no log; _7_Infield, looking square across the middle of the "
                      + "loop, where ground that stops in mid-air is the corridor hole this layout was "
                      + "measured against; and _5_Blossom, the only check anywhere on whether a valley "
                      + "meant to be in flower reads as one. Then: the grid staggered rather than "
                      + "abreast and its paint actually visible in _2b_Grid; a corner that keeps "
                      + "turning for a third of a kilometre in _6_Turn8; and orchards rather than scrub "
                      + "either side of _8_Back. The pit mouth belongs to Render Junction Preview now, "
                      + "which frames it from the branch, from the track both ways and from above — "
                      + "_3_Fork here has only ever been aimed at the throat.");
        }

        /// <summary>
        /// Istanbul Park in an orchard valley, and the frames that say whether it is either of those
        /// things.
        /// </summary>
        private static void CaptureBahceRing(
            Camera camera, RoadPath ring, RoadPath access, string directory, string suffix)
        {
            RoadCourse course = BahceRingCourse.Build();

            void FromRoad(RoadPath road, float at, float back, float lift, float pitch, float yaw,
                string name)
            {
                float distance = road.IsLoop
                    ? Mathf.Repeat(at, road.Length)
                    : Mathf.Clamp(at, 0f, road.Length);

                Vector3 on = road.GetPositionAtDistance(distance);
                Vector3 forward = road.GetDirectionAtDistance(distance);
                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on - forward * back + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                Capture(camera, Path.Combine(directory, $"WorldPreview_Bahce_{name}{suffix}.png"));
            }

            float lap = ring.Length;
            float line = BahceRingCourse.LineDistance;

            // 1. Coming down the access road. Does the place arrive, and does it arrive through
            // blossom rather than through Anadolu's scrub carried two kilometres too far?
            FromRoad(access, access.Length - 200f, 0f, 2.4f, -0.04f, 0f, "1_Approach");

            // 2. THE acceptance frame. On the main straight, looking through the line — the lap closes
            // at distance zero, so the seam is in this picture and in no log anywhere.
            FromRoad(ring, line - 130f, 0f, 2.0f, 0.02f, 0f, "2_Line");

            // 2b. The grid, from behind the back row and low. Everything painted on a road is invisible
            // from anywhere else, and the sign of the crown decides whether it is visible at all: laid
            // with it inverted the boxes sit eleven centimetres inside the tarmac, built, counted
            // correctly and completely unseen.
            FromRoad(ring, line - 108f, 0f, 1.1f, -0.05f, 0f, "2b_Grid");

            // 3. The pit mouth. Standing seventy metres short of it and yawed towards the throat rather
            // than along the road: the branch arrives from behind and to the right, so a camera pointed
            // down the straight photographs the forecourt instead — which is exactly what the
            // Weissjochring's own _3_Fork does, and why that one still cannot answer this question.
            FromRoad(ring, 30f, 0f, 2.4f, -0.05f, 45f, "3_Fork");

            // 3b/3c. The mouth itself, from the two lines that actually cross it: a driver arriving
            // down the access road, and a driver on the circuit looking back at where it came in.
            FromRoad(access, access.Length - 55f, 0f, 2.2f, -0.10f, 0f, "3b_Mouth");
            FromRoad(ring, 175f, 0f, 3.0f, -0.14f, 180f, "3c_MouthBack");

            // 3d. The mouth in plan, which is the only frame that shows the whole of it at once. A
            // junction is the one piece of road whose fault is a shape rather than a step, and a shape
            // is not readable from inside it.
            {
                Vector3 mouth = BahceRingCourse.JunctionPoint;

                // Fog off for this one. It is a plan of a shape rather than a view of a place, and two
                // hundred metres of the world's own haze turns a junction into a smudge — which is what
                // the first two attempts at this frame came back as.
                bool fogWasOn = RenderSettings.fog;
                RenderSettings.fog = false;

                try
                {
                    camera.fieldOfView = 60f;
                    camera.farClipPlane = 900f;
                    camera.nearClipPlane = 0.3f;
                    camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);

                    camera.transform.position = mouth + Vector3.up * 220f;
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Bahce_3d_MouthPlan{suffix}.png"));

                    // And close, because the fillets live in the last sixty metres and at two hundred
                    // they are three pixels of tone against the verge.
                    camera.transform.position = mouth + Vector3.up * 110f;
                    Capture(camera,
                        Path.Combine(directory, $"WorldPreview_Bahce_3e_MouthClose{suffix}.png"));
                }
                finally
                {
                    RenderSettings.fog = fogWasOn;
                }
            }

            // 4. Turn 1, from its entry: the fastest corner on the lap and the start of the descent
            // into the valley. If the profile is doing anything, it is doing it here.
            FromRoad(ring, line + 60f, 0f, 2.2f, -0.03f, 0f, "4_Turn1");

            // 5. The blossom, from the layby in it. The one frame that asks whether "in flower" reads
            // as anything at all — a tint table and a mesh both build perfectly while looking like a
            // slightly odd wood.
            FromRoad(access, ViewpointOn(BahceRingCourse.BuildAccess(), BahceRingCourse.GroveName),
                0f, 2.4f, 0f, -35f, "5_Blossom");

            // 6. The eighth corner, entered. It is 330 m of continuous left; a frame taken here should
            // still be turning at the far edge of it, and if it is not, the fit has straightened the
            // one corner this circuit is known for.
            FromRoad(ring, ViewpointOn(course, BahceRingCourse.Turn8Name) - 30f, 0f, 2.2f, -0.02f, 0f,
                "6_Turn8");

            // 7. THE other acceptance frame. Square across the middle of the loop from the back
            // straight. The infield is on the left of travel everywhere — the lap runs anti-clockwise —
            // so a fixed −90° yaw looks into it wherever it is taken.
            FromRoad(ring, ViewpointOn(course, BahceRingCourse.SlowName) - 420f, 0f, 3.0f, -0.05f, -90f,
                "7_Infield");

            // 8. Down the back straight, the longest on the lap and the only place with enough distance
            // to see what the valley is planted with rather than what is beside the kerb.
            FromRoad(ring, ViewpointOn(course, BahceRingCourse.SlowName) - 640f, 0f, 2.4f, -0.01f, 0f,
                "8_Back");

            // 9. The first sector gate, from sixty metres short of it and low. Its own frame because
            // the whole of a gate is two 0.5 m bands of paint seen nearly edge-on, and paint is the one
            // thing in this project that builds, counts and validates perfectly while being invisible —
            // see CircuitMeshes.AddStripe. A rule the player cannot see reads as the game being broken,
            // so this is the frame that says the rule exists.
            // Close and high, looking down: 0.5 m of white across a 13 m road is a couple of pixels
            // from any ordinary driving pose, so a frame taken from one answers nothing.
            FromRoad(ring, line + lap / 7f - 24f, 0f, 3.4f, -0.34f, 0f, "9_Gate");
        }

        /// <summary>
        /// Every place two roads meet, from the driver's seat and from above.
        ///
        /// <para><b>Why this exists.</b> This world is fourteen courses, four towns and two circuits, and
        /// the seams between them were the one thing nothing photographed and nothing measured. The three
        /// faults the player reported — a pit road lying across a racing line, a city with no way out onto
        /// the motorway, and carriageway standing in the air — are all seam faults, and every build that
        /// contained them was otherwise clean. The rule this project keeps relearning is that a number
        /// says a thing was built and only a picture says where.</para>
        ///
        /// <para>Three kinds of join, one shot set each: a <b>fork</b>, where a branch leaves a road that
        /// carries on; a <b>handover</b>, where one course ends and the next begins on its pose; and a
        /// <b>gate</b>, where a road runs into a town's street network. The plan frame is the one that
        /// carries them: a ridge between two shelves, paving on the wrong carriageway and a barrier
        /// standing across a mouth are all invisible at eye level and unmistakable from a hundred and
        /// sixty metres up.</para>
        /// </summary>
        [MenuItem("Tools/Horizon/Render Junction Preview", priority = 50)]
        public static void RenderJunctions()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath pass = FindTrunkRoad();
            RoadPath ebental = FindEbentalRoad();
            RoadPath stadtfeld = FindStadtfeldRoad();
            RoadPath kalkgrat = FindKalkgratRoad();
            RoadPath meerenge = FindMeerengeRoad();
            RoadPath yalikoy = FindYalikoyRoad();
            RoadPath weissjoch = FindWeissjochRoad();
            RoadPath ring = FindRoad("WeissjochringPath");
            RoadPath ringAccess = FindRoad("WeissjochringAccessPath");
            RoadPath bahce = FindRoad("BahceRingPath");
            RoadPath bahceAccess = FindRoad("BahceRingAccessPath");
            RoadPath motorway = FindRoad("MotorwayPath");
            RoadPath link = FindRoad("MotorwayLinkPath");
            RoadPath coast = FindRoad("CoastRoadPath");

            if (motorway == null || ebental == null || ring == null || bahce == null)
            {
                Debug.LogError("[Horizon] The world scene is missing roads this needs. Run Rebuild "
                               + "Prototype Scene first.");
                return;
            }

            var westbound = new OffsetRoadPath(motorway, -AutobahnCourse.CarriagewayOffset);
            var eastbound = new OffsetRoadPath(motorway, AutobahnCourse.CarriagewayOffset);

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("JunctionPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                for (int pass2 = 0; pass2 < 2; pass2++)
                {
                    bool night = pass2 == 1;
                    string suffix = night ? "_Night" : string.Empty;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    if (lights != null)
                    {
                        lights.Refresh();
                    }

                    // --- The three forks. A branch arrives, the road it joins carries on both ways.
                    Fork(camera, directory, suffix, "1_StadtfeldFork",
                        stadtfeld, ebental, EbentalCourse.ForkPoint);

                    Fork(camera, directory, suffix, "2_WeissjochringPit",
                        ringAccess, ring, WeissjochringCourse.JunctionPoint);

                    Fork(camera, directory, suffix, "3_BahceRingPit",
                        bahceAccess, bahce, BahceRingCourse.JunctionPoint);

                    // --- The motorway's two ends, which are the joins this whole pass was opened for.
                    // Taken on the carriageways rather than the median, because the median is a line
                    // nobody drives and the carriageways are where the barrier stood.
                    Handover(camera, directory, suffix, "4_TerminusWest",
                        westbound, 0f, -1f, coast, 0f, 1f);

                    Handover(camera, directory, suffix, "5_TerminusHochstadt",
                        eastbound, eastbound.Length, 1f, null, 0f, 1f);

                    // --- The motorway's on-ramp, the one join that always had a seam check.
                    Handover(camera, directory, suffix, "6_LinkMerge",
                        link, 0f, -1f, westbound, 0f, 1f);

                    // --- The chain of country courses. Each begins on the pose the last one ended at,
                    // and no build has ever measured what happens across that plane.
                    Handover(camera, directory, suffix, "7_PassToEbental",
                        pass, pass != null ? pass.Length : 0f, 1f, ebental, 0f, 1f);

                    Handover(camera, directory, suffix, "8_EbentalToKalkgrat",
                        ebental, ebental.Length, 1f, kalkgrat, 0f, 1f);

                    Handover(camera, directory, suffix, "9_KalkgratToMeerenge",
                        kalkgrat, kalkgrat != null ? kalkgrat.Length : 0f, 1f, meerenge, 0f, 1f);

                    Handover(camera, directory, suffix, "10_MeerengeToYalikoy",
                        meerenge, meerenge != null ? meerenge.Length : 0f, 1f, yalikoy, 0f, 1f);

                    Handover(camera, directory, suffix, "11_YalikoyToBahce",
                        yalikoy, yalikoy != null ? yalikoy.Length : 0f, 1f, bahceAccess, 0f, 1f);

                    Handover(camera, directory, suffix, "12_WeissjochToRing",
                        weissjoch, weissjoch != null ? weissjoch.Length : 0f, 1f, ringAccess, 0f, 1f);

                    // --- The town gates. Seeburg's is the coast road running into the waterfront; the
                    // other two towns are strung along a road that passes through them and are covered
                    // by their own previews.
                    Handover(camera, directory, suffix, "13_SeeburgGate",
                        coast, coast != null ? coast.Length : 0f, 1f, null, 0f, 1f);
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (lights != null)
                {
                    lights.Refresh();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Junction preview written beside the project. What to look for, in this "
                      + "order: in every _Plan, one continuous piece of asphalt with no ridge across it "
                      + "and nothing paved over the carriageway that carries on — a branch's ribbon and "
                      + "the throat it hands to both stop at the paved edge, so any tarmac past the "
                      + "centre line is the fault this pass exists for. In _Arrive, a mouth you can see "
                      + "the way through; in _Onward and _Back, a road that widens rather than one with "
                      + "a lane running into the side of it. And in the two terminus frames, no barrier "
                      + "and no step where the dual carriageway becomes one road.");
        }

        /// <summary>
        /// A fork: four frames. Down the branch at the mouth, the trunk from each direction, and the
        /// whole thing from above.
        /// </summary>
        private static void Fork(
            Camera camera, string directory, string suffix, string name,
            IRoadPath branch, IRoadPath trunk, Vector3 at)
        {
            if (branch == null || trunk == null)
            {
                Debug.LogWarning($"[Horizon] Junction preview: {name} has no road to stand on.");
                return;
            }

            // Which end of the branch is the mouth, by the same rule the builder uses — a branch grafted
            // onto a pose starts there and one solved into it finishes there.
            float toStart = Flat(branch.GetPositionAtDistance(0f) - at).sqrMagnitude;
            float toEnd = Flat(branch.GetPositionAtDistance(branch.Length) - at).sqrMagnitude;

            bool mouthAtStart = toStart <= toEnd;
            float branchAt = mouthAtStart ? 0f : branch.Length;
            float branchSign = mouthAtStart ? 1f : -1f;

            float trunkAt = NearestOn(trunk, at);

            // Forty-five metres, not ninety. At ninety a mouth three metres wider than the road is four
            // pixels of dark asphalt against dark asphalt and the frame answers nothing — which is the
            // fault already recorded against the two `_3_Fork` shots these replace. Close enough that
            // the widening is the subject, far enough to still see where it goes.
            Shot(camera, directory, suffix, name + "_1_Arrive",
                branch, branchAt + branchSign * 45f, -branchSign, 1.6f, -0.10f);

            Shot(camera, directory, suffix, name + "_2_Onward",
                trunk, trunkAt - 45f, 1f, 1.6f, -0.08f);

            Shot(camera, directory, suffix, name + "_3_Back",
                trunk, trunkAt + 45f, -1f, 1.6f, -0.08f);

            Plan(camera, directory, suffix, name + "_4_Plan", trunk, trunkAt, at);
        }

        /// <summary>
        /// A handover: one road ends, another begins on its pose — or runs into a town, in which case
        /// <paramref name="onward"/> is null and the frame looking back is dropped.
        /// </summary>
        private static void Handover(
            Camera camera, string directory, string suffix, string name,
            IRoadPath arriving, float arrivingAt, float arrivingSign,
            IRoadPath onward, float onwardAt, float onwardSign)
        {
            if (arriving == null)
            {
                Debug.LogWarning($"[Horizon] Junction preview: {name} has no road to stand on.");
                return;
            }

            Vector3 at = arriving.GetPositionAtDistance(Mathf.Clamp(arrivingAt, 0f, arriving.Length));

            Shot(camera, directory, suffix, name + "_1_Arrive",
                arriving, arrivingAt - arrivingSign * 70f, arrivingSign, 1.6f, -0.07f);

            if (onward != null)
            {
                Shot(camera, directory, suffix, name + "_2_Back",
                    onward, onwardAt + onwardSign * 70f, -onwardSign, 1.6f, -0.07f);
            }

            Plan(camera, directory, suffix, name + "_3_Plan", arriving, arrivingAt, at);
        }

        /// <summary>One eye-level frame, standing on a road and looking along it.</summary>
        private static void Shot(
            Camera camera, string directory, string suffix, string name,
            IRoadPath road, float at, float sign, float lift, float pitch)
        {
            float distance = Mathf.Clamp(at, 0f, road.Length);

            Vector3 on = road.GetPositionAtDistance(distance);
            Vector3 look = road.GetDirectionAtDistance(distance) * sign;

            camera.fieldOfView = 60f;
            camera.farClipPlane = 900f;
            camera.nearClipPlane = 0.3f;
            camera.transform.position = on + Vector3.up * lift;
            camera.transform.rotation = Quaternion.LookRotation(
                (look + Vector3.up * pitch).normalized, Vector3.up);

            Capture(camera, Path.Combine(directory, $"WorldPreview_Junction_{name}{suffix}.png"));
        }

        /// <summary>
        /// Straight down from a hundred and sixty metres, with the road running up the frame.
        ///
        /// <para>This is the frame that carries the whole pass. Asphalt laid across a carriageway, a
        /// ridge standing between two branches and a barrier across a mouth are all things you look
        /// straight past at eye level and cannot miss from above.</para>
        /// </summary>
        private static void Plan(
            Camera camera, string directory, string suffix, string name,
            IRoadPath along, float at, Vector3 centre)
        {
            float distance = Mathf.Clamp(at, 0f, along.Length);
            Vector3 forward = along.GetDirectionAtDistance(distance);

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            camera.fieldOfView = 60f;
            camera.farClipPlane = 900f;
            camera.nearClipPlane = 0.3f;
            camera.transform.position = centre + Vector3.up * 160f;
            camera.transform.rotation = Quaternion.LookRotation(Vector3.down, forward.normalized);

            Capture(camera, Path.Combine(directory, $"WorldPreview_Junction_{name}{suffix}.png"));
        }

        /// <summary>Distance along a path of the point nearest <paramref name="to"/>. Coarse, then halved.</summary>
        private static float NearestOn(IRoadPath path, Vector3 to)
        {
            const float coarse = 20f;

            float best = 0f;
            float bestSqr = float.MaxValue;

            for (float distance = 0f; distance <= path.Length; distance += coarse)
            {
                float sqr = Flat(path.GetPositionAtDistance(distance) - to).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = distance;
                }
            }

            float window = coarse;

            for (int i = 0; i < 8; i++)
            {
                window *= 0.5f;

                for (int side = -1; side <= 1; side += 2)
                {
                    float candidate = Mathf.Clamp(best + side * window, 0f, path.Length);
                    float sqr = Flat(path.GetPositionAtDistance(candidate) - to).sqrMagnitude;

                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = candidate;
                    }
                }
            }

            return best;
        }

        /// <summary>The same vector with its height thrown away. A junction is a question in plan.</summary>
        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        [MenuItem("Tools/Horizon/Render Fuel Station Preview", priority = 44)]
        public static void RenderFuelStations()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            var stations = new List<MeshRenderer>();
            var all = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            var roads = new List<RoadPath>(Object.FindObjectsByType<RoadPath>(FindObjectsSortMode.None));

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name.StartsWith("FuelStation", System.StringComparison.Ordinal))
                {
                    stations.Add(all[i]);
                }
            }

            if (stations.Count == 0)
            {
                Debug.LogError("[Horizon] No filling stations in the world scene. Run Rebuild Prototype "
                               + "Scene first.");
                return;
            }

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            var cameraObject = new GameObject("FuelStationPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 55f;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 900f;
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;

                for (int pass = 0; pass < 2; pass++)
                {
                    bool night = pass == 1;

                    if (clock != null)
                    {
                        clock.Running = false;
                        clock.TimeOfDayHours = night ? NightHours : 16.5f;
                        clock.Apply();
                    }

                    // In the same frame as the capture and with no Update between, which is what Refresh
                    // is for — see the note in RenderNight.
                    lights?.Refresh();

                    for (int i = 0; i < stations.Count; i++)
                    {
                        MeshRenderer station = stations[i];
                        Bounds bounds = station.bounds;

                        if (!TryRoadSide(roads, bounds.center, out Vector3 from))
                        {
                            continue;
                        }

                        camera.transform.position = from + Vector3.up * 3f;
                        camera.transform.rotation = Quaternion.LookRotation(
                            (bounds.center - from).normalized, Vector3.up);

                        string suffix = night ? "_Night" : string.Empty;
                        Capture(camera, Path.Combine(
                            directory, $"WorldPreview_{station.name}{suffix}.png"));

                        // The approach: standing on the carriageway well back from the advance sign,
                        // looking the way a driver is looking. This is the shot that answers "can you
                        // tell where the filling stations are" — the only one that would catch a sign
                        // facing backwards, standing on a viaduct, or lost in the trees.
                        Transform sign = station.transform.Find("AdvanceSign");
                        if (!night && sign != null
                            && TryApproach(roads, sign.GetComponent<MeshRenderer>().bounds.center,
                                bounds.center, out Vector3 eye, out Quaternion look))
                        {
                            camera.transform.SetPositionAndRotation(eye, look);
                            Capture(camera, Path.Combine(
                                directory, $"WorldPreview_{station.name}_Advance.png"));
                        }

                        // And once from under the canopy, which is the only place the soffit is seen at
                        // all: from the road it is edge-on and subtends a couple of degrees. It is also
                        // 240 square metres of unlit white after dusk, parked directly above the driver's
                        // head — a thing that has to be looked at rather than reasoned about.
                        if (i != 0)
                        {
                            continue;
                        }

                        // The aisles, from where a car turns in. Straight down would be the obvious
                        // shot and is useless: the canopy roof is 22 by 11 metres of opaque deck
                        // directly over the paint, so an overhead sees a rectangle and two stripe ends.
                        // This sits under the canopy's leading edge instead, which is both the only
                        // angle the marks are visible from and the angle the driver has.
                        Vector3 in3 = bounds.center - from;
                        in3.y = 0f;

                        // Six and a half metres up and a third of the way in. Above head height so the
                        // ground is not foreshortened into nothing, and shallow enough that the sight
                        // line still passes under the canopy's leading edge at 4.6 m rather than over
                        // the roof — which is the whole difficulty with photographing this.
                        Vector3 over = new Vector3(from.x, bounds.min.y + 6.5f, from.z) + in3 * 0.32f;
                        var floor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

                        camera.transform.SetPositionAndRotation(
                            over, Quaternion.LookRotation((floor - over).normalized, Vector3.up));

                        Capture(camera, Path.Combine(
                            directory, $"WorldPreview_FuelStation_Bays{suffix}.png"));

                        // And one from directly above anyway, because it is the only view that would
                        // show paint creeping onto the sloped entry ramp at the frontage.
                        camera.transform.SetPositionAndRotation(
                            bounds.center + Vector3.up * 52f, Quaternion.Euler(90f, 0f, 0f));

                        Capture(camera, Path.Combine(
                            directory, $"WorldPreview_FuelStation_Above{suffix}.png"));

                        camera.transform.position = new Vector3(
                            bounds.center.x, bounds.min.y + 1.6f, bounds.center.z)
                            - (bounds.center - from).normalized * 6f;

                        camera.transform.rotation = Quaternion.LookRotation(
                            ((bounds.center - from).normalized + Vector3.up * 0.35f).normalized,
                            Vector3.up);

                        Capture(camera, Path.Combine(
                            directory, $"WorldPreview_FuelStation_Forecourt{suffix}.png"));
                    }
                }

                Debug.Log($"[Horizon] Filling stations: {stations.Count} photographed from the road, day "
                          + "and night. In the night shots the canopy soffit, the shop glazing and the "
                          + "sign face must be lit — dark ones mean the lit submesh was registered by its "
                          + "constant rather than by the slot ToMesh kept.");
            }
            finally
            {
                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                    lights?.Refresh();
                }

                Object.DestroyImmediate(cameraObject);

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>
        /// A driver's-eye view of an advance sign, taken from up the road behind it.
        ///
        /// <para>The direction to look is taken from the sign towards the station rather than from the
        /// road's own forward, because a carriageway's forward is whichever way its course was walked
        /// and half of them are walked against the traffic that uses them.</para>
        /// </summary>
        private static bool TryApproach(
            List<RoadPath> roads, Vector3 sign, Vector3 station, out Vector3 eye, out Quaternion look)
        {
            eye = Vector3.zero;
            look = Quaternion.identity;

            // Standoff zero: this wants the road point beside the sign itself, and then to walk back
            // from there along the direction of travel. Taking the default would apply that offset
            // along the road's own forward first, which is whichever way its course happened to be
            // walked — compounding two steps in possibly opposite directions, which is how the first
            // version of this shot came out pointing at a village with the sign behind the camera.
            if (!TryRoadSide(roads, sign, out Vector3 beside, 0f))
            {
                return false;
            }

            Vector3 travel = station - sign;
            travel.y = 0f;

            if (travel.sqrMagnitude < 1f)
            {
                return false;
            }

            travel.Normalize();

            // 65 m back, which is where the board is still comfortably readable and the station behind
            // it is in the same frame — the pair being legible together is the whole claim.
            eye = beside - travel * 65f + Vector3.up * 1.6f;
            look = Quaternion.LookRotation(travel, Vector3.up);
            return true;
        }

        /// <summary>
        /// A standing point on the nearest carriageway to <paramref name="target"/>, set back far enough
        /// to hold a forecourt in frame.
        ///
        /// <para>Coarse sweep only. This runs on a menu press over a handful of stations, so the cost of
        /// walking every road at 20 m is nothing, and being a metre out does not change a photograph.</para>
        /// </summary>
        private static bool TryRoadSide(
            List<RoadPath> roads, Vector3 target, out Vector3 from, float standoff = 26f)
        {
            from = Vector3.zero;

            Vector3 best = Vector3.zero;
            Vector3 bestForward = Vector3.forward;
            float bestSqr = float.MaxValue;

            for (int r = 0; r < roads.Count; r++)
            {
                RoadPath road = roads[r];
                if (road == null || road.Length <= 0f)
                {
                    continue;
                }

                for (float at = 0f; at <= road.Length; at += 20f)
                {
                    Vector3 on = road.GetPositionAtDistance(at);
                    float sqr = (on - target).sqrMagnitude;

                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = on;
                        bestForward = road.GetDirectionAtDistance(at);
                    }
                }
            }

            if (bestSqr == float.MaxValue)
            {
                return false;
            }

            // Backed off along the road rather than pushed away across it: standing on the carriageway
            // looking sideways is the one view a driver never has.
            //
            // 26 m and not further. At forty the whole forecourt is in frame and the canopy is edge-on —
            // its soffit subtends about two degrees, so the lit ceiling that is the point of the thing at
            // night is a sliver. This is roughly where a driver decides to pull in, and it is the angle
            // that shows whether they would want to.
            from = best - bestForward * standoff;
            return true;
        }

        [MenuItem("Tools/Horizon/Render Water Preview", priority = 43)]
        public static void RenderWater()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            var bodies = new System.Collections.Generic.List<Bounds>();
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);

            for (int i = 0; i < renderers.Length; i++)
            {
                if (!renderers[i].name.EndsWith("_Water"))
                {
                    continue;
                }

                Bounds tile = renderers[i].bounds;
                int joined = -1;

                // Grown one tile at a time, and a tile that bridges two clusters merges them — otherwise a
                // river that reaches its neighbours in the wrong order comes out as two rivers.
                for (int b = 0; b < bodies.Count; b++)
                {
                    Bounds grown = bodies[b];
                    grown.Expand(new Vector3(60f, 400f, 60f));

                    if (!grown.Intersects(tile))
                    {
                        continue;
                    }

                    if (joined < 0)
                    {
                        Bounds merged = bodies[b];
                        merged.Encapsulate(tile);
                        bodies[b] = merged;
                        joined = b;
                    }
                    else
                    {
                        Bounds merged = bodies[joined];
                        merged.Encapsulate(bodies[b]);
                        bodies[joined] = merged;
                        bodies.RemoveAt(b);
                        b--;
                    }
                }

                if (joined < 0)
                {
                    bodies.Add(tile);
                }
            }

            if (bodies.Count == 0)
            {
                Debug.LogWarning("[Horizon] No water surfaces in the world scene. Run Rebuild Prototype "
                                 + "Scene first, or there is genuinely no water in the world.");
                return;
            }

            var cameraObject = new GameObject("WaterPreviewCamera");
            bool fogWasOn = RenderSettings.fog;

            // Held at mid-morning, and this one is not a nicety. The scene comes out of a rebuild at a
            // low sun, which lays half a hillside in shadow and turns everything else orange — and the
            // three things worth checking here are all colours: sand against grass, shallow against
            // deep, foam against bank. Under a sunset none of them can be told apart.
            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;

            try
            {
                if (clock != null)
                {
                    clock.Running = false;
                    clock.TimeOfDayHours = DaylightHours;
                    clock.Apply();
                }

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.3f;
                camera.enabled = false;

                string directory = Directory.GetParent(Application.dataPath).FullName;

                for (int i = 0; i < bodies.Count; i++)
                {
                    Bounds body = bodies[i];
                    float span = Mathf.Max(60f, Mathf.Max(body.size.x, body.size.z));
                    string label = $"{i + 1}_at{body.center.x:0}_{body.center.z:0}";

                    // Fog off from above for the same reason the overview turns it off: it is tuned to the
                    // draw distance of a car, and from four hundred metres up it is the only thing in shot.
                    // Steeply from above rather than obliquely: the question this shot answers is the
                    // plan one — whether the surface covers its own basin and where the sand runs — and
                    // at a low angle a body two hundred metres across is a sliver behind whatever
                    // hillside stands between it and the camera.
                    RenderSettings.fog = false;
                    camera.farClipPlane = span * 6f;
                    camera.fieldOfView = 45f;
                    camera.transform.position = body.center
                                                + new Vector3(0.22f, 1f, -0.22f).normalized * span * 1.15f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        body.center - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Water_{label}_Above.png"));

                    // And from the bank, at about the height of a driver standing on it — the only view
                    // that shows whether the sand band reads as a shore or as a stripe.
                    //
                    // Stood off the *narrow* side, and that is the whole trick to framing a river. Both
                    // of these bodies are four hundred metres long and forty across; stepping back by a
                    // share of the longer dimension puts the camera in the woods beyond the far bank,
                    // looking at trees. Across the channel it is a shoreline seen from a shoreline.
                    // On dry land, whichever way that turns out to be. Stepping back along the body's
                    // narrow axis frames a river, and put the sea's camera six hundred metres offshore
                    // looking back at the coast from outside the world — the ground is what says which
                    // side is a shore, so the ground is asked.
                    RenderSettings.fog = fogWasOn;
                    float back = Mathf.Min(body.size.x, body.size.z) * 0.5f + 25f;
                    Vector3 station = body.center;

                    for (int step = 0; step < 8; step++)
                    {
                        float angle = step / 8f * Mathf.PI * 2f;
                        Vector3 candidate = body.center
                                            + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * back;

                        if (!Physics.Raycast(candidate + Vector3.up * 600f, Vector3.down,
                                out RaycastHit ground, 1200f))
                        {
                            continue;
                        }

                        candidate.y = ground.point.y;
                        station = candidate;

                        // Above the surface is a bank; below it is the bed, and standing on the bed is
                        // how the last of these shots came back looking out of the side of the world.
                        if (ground.point.y > body.center.y)
                        {
                            break;
                        }
                    }

                    camera.farClipPlane = 900f;
                    camera.fieldOfView = 55f;
                    camera.transform.position = station + Vector3.up * 8f;
                    camera.transform.rotation = Quaternion.LookRotation(
                        body.center - camera.transform.position, Vector3.up);
                    Capture(camera, Path.Combine(directory, $"WorldPreview_Water_{label}_Shore.png"));
                }

                Debug.Log($"[Horizon] Water preview: {bodies.Count} bodies written to "
                          + $"{directory}/WorldPreview_Water_*.png");
            }
            finally
            {
                RenderSettings.fog = fogWasOn;
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Running = runningWas;
                    clock.Apply();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>Hour to hold the clock at for the water shots. High sun, flat light, true colours.</summary>
        private const float DaylightHours = 10.5f;

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
