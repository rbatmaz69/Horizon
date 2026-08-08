using System.Collections.Generic;
using Horizon.Atmosphere;
using Horizon.Core;
using Horizon.Game;
using Horizon.Input;
using Horizon.Vehicle;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Builds the entire prototype — scenes, prefab, meshes, materials — from code.
    ///
    /// Unity scenes and prefabs are GUID-linked YAML, so hand-authoring them is fragile and
    /// unreviewable. Generating them from a tool instead means the setup is reproducible, the diff
    /// that matters is this file, and re-running it after a change is one menu click.
    ///
    /// Configs and materials are created only if missing, so hand-tuning survives a rebuild.
    /// Meshes, prefabs and scenes are derived output and are always regenerated.
    /// </summary>
    public static class PrototypeSetup
    {
        private const string ProjectRoot = "Assets/_Project";
        private const string ScenesFolder = ProjectRoot + "/Scenes";
        private const string SettingsFolder = ProjectRoot + "/Settings";
        private const string MaterialsFolder = ProjectRoot + "/Art/Materials";
        private const string GeneratedFolder = ProjectRoot + "/Art/Models/Generated";
        private const string PrefabsFolder = ProjectRoot + "/Prefabs/Vehicles";

        private const string BootstrapScenePath = ScenesFolder + "/Bootstrap.unity";
        private const string WorldScenePath = ScenesFolder + "/World_MountainPass.unity";
        private const string WorldSceneName = "World_MountainPass";
        private const string VehiclePrefabPath = PrefabsFolder + "/Vehicle_Prototype.prefab";
        private const string VehicleConfigPath = SettingsFolder + "/VehicleConfig_Prototype.asset";
        private const string TimeOfDayProfilePath = SettingsFolder + "/TimeOfDayProfile_Default.asset";

        [MenuItem("Tools/Horizon/Rebuild Prototype Scene", priority = 0)]
        public static void Rebuild()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EnsureFolders();

            // Make sure the configs exist on disk, but deliberately do not keep the references:
            // see the note on LoadVehicleConfig for why they must be re-loaded after a scene switch.
            CreateVehicleConfig();
            CreateTimeOfDayProfile();

            // Start from a throwaway scene so the temporary objects used to author the prefab never
            // touch whatever the user had open.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject vehiclePrefab = BuildVehiclePrefab();
            if (vehiclePrefab == null)
            {
                return;
            }

            BuildWorldScene(vehiclePrefab);
            BuildBootstrapScene();
            RegisterScenesInBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Render the preview here, as part of the rebuild. Running it as a separate command invites
            // rendering the previous car by mistake, which is exactly what happened once. The temporary
            // rig dirties the current scene, but that scene is already saved and is replaced below.
            CarPreviewRenderer.Render();

            // Leave the editor in the state you actually want to work in: Bootstrap active, with the
            // world open alongside it. Opening Bootstrap alone looks broken — it holds one object,
            // no camera and no geometry, because the world is loaded at runtime. GameBootstrap skips
            // its additive load when the scene is already open, so Play works either way.
            EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);

            Debug.Log(
                "[Horizon] Prototype rebuilt. Both scenes are open: Bootstrap holds the persistent "
                + "systems, World_MountainPass holds the road and the car. Press Play and drive with "
                + "WASD or a gamepad; the overlay switches control schemes.");
        }

        [MenuItem("Tools/Horizon/Open Bootstrap Scene", priority = 20)]
        public static void OpenBootstrap()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
            }
        }

        private static void EnsureFolders()
        {
            HorizonAssetUtility.EnsureFolder(ScenesFolder);
            HorizonAssetUtility.EnsureFolder(SettingsFolder);
            HorizonAssetUtility.EnsureFolder(MaterialsFolder);
            HorizonAssetUtility.EnsureFolder(GeneratedFolder);
            HorizonAssetUtility.EnsureFolder(PrefabsFolder);
            HorizonAssetUtility.EnsureFolder(ProjectRoot + "/Art/Skybox");
            HorizonAssetUtility.EnsureFolder(ProjectRoot + "/Audio");
            HorizonAssetUtility.EnsureFolder(ProjectRoot + "/Prefabs/World");
        }

        /// <summary>Materials for the prototype. Created once, then left alone so retints survive.</summary>
        private sealed class PrototypeMaterials
        {
            public readonly Material RoadSurface;
            public readonly Material RoadShoulder;
            public readonly Material Concrete;
            public readonly Material GuardRail;
            public readonly Material Grass;
            public readonly Material Rock;
            public readonly Material CarBody;
            public readonly Material Tyre;
            public readonly Material CarGlass;
            public readonly Material CarRim;
            public readonly Material LightFront;
            public readonly Material LightRear;
            public readonly Material Smoke;

            public PrototypeMaterials()
            {
                // New names rather than reusing M_Asphalt: materials are created only if missing, so an
                // existing one would never pick up a texture.
                RoadShape roadShape = RoadShape.Default;

                Texture2D surfaceTexture = HorizonAssetUtility.LoadOrCreateTexture(
                    ProjectRoot + "/Art/T_RoadSurface.png",
                    () => RoadTextureBuilder.BuildSurface(roadShape),
                    anisoLevel: 8);

                Texture2D shoulderTexture = HorizonAssetUtility.LoadOrCreateTexture(
                    ProjectRoot + "/Art/T_RoadShoulder.png",
                    () => RoadTextureBuilder.BuildShoulder(),
                    anisoLevel: 4);

                RoadSurface = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_RoadSurface.mat", "M_RoadSurface", Color.white, 0.34f, 0f, null,
                    surfaceTexture);

                RoadShoulder = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_RoadShoulder.mat", "M_RoadShoulder", Color.white, 0.12f, 0f, null,
                    shoulderTexture);
                Grass = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Grass.mat", "M_Grass", new Color(0.36f, 0.48f, 0.26f), 0.08f);
                Rock = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Rock.mat", "M_Rock", new Color(0.44f, 0.39f, 0.34f), 0.12f);
                Concrete = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Concrete.mat", "M_Concrete", new Color(0.52f, 0.51f, 0.49f), 0.20f);
                GuardRail = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_GuardRail.mat", "M_GuardRail", new Color(0.66f, 0.68f, 0.70f), 0.55f, 0.6f);
                CarBody = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_CarBody.mat", "M_CarBody", new Color(0.86f, 0.36f, 0.17f), 0.62f, 0.1f);
                Tyre = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Tyre.mat", "M_Tyre", new Color(0.07f, 0.07f, 0.08f), 0.18f);

                // Glass is dark and smooth rather than transparent: an opaque tint costs nothing on
                // mobile and reads perfectly well at this level of stylisation.
                CarGlass = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_CarGlass.mat", "M_CarGlass", new Color(0.10f, 0.13f, 0.17f), 0.92f);
                CarRim = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_CarRim.mat", "M_CarRim", new Color(0.62f, 0.64f, 0.67f), 0.78f, 0.85f);
                // Emissive so the lamps read as lamps. VehicleLights animates the glow at runtime
                // through a property block, which only works because _EMISSION is on here.
                LightFront = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_LightFront.mat", "M_LightFront",
                    new Color(0.95f, 0.94f, 0.82f), 0.9f, 0f,
                    new Color(0.95f, 0.92f, 0.78f) * 0.3f);
                LightRear = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_LightRear.mat", "M_LightRear",
                    new Color(0.42f, 0.05f, 0.04f), 0.85f, 0f,
                    new Color(1f, 0.10f, 0.06f) * 0.5f);

                Texture2D smokeTexture = HorizonAssetUtility.LoadOrCreateSoftCircleTexture(
                    ProjectRoot + "/Art/T_SmokePuff.png");
                Smoke = HorizonAssetUtility.LoadOrCreateParticleMaterial(
                    MaterialsFolder + "/M_ExhaustSmoke.mat", "M_ExhaustSmoke", smokeTexture,
                    new Color(0.62f, 0.62f, 0.64f, 0.5f));
            }
        }

        private static VehicleConfig CreateVehicleConfig()
        {
            return HorizonAssetUtility.LoadOrCreate(
                VehicleConfigPath,
                ScriptableObject.CreateInstance<VehicleConfig>);
        }

        /// <summary>
        /// Re-loads the vehicle config from disk.
        ///
        /// This exists because an asset reference does **not** survive
        /// <c>EditorSceneManager.NewScene(..., Single)</c>: after the scene switch the managed
        /// wrapper no longer resolves, and assigning it through a SerializedProperty silently writes
        /// null — no exception, no warning, just a broken prefab. So every function that switches
        /// scenes loads the assets it needs afterwards, by path, rather than receiving them as
        /// arguments from before the switch.
        /// </summary>
        private static VehicleConfig LoadVehicleConfig()
        {
            return AssetDatabase.LoadAssetAtPath<VehicleConfig>(VehicleConfigPath);
        }

        /// <summary>Re-loads the time-of-day profile from disk. See <see cref="LoadVehicleConfig"/>.</summary>
        private static TimeOfDayProfile LoadTimeOfDayProfile()
        {
            return AssetDatabase.LoadAssetAtPath<TimeOfDayProfile>(TimeOfDayProfilePath);
        }

        private static TimeOfDayProfile CreateTimeOfDayProfile()
        {
            return HorizonAssetUtility.LoadOrCreate(
                TimeOfDayProfilePath,
                () =>
                {
                    var profile = ScriptableObject.CreateInstance<TimeOfDayProfile>();

                    // Warm, inviting palette: cool blue night, amber sunrise and sunset, neutral noon.
                    profile.SunColor = HorizonAssetUtility.BuildGradient(
                        (0.00f, new Color(0.18f, 0.24f, 0.42f)),
                        (0.25f, new Color(0.95f, 0.52f, 0.28f)),
                        (0.35f, new Color(1.00f, 0.86f, 0.70f)),
                        (0.50f, new Color(1.00f, 0.97f, 0.90f)),
                        (0.70f, new Color(1.00f, 0.72f, 0.42f)),
                        (0.78f, new Color(0.93f, 0.40f, 0.24f)),
                        (1.00f, new Color(0.18f, 0.24f, 0.42f)));

                    profile.AmbientColor = HorizonAssetUtility.BuildGradient(
                        (0.00f, new Color(0.07f, 0.09f, 0.16f)),
                        (0.28f, new Color(0.38f, 0.34f, 0.36f)),
                        (0.50f, new Color(0.55f, 0.58f, 0.62f)),
                        (0.74f, new Color(0.42f, 0.32f, 0.31f)),
                        (1.00f, new Color(0.07f, 0.09f, 0.16f)));

                    profile.FogColor = HorizonAssetUtility.BuildGradient(
                        (0.00f, new Color(0.10f, 0.13f, 0.22f)),
                        (0.27f, new Color(0.86f, 0.62f, 0.46f)),
                        (0.50f, new Color(0.72f, 0.80f, 0.88f)),
                        (0.75f, new Color(0.90f, 0.56f, 0.38f)),
                        (1.00f, new Color(0.10f, 0.13f, 0.22f)));

                    return profile;
                });
        }

        /// <summary>
        /// Builds the vehicle: a generated low-poly body and four generated wheels on pivots.
        ///
        /// The physics side is untouched by the shape of the art — the raycast wheels work off the
        /// anchors and the config, so the body mesh can be replaced freely without retuning handling.
        /// </summary>
        private static GameObject BuildVehiclePrefab()
        {
            // Loaded here, after Rebuild's scene switch, not passed in from before it.
            var materials = new PrototypeMaterials();
            VehicleConfig config = LoadVehicleConfig();
            if (config == null)
            {
                Debug.LogError($"[Horizon] Could not load {VehicleConfigPath}. Aborting prefab build.");
                return null;
            }

            var root = new GameObject("Vehicle_Prototype");

            var body = root.AddComponent<Rigidbody>();
            body.mass = config.Mass;

            // Collider spans the body shell. It only matters for hitting scenery — the wheels are
            // raycasts, so this box has no say in how the car drives.
            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.06f, 0f);
            collider.size = new Vector3(2.00f, 1.18f, 4.70f);

            Mesh bodyMesh = HorizonAssetUtility.ReplaceAsset(
                CarMeshBuilder.BuildBody(), GeneratedFolder + "/CarBodyMesh.asset");

            // Material order must match the Submesh constants in CarMeshBuilder.
            GameObject bodyObject = CreateMeshObject(
                root.transform,
                "Body",
                bodyMesh,
                new[]
                {
                    materials.CarBody,
                    materials.CarGlass,
                    materials.LightFront,
                    materials.LightRear,
                    materials.CarRim,
                },
                addCollider: false,
                markStatic: false);

            Light[] headlights = CreateHeadlights(root.transform);
            CreateExhaustEmitters(root.transform, materials);

            AudioSource engineSource = CreateAudioSource(root.transform, "Audio_Engine", 0.25f);
            AudioSource windSource = CreateAudioSource(root.transform, "Audio_Wind", 0f);

            // Reverb on the engine layer only. Configured as a stone corridor but starting silent — the
            // level is faded in from the cover probe, so an open road is unaffected.
            AudioReverbFilter reverb = engineSource.gameObject.AddComponent<AudioReverbFilter>();
            reverb.reverbPreset = AudioReverbPreset.User;
            reverb.room = -900f;
            reverb.roomHF = -400f;
            reverb.decayTime = 1.5f;
            reverb.decayHFRatio = 0.7f;
            reverb.reflectionsLevel = -400f;
            reverb.reverbDelay = 0.02f;
            reverb.diffusion = 100f;
            reverb.density = 100f;
            reverb.reverbLevel = -10000f;

            // One probe for the whole car: the lights and the engine note both read it, so they agree and
            // it costs a single raycast.
            VehicleCover cover = root.AddComponent<VehicleCover>();

            EngineAudio engineAudio = root.AddComponent<EngineAudio>();
            HorizonAssetUtility.Configure(engineAudio, serialized =>
            {
                serialized.FindProperty("engineSource").objectReferenceValue = engineSource;
                serialized.FindProperty("windSource").objectReferenceValue = windSource;
                serialized.FindProperty("engineReverb").objectReferenceValue = reverb;
                serialized.FindProperty("cover").objectReferenceValue = cover;
            });

            VehicleLights lights = root.AddComponent<VehicleLights>();
            HorizonAssetUtility.Configure(lights, serialized =>
            {
                HorizonAssetUtility.SetObjectArray(serialized, "headlights", headlights);
                serialized.FindProperty("bodyRenderer").objectReferenceValue =
                    bodyObject.GetComponent<MeshRenderer>();
                serialized.FindProperty("headlightMaterialIndex").intValue = CarMeshBuilder.HeadlightSubmesh;
                serialized.FindProperty("taillightMaterialIndex").intValue = CarMeshBuilder.TaillightSubmesh;
                serialized.FindProperty("cover").objectReferenceValue = root.GetComponent<VehicleCover>();
            });

            // Anchors sit at the top of the suspension travel; wheels hang below by spring length.
            // Track and wheelbase come from the mesh builder so the wheels always land in the arches
            // it carved — changing one without the other is how you get wheels inside the bodywork.
            const float trackX = CarMeshBuilder.TrackHalfWidth;
            const float baseZ = CarMeshBuilder.WheelBaseHalf;

            var anchorPositions = new[]
            {
                new Vector3(-trackX, 0f, baseZ),
                new Vector3(trackX, 0f, baseZ),
                new Vector3(-trackX, 0f, -baseZ),
                new Vector3(trackX, 0f, -baseZ),
            };
            var anchorNames = new[] { "Anchor_FL", "Anchor_FR", "Anchor_RL", "Anchor_RR" };

            var anchors = new Transform[4];
            var visuals = new Transform[4];

            // One shared wheel mesh for all four. Built with its axle on X so the controller can write
            // the pivot's rotation directly as spin plus steer, with no correcting child transform.
            // Wide enough to stand slightly proud of the arch, which is what makes the stance read.
            Mesh wheelMesh = HorizonAssetUtility.ReplaceAsset(
                CarMeshBuilder.BuildWheel(config.WheelRadius, 0.28f),
                GeneratedFolder + "/WheelMesh.asset");

            for (int i = 0; i < 4; i++)
            {
                var anchor = new GameObject(anchorNames[i]);
                anchor.transform.SetParent(root.transform, false);
                anchor.transform.localPosition = anchorPositions[i];
                anchors[i] = anchor.transform;

                var pivot = new GameObject(anchorNames[i].Replace("Anchor", "Wheel"));
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.localPosition = anchorPositions[i] - new Vector3(0f, config.SuspensionRestLength, 0f);
                visuals[i] = pivot.transform;

                pivot.AddComponent<MeshFilter>().sharedMesh = wheelMesh;
                pivot.AddComponent<MeshRenderer>().sharedMaterials =
                    new[] { materials.Tyre, materials.CarRim };
            }

            VehicleController controller = root.AddComponent<VehicleController>();
            HorizonAssetUtility.Configure(controller, serialized =>
            {
                serialized.FindProperty("config").objectReferenceValue = config;
                HorizonAssetUtility.SetObjectArray(serialized, "wheelAnchors", anchors);
                HorizonAssetUtility.SetObjectArray(serialized, "wheelVisuals", visuals);
            });

            HorizonAssetUtility.AssertReferenceAssigned(controller, "config");

            HorizonAssetUtility.EnsureFolder(PrefabsFolder);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, VehiclePrefabPath);
            Object.DestroyImmediate(root);

            // Check the saved asset too, not just the scene instance it was built from.
            HorizonAssetUtility.AssertReferenceAssigned(prefab.GetComponent<VehicleController>(), "config");
            return prefab;
        }

        private static void BuildWorldScene(GameObject vehiclePrefab)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Everything asset-related is resolved after the scene switch above, never before it.
            var materials = new PrototypeMaterials();
            TimeOfDayProfile timeOfDayProfile = LoadTimeOfDayProfile();
            VehicleConfig config = LoadVehicleConfig();

            var worldRoot = new GameObject("World");

            // --- Road centreline. Kept in the scene so it can be re-edited and regenerated.
            var pathObject = new GameObject("RoadPath");
            pathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath path = pathObject.AddComponent<RoadPath>();

            RoadCourse course = MountainPassCourse.Build();
            path.SetControlPoints(course.ControlPoints);
            ReportCourse(course, path);

            // --- Generated geometry.
            RoadShape roadShape = RoadShape.Default;

            TerrainShape terrainShape = TerrainShape.Default;

            Mesh roadMesh = RoadMeshBuilder.BuildRoad(path, roadShape, "RoadMesh");

            // Use the returned instance: it is the imported asset, not the in-memory mesh.
            roadMesh = HorizonAssetUtility.ReplaceAsset(roadMesh, GeneratedFolder + "/RoadMesh.asset");

            // Material order follows the Submesh constants on RoadMeshBuilder.
            GameObject roadObject = CreateMeshObject(worldRoot.transform, "Road", roadMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder });

            // The road is a chunk of its own with a radius large enough that it never unloads: it is a
            // thin ribbon costing little, and the car is by definition standing on it.
            WorldChunk roadChunk = roadObject.AddComponent<WorldChunk>();
            roadChunk.RecalculateBounds();
            roadChunk.SetBounds(roadChunk.Center, 100000f);
            EditorUtility.SetDirty(roadChunk);

            // One field, shared: the terrain is built from it, the guard rails ask it where the ground falls
            // away, and the tunnel bodies use it to bury their feet. Building a second would be slow and
            // could disagree with the first.
            var field = new MountainField(path, terrainShape);

            ValidateRoadClearance(path, roadShape, field, course);

            BuildTerrainTiles(worldRoot.transform, field, terrainShape, materials);
            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);

            // --- Streaming.
            var streamingObject = new GameObject("Streaming");
            WorldStreamer streamer = streamingObject.AddComponent<WorldStreamer>();
            WorldStreamingDriver driver = streamingObject.AddComponent<WorldStreamingDriver>();
            HorizonAssetUtility.Configure(driver, serialized =>
                serialized.FindProperty("streamer").objectReferenceValue = streamer);

            // --- Sun and atmosphere.
            var sunObject = new GameObject("Sun");
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;

            var atmosphereObject = new GameObject("Atmosphere");
            TimeOfDayController timeOfDay = atmosphereObject.AddComponent<TimeOfDayController>();
            HorizonAssetUtility.Configure(timeOfDay, serialized =>
            {
                serialized.FindProperty("profile").objectReferenceValue = timeOfDayProfile;
                serialized.FindProperty("sun").objectReferenceValue = sun;
            });

            HorizonAssetUtility.AssertReferenceAssigned(timeOfDay, "profile");
            HorizonAssetUtility.AssertReferenceAssigned(timeOfDay, "sun");

            // --- Vehicle, dropped onto the road a little way in from the start.
            const float spawnDistance = 25f;
            Vector3 spawnDirection = path.GetDirectionAtDistance(spawnDistance);
            float rideHeight = config != null
                ? config.SuspensionRestLength + config.WheelRadius + 0.05f
                : 0.75f;

            // In the right-hand lane, not astride the centre line. Small thing, but with markings drawn
            // it is a large part of the road reading as something you drive on.
            Vector3 laneOffset = path.GetRightAtDistance(spawnDistance) * (roadShape.HalfWidth * 0.5f);

            Vector3 spawnPosition = path.GetPositionAtDistance(spawnDistance)
                                    + laneOffset
                                    + Vector3.up * rideHeight;

            var vehicle = (GameObject)PrefabUtility.InstantiatePrefab(vehiclePrefab);
            vehicle.transform.SetPositionAndRotation(
                spawnPosition,
                Quaternion.LookRotation(spawnDirection, Vector3.up));

            // --- Camera.
            var cameraObject = new GameObject("ChaseCamera");

            // Tagged, so Camera.main resolves — the streaming driver uses it to find the viewer.
            cameraObject.tag = "MainCamera";

            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60f;

            // Far plane sits inside the fog wall: anything beyond it is invisible anyway, and a
            // tighter plane is free performance on mobile.
            camera.farClipPlane = 600f;
            camera.nearClipPlane = 0.3f;
            cameraObject.AddComponent<AudioListener>();

            ChaseCamera chaseCamera = cameraObject.AddComponent<ChaseCamera>();
            HorizonAssetUtility.Configure(chaseCamera, serialized =>
            {
                serialized.FindProperty("target").objectReferenceValue = vehicle.transform;
                serialized.FindProperty("targetBody").objectReferenceValue = vehicle.GetComponent<Rigidbody>();

                // Layer 0 (Default) carries the terrain, so the camera pulls in instead of clipping
                // into the mountain on an uphill hairpin.
                serialized.FindProperty("obstacleMask").intValue = 1;
            });

            timeOfDay.Apply();

            // Rendered here, while the world objects are in the active scene and before it is saved, so
            // the temporary camera never ends up in the saved scene.
            CoursePreviewRenderer.Render(path);

            EditorSceneManager.SaveScene(scene, WorldScenePath);
        }

        private static void BuildBootstrapScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("GameBootstrap");
            GameBootstrap bootstrap = root.AddComponent<GameBootstrap>();
            DriveInputRouter router = root.AddComponent<DriveInputRouter>();
            DriveDebugOverlay overlay = root.AddComponent<DriveDebugOverlay>();

            HorizonAssetUtility.Configure(bootstrap, serialized =>
            {
                serialized.FindProperty("worldSceneName").stringValue = WorldSceneName;
                serialized.FindProperty("inputRouter").objectReferenceValue = router;
            });

            HorizonAssetUtility.Configure(overlay, serialized =>
            {
                SerializedProperty property = serialized.FindProperty("inputRouter");
                if (property != null)
                {
                    property.objectReferenceValue = router;
                }
            });

            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
        }

        private static void RegisterScenesInBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootstrapScenePath, true),
                new EditorBuildSettingsScene(WorldScenePath, true),
            };
        }

        /// <summary>
        /// Measures the finished course and logs it, warning on anything outside what the car can
        /// comfortably do.
        ///
        /// The M1 gate is "are the hairpins enjoyable", which only driving can answer — but grade and
        /// radius are numbers, and getting them wrong is the likeliest reason driving would feel bad.
        /// Checking them here means a bad profile table is caught before anyone drives it.
        /// </summary>
        private static void ReportCourse(RoadCourse course, RoadPath path)
        {
            float length = path.Length;
            const float step = 5f;

            float steepest = 0f;
            float steepestAt = 0f;
            float tightestRadius = float.MaxValue;
            float tightestAt = 0f;

            for (float distance = 0f; distance + step < length; distance += step)
            {
                Vector3 here = path.GetPositionAtDistance(distance);
                Vector3 ahead = path.GetPositionAtDistance(distance + step);

                float horizontal = new Vector2(ahead.x - here.x, ahead.z - here.z).magnitude;
                if (horizontal > 0.01f)
                {
                    float grade = Mathf.Abs(ahead.y - here.y) / horizontal * 100f;
                    if (grade > steepest)
                    {
                        steepest = grade;
                        steepestAt = distance;
                    }
                }

                // Radius from how much the heading swings over a known arc length.
                float turned = Vector3.Angle(
                    path.GetDirectionAtDistance(distance),
                    path.GetDirectionAtDistance(distance + step)) * Mathf.Deg2Rad;

                if (turned > 0.0005f)
                {
                    float radius = step / turned;
                    if (radius < tightestRadius)
                    {
                        tightestRadius = radius;
                        tightestAt = distance;
                    }
                }
            }

            float elevationGain = course.Summit.y - course.LowestElevation;

            Debug.Log(
                $"[Horizon] Pass course: {length:0} m long, {elevationGain:0} m of elevation, "
                + $"summit at {course.Summit.y:0} m. Steepest {steepest:0.0}% at {steepestAt:0} m, "
                + $"tightest radius {tightestRadius:0.0} m at {tightestAt:0} m. "
                + $"{course.Features.Count} features.");

            // The car's minimum radius is about 4.3 m at full lock and ~8 m at the reduced lock it has
            // at speed, so 12 m is the point where a hairpin stops being generous.
            if (tightestRadius < 12f)
            {
                Debug.LogWarning(
                    $"[Horizon] Tightest radius is {tightestRadius:0.0} m at {tightestAt:0} m — the car "
                    + "may need to reverse. Open up the hairpin radius in MountainPassCourse.");
            }

            if (steepest > 12f)
            {
                Debug.LogWarning(
                    $"[Horizon] Steepest grade is {steepest:0.0}% at {steepestAt:0} m. Above roughly 12% "
                    + "the car struggles uphill and runs away downhill; ease the grade or lengthen the leg.");
            }
        }

        /// <summary>
        /// Two spot lights at the front. Deliberately only two, with shadows off: additional realtime
        /// lights are the single most expensive thing this car could ask of a mid-range mobile GPU.
        /// They start disabled — <see cref="VehicleLights"/> switches them on when it gets dark.
        /// </summary>
        private static Light[] CreateHeadlights(Transform parent)
        {
            var offsets = new[]
            {
                new Vector3(0.47f, 0.20f, 2.05f),
                new Vector3(-0.47f, 0.20f, 2.05f),
            };

            var lights = new Light[offsets.Length];

            for (int i = 0; i < offsets.Length; i++)
            {
                var lightObject = new GameObject(i == 0 ? "Headlight_R" : "Headlight_L");
                lightObject.transform.SetParent(parent, false);
                lightObject.transform.localPosition = offsets[i];

                // Aimed slightly down, so the beam lands on the road rather than the horizon.
                lightObject.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);

                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Spot;
                light.spotAngle = 58f;
                light.innerSpotAngle = 26f;
                light.range = 48f;
                light.intensity = 3.2f;
                light.color = new Color(1f, 0.95f, 0.84f);
                light.shadows = LightShadows.None;
                light.enabled = false;

                lights[i] = light;
            }

            return lights;
        }

        /// <summary>
        /// An audio source for one engine layer. Kept mostly 2D with doppler off: the camera is
        /// permanently a few metres behind the car, so full 3D positioning would only add pitch
        /// artefacts on every corner without making anything clearer.
        ///
        /// The clip is generated at runtime by <see cref="EngineAudio"/>, so nothing is assigned here.
        /// </summary>
        private static AudioSource CreateAudioSource(Transform parent, string name, float spatialBlend)
        {
            var audioObject = new GameObject(name);
            audioObject.transform.SetParent(parent, false);

            AudioSource source = audioObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = spatialBlend;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = 30f;
            source.volume = 0f;

            return source;
        }

        /// <summary>Smoke emitters at the tailpipe mouths, pointing backwards out of the car.</summary>
        private static void CreateExhaustEmitters(Transform parent, PrototypeMaterials materials)
        {
            for (int i = 0; i < CarMeshBuilder.ExhaustOutlets.Length; i++)
            {
                var emitterObject = new GameObject(i == 0 ? "Exhaust_R" : "Exhaust_L");
                emitterObject.transform.SetParent(parent, false);
                emitterObject.transform.localPosition = CarMeshBuilder.ExhaustOutlets[i];
                emitterObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

                ParticleSystem particles = emitterObject.AddComponent<ParticleSystem>();

                ParticleSystem.MainModule main = particles.main;
                main.duration = 1f;
                main.loop = true;
                main.startLifetime = 1.1f;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 1.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.22f);
                main.startColor = new Color(0.72f, 0.72f, 0.74f, 0.42f);
                main.gravityModifier = -0.05f;
                main.maxParticles = 60;

                // World space, so the plume stays behind the car instead of being dragged along with it.
                main.simulationSpace = ParticleSystemSimulationSpace.World;

                ParticleSystem.EmissionModule emission = particles.emission;
                emission.rateOverTime = 8f;

                ParticleSystem.ShapeModule shape = particles.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 9f;
                shape.radius = 0.05f;

                // Puffs grow and fade as they drift.
                ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 2.4f));

                ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
                color.enabled = true;
                var fade = new Gradient();
                fade.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(Color.white, 1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(1f, 0.18f),
                        new GradientAlphaKey(0f, 1f),
                    });
                color.color = new ParticleSystem.MinMaxGradient(fade);

                var renderer = emitterObject.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = materials.Smoke;
                renderer.sortingFudge = 20f;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                emitterObject.AddComponent<ExhaustSmoke>();
            }
        }

        /// <summary>
        /// Generates the terrain as streamable tiles along a corridor either side of the road.
        ///
        /// Each tile is its own mesh asset, renderer, collider and <see cref="WorldChunk"/>. The tiles
        /// sit on a global lattice and the height field is purely a function of world position, so
        /// neighbouring tiles agree exactly along their shared edges — sampling per-tile instead is the
        /// standard way to end up with cracks between them.
        /// </summary>
        private static void BuildTerrainTiles(
            Transform parent,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials)
        {
            List<TerrainTileKey> tiles = TerrainTileBuilder.ListTiles(field, terrainShape, terrainShape.CorridorWidth);

            var terrainRoot = new GameObject("Terrain");
            terrainRoot.transform.SetParent(parent, false);

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            int totalTriangles = 0;

            for (int i = 0; i < tiles.Count; i++)
            {
                TerrainTileKey key = tiles[i];
                string name = $"Terrain_{key.Column}_{key.Row}";

                Mesh mesh = TerrainTileBuilder.BuildTile(key, field, terrainShape, name);
                totalTriangles += mesh.triangles.Length / 3;

                mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}.asset");

                GameObject tileObject = CreateMeshObject(
                    terrainRoot.transform, name, mesh, new[] { materials.Grass, materials.Rock });

                WorldChunk chunk = tileObject.AddComponent<WorldChunk>();
                chunk.RecalculateBounds();
                EditorUtility.SetDirty(chunk);
            }

            Debug.Log($"[Horizon] Terrain: {tiles.Count} tiles of {tileSize:0} m, "
                      + $"{totalTriangles} triangles total, corridor {terrainShape.CorridorWidth:0} m.");
        }

        /// <summary>
        /// Builds a bore or a gallery for every covered stretch the course declares.
        ///
        /// These stay resident rather than streaming: there are two of them, they are small, and a bore
        /// that has not loaded yet is a hole in the mountain you drive into.
        /// </summary>
        private static void BuildCoveredSections(
            Transform parent,
            RoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            MountainField field,
            PrototypeMaterials materials)
        {
            var root = new GameObject("CoveredSections");
            root.transform.SetParent(parent, false);

            int built = 0;

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                bool covers = feature.Kind == RoadFeatureKind.Tunnel || feature.Kind == RoadFeatureKind.Gallery;
                if (!covers)
                {
                    continue;
                }

                string name = $"{feature.Kind}_{feature.Name}";
                Mesh mesh = TunnelBuilder.Build(path, roadShape, feature, field, name);
                if (mesh == null)
                {
                    Debug.LogWarning($"[Horizon] '{feature.Name}' is too short to build ({feature.Length:0} m).");
                    continue;
                }

                mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}.asset");

                // Material order follows the Submesh constants on TunnelBuilder.
                CreateMeshObject(root.transform, name, mesh,
                    new[] { materials.Rock, materials.Concrete }, addCollider: true, markStatic: true);

                built++;
            }

            Debug.Log($"[Horizon] Built {built} covered section(s).");
        }

        /// <summary>
        /// Walks the carriageway and reports anywhere the terrain stands above it.
        ///
        /// "The mountain cuts through the road in places" is not something to search for by eye across
        /// five kilometres. The height field is a pure function, so the same question can simply be asked
        /// at every metre of road and answered with numbers — including *where*, and by how much.
        /// </summary>
        private static void ValidateRoadClearance(
            RoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course)
        {
            const float step = 2f;
            float length = path.Length;

            int breaches = 0;
            float worst = 0f;
            float worstAt = 0f;
            float worstAcross = 0f;

            // Checked across the full paved width, not just the centreline: the terrain intrudes from the
            // side, so the centreline is the last place it would show up.
            float[] offsets = { -roadShape.HalfWidth, -roadShape.HalfWidth * 0.5f, 0f,
                                roadShape.HalfWidth * 0.5f, roadShape.HalfWidth };

            for (float distance = 0f; distance <= length; distance += step)
            {
                // Inside a bore the mountain is meant to be overhead.
                if (course != null && course.IsCovered(distance))
                {
                    continue;
                }

                Vector3 center = path.GetPositionAtDistance(distance);
                Vector3 right = path.GetBankedRightAtDistance(
                    distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                for (int i = 0; i < offsets.Length; i++)
                {
                    Vector3 point = center + right * offsets[i];
                    float surface = point.y + roadShape.SurfaceLift;
                    float ground = field.HeightAt(point.x, point.z);

                    float intrusion = ground - surface;
                    if (intrusion <= 0.02f)
                    {
                        continue;
                    }

                    breaches++;
                    if (intrusion > worst)
                    {
                        worst = intrusion;
                        worstAt = distance;
                        worstAcross = offsets[i];
                    }
                }
            }

            if (breaches == 0)
            {
                Debug.Log("[Horizon] Road clearance: the terrain is below the carriageway everywhere.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Road clearance: terrain stands above the asphalt at {breaches} sampled points. "
                + $"Worst is {worst:0.00} m at {worstAt:0} m along the course, {worstAcross:0.0} m across "
                + "from the centreline.");
        }

        /// <summary>
        /// Builds the guard rails. They stay resident with the road rather than streaming: it is a few
        /// thousand triangles in one draw call, and a missing rail on an exposed hairpin is worse than the
        /// cost of keeping it.
        /// </summary>
        private static void BuildGuardRails(
            Transform parent,
            RoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            PrototypeMaterials materials)
        {
            Mesh mesh = GuardRailBuilder.Build(path, roadShape, field, course);
            if (mesh == null)
            {
                Debug.Log("[Horizon] No guard rails needed — nothing on the course is exposed enough.");
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/GuardRailMesh.asset");

            // No collider: the rails are visual. Hitting one should not be a wall the car can lean on
            // until the vehicle has a proper collision response, and a concave mesh collider here would
            // catch the car in ways that feel arbitrary.
            CreateMeshObject(parent, "GuardRails", mesh, new[] { materials.GuardRail },
                addCollider: false, markStatic: true);

            Debug.Log($"[Horizon] Guard rails: {triangles} triangles.");
        }

        private static GameObject CreateMeshObject(
            Transform parent,
            string name,
            Mesh mesh,
            Material[] materials,
            bool addCollider = true,
            bool markStatic = true)
        {
            var meshObject = new GameObject(name);
            meshObject.transform.SetParent(parent, false);

            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;

            if (addCollider)
            {
                meshObject.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            if (markStatic)
            {
                // Generated world geometry never moves, so let Unity batch and light-bake it. The car
                // obviously must not be marked static.
                GameObjectUtility.SetStaticEditorFlags(meshObject, StaticEditorFlags.BatchingStatic
                                                                 | StaticEditorFlags.ContributeGI
                                                                 | StaticEditorFlags.OccluderStatic
                                                                 | StaticEditorFlags.OccludeeStatic);
            }

            return meshObject;
        }
    }
}
