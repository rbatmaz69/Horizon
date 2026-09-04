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
                RenderEndViews(camera, car, directory);

                Debug.Log($"[Horizon] Car preview written to {directory}/CarPreview_Front.png, _Rear.png, "
                          + "_Side.png, one _Side_<body>.png per ambient body type and a "
                          + "_Front_<body>.png and _Rear_<body>.png per player body.");
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
        /// <para>Ten silhouettes cannot be judged from a table of cross-sections — the numbers say
        /// nothing about whether a van reads as a van — and this project already reviews geometry by
        /// rendering it rather than by opening the editor.</para>
        ///
        /// <para><b>Two views each, and the second one is not a luxury.</b> Side-on is the view
        /// proportion lives in: a roofline, an overhang and where the cabin sits over the wheelbase. But
        /// it is also the one view a player never has of any car except their own, and everything that
        /// tells one of these apart from behind — the lamp cluster, the pipes, whether the tailgate has
        /// a window in it — is invisible in it. A tail that was wrong stayed wrong for as long as the
        /// only render of it was a profile.</para>
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
            // camera framing is identical between its side view and these and the ten can be compared
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
                        // Lifted by the pool's own constant rather than by the profile's ride height:
                        // a traffic mesh already carries the difference between the two baked into its
                        // vertices, so this is exactly what TrafficDirector does to it in the world.
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
        /// Three-quarter front and rear views of every body at <i>full</i> detail, which is where the
        /// lamps, the grille, the tailpipes and the tailgate glass live.
        ///
        /// <para>The reduced traffic body has none of those — <c>BuildTrafficBody</c> skips the whole
        /// detail pass — so these are built with <c>BuildBody</c> and wear the player's materials, and
        /// they are the only render in the project that shows what either end of a car actually looks
        /// like. <see cref="RenderTrafficProfiles"/>'s side elevations own proportion; these own the
        /// furniture, and the two questions genuinely need different pictures.</para>
        ///
        /// <para>Three-quarter rather than straight-on: a flat elevation of a flat panel hides which of
        /// the lamps stand proud of it and where the pipes sit under the bumper.</para>
        /// </summary>
        private static void RenderEndViews(Camera camera, GameObject car, string directory)
        {
            Material[] shared = PlayerMaterials();
            Material[] wheelMaterials = WheelMaterials();

            car.SetActive(false);

            try
            {
                foreach (CarMeshBuilder.CarProfile profile in CarMeshBuilder.PlayerProfiles)
                {
                    Mesh mesh = CarMeshBuilder.BuildBody(profile, $"Ends_{profile.Name}");
                    Mesh wheelMesh = CarMeshBuilder.BuildWheel(
                        profile.WheelRadius, profile.TyreWidth, 18, $"EndsWheel_{profile.Name}",
                        profile.RimFraction, profile.Rim);

                    var stand = new GameObject($"Ends_{profile.Name}");

                    try
                    {
                        stand.transform.position = StagePosition + Vector3.up * profile.RideHeight;
                        stand.AddComponent<MeshFilter>().sharedMesh = mesh;

                        MeshRenderer standRenderer = stand.AddComponent<MeshRenderer>();
                        if (shared != null)
                        {
                            standRenderer.sharedMaterials = shared;
                        }

                        AddThumbnailWheels(stand.transform, profile, wheelMesh, wheelMaterials);

                        // High enough to look into an open load bed, low enough that the tail panel is
                        // still a panel rather than a sliver. This is the only render in the project
                        // that can show either, and the pickup's bed needs it as much as the lamps do.
                        RenderFrom(camera, stand.transform, new Vector3(4.2f, 3.0f, -9.0f),
                            Path.Combine(directory, $"CarPreview_Rear_{profile.Name}.png"));
                        RenderFrom(camera, stand.transform, new Vector3(4.6f, 1.9f, 9.4f),
                            Path.Combine(directory, $"CarPreview_Front_{profile.Name}.png"));
                    }
                    finally
                    {
                        Object.DestroyImmediate(stand);
                        Object.DestroyImmediate(mesh);
                        Object.DestroyImmediate(wheelMesh);
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

                // A wheel per body, hung four times under it. The player's shell carries no wheels —
                // they are separate objects on the prefab, hung off the suspension — so a thumbnail of
                // the shell alone is a car up on blocks. Built here rather than loaded from the
                // generated asset because this runs before the prefab does, and per body rather than
                // once because the ten no longer share a tyre.
                Material[] wheelMaterials = WheelMaterials();

                foreach (CarMeshBuilder.CarProfile profile in CarMeshBuilder.PlayerProfiles)
                {
                    Mesh mesh = CarMeshBuilder.BuildBody(profile, $"Thumb_{profile.Name}");
                    Mesh wheelMesh = CarMeshBuilder.BuildWheel(
                        profile.WheelRadius, profile.TyreWidth, 18, $"ThumbWheel_{profile.Name}",
                        profile.RimFraction, profile.Rim);

                    var stand = new GameObject($"Thumb_{profile.Name}");

                    try
                    {
                        // Lifted so the wheels stand where the road would be. Its own ride height, not
                        // one shared number: framed on its origin every body is framed on a different
                        // part of itself, and an off-roader lifted by a fastback's 0.74 stands with its
                        // tyres twelve centimetres into the tarmac.
                        stand.transform.position = StagePosition + Vector3.up * profile.RideHeight;

                        stand.AddComponent<MeshFilter>().sharedMesh = mesh;

                        MeshRenderer renderer = stand.AddComponent<MeshRenderer>();
                        if (shared != null)
                        {
                            renderer.sharedMaterials = shared;
                        }

                        AddThumbnailWheels(stand.transform, profile, wheelMesh, wheelMaterials);

                        RenderThumbnail(camera, stand.transform,
                            $"{UiFolder}/CarThumb_{profile.Name}.png");
                    }
                    finally
                    {
                        Object.DestroyImmediate(stand);
                        Object.DestroyImmediate(mesh);
                        Object.DestroyImmediate(wheelMesh);
                    }
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
        /// <para>The drop comes off the profile rather than off the loaded <c>VehicleConfig</c>, and
        /// that is not a shortcut: this runs before the configs exist, and the profile is where the
        /// number is authored anyway — <c>VehicleConfigPresets</c> copies it out of here into the asset,
        /// not the other way round.</para>
        /// </summary>
        private static void AddThumbnailWheels(
            Transform parent, in CarMeshBuilder.CarProfile profile, Mesh mesh, Material[] materials)
        {
            float restLength = profile.SuspensionRestLength;

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
        ///
        /// <para><b>It is also the one preview here that deliberately does not run post</b>, which is why
        /// it does not go through <see cref="PreviewCapture"/>. Post-processing on a camera clearing to
        /// alpha zero does not preserve that alpha, and the thumbnail needs it. That is the right answer
        /// on its own terms too: this is drawn on the menu's <c>ScreenSpaceOverlay</c> canvas, which URP
        /// composites after the post stack, so a tone-mapped thumbnail would carry the tone map twice
        /// where the world it depicts carries it once.</para>
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

        /// <summary>
        /// A studio shot of one body, for a human to judge the mesh by.
        ///
        /// <para>Post is on: these are looked at to decide whether a car reads well, and the car the
        /// player sees is tone mapped. Judging a shell through an untone-mapped frame would be tuning
        /// against a picture the game never draws.</para>
        /// </summary>
        private static void RenderFrom(Camera camera, Transform target, Vector3 offset, string filePath)
        {
            Vector3 focus = target.position + new Vector3(0f, 0.35f, 0f);
            camera.transform.position = focus + offset;
            camera.transform.rotation = Quaternion.LookRotation(focus - camera.transform.position, Vector3.up);

            PreviewCapture.Shoot(camera, Width, Height, filePath, fog: false);
        }
    }
}
