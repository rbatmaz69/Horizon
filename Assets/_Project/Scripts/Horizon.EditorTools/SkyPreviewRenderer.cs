using System.IO;
using Horizon.Atmosphere;
using Horizon.Game;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs the sky at four hours against four weathers, from one place with a low horizon.
    ///
    /// <para><b>Its own command, because the questions are its own.</b> Every other preview here is
    /// pointed at a piece of road and takes the sky as whatever was behind it; this one is pointed at
    /// the sky and takes the road as whatever is under it. The grid is the point — a sky is a continuum
    /// over two axes and no single frame can show that a continuum is continuous.</para>
    ///
    /// <para><b>The acceptance frames are deliberately not the default hour.</b> A clear sky at 17.6 h
    /// with a few clouds in it is close to what Unity's stock procedural dome already gave, and a
    /// reviewer who opens only that one concludes that nothing shipped. The two that decide this feature
    /// are <c>Sky_23h0_Rain</c>, which has to be dark — that is the bug CLAUDE.md recorded, where a
    /// painted grey dome at a fixed exposure read the same at midnight as at noon — and
    /// <c>Sky_17h6_Hazy</c>, which has to have cloud in it, because Hazy sits at 0.45 and the sky this
    /// replaced swapped at 0.60, so that setting had never changed the sky by so much as a pixel.</para>
    ///
    /// <para><b>The sun frame is aimed by the light and not by the profile.</b> The shader is handed a
    /// direction read off <c>sun.transform</c> after the rotation is written, precisely so that it
    /// cannot disagree with where the shadows come from — and a camera aimed the same way is the only
    /// frame in this project that would show it disagreeing. Aiming it from <c>SunAzimuth</c> and
    /// <c>SunElevation</c> instead would be the second copy of the formula that the arrangement exists
    /// to avoid, and it would agree with itself while the sky was wrong.</para>
    ///
    /// <para>The tool drives the clock itself, for the reason <c>WeatherPreviewRenderer</c> gives: a
    /// saved scene is a scene in which no <c>Update</c> has ever run. What it must not do is carry its
    /// own idea of what a weather is, so the four levels come from <c>PlayerChoices.OvercastFor</c>.</para>
    /// </summary>
    public static class SkyPreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";

        private const int Width = 1280;
        private const int Height = 720;

        /// <summary>
        /// Where the camera stands: the Talblick viewpoint on the pass, high and looking out.
        ///
        /// <para>Chosen for its horizon rather than for its view. What this tool needs is a frame that is
        /// mostly sky with a skyline across the bottom of it — which is also the frame in which a
        /// mismatch between the sky's horizon colour and the fog would show as a seam.</para>
        /// </summary>
        private const float Station = 3100f;

        /// <summary>Metres above the carriageway. Head height plus enough to clear the guard rail.</summary>
        private const float EyeHeight = 3.2f;

        /// <summary>
        /// Pitched up, because the subject is above the horizon.
        ///
        /// <para>Not so far up that the skyline leaves the frame: the horizon band is where the dome, the
        /// cloud's own fade-out and the fog all have to agree, and a frame with no ground in it cannot
        /// show two of those three failing.</para>
        /// </summary>
        private const float Pitch = -18f;

        private static readonly float[] Hours = { 5.6f, 12.0f, 17.6f, 23.0f };

        /// <summary>
        /// The hour the two sun frames are taken at. See the note beside them.
        ///
        /// <para>Mid-afternoon, so the disc is high enough that nothing in the world can stand in front
        /// of it. It is deliberately not one of the four above: those are about the sky and this one is
        /// about a single object in it.</para>
        /// </summary>
        private const float SunHours = 14.5f;

        private static readonly WeatherPreset[] Weathers =
        {
            WeatherPreset.Clear, WeatherPreset.Hazy, WeatherPreset.Rain, WeatherPreset.Overcast,
        };

        [MenuItem("Tools/Horizon/Render Sky Preview", priority = 52)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            RoadPath pass = FindRoad("RoadPath");
            var clock = Object.FindFirstObjectByType<TimeOfDayController>();

            if (pass == null || clock == null)
            {
                Debug.LogError("[Horizon] The world scene has no pass road or no TimeOfDayController. "
                               + "Run Rebuild Prototype Scene first — and if it has just been run, that "
                               + "is the finding.");

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }

                return;
            }

            float hoursWere = clock.TimeOfDayHours;
            float overcastWas = clock.Overcast;
            bool runningWas = clock.Running;

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("SkyPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 60f;
                camera.farClipPlane = 600f;
                camera.enabled = false;

                Vector3 centre = pass.GetPositionAtDistance(Station);
                Vector3 forward = pass.GetDirectionAtDistance(Station);

                cameraObject.transform.position = centre + Vector3.up * EyeHeight;
                cameraObject.transform.rotation =
                    Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(Pitch, 0f, 0f);

                for (int h = 0; h < Hours.Length; h++)
                {
                    for (int w = 0; w < Weathers.Length; w++)
                    {
                        Set(clock, Hours[h], PlayerChoices.OvercastFor(Weathers[w]));

                        string name = $"Sky_{Hours[h]:0.0}h_{Weathers[w]}".Replace(".", "h");
                        Capture(camera, Path.Combine(directory, $"{name}.png"));
                    }
                }

                // The sun, framed by the light rather than by the profile. See the class note.
                Light sun = RenderSettings.sun;

                if (sun != null)
                {
                    // <b>Mid-afternoon and not the default hour, because at the default hour this frame
                    // could not answer its own question.</b> SunElevation puts 17.6 h six degrees above
                    // the horizon, and a camera aimed straight at a sun six degrees up from a road in a
                    // valley is a camera aimed at the hillside in front of it: the first version came
                    // back as a warm glow spilling off one edge with the disc nowhere in it, which is
                    // indistinguishable from a disc drawn in the wrong place. At SunHours the sun is
                    // better than fifty degrees up and there is nothing between the camera and it.
                    Set(clock, SunHours, PlayerChoices.OvercastFor(WeatherPreset.Clear));

                    cameraObject.transform.rotation =
                        Quaternion.LookRotation(-sun.transform.forward, Vector3.up);

                    Capture(camera, Path.Combine(directory, "Sky_Sun.png"));

                    // And the same aim under cloud, which is the only check of the term that lets a
                    // cloud drifting over the sun put it out.
                    Set(clock, SunHours, PlayerChoices.OvercastFor(WeatherPreset.Hazy));
                    Capture(camera, Path.Combine(directory, "Sky_Sun_Hazy.png"));
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);

                clock.TimeOfDayHours = hoursWere;
                clock.Overcast = overcastWas;
                clock.Running = runningWas;
                clock.Apply();

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log("[Horizon] Sky preview written beside the project: sixteen frames of four hours "
                      + "against four weathers, plus two aimed at the sun. Two of them carry it. "
                      + "Sky_23h0h_Rain must be dark — a painted grey dome at a fixed exposure read the "
                      + "same at midnight as at noon, and that is the bug this closes. Sky_17h6h_Hazy "
                      + "must have cloud in it — Hazy is 0.45 and the sky this replaced swapped at 0.60, "
                      + "so that setting had never changed the sky at all. Sky_Sun is the only frame "
                      + "anywhere that would show the disc sitting somewhere the shadows do not come "
                      + "from. Do not judge this from the clear frame at 17.6 h: that is the one hour "
                      + "where the old procedural dome already looked much like this.");
        }

        private static void Set(TimeOfDayController clock, float hours, float overcast)
        {
            clock.Running = false;
            clock.TimeOfDayHours = hours;
            clock.Overcast = overcast;

            // Apply rather than waiting for Update, which does not run here — and Apply is what pushes
            // the shader globals and rebuilds the environment reflection, so a frame taken without it
            // would photograph the previous hour's sky.
            clock.Apply();
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
        /// Fog stays on, deliberately, unlike the overview shots.
        ///
        /// <para>The subject includes whether the sky's horizon is the same colour as the air in front
        /// of it. Turning the fog off removes exactly the half of that question worth asking.</para>
        /// </summary>
        private static void Capture(Camera camera, string filePath) =>
            PreviewCapture.Shoot(camera, Width, Height, filePath);
    }
}
