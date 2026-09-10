using System.IO;
using Horizon.Core;
using Horizon.Game;
using Horizon.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Every car in the garage, photographed from the chase camera's own resting pose, in the world,
    /// through the post stack the player gets.
    ///
    /// <para><b>This is the frame the cars are judged in and it did not exist.</b>
    /// <see cref="CarPreviewRenderer"/> is a studio: a turntable five kilometres above the world against
    /// a flat colour, with no sky, no fog, no tone map and a key light placed to flatter. A studio
    /// flatters exactly the thing that was wrong — a smooth-shaded body reads as a soft gradient there
    /// and as a soap bar under a low sun — and it has no skybox, which is what every above-half
    /// smoothness material in this project actually reflects, there being no reflection probes in the
    /// world. So the paint could not be judged either.</para>
    ///
    /// <para><see cref="DriverPreviewRenderer"/> takes this frame already and takes it correctly, but of
    /// one car and for a different question: it is a shadow-acne hunt, four variants of the default
    /// fastback differing by one render setting each. What was missing is the other axis — ten bodies,
    /// one setting.</para>
    ///
    /// <para><b>One paint for all ten, and it is the default.</b> These frames are compared with each
    /// other, so the body has to be the only thing that changes between them; ten cars in ten colours is
    /// ten pictures with two variables in them. The paint the player actually starts in is the honest
    /// choice of the one.</para>
    ///
    /// <para><b>A menu item and deliberately not part of <c>Rebuild</c>.</b>
    /// <see cref="VehicleBodySet.Select"/> writes to the world scene — it resizes a collider, swaps four
    /// wheel meshes and pushes a config into a live <c>VehicleController</c> — so a rebuild that ended by
    /// running this would hand the author a modified scene for having looked at its cars. That is the
    /// working-tree hazard <c>Backdrop</c> refuses <c>[ExecuteAlways]</c> over and
    /// <c>QualityDirector</c>, <c>TownLights</c> and <c>WetSurfaces</c> each document. The scene is
    /// closed without saving when this tool opened it, exactly as the driver preview does.</para>
    /// </summary>
    public static class CarDrivePreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";

        /// <summary>
        /// 1920 × 1080, matching <see cref="DriverPreviewRenderer"/> and not the studio's 900 × 600,
        /// for the reason that tool gives at length: every fault this frame exists to catch is measured
        /// in pixels against a fixed world size, so shooting it small makes it look better than it ships.
        /// </summary>
        private const int Width = 1920;

        private const int Height = 1080;

        [MenuItem("Tools/Horizon/Render Car Drive Preview", priority = 41)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            // Inactive included, for the reason DriverPreviewRenderer records: the car in a saved world
            // scene is switched off until GameBootstrap wakes it, and a search that misses it reports
            // "no car" against a scene that has one.
            var vehicle = Object.FindFirstObjectByType<VehicleController>(FindObjectsInactive.Include);
            var chase = Object.FindFirstObjectByType<ChaseCamera>(FindObjectsInactive.Include);
            var bodies = Object.FindFirstObjectByType<VehicleBodySet>(FindObjectsInactive.Include);

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("CarDrivePreviewCamera");

            int bodyWas = bodies != null ? bodies.ActiveBody : -1;
            int paintWas = bodies != null ? bodies.ActivePaint : -1;

            try
            {
                if (vehicle == null || chase == null || bodies == null)
                {
                    Debug.LogError("[Horizon] No car, no ChaseCamera or no VehicleBodySet in the world "
                                   + "scene, so the drive views were not photographed. Run Rebuild "
                                   + "Prototype Scene first.");
                    return;
                }

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = Mathf.Max(900f, Horizon.World.BackdropBuilder.MinimumFarPlane);

                int count = bodies.BodyCount;
                if (count == 0)
                {
                    Debug.LogError("[Horizon] The VehicleBodySet has no bodies in it, so the drive "
                                   + "views were not photographed.");
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    // Appearance only. Select would also resize the hull and push a config into the
                    // controller, whose per-wheel spring array Awake builds — and no Awake has run in a
                    // saved scene, so that path throws. Nothing it does affects a still frame.
                    bodies.SelectAppearance(i, 0);

                    // Asked of the rig rather than worked out here, and asked again per body: a taller
                    // car rests the camera higher, so a pose taken once and reused would frame nine of
                    // the ten wrong.
                    PlaceLikeTheRig(chase, vehicle, camera);

                    string name = bodies.NameOf(i);
                    PreviewCapture.Shoot(camera, Width, Height,
                        Path.Combine(directory, $"CarPreview_Drive_{name}.png"));
                }

                Debug.Log($"[Horizon] Drive views: {count} frames at {Width}x{Height} from the chase "
                          + "camera's own resting pose, one per body, all in the default paint — "
                          + $"{directory}/CarPreview_Drive_<body>.png.");
            }
            finally
            {
                if (bodies != null && bodyWas >= 0)
                {
                    bodies.SelectAppearance(bodyWas, Mathf.Max(paintWas, 0));
                }

                Object.DestroyImmediate(cameraObject);

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>
        /// Puts the preview camera where the rig would rest behind this car.
        ///
        /// <para>The same three lines <see cref="DriverPreviewRenderer"/> uses, and for the same reason:
        /// <c>SnapToTarget</c> is the code that places the chase camera after a respawn, so it is the one
        /// expression of where that camera belongs. A tool carrying its own distance, height and
        /// look-ahead photographs a framing the game does not use.</para>
        /// </summary>
        private static void PlaceLikeTheRig(ChaseCamera chase, VehicleController vehicle, Camera camera)
        {
            chase.SetTarget(vehicle.transform, vehicle.GetComponent<Rigidbody>());
            chase.SnapToTarget();

            Transform rig = chase.transform;
            camera.transform.SetPositionAndRotation(rig.position, rig.rotation);

            var rigCamera = chase.GetComponent<Camera>();
            camera.fieldOfView = rigCamera != null ? rigCamera.fieldOfView : 60f;
        }
    }
}
