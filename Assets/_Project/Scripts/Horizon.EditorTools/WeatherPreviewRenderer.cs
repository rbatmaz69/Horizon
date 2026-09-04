using System.IO;
using Horizon.Atmosphere;
using Horizon.Game;
using Horizon.Vehicle;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs the same three places dry and in the rain.
    ///
    /// <para><b>Rain is the first thing built here that a picture can check and a log cannot</b> — which
    /// is the ordinary case in this project and was not the case for the surfaces one system earlier.
    /// Three questions have no other answer:</para>
    ///
    /// <list type="bullet">
    /// <item><b>Are the drops visible at all?</b> They are stretched billboards whose length comes from
    /// velocity rather than from a constant, precisely so that standing rain does not look like falling
    /// rain — and a still frame is exactly the case where that decision can go wrong and leave a frame
    /// with nothing in it.</item>
    /// <item><b>Does the carriageway actually darken?</b> The swap is counted in the build log, and a
    /// count says the material was assigned, never that it looks any different from the dry one.</item>
    /// <item><b>Does it stay out of the tunnels?</b> The emitter hangs 14 m over the camera, which
    /// inside a bore is 14 m into solid rock. Nothing in the build would say a word about rain falling
    /// through a mountain.</item>
    /// </list>
    ///
    /// <para><b>The tool drives the weather itself, because <c>WeatherDirector</c> does not run at edit
    /// time.</b> That is the same reason <c>HudPreviewRenderer</c> has to call <c>LayOutFace</c> on the
    /// gauges: a saved scene is a scene in which no <c>Update</c> has ever happened. What it must not do
    /// is carry its own opinion of what "wet" means — so it calls <c>WetSurfaces.SetWet</c> and writes
    /// the emitter's rate, and every material and every drop comes from the same code the running game
    /// uses.</para>
    ///
    /// <para><b>The rain is reparented to the preview camera and put back afterwards.</b> It lives under
    /// the chase rig, which is not the camera these frames are taken with, so left alone it would rain
    /// wherever the saved scene happens to have parked the car.</para>
    /// </summary>
    public static class WeatherPreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";
        private const int Width = 1280;
        private const int Height = 720;

        private const float DayHours = 16.2f;
        private const float NightHours = 22.5f;

        /// <summary>
        /// How long the emitter is run before the shutter opens, seconds.
        ///
        /// <para>Longer than a drop's own lifetime, so the box is full rather than half filled from the
        /// top. Simulated rather than played, because nothing ticks in a scene that is not in Play
        /// mode — an unsimulated system is an empty one, and the frame would come back looking exactly
        /// like rain that does not draw.</para>
        /// </summary>
        private const float SettleSeconds = 1.4f;

        [MenuItem("Tools/Horizon/Render Weather Preview", priority = 51)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath pass = FindRoad("RoadPath");
            // The median, not a carriageway: the two carriageways are OffsetRoadPaths built in code and
            // have no object of their own to find. The shot steps across to one of them by the same
            // constant the builder offsets them with, rather than by a number typed here.
            RoadPath motorway = FindRoad("MotorwayPath");

            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();
            var wet = Object.FindFirstObjectByType<WetSurfaces>();
            var weather = Object.FindFirstObjectByType<WeatherDirector>();
            ParticleSystem rain = FindRain();

            if (pass == null || wet == null || rain == null)
            {
                Debug.LogError(
                    "[Horizon] The world scene has no pass road, no WetSurfaces or no Rain emitter. "
                    + "Run Rebuild Prototype Scene first — and if it has just been run, that is the "
                    + "finding: one of the three did not get built.");

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }

                return;
            }

            float hoursWere = clock != null ? clock.TimeOfDayHours : 0f;
            bool runningWas = clock != null && clock.Running;
            float overcastWas = clock != null ? clock.Overcast : 0f;

            Transform rainParent = rain.transform.parent;
            Vector3 rainOffset = rain.transform.localPosition;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("WeatherPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.enabled = false;

                rain.transform.SetParent(cameraObject.transform, false);
                rain.transform.localPosition = rainOffset;

                // Dry by day, wet by day, wet by night. Dry by night is deliberately not taken: every
                // other preview in this project already photographs these roads dark and empty, and a
                // frame whose only job is to look like one of those is a frame nobody opens.
                Shoot(camera, clock, lights, wet, rain, weather, pass, motorway, directory,
                    DayHours, 0f, false, string.Empty);

                Shoot(camera, clock, lights, wet, rain, weather, pass, motorway, directory,
                    DayHours, PlayerChoices.OvercastFor(WeatherPreset.Rain), true, "_Rain");

                Shoot(camera, clock, lights, wet, rain, weather, pass, motorway, directory,
                    NightHours, PlayerChoices.OvercastFor(WeatherPreset.Rain), true, "_RainNight");
            }
            finally
            {
                rain.transform.SetParent(rainParent, false);
                rain.transform.localPosition = rainOffset;

                ParticleSystem.EmissionModule emission = rain.emission;
                emission.rateOverTime = 0f;
                rain.Clear(true);

                wet.SetWet(false);

                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = hoursWere;
                    clock.Overcast = overcastWas;
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

            Debug.Log(
                "[Horizon] Weather preview written beside the project. Three frames carry it. "
                + "_1_Pass against _1_Pass_Rain is the only place that says whether the carriageway "
                + "actually darkens — the build counts the material swap and a count cannot see. "
                + "_2_Motorway_Rain is where the sheen has to appear, because it is the one shot with "
                + "a low sun down a long straight, and smoothness with no sun in frame looks like "
                + "nothing. _3_Portal_Rain answers whether it rains inside the mountain: the emitter "
                + "hangs 14 m over the camera, which in a bore is 14 m of rock, and no log anywhere "
                + "would mention it. Also worth reading: whether the drops are visible at all in a "
                + "still frame — they are stretched by velocity on purpose, and a still frame is "
                + "exactly where that can come back empty.");
        }

        /// <summary>One set of conditions, and the three places seen under them.</summary>
        private static void Shoot(
            Camera camera,
            TimeOfDayController clock,
            TownLights lights,
            WetSurfaces wet,
            ParticleSystem rain,
            WeatherDirector weather,
            RoadPath pass,
            RoadPath motorway,
            string directory,
            float hours,
            float overcast,
            bool raining,
            string suffix)
        {
            if (clock != null)
            {
                clock.Running = false;
                clock.TimeOfDayHours = hours;
                clock.Overcast = overcast;
                clock.Apply();
            }

            if (lights != null)
            {
                lights.Refresh();
            }

            wet.SetWet(raining);

            // The heaviest rate the running game would ever ask for, read off the component rather than
            // typed here. A second copy of that number agrees until the first retune and then quietly
            // photographs a downpour the game does not have.
            float fullRate = weather != null ? weather.MaxDropsPerSecond : 0f;

            ParticleSystem.EmissionModule emission = rain.emission;

            void FromRoad(
                RoadPath road, float at, float across, float lift, float pitch, float yaw, string name)
            {
                if (road == null)
                {
                    return;
                }

                float distance = Mathf.Clamp(at, 0f, road.Length);

                Vector3 on = road.GetPositionAtDistance(distance)
                    + road.GetRightAtDistance(distance) * across;
                Vector3 forward = road.GetDirectionAtDistance(distance);
                Vector3 look = Quaternion.Euler(0f, yaw, 0f) * forward;

                camera.fieldOfView = 60f;
                camera.farClipPlane = 900f;
                camera.nearClipPlane = 0.3f;
                camera.transform.position = on + Vector3.up * lift;
                camera.transform.rotation = Quaternion.LookRotation(
                    (look + Vector3.up * pitch).normalized, Vector3.up);

                // Through the same roof probe the running game uses, not around it. Written the first
                // way — a flat rate — this frame showed rain falling through a mountain and would have
                // gone on showing it after the fix, because the tool was bypassing the very thing being
                // tested. A frame that cannot fail is not a check.
                bool roofed = raining && VehicleCover.RoofedAt(
                    camera.transform.position, Vector3.up, 16f, ~0, out _);

                emission.rateOverTime = raining && !roofed ? fullRate : 0f;

                // Filled before the shutter, not played: nothing ticks outside Play mode, so an
                // unsimulated emitter is an empty one and every rain frame would come back looking
                // like rain that does not draw.
                if (raining && !roofed)
                {
                    rain.Simulate(SettleSeconds, true, true);
                }
                else
                {
                    rain.Clear(true);
                }

                Capture(camera, Path.Combine(directory, $"WeatherPreview_{name}{suffix}.png"));
            }

            // 1. Low over the carriageway on the pass, pitched down so the road fills the bottom half of
            // the frame. This is the darkening shot and nothing else in the project takes one: every
            // other preview stands high enough to see the scenery, which is the wrong height to judge
            // a road surface from.
            FromRoad(pass, 1450f, 0f, 1.6f, -0.22f, 0f, "1_Pass");

            // 2. Down the motorway with the sun low and ahead. The smoothness half of "wet" only exists
            // where there is something for it to reflect, so a wet road photographed under flat cloud is
            // a dark road — correct, and silent about half of what was built.
            FromRoad(motorway, 2600f, -AutobahnCourse.CarriagewayOffset, 2.0f, -0.04f, 0f, "2_Motorway");

            // 3. The Kehrtunnel's mouth from inside it, looking out. The one frame that would show rain
            // falling through a mountain.
            FromRoad(pass, TunnelMiddle(), 0f, 2.2f, 0f, 0f, "3_Portal");
        }

        /// <summary>
        /// Halfway through the pass's first bore, read off the course rather than written down.
        ///
        /// <para>A literal here is a number that goes stale the first time the tunnel moves, and the
        /// symptom is a frame of open hillside labelled Portal — which looks like a shot that simply
        /// missed rather than like a stale constant. Falls back to the middle of the road if the course
        /// carries no bore at all, so the tool still produces its third frame and the frame itself says
        /// there is no tunnel.</para>
        /// </summary>
        private static float TunnelMiddle()
        {
            RoadCourse course = MountainPassCourse.Build();

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];

                if (feature.Kind == RoadFeatureKind.Tunnel)
                {
                    return (feature.StartDistance + feature.EndDistance) * 0.5f;
                }
            }

            Debug.LogWarning("[Horizon] The pass carries no tunnel, so _3_Portal is a photograph of open "
                             + "road. That frame exists to ask whether it rains inside a mountain, and "
                             + "without a mountain it cannot.");

            return course.PlannedLength * 0.5f;
        }

        private static ParticleSystem FindRain()
        {
            ParticleSystem[] systems = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);

            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].name == "Rain")
                {
                    return systems[i];
                }
            }

            return null;
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
        /// Photographs the world, so post-processing is on and the fog is left alone — this tool's whole
        /// subject is what the driver sees out of the windscreen.
        /// </summary>
        private static void Capture(Camera camera, string filePath) =>
            PreviewCapture.Shoot(camera, Width, Height, filePath);
    }
}
