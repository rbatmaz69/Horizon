using System.IO;
using Horizon.Atmosphere;
using Horizon.Game;
using Horizon.Net;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs four other players' cars on the road, by day and by night.
    ///
    /// <para><b>Every fault this feature can have that is not a number is in these frames.</b> The build
    /// counts the pool's slots, its bodies and its paints, and every one of those counts is correct in a
    /// world where the wheels sit inside the arches, the paint has landed on the glass, the brake lamps
    /// are the same dark red at every hour, or a car is drawn as a fastback whatever its owner chose.
    /// Those are the five things that have gone wrong with every other piece of car geometry in this
    /// project, and none of them has ever been reported by a log line.</para>
    ///
    /// <para><b>The tool places the cars itself, because nothing ticks in a saved scene.</b> A remote
    /// car exists because a packet arrived; in the editor no packet ever will, and a preview that
    /// waited for one would photograph an empty road for ever. <c>RemoteCar.ShowAt</c> is public for
    /// exactly this caller — the same argument <c>HudPreviewRenderer</c> makes about having to lay out
    /// the gauge faces itself. What it must not do is carry its own idea of how a body is chosen or
    /// where a wheel sits, so it calls into the component under test rather than around it.</para>
    ///
    /// <para><b>Four cars and not seven.</b> The protocol carries eight players and the presentation is
    /// tuned so four is the comfortable case; four is therefore what the picture has to answer for. A
    /// frame with seven in it would be a frame about whether seven fit on a screen, which is a
    /// different question and not the one that has ever gone wrong here.</para>
    /// </summary>
    public static class MultiplayerPreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";
        private const int Width = 1280;
        private const int Height = 720;

        /// <summary>Low sun, long shadows — the light this game is at its best in.</summary>
        private const float DayHours = 16.2f;

        /// <summary>Dark enough that headlamps and tail lamps are the only thing lighting a car.</summary>
        private const float NightHours = 22.5f;

        /// <summary>Where along the pass the shot is taken. Past the town, on the open approach.</summary>
        private const float StationDistance = 620f;

        [MenuItem("Tools/Horizon/Render Multiplayer Preview", priority = 52)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            var pool = Object.FindFirstObjectByType<RemoteCarPool>();
            RoadPath road = FindRoad("RoadPath");
            var clock = Object.FindFirstObjectByType<TimeOfDayController>();
            var lights = Object.FindFirstObjectByType<TownLights>();

            if (pool == null || road == null)
            {
                Debug.LogError(
                    "[Horizon] No RemoteCarPool or no pass road in the world scene. Run "
                    + "Tools > Horizon > Rebuild Prototype Scene first — and if it has just been run, "
                    + "that is the finding rather than the obstacle.");

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }

                return;
            }

            // Beside every other preview this project takes, which is the repository root rather than
            // anywhere under Assets: a PNG in the project is an asset Unity imports, generates a .meta
            // for and — since these are rewritten on every run — churns in git. The .gitignore has a
            // line per tool for exactly this.
            string directory = Directory.GetParent(Application.dataPath).FullName;

            var cameraObject = new GameObject("MultiplayerPreviewCamera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 55f;
            camera.farClipPlane = 600f;
            camera.nearClipPlane = 0.3f;

            float savedHours = clock != null ? clock.TimeOfDayHours : 0f;

            try
            {
                Place(pool, road, night: false);

                Shoot(camera, road, clock, lights, directory, DayHours, "Multiplayer_1_Convoy_Day", 34f, 1.9f);
                Shoot(camera, road, clock, lights, directory, DayHours, "Multiplayer_2_Close_Day", 13f, 1.4f);
                ShootHeadOn(camera, road, clock, lights, directory, DayHours, "Multiplayer_3_Faces_Day");

                Place(pool, road, night: true);

                Shoot(camera, road, clock, lights, directory, NightHours, "Multiplayer_4_Convoy_Night", 34f, 1.9f);
                Shoot(camera, road, clock, lights, directory, NightHours, "Multiplayer_5_Close_Night", 13f, 1.4f);

                // The one frame that can show a headlamp at all.
                //
                // <b>The first four shots stood behind the cars, and a car's headlights are on the
                // front of it.</b> So every picture this tool took was of four sets of tail lamps, and
                // the lit-lens material — the whole reason another player's car is visible at night,
                // since it carries no real Light components — appeared in none of them. A frame that
                // cannot resolve its subject is worse than no frame, because it looks like an answer.
                ShootHeadOn(camera, road, clock, lights, directory, NightHours, "Multiplayer_6_Faces_Night");
            }
            finally
            {
                pool.ReleaseAll();
                Object.DestroyImmediate(cameraObject);

                if (clock != null)
                {
                    clock.TimeOfDayHours = savedHours;
                    clock.Apply();
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Debug.Log(
                $"[Horizon] Multiplayer preview written to {Directory.GetParent(Application.dataPath)}: "
                + "four cars in four bodies and four paints, from behind and head on, day and night.");
        }

        /// <summary>
        /// Puts four cars on the road, each in a different body and paint.
        ///
        /// <para>Different bodies because the failure this is looking for is per-body: a wheel mesh that
        /// did not follow its shell, a collider box that did, a hatchback's tyre in an off-roader's
        /// arch. One car repeated four times would answer none of it.</para>
        ///
        /// <para>Two of them braking and two not, because the lamps are the one part of a remote car
        /// that changes, and a frame in which every lamp is in the same state cannot show that the
        /// braking material and the night material are different materials.</para>
        /// </summary>
        private static void Place(RemoteCarPool pool, RoadPath road, bool night)
        {
            pool.ReleaseAll();

            // Bodies far apart in the table rather than 0..3: an estate and a saloon are two boxes at
            // this distance, and the point of the frame is that the four cars are visibly four cars.
            byte[] bodies = { 0, 2, 4, 9 };
            byte[] paints = { 1, 3, 5, 7 };
            float[] along = { 26f, 48f, 74f, 96f };
            float[] across = { 0.5f, -0.5f, 0.5f, -0.5f };

            // Headlamps only at night, because that is the only time the sender ever sets the flag —
            // it is read straight off its own VehicleLights. The first version lit them in the day
            // frames too, which put every tail lamp on the lit night material at four in the
            // afternoon: a picture of a state the game cannot be in.
            CarFlags lit = night ? CarFlags.Headlights : CarFlags.None;

            CarFlags[] flags =
            {
                lit,
                lit | CarFlags.Braking,
                lit,
                lit | CarFlags.Braking,
            };

            RoadShape shape = RoadShape.Default;

            for (int i = 0; i < bodies.Length && i < pool.SlotCount; i++)
            {
                RemoteCar car = pool.At(i);

                if (car == null)
                {
                    continue;
                }

                float at = StationDistance + along[i];
                Vector3 forward = road.GetDirectionAtDistance(at);

                // The road surface, not a ride height: RemoteCar.ShowAt lifts the car by the height of
                // whichever body it has just put on, which is a quarter of a metre more for the
                // off-roader than for the hatchback. A figure typed here would sink one and float the
                // other, and the first version did exactly that with a flat 0.6.
                Vector3 ground = road.GetPositionAtDistance(at)
                                 + road.GetRightAtDistance(at) * (shape.HalfWidth * across[i]);

                car.Bind((byte)(i + 1));
                car.ShowAt(
                    ground, Quaternion.LookRotation(forward, Vector3.up),
                    bodies[i], paints[i], flags[i]);
            }
        }

        private static void Shoot(
            Camera camera, RoadPath road, TimeOfDayController clock, TownLights lights,
            string directory, float hours, string name, float back, float height)
        {
            if (clock != null)
            {
                clock.TimeOfDayHours = hours;
                clock.Apply();
            }

            // The lamps and the windows are a material swap on a timer that does not run at edit time,
            // so the night frames would otherwise be a dark world with every window lit for noon.
            lights?.Refresh();

            float at = StationDistance + 26f - back;
            Vector3 forward = road.GetDirectionAtDistance(at);

            Vector3 eye = road.GetPositionAtDistance(at)
                          + road.GetRightAtDistance(at) * 2.6f
                          + Vector3.up * height;

            Vector3 target = road.GetPositionAtDistance(StationDistance + 60f) + Vector3.up * 0.9f;

            camera.transform.SetPositionAndRotation(
                eye, Quaternion.LookRotation(target - eye, Vector3.up));

            PreviewCapture.Shoot(camera, Width, Height, Path.Combine(directory, $"{name}.png"));
        }

        /// <summary>
        /// Stands in front of the convoy and looks back down it.
        ///
        /// <para>The only frame that contains a headlamp, a grille, a number plate or the front half of
        /// a paint job. Everything else here is taken from behind, which is where a driver normally
        /// sees another car and is therefore where the tail lamps had to be checked — but it means four
        /// of the six frames cannot answer half the questions this tool exists for.</para>
        /// </summary>
        private static void ShootHeadOn(
            Camera camera, RoadPath road, TimeOfDayController clock, TownLights lights,
            string directory, float hours, string name)
        {
            if (clock != null)
            {
                clock.TimeOfDayHours = hours;
                clock.Apply();
            }

            lights?.Refresh();

            RoadShape shape = RoadShape.Default;

            // Beyond the furthest car, in the other lane, at about the height of an oncoming driver's
            // eye — so the frame is the view a car coming the other way would have.
            //
            // Twenty-six metres clear of the furthest car, and aimed at the <i>middle</i> of the
            // convoy rather than at its far end. The first version stood at eighteen and looked past
            // the nearest car entirely, which put half of it outside the frame; the second backed off
            // to fifty-four and made all four of them specks. What was actually wrong both times was
            // the aim, not the distance.
            float at = StationDistance + 122f;

            Vector3 eye = road.GetPositionAtDistance(at)
                          + road.GetRightAtDistance(at) * (shape.HalfWidth * 0.5f)
                          + Vector3.up * 1.4f;

            Vector3 target = road.GetPositionAtDistance(StationDistance + 62f) + Vector3.up * 0.8f;

            camera.transform.SetPositionAndRotation(
                eye, Quaternion.LookRotation(target - eye, Vector3.up));

            PreviewCapture.Shoot(camera, Width, Height, Path.Combine(directory, $"{name}.png"));
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
    }
}
