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

        /// <summary>Where the menu's car thumbnails live, alongside the generated control sprites.</summary>
        private const string UiFolder = "Assets/_Project/Art/UI";

        /// <summary>
        /// Thumbnail size. Two to one, because a car seen from the side is, and a power of two in both
        /// axes so the importer never has to rescale it.
        /// </summary>
        private const int ThumbWidth = 512;

        private const int ThumbHeight = 256;

        /// <summary>
        /// Rolling radius, matching <c>VehicleConfig.WheelRadius</c> on every one of the five cars. See
        /// <see cref="AddThumbnailWheels"/> for why it is a literal here.
        /// </summary>
        private const float WheelRadius = 0.44f;

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

                RenderTrafficProfiles(camera, car, directory);

                Debug.Log($"[Horizon] Car preview written to {directory}/CarPreview_Front.png, _Rear.png, "
                          + $"_Side.png and one _Side_<body>.png per ambient body type.");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(lightObject);
                Object.DestroyImmediate(car);
            }
        }

        /// <summary>
        /// A side elevation of every ambient body type, on the same stage and the same camera as the
        /// player's car.
        ///
        /// <para>Four silhouettes cannot be judged from a table of cross-sections — the numbers say
        /// nothing about whether a van reads as a van — and this project already reviews geometry by
        /// rendering it rather than by opening the editor. Side-on only, because that is the view
        /// proportion lives in: a roofline, an overhang and where the cabin sits over the wheelbase.</para>
        ///
        /// <para>Built from the mesh straight out of <see cref="CarMeshBuilder"/> rather than from the
        /// saved assets, so this works before a rebuild has ever run and cannot show a stale shape.
        /// It borrows the player car's materials, which is what puts glass and lamps in the right
        /// slots without a second material table to keep in step.</para>
        /// </summary>
        private static void RenderTrafficProfiles(Camera camera, GameObject car, string directory)
        {
            Material[] shared = TrafficMaterials();

            // The player's car is standing on the same spot. Switched off rather than moved, so the
            // camera framing is identical between its side view and these and the five can be compared
            // by flicking between the files.
            car.SetActive(false);

            try
            {
                foreach (CarMeshBuilder.CarProfile profile in CarMeshBuilder.TrafficProfiles)
                {
                    Mesh mesh = CarMeshBuilder.BuildTrafficBody(profile, new System.Collections.Generic.List<int>());
                    var stand = new GameObject($"Preview_{profile.Name}");

                    try
                    {
                        // Lifted so the wheels stand where the road would be. Without it each body is
                        // framed on a different part of itself and the shots cannot be compared, which
                        // is the entire point of rendering them.
                        stand.transform.position =
                            StagePosition + Vector3.up * CarMeshBuilder.TrafficRideHeight;

                        stand.AddComponent<MeshFilter>().sharedMesh = mesh;

                        MeshRenderer standRenderer = stand.AddComponent<MeshRenderer>();
                        if (shared != null)
                        {
                            standRenderer.sharedMaterials = shared;
                        }

                        RenderFrom(camera, stand.transform, new Vector3(11f, 0.5f, 0f),
                            Path.Combine(directory, $"CarPreview_Side_{profile.Name}.png"));
                    }
                    finally
                    {
                        Object.DestroyImmediate(stand);
                        Object.DestroyImmediate(mesh);
                    }
                }
            }
            finally
            {
                car.SetActive(true);
            }
        }

        /// <summary>
        /// The five slots an ambient car is actually rendered with, in the order
        /// <c>PrototypeSetup.BuildTraffic</c> assigns them.
        ///
        /// <para>Not the player car's set, which was the obvious shortcut and is wrong in the one slot
        /// that matters: on the player's body the chrome slot is the wheel <i>rim</i>, while a reduced
        /// body puts its whole wheel there and takes the tyre material. Borrowing the player's array
        /// rendered every ambient car on bright chrome wheels — which looks like a styling choice rather
        /// than like a preview lying to you, and is exactly the kind of thing a preview exists to not
        /// do.</para>
        ///
        /// <para>Loaded by path rather than through <c>PrototypeMaterials</c>, which is private to the
        /// setup tool. If a rebuild has never run these do not exist yet and the bodies render
        /// untextured, which is still a usable silhouette.</para>
        /// </summary>
        private static Material[] TrafficMaterials()
        {
            const string folder = "Assets/_Project/Art/Materials";

            Material body = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_TrafficSlate.mat");
            Material glass = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_CarGlass.mat");
            Material lamp = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_WindowDay.mat");
            Material tyre = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_Tyre.mat");

            if (body == null || glass == null || lamp == null || tyre == null)
            {
                Debug.LogWarning("[Horizon] Car preview: the traffic materials are missing, so the body "
                                 + "types are rendered untextured. Run Rebuild Prototype Scene first.");
                return null;
            }

            return new[] { body, glass, lamp, lamp, tyre };
        }

        /// <summary>
        /// Renders one side-on thumbnail per body into <c>Assets/_Project/Art/UI</c>, for the garage
        /// page to put next to each car's name.
        ///
        /// <para><b>Full detail, not the traffic bodies</b> that <see cref="RenderTrafficProfiles"/>
        /// shoots. These are what the player is about to drive, and the reduced bodies have no grille,
        /// no recessed lamps and no exhausts — a picture of a different car.</para>
        ///
        /// <para><b>Called before the prefab is built, not after.</b> It needs no prefab, only
        /// <see cref="CarMeshBuilder"/>, and <c>TouchUiSetup</c> needs the finished sprites <i>during</i>
        /// the Bootstrap scene build. The existing <see cref="Render"/> keeps its place at the end of the
        /// rebuild, where it is a look at the car rather than an input to it.</para>
        ///
        /// <para>Transparent background, so a thumbnail sits on the menu's own panel rather than in a
        /// grey box of its own. That is the reason for the RGBA formats and for clearing to a colour
        /// with no alpha.</para>
        /// </summary>
        public static void RenderUiThumbnails()
        {
            HorizonAssetUtility.EnsureFolder(UiFolder);

            Material[] shared = PlayerMaterials();
            var lightObject = new GameObject("ThumbnailLight");
            var cameraObject = new GameObject("ThumbnailCamera");

            try
            {
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.color = new Color(1f, 0.97f, 0.90f);
                lightObject.transform.rotation = Quaternion.Euler(38f, 145f, 0f);

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.fieldOfView = 30f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.enabled = false;

                // One wheel mesh, hung four times under every body. The player's shell carries no wheels
                // — they are separate objects on the prefab, hung off the suspension — so a thumbnail of
                // the shell alone is a car up on blocks. Built here rather than loaded from the
                // generated asset because this runs before the prefab does.
                Mesh wheelMesh = CarMeshBuilder.BuildWheel(WheelRadius, 0.34f, 18, "ThumbWheel");
                Material[] wheelMaterials = WheelMaterials();

                try
                {
                    foreach (CarMeshBuilder.CarProfile profile in CarMeshBuilder.PlayerProfiles)
                    {
                        Mesh mesh = CarMeshBuilder.BuildBody(profile, $"Thumb_{profile.Name}");
                        var stand = new GameObject($"Thumb_{profile.Name}");

                        try
                        {
                            // Lifted so the wheels stand where the road would be, for the same reason the
                            // side views are: framed on its own origin, every body is framed on a
                            // different part of itself and the five cannot be compared.
                            stand.transform.position =
                                StagePosition + Vector3.up * CarMeshBuilder.TrafficRideHeight;

                            stand.AddComponent<MeshFilter>().sharedMesh = mesh;

                            MeshRenderer renderer = stand.AddComponent<MeshRenderer>();
                            if (shared != null)
                            {
                                renderer.sharedMaterials = shared;
                            }

                            AddThumbnailWheels(stand.transform, wheelMesh, wheelMaterials);

                            RenderThumbnail(camera, stand.transform,
                                $"{UiFolder}/CarThumb_{profile.Name}.png");
                        }
                        finally
                        {
                            Object.DestroyImmediate(stand);
                            Object.DestroyImmediate(mesh);
                        }
                    }
                }
                finally
                {
                    Object.DestroyImmediate(wheelMesh);
                }

                Debug.Log($"[Horizon] {CarMeshBuilder.PlayerProfiles.Length} car thumbnails written to "
                          + $"{UiFolder}/CarThumb_<body>.png.");
            }
            finally
            {
                Object.DestroyImmediate(lightObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        /// <summary>
        /// Hangs the four wheels where the prefab hangs them: at the track and wheelbase the arches were
        /// carved for, dropped by the suspension rest length so the tyres sit in the openings rather
        /// than beside them.
        ///
        /// <para>The 0.30 m drop is <c>VehicleConfig.SuspensionRestLength</c>, restated rather than read:
        /// this runs before the configs are loaded, and it is the same number on all five cars by
        /// construction — see <c>VehicleConfigPresets</c> on why the running gear cannot vary.</para>
        /// </summary>
        private static void AddThumbnailWheels(Transform parent, Mesh mesh, Material[] materials)
        {
            const float restLength = 0.30f;

            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? -CarMeshBuilder.TrackHalfWidth : CarMeshBuilder.TrackHalfWidth;
                float z = (i & 2) == 0 ? -CarMeshBuilder.WheelBaseHalf : CarMeshBuilder.WheelBaseHalf;

                var wheel = new GameObject($"Wheel{i}");
                wheel.transform.SetParent(parent, false);
                wheel.transform.localPosition = new Vector3(x, -restLength, z);

                wheel.AddComponent<MeshFilter>().sharedMesh = mesh;

                MeshRenderer renderer = wheel.AddComponent<MeshRenderer>();
                if (materials != null)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        /// <summary>Tyre and rim, in <c>CarMeshBuilder</c>'s wheel submesh order.</summary>
        private static Material[] WheelMaterials()
        {
            const string folder = "Assets/_Project/Art/Materials";

            Material tyre = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_Tyre.mat");
            Material rim = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_CarRim.mat");

            return tyre != null && rim != null ? new[] { tyre, rim } : null;
        }

        /// <summary>
        /// The player car's five slots, in <c>CarMeshBuilder</c>'s constant order. Loaded by path
        /// because <c>PrototypeMaterials</c> is private to the setup tool; missing assets give an
        /// untextured but still readable silhouette. See <see cref="TrafficMaterials"/> for why the two
        /// sets are not interchangeable.
        /// </summary>
        private static Material[] PlayerMaterials()
        {
            const string folder = "Assets/_Project/Art/Materials";

            Material body = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_CarBody.mat");
            Material glass = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_CarGlass.mat");
            Material front = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_LightFront.mat");
            Material rear = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_LightRear.mat");
            Material rim = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/M_CarRim.mat");

            if (body == null || glass == null || front == null || rear == null || rim == null)
            {
                Debug.LogWarning("[Horizon] Car thumbnails: the car materials are missing, so the bodies "
                                 + "are rendered untextured. They will be correct on the next rebuild.");
                return null;
            }

            return new[] { body, glass, front, rear, rim };
        }

        /// <summary>
        /// One side-on shot, straight to a sprite asset with an alpha channel.
        ///
        /// <para>Separate from <see cref="RenderFrom"/> because that one writes an opaque RGB24 PNG to a
        /// path outside the project, for looking at. This writes RGBA into <c>Assets</c> and imports it
        /// as a sprite, and the two differences are not worth a pile of parameters on one method.</para>
        /// </summary>
        private static void RenderThumbnail(Camera camera, Transform target, string assetPath)
        {
            Vector3 focus = target.position + new Vector3(0f, 0.15f, 0f);
            camera.transform.position = focus + new Vector3(12f, 1.2f, 0f);
            camera.transform.rotation =
                Quaternion.LookRotation(focus - camera.transform.position, Vector3.up);

            var renderTexture = new RenderTexture(ThumbWidth, ThumbHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
            };

            var texture = new Texture2D(ThumbWidth, ThumbHeight, TextureFormat.RGBA32, false);
            RenderTexture previous = RenderTexture.active;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0f, 0f, ThumbWidth, ThumbHeight), 0, 0);

                // SaveSpriteTexture applies, encodes, writes and reimports synchronously — the last of
                // those being what stops the sprite coming back null when the UI asks for it later in
                // this same run.
                HorizonAssetUtility.SaveSpriteTexture(texture, assetPath);
                texture = null;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;

                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }

                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
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
