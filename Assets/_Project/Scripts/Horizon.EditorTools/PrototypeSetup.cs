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
            HorizonAssetUtility.BeginGeneratedRun();

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

            HorizonAssetUtility.ReportOrphanedAssets(GeneratedFolder);

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
            public readonly Material Lane;
            public readonly Material Footway;
            public readonly Material[] Walls;
            public readonly Material[] Roofs;
            public readonly Material Trim;
            public readonly Material WindowDay;
            public readonly Material WindowNight;
            public readonly Material Bark;
            public readonly Material Conifer;
            public readonly Material Broadleaf;
            public readonly Material Undergrowth;
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
                    MaterialsFolder + "/M_RoadSurface.mat", "M_RoadSurface", Color.white, 0.34f, 0f,
                    surfaceTexture);

                RoadShoulder = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_RoadShoulder.mat", "M_RoadShoulder", Color.white, 0.12f, 0f,
                    shoulderTexture);
                Grass = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Grass.mat", "M_Grass", new Color(0.36f, 0.48f, 0.26f), 0.08f);
                Rock = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Rock.mat", "M_Rock", new Color(0.44f, 0.39f, 0.34f), 0.12f);

                // Untextured, so it carries no centre line — the markings live in a baked texture, and a
                // town lane with a dashed centre line reads as a main road.
                Lane = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Lane.mat", "M_Lane", new Color(0.27f, 0.27f, 0.29f), 0.30f);

                // Paler and rougher than the carriageway. The step in value between road and pavement is
                // what makes a kerb read at all from a car — the 14 cm of geometry is far too small to
                // see, and it is the colour boundary that draws the line down the street.
                Footway = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Footway.mat", "M_Footway", new Color(0.60f, 0.58f, 0.55f), 0.10f);

                // A palette, because URP/Lit reads no vertex colours and the building meshes carry no UVs
                // — a per-house tint has to be a per-house material. Warm plaster tones, the kind an
                // alpine town is actually rendered and limewashed in.
                Walls = new[]
                {
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_Wall.mat", "M_Wall", new Color(0.87f, 0.83f, 0.75f), 0.10f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_WallCream.mat", "M_WallCream",
                        new Color(0.91f, 0.86f, 0.70f), 0.10f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_WallOchre.mat", "M_WallOchre",
                        new Color(0.80f, 0.68f, 0.53f), 0.10f),
                };

                Roofs = new[]
                {
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_Roof.mat", "M_Roof", new Color(0.44f, 0.23f, 0.18f), 0.15f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_RoofSlate.mat", "M_RoofSlate",
                        new Color(0.31f, 0.30f, 0.32f), 0.18f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_RoofRust.mat", "M_RoofRust",
                        new Color(0.55f, 0.32f, 0.20f), 0.14f),
                };
                Trim = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Trim.mat", "M_Trim", new Color(0.38f, 0.31f, 0.25f), 0.18f);

                // Unlit, both of them. TownLights swaps between the two on the window submesh at dusk
                // and dawn — no keyword, no property block, and nothing written to a material at runtime.
                WindowDay = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_WindowDay.mat", "M_WindowDay", new Color(0.20f, 0.23f, 0.27f));
                WindowNight = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_WindowNight.mat", "M_WindowNight",
                    new Color(1.55f, 1.25f, 0.72f));
                Concrete = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Concrete.mat", "M_Concrete", new Color(0.52f, 0.51f, 0.49f), 0.20f);

                // Four flat colours carry the whole forest. URP/Lit does not read vertex colours, so
                // variation between one tree and the next has to come from geometry — but variation between
                // *kinds* of tree is what actually reads at driving speed, and that is these.
                Bark = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Bark.mat", "M_Bark", new Color(0.29f, 0.21f, 0.16f), 0.05f);
                Conifer = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Conifer.mat", "M_Conifer", new Color(0.16f, 0.29f, 0.22f), 0.06f);
                Broadleaf = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Broadleaf.mat", "M_Broadleaf", new Color(0.43f, 0.53f, 0.24f), 0.07f);
                Undergrowth = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Undergrowth.mat", "M_Undergrowth", new Color(0.32f, 0.44f, 0.22f), 0.06f);
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
                // Unlit, not emissive Lit. A lamp lens should be drawn at its own brightness whatever
                // the scene lighting is doing, and VehicleLights animates _BaseColor through a property
                // block — no shader keyword involved, which is what made the emissive version fail
                // silently for the whole life of the project. See LoadOrCreateUnlitMaterial.
                LightFront = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_LightFront.mat", "M_LightFront",
                    new Color(0.62f, 0.60f, 0.50f));
                LightRear = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_LightRear.mat", "M_LightRear",
                    new Color(0.34f, 0.04f, 0.03f));

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
            //
            // Every figure here tracks CarMeshBuilder's silhouette and has to be revisited whenever that
            // changes, or bodywork ends up outside its own collider and clips through scenery.
            //   X: widest flank is 1.02 + FlareWidth 0.09 = 1.11 over the rear arch.
            //   Y: sill -0.52 to crowned roof ~0.72.
            //   Z: tail cap -2.36 to nose cap 2.52, so 4.88 long and biased forward.
            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.10f, 0.08f);
            collider.size = new Vector3(2.25f, 1.25f, 4.94f);

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
                CarMeshBuilder.BuildWheel(config.WheelRadius, 0.34f),
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

            // Wired here rather than with the rest of VehicleLights, because the controller does not
            // exist yet at that point. VehicleLights falls back to a GetComponentInParent in Awake, but
            // an explicit reference is one less thing to be surprised by.
            HorizonAssetUtility.Configure(lights, serialized =>
                serialized.FindProperty("controller").objectReferenceValue = controller);

            HorizonAssetUtility.EnsureFolder(PrefabsFolder);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, VehiclePrefabPath);
            Object.DestroyImmediate(root);

            // Check the saved asset too, not just the scene instance it was built from.
            HorizonAssetUtility.AssertReferenceAssigned(prefab.GetComponent<VehicleController>(), "config");
            return prefab;
        }

        private static void BuildWorldScene(GameObject vehiclePrefab)
        {
            // Four lines, and they are how you find out which loop bit. Three things here scale badly
            // with the town — clearing parcels off streets, the plant scatter's occupancy query, and
            // MountainField's un-bucketed coarse grid — and an opinion about which of them is the
            // expensive one is worth nothing next to a number per phase.
            var clock = new System.Diagnostics.Stopwatch();
            clock.Start();

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

            // --- The town's street network.
            //
            // The order below is the whole of the town's build and it is not free to change. The network
            // has to exist before MountainField, because its centrelines are what the ground is levelled
            // to; the plots have to come after, because they are seated on the finished terrain mesh.
            // The street *meshes* sit in between quite happily, and that is the point of
            // TownShape.FloorHeight — a street takes its height from the same function the level samples
            // do, so neither has to wait for the other.
            TownShape townShape = TownShape.Default;
            ValidateTownMapping(path, townShape);

            var streetsRoot = new GameObject("TownStreets");
            streetsRoot.transform.SetParent(worldRoot.transform, false);

            StreetNetwork network = StreetNetwork.Build(
                path, townShape, TalheimLayout.Build(), streetsRoot.transform);

            StreetJunctionBuilder.ResolveTrims(network, roadShape.OuterHalfWidth);
            BuildStreetMeshes(streetsRoot.transform, network, path, roadShape, townShape, materials);
            Phase(clock, "road and streets");

            // One field, shared: the terrain is built from it, the guard rails ask it where the ground falls
            // away, and the tunnel bodies use it to bury their feet. Building a second would be slow and
            // could disagree with the first.
            //
            // The town's floor is levelled by handing the field a grid of level samples over the whole
            // basin. The streets alone will not do it — a road levels a 24 m ribbon either side and leaves
            // the ground between them untouched, which measured 22 m of relief on the village that was
            // here before. See TownShape.BuildLevelSamples.
            List<Vector3> levelSamples = TownShape.BuildLevelSamples(path, townShape);
            for (int i = 0; i < network.Edges.Count; i++)
            {
                // Every street centreline, on top of the basin grid. The basin's heights come from
                // FloorHeight and so do the streets', so this is belt and braces rather than a
                // correction — but it is what makes the shelf follow a street exactly rather than to
                // within half a sample pitch.
                TownPlanner.AddPathSamples(network.Edges[i].Path, 8f, levelSamples);
            }

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                levelSamples.Add(network.Nodes[i].Position);
            }

            // Known before the field exists, and deliberately so: it depends on the basin's extent alone,
            // never on where plots ended up, so the terrain corridor can be widened locally without
            // anything having to be built first.
            Bounds townFootprint = TownShape.Footprint(levelSamples, townShape.CorridorMargin);

            var field = new MountainField(path, terrainShape, 4f, levelSamples);
            Phase(clock, $"height field ({levelSamples.Count} level samples)");

            ValidateRoadClearance(path, roadShape, field, course);
            ReportTownGround(field, path, terrainShape, townShape);

            // Planned after the field exists, because the plots are seated on the finished terrain.
            var streetIndex = new StreetIndex(network);
            List<TownBlock> blocks = network.FindBlocks(out int[] blockOfHalfEdge);
            ReportBlocks(blocks);

            TownPlan townPlan = TownPlanner.Plan(
                network, streetIndex, field, terrainShape, townShape, path, blocks, blockOfHalfEdge);
            Phase(clock, $"blocks and parcels ({townPlan.Plots.Count} plots)");

            BuildTerrainTiles(worldRoot.transform, path, roadShape, course, field, terrainShape,
                townShape, townFootprint, network, townPlan, materials);
            ValidateLandmarkVisibility(field, course, path, townPlan);
            Phase(clock, "terrain, vegetation and buildings");

            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);

            // After every builder and before the car exists — otherwise the car is the obstruction.
            ValidateDriveableCorridor(path, "the pass", 1.3f, 4f);
            Phase(clock, "validation");
            int worstJunction = ValidateStreetNetwork(network, path, roadShape);
            MarkWorstJunction(worldRoot.transform, network, worstJunction);

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

            // --- Vehicle, dropped onto the road among the houses rather than at the start of the course.
            // The arrival road in front of the town is 700 m of scenery to drive *back* along, not
            // something to make the player sit through before anything happens.
            float spawnDistance = MountainPassCourse.TownStartDistance + 45f;
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
        /// <summary>
        /// Builds every street ribbon and every junction into one mesh, and hangs it on one object.
        ///
        /// <para>One mesh for the whole network rather than one per street or one per terrain tile. Three
        /// kilometres of street is around thirteen thousand triangles and three draw calls, which is
        /// cheap enough that splitting it could only cost — and it takes the entire class of
        /// seam-at-a-tile-boundary questions off the table, along with giving the network a single
        /// MeshCollider for the wheels to find. The chunk radius is the trunk road's: a town you can see
        /// from the pass above should not be streamed out from under itself.</para>
        /// </summary>
        private static void BuildStreetMeshes(
            Transform parent,
            StreetNetwork network,
            RoadPath trunk,
            in RoadShape trunkShape,
            in TownShape townShape,
            PrototypeMaterials materials)
        {
            if (network.Edges.Count == 0)
            {
                Debug.LogWarning("[Horizon] Town streets: the layout table produced no streets at all.");
                return;
            }

            var buffer = new VegetationMeshBuffer(TownStreetBuilder.StreetSubmeshCount);

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                // Started a little before the trim point and ended a little after, so ribbon and pad
                // overlap rather than abut — two coplanar colliders that merely touch can drop a raycast
                // wheel for a frame on the seam.
                TownStreetBuilder.AppendStreet(
                    edge.Path, edge.Shape,
                    edge.TrimStart - StreetJunctionBuilder.RibbonOverlap,
                    edge.Length - edge.TrimEnd + StreetJunctionBuilder.RibbonOverlap,
                    buffer);
            }

            // Counted in three, because "five thousand faces are backwards" says nothing about which
            // builder to go and read. One number per builder is the difference between a tripwire and a
            // hunt.
            int ribbonFlips = buffer.FlipCount;

            int pads = 0;
            int mouths = 0;

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                if (network.Nodes[i].OnTrunkRoad)
                {
                    continue;
                }

                StreetJunctionBuilder.AppendPad(network, i, buffer);
                pads++;
            }

            int padFlips = buffer.FlipCount - ribbonFlips;

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                if (!network.Nodes[i].OnTrunkRoad)
                {
                    continue;
                }

                float alongTrunk = NearestDistanceAlong(trunk, network.Nodes[i].Position);
                StreetJunctionBuilder.AppendTrunkMouth(
                    network, i, trunk, trunkShape, alongTrunk, buffer);
                mouths++;
            }

            int mouthFlips = buffer.FlipCount - ribbonFlips - padFlips;

            var used = new List<int>(TownStreetBuilder.StreetSubmeshCount);
            Mesh mesh = buffer.ToMesh("TownStreetsMesh", used);
            if (mesh == null)
            {
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/TownStreetsMesh.asset");

            GameObject streetObject = CreateMeshObject(
                parent, "Streets", mesh, StreetMaterials(materials, used));

            WorldChunk chunk = streetObject.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);
            EditorUtility.SetDirty(chunk);

            Debug.Log($"[Horizon] Town streets: {network.Nodes.Count} nodes, {network.Edges.Count} "
                      + $"streets, {network.TotalLength:0} m, {pads} junction pads and {mouths} trunk "
                      + $"mouths — {mesh.triangles.Length / 3} triangles in {used.Count} draw calls.");

            ReportWindingFlips("Town street ribbons", ribbonFlips);
            ReportWindingFlips("Town junction pads", padFlips);
            ReportWindingFlips("Town trunk mouths", mouthFlips);
        }

        /// <summary>The materials for the street mesh, in the order its submeshes survived compaction.</summary>
        private static Material[] StreetMaterials(PrototypeMaterials materials, List<int> used)
        {
            var result = new Material[used.Count];

            for (int i = 0; i < used.Count; i++)
            {
                switch (used[i])
                {
                    case TownStreetBuilder.SurfaceSubmesh:
                        result[i] = materials.Lane;
                        break;
                    case TownStreetBuilder.KerbSubmesh:
                        result[i] = materials.Concrete;
                        break;
                    default:
                        result[i] = materials.Footway;
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Where along a path a world point lies, by walking it.
        ///
        /// There is no inverse projection anywhere in this codebase and adding one for five trunk mouths
        /// would be a poor trade. Two passes — coarse then fine — so a five-kilometre course costs a few
        /// hundred samples rather than a few thousand.
        /// </summary>
        private static float NearestDistanceAlong(IRoadPath path, Vector3 point)
        {
            float best = 0f;
            float bestSqr = float.MaxValue;

            for (float along = 0f; along <= path.Length; along += 10f)
            {
                Vector3 at = path.GetPositionAtDistance(along);
                float dx = at.x - point.x;
                float dz = at.z - point.z;
                float distanceSqr = dx * dx + dz * dz;

                if (distanceSqr < bestSqr)
                {
                    bestSqr = distanceSqr;
                    best = along;
                }
            }

            for (float along = best - 10f; along <= best + 10f; along += 0.5f)
            {
                float clamped = Mathf.Clamp(along, 0f, path.Length);
                Vector3 at = path.GetPositionAtDistance(clamped);
                float dx = at.x - point.x;
                float dz = at.z - point.z;
                float distanceSqr = dx * dx + dz * dz;

                if (distanceSqr < bestSqr)
                {
                    bestSqr = distanceSqr;
                    best = clamped;
                }
            }

            return best;
        }

        /// <summary>
        /// Checks that town-local coordinates are not folding anywhere inside the basin.
        ///
        /// <para>The layout table is written in metres along and across the trunk road, and that mapping
        /// stretches or squeezes wherever the road bends: a point <c>d</c> metres out on the inside of a
        /// bend of radius <c>r</c> has its along-axis compressed by <c>1 - d/r</c>, which reaches zero
        /// when the town is as wide as the bend is tight. At that point streets authored a hundred metres
        /// apart arrive on top of each other, and past it the coordinate system turns inside out.</para>
        ///
        /// <para>This is a property of the *course*, not of the layout, which is why it is checked here
        /// and why MountainPassCourse stops the town where the pass's first bend begins. It costs one
        /// curvature lookup per sample and it is the difference between that being a rule someone
        /// remembers and a rule that holds.</para>
        /// </summary>
        private static void ValidateTownMapping(RoadPath path, in TownShape shape)
        {
            float minimumScale = TownShape.MinimumMappingScale;

            float worst = float.MaxValue;
            float worstAlong = 0f;

            // The town's own extent, not the basin's margins. ToWorld caps the margins rather than
            // letting them fold, and the streets are the only thing whose coordinates have to mean
            // exactly what the table says.
            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += 10f)
            {
                float clamped = Mathf.Clamp(along, 0f, path.Length);
                float curvature = path.GetSignedCurvatureAtDistance(clamped, 20f);

                // The far edge of the basin is the worst case in both directions across the road.
                float inner = 1f - shape.AcrossInner * shape.Side * curvature;
                float outer = 1f - shape.AcrossOuter * shape.Side * curvature;
                float scale = Mathf.Min(inner, outer);

                if (scale < worst)
                {
                    worst = scale;
                    worstAlong = clamped;
                }
            }

            if (worst >= minimumScale)
            {
                Debug.Log($"[Horizon] Town mapping: town-local coordinates hold, tightest scale "
                          + $"{worst:0.00} at {worstAlong:0} m along.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Town mapping folds: the along-axis is squeezed to {worst:0.00} of its length "
                + $"at {worstAlong:0} m along, against a floor of {minimumScale:0.00}. The trunk road "
                + "bends towards the town more tightly than the town is wide, so streets authored a "
                + "hundred metres apart will arrive on top of one another. Either move the town's extent "
                + "off that bend or open the bend out.");
        }


        /// <summary>
        /// Measures how closely the delivered ground follows the floor the town was designed against, as
        /// numbers rather than as an impression.
        ///
        /// <para>Three deliberate changes from the version that measured the town. It sweeps the whole
        /// basin rather than a 105 m strip beside the lanes. It compares each point against
        /// <see cref="TownShape.FloorHeight"/> at that point rather than against the trunk road's height,
        /// because the floor is *meant* to rise 4.5 m across the basin and a check that called that a
        /// defect would be measuring the design instead of the build. And it warns on <b>local</b>
        /// steepness only: total relief across half a kilometre of valley says nothing about whether a
        /// house can stand on it, while a 12 % step between two adjacent cells says everything.</para>
        ///
        /// <para>The last number is the one that answers the question this stage exists to ask: what
        /// fraction of the basin is too steep to build on. Anything but a very small percentage means the
        /// town has no room, whatever the previews look like.</para>
        /// </summary>
        private static void ReportTownGround(
            MountainField field,
            RoadPath path,
            in TerrainShape terrainShape,
            in TownShape shape)
        {
            // The cell size, so the steepness measured is the steepness the mesh actually has. Sampling
            // finer than the terrain grid measures the interpolation, not the ground.
            float step = terrainShape.CellSize;

            float lowest = float.MaxValue;
            float highest = float.MinValue;
            float steepest = 0f;
            float steepestAlong = 0f;
            float steepestAcross = 0f;
            int samples = 0;
            int steep = 0;

            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += step)
            {
                for (float across = shape.AcrossInner; across <= shape.AcrossOuter; across += step)
                {
                    Vector3 point = TownShape.ToWorld(path, shape, along, across);

                    float here = field.HeightAt(point.x, point.z);
                    float ahead = field.HeightAt(point.x + step, point.z);
                    float beside = field.HeightAt(point.x, point.z + step);

                    // Against the floor the town was planned on, less the shelf drop the field applies to
                    // every road and level sample it is given. A constant offset here is correct and
                    // expected; a varying one is the ground disagreeing with the plan.
                    float expected = TownShape.FloorHeight(path, shape, along, across)
                                     - terrainShape.RoadShelfDrop;
                    float error = here - expected;

                    lowest = Mathf.Min(lowest, error);
                    highest = Mathf.Max(highest, error);

                    float grade = Mathf.Max(
                        Mathf.Abs(ahead - here) / step, Mathf.Abs(beside - here) / step);

                    if (grade > steepest)
                    {
                        steepest = grade;
                        steepestAlong = along;
                        steepestAcross = across;
                    }

                    if (grade > 0.08f)
                    {
                        steep++;
                    }

                    samples++;
                }
            }

            if (samples == 0)
            {
                return;
            }

            float steepFraction = steep / (float)samples;

            Debug.Log($"[Horizon] Town ground: {samples} samples over "
                      + $"{shape.AlongEnd - shape.AlongStart:0} x {shape.AcrossSpan:0} m, "
                      + $"{lowest:0.0} m to {highest:0.0} m off the planned floor, steepest "
                      + $"{steepest * 100f:0} % at {steepestAlong:0} m along / {steepestAcross:0} m across, "
                      + $"{steepFraction * 100f:0.0} % of the basin over 8 %.");

            if (steepFraction > 0.06f || steepest > 0.30f)
            {
                Debug.LogWarning(
                    "[Horizon] Town ground has too little buildable area. The level samples from "
                    + "TownShape.BuildLevelSamples are either not reaching MountainField, or their grid "
                    + "pitch is too coarse for the shelves to merge — it has to stay under twice "
                    + $"MountainField.Verge, which is {Mathf.Max(terrainShape.VergeWidth, terrainShape.CellSize * 2f):0} m.");
            }

            if (highest - lowest > 3f)
            {
                Debug.LogWarning(
                    $"[Horizon] Town ground wanders {highest - lowest:0.0} m either side of the floor "
                    + "TownShape.FloorHeight describes. Streets take their height from that function and "
                    + "the ground evidently does not, which is the plinth-and-daylight failure the shared "
                    + "floor function exists to prevent.");
            }
        }

        private static void BuildTerrainTiles(
            Transform parent,
            RoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape townShape,
            Bounds townFootprint,
            StreetNetwork network,
            TownPlan townPlan,
            PrototypeMaterials materials)
        {
            var extraRegions = new[] { townFootprint };
            List<TerrainTileKey> tiles = TerrainTileBuilder.ListTiles(
                field, terrainShape, terrainShape.CorridorWidth, extraRegions);

            var terrainRoot = new GameObject("Terrain");
            terrainRoot.transform.SetParent(parent, false);

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            int totalTriangles = 0;

            VegetationShape vegetationShape = VegetationShape.Default;
            var vegetationContext = new VegetationContext(
                path, course, vegetationShape, townPlan,
                townShape.PlotClearance, townShape.TreeKeepOut, network);
            var vegetationTotal = new VegetationStats();
            int heaviestTile = 0;
            string heaviestTileName = "none";

            var townTotal = new TownStats();
            var townRenderers = new List<MeshRenderer>();
            var townWindowSlots = new List<int>();

            for (int i = 0; i < tiles.Count; i++)
            {
                TerrainTileKey key = tiles[i];
                string name = $"Terrain_{key.Column}_{key.Row}";

                Mesh mesh = TerrainTileBuilder.BuildTile(key, field, terrainShape, name);
                totalTriangles += mesh.triangles.Length / 3;

                mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}.asset");

                GameObject tileObject = CreateMeshObject(
                    terrainRoot.transform, name, mesh, new[] { materials.Grass, materials.Rock });

                Mesh plants = VegetationBuilder.BuildTile(
                    key, field, terrainShape, vegetationShape, vegetationContext,
                    name + "_Plants", out VegetationStats stats);

                if (plants != null)
                {
                    plants = HorizonAssetUtility.ReplaceAsset(plants, $"{GeneratedFolder}/{name}_Plants.asset");

                    // Static, but not batched and not lit-baked. The mesh is already one merged draw, so
                    // BatchingStatic would only duplicate a few hundred thousand vertices into the static
                    // batch buffer, and putting this much geometry into a lightmap bake is not a trade worth
                    // making for foliage. Trees are poor occluders, so they are only ever occludees.
                    CreateMeshObject(
                        tileObject.transform, name + "_Plants", plants, PlantMaterials(materials, stats),
                        addCollider: false, markStatic: true,
                        staticFlags: StaticEditorFlags.OccludeeStatic);

                    vegetationTotal.Add(stats);
                    if (stats.Triangles > heaviestTile)
                    {
                        heaviestTile = stats.Triangles;
                        heaviestTileName = name;
                    }
                }

                Mesh buildings = TownPlanner.BuildTile(
                    key, terrainShape, townShape, townPlan, name + "_Town",
                    out TownStats townStats);

                if (buildings != null)
                {
                    buildings = HorizonAssetUtility.ReplaceAsset(
                        buildings, $"{GeneratedFolder}/{name}_Town.asset");

                    // Houses keep OccluderStatic, unlike the trees. A town street is the one place in
                    // this world where occlusion culling has something solid to work with.
                    //
                    // No MeshCollider on the merged mesh: it would be a large concave collider full of
                    // window ledges and fence rails for the car to snag on, the same reason the tunnel
                    // skin was taken out of collision. Each plot gets a box below instead.
                    GameObject townObject = CreateMeshObject(
                        tileObject.transform, name + "_Town", buildings,
                        TownMaterials(materials, townStats),
                        addCollider: false, markStatic: true,
                        staticFlags: StaticEditorFlags.BatchingStatic
                                     | StaticEditorFlags.OccluderStatic
                                     | StaticEditorFlags.OccludeeStatic);

                    int windowSlot = townStats.Submeshes.IndexOf(BuildingMeshes.WindowSubmesh);
                    if (windowSlot >= 0)
                    {
                        townRenderers.Add(townObject.GetComponent<MeshRenderer>());
                        townWindowSlots.Add(windowSlot);
                    }

                    AddPlotColliders(townObject.transform, key, terrainShape, townPlan);
                    townTotal.Add(townStats);
                }

                // After the plants and the houses, never before: the chunk takes its radius from the
                // renderers below it, and anything standing proud of the terrain bounds would pop in and
                // out on its own.
                WorldChunk chunk = tileObject.AddComponent<WorldChunk>();
                chunk.RecalculateBounds();
                EditorUtility.SetDirty(chunk);
            }

            // The baseline is worth the second pass: the whole argument for a local corridor is that it
            // costs a dozen tiles rather than doubling the pass, and that is a number, not an opinion.
            int baseline = TerrainTileBuilder.ListTiles(field, terrainShape, terrainShape.CorridorWidth).Count;

            Debug.Log($"[Horizon] Terrain: {tiles.Count} tiles of {tileSize:0} m, "
                      + $"{totalTriangles} triangles total, corridor {terrainShape.CorridorWidth:0} m "
                      + $"plus {tiles.Count - baseline} for the town basin.");

            ReportVegetation(vegetationTotal, vegetationShape, roadShape, vegetationContext,
                heaviestTile, heaviestTileName);

            ReportTown(townTotal, townShape, townPlan);
            WireTownLights(parent, townRenderers, townWindowSlots, materials);
        }

        /// <summary>
        /// Hangs one TownLights on the world root, holding every renderer that owns a window submesh.
        ///
        /// One component for the whole town rather than one per tile: every window in the place lights
        /// at the same instant, so there is nothing to be gained by deciding it thirty times over.
        /// </summary>
        private static void WireTownLights(
            Transform parent,
            List<MeshRenderer> renderers,
            List<int> windowSlots,
            PrototypeMaterials materials)
        {
            if (renderers.Count == 0)
            {
                return;
            }

            var lightsObject = new GameObject("TownLights");
            lightsObject.transform.SetParent(parent, false);

            TownLights lights = lightsObject.AddComponent<TownLights>();
            HorizonAssetUtility.Configure(lights, serialized =>
            {
                HorizonAssetUtility.SetObjectArray(serialized, "renderers", renderers.ToArray());

                SerializedProperty slots = serialized.FindProperty("windowSlots");
                slots.arraySize = windowSlots.Count;
                for (int i = 0; i < windowSlots.Count; i++)
                {
                    slots.GetArrayElementAtIndex(i).intValue = windowSlots[i];
                }

                serialized.FindProperty("dayMaterial").objectReferenceValue = materials.WindowDay;
                serialized.FindProperty("nightMaterial").objectReferenceValue = materials.WindowNight;
            });
        }

        private static void ReportTown(TownStats stats, in TownShape shape, TownPlan plan)
        {
            if (plan == null || stats.Houses + stats.Windmills == 0)
            {
                Debug.LogWarning("[Horizon] Town: nothing was built. Check TownShape's extent against "
                                 + "the course, and that the plots landed inside terrain tiles.");
                return;
            }

            Debug.Log($"[Horizon] Town: {stats.Houses} houses, {stats.Churches} church, "
                      + $"{stats.Windmills} windmill, "
                      + $"{stats.Barns} barns, {stats.Sawmills} sawmills, {stats.Fences} fences, "
                      + $"{stats.Lamps} lamps, {stats.Cars} parked cars — {stats.Triangles} triangles "
                      + $"over {plan.Footprint.size.x:0} x {plan.Footprint.size.z:0} m.");

            if (stats.Triangles > shape.MaxTrianglesPerTile * 4)
            {
                Debug.LogWarning($"[Horizon] Town: {stats.Triangles} triangles is heavier than expected. "
                                 + "Raise PlotSpacing or lower the plot count in TownShape.");
            }

            ReportWindingFlips("Town", stats.Flips);
        }

        /// <summary>
        /// Whether any face had to be turned round on its way into a mesh buffer.
        ///
        /// Zero is the only acceptable answer and it costs nothing to ask. The correction happens either
        /// way, so a non-zero count is never a broken build — it is a builder whose vertex order has drifted
        /// from the direction it claims the face looks in, and it is worth knowing on the run that
        /// introduces it rather than three stages later. Silent self-correction is how the mirrored
        /// placement basis went unnoticed until every wall in the town was inside out.
        ///
        /// <para>This is the only automated winding check there is, and that is a deliberate stopping point
        /// rather than a gap waiting to be filled. Four attempts were made at a second one that inspects the
        /// finished meshes — crossing parity on a <c>MeshCollider</c>, the same in exact Möller–Trumbore,
        /// per-face front visibility, and two-culling-mode ray sampling from outside the mesh bounds. Every
        /// one of them reported nine of ten wall submeshes riddled with holes on a town that renders
        /// solid, because none of this geometry is a manifold solid: windows are recesses drawn inside solid
        /// wall boxes with panes buried behind them, roofs emit their undersides at the same coordinates as
        /// their slopes, and boxes interpenetrate freely. A check nobody can trust gets switched off, and is
        /// then worth nothing on the day it is right. If a second layer is wanted, the place for it is a
        /// signed-volume assertion on each closed primitive at the point it is authored — which needs those
        /// primitives exposed for test — not an inspection of the merged result.</para>
        /// </summary>
        private static void ReportWindingFlips(string what, int flips)
        {
            if (flips <= 0)
            {
                return;
            }

            Debug.LogWarning($"[Horizon] {what}: {flips} faces were wound backwards and were corrected at "
                             + "build time. The helper that emitted them disagrees with its own outward "
                             + "direction — the geometry is right, the code that wrote it is not.");
        }

        /// <summary>
        /// The materials for a tile's buildings, in the order its submeshes ended up in.
        ///
        /// The wall and roof palettes are contiguous ranges rather than named cases, so adding a fourth
        /// colour is one entry in the array and nothing else.
        /// </summary>
        private static Material[] TownMaterials(PrototypeMaterials materials, TownStats stats)
        {
            var result = new Material[stats.Submeshes.Count];

            for (int i = 0; i < stats.Submeshes.Count; i++)
            {
                int submesh = stats.Submeshes[i];

                if (submesh >= BuildingMeshes.FirstWallSubmesh
                    && submesh < BuildingMeshes.FirstWallSubmesh + BuildingMeshes.WallVariants)
                {
                    result[i] = materials.Walls[submesh - BuildingMeshes.FirstWallSubmesh];
                }
                else if (submesh >= BuildingMeshes.FirstRoofSubmesh
                         && submesh < BuildingMeshes.FirstRoofSubmesh + BuildingMeshes.RoofVariants)
                {
                    result[i] = materials.Roofs[submesh - BuildingMeshes.FirstRoofSubmesh];
                }
                else if (submesh == BuildingMeshes.WindowSubmesh)
                {
                    // Starts dark. TownLights swaps in the lit one after sunset.
                    result[i] = materials.WindowDay;
                }
                else if (submesh == BuildingMeshes.GardenSubmesh)
                {
                    result[i] = materials.Undergrowth;
                }
                else
                {
                    result[i] = materials.Trim;
                }
            }

            return result;
        }

        /// <summary>
        /// One box collider per plot, sized to the house rather than to the garden.
        ///
        /// Boxes rather than the merged mesh because the mesh is concave and full of window ledges, fence
        /// rails and roof eaves — exactly the kind of surface a car catches on in ways that feel arbitrary,
        /// which is why the guard rails and the tunnel skin are not colliders either.
        /// </summary>
        private static void AddPlotColliders(
            Transform parent,
            TerrainTileKey key,
            in TerrainShape terrainShape,
            TownPlan plan)
        {
            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                TownPlan.Plot plot = plan.Plots[i];

                if (plot.Position.x < originX || plot.Position.x >= originX + tileSize
                    || plot.Position.z < originZ || plot.Position.z >= originZ + tileSize)
                {
                    continue;
                }

                bool church = plot.Kind == TownPlotKind.Church;
                bool tall = plot.Kind == TownPlotKind.Windmill;
                bool wide = plot.Kind == TownPlotKind.Barn;

                // The church gets the nave and leaves the tower out. One box round both would wall off
                // the churchyard, and the tower is 3.6 m of it against 11 m of nave.
                float halfWidth = church ? 7f : tall ? 4.5f : wide ? 7.5f : 5.6f;
                float halfDepth = church ? 12f : tall ? 4.5f : wide ? 5.5f : 4.8f;
                float height = church ? 15f : tall ? 16f : wide ? 8f : 6f;

                var box = new GameObject($"Plot_{i}");
                box.transform.SetParent(parent, false);
                box.transform.position = plot.Position;
                box.transform.rotation = Quaternion.Euler(0f, plot.Yaw, 0f);

                BoxCollider collider = box.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, height * 0.5f, 0f);
                collider.size = new Vector3(halfWidth * 2f, height, halfDepth * 2f);

                GameObjectUtility.SetStaticEditorFlags(box, StaticEditorFlags.BatchingStatic);
            }
        }

        /// <summary>
        /// Six checks over the finished street graph, replacing the pair that watched two village lanes.
        ///
        /// <para>Every one of them is here because the thing it tests is invisible in a render. A street
        /// crossing another in plan, a pad polygon folded through itself, a quarter with no route into
        /// it, an edge end half a metre off its junction — all of them produce a picture that looks like
        /// a town. The two bugs the old version caught both shipped and survived two rounds of looking at
        /// screenshots.</para>
        ///
        /// <list type="bullet">
        /// <item>(a) No two non-incident streets cross in plan. This is the planarity the block finder in
        /// the next stage depends on, so it is not only a rendering question.</item>
        /// <item>(b) No two streets meet at under 20°, which is where pad triangulation starts to
        /// struggle however carefully the trims are corrected.</item>
        /// <item>(c) Every pad polygon is star-shaped about its node — the mechanical form of "the pad
        /// did not fold through itself".</item>
        /// <item>(d) The network is connected. A quarter reachable only on foot is a quarter no
        /// screenshot will ever show.</item>
        /// <item>(e) Every street end is flush with its junction in Y.</item>
        /// <item>(f) There is actually something solid under every node and every trim point. A missing
        /// pad is a hole that the corridor sweep only finds if it happens to sample there.</item>
        /// </list>
        /// </summary>
        private static int ValidateStreetNetwork(
            StreetNetwork network, RoadPath trunk, in RoadShape trunkShape)
        {
            if (network.Edges.Count == 0)
            {
                return -1;
            }

            int crossings = 0;
            string worstCrossing = null;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                for (int j = i + 1; j < network.Edges.Count; j++)
                {
                    StreetEdge a = network.Edges[i];
                    StreetEdge b = network.Edges[j];

                    if (a.FromNode == b.FromNode || a.FromNode == b.ToNode
                        || a.ToNode == b.FromNode || a.ToNode == b.ToNode)
                    {
                        continue;
                    }

                    float clearance = a.HalfOuter + b.HalfOuter;
                    if (NearestApproach(a.Path, b.Path) < clearance)
                    {
                        crossings++;
                        worstCrossing ??= $"{i} and {j}";
                    }
                }
            }

            int shallow = 0;
            float tightestAngle = 180f;
            int tightestNode = -1;

            for (int n = 0; n < network.Nodes.Count; n++)
            {
                StreetNode node = network.Nodes[n];

                for (int i = 0; i < node.Degree; i++)
                {
                    for (int j = i + 1; j < node.Degree; j++)
                    {
                        float between = Mathf.Abs(Mathf.DeltaAngle(node.Bearings[i], node.Bearings[j]));
                        if (between < tightestAngle)
                        {
                            tightestAngle = between;
                            tightestNode = n;
                        }

                        if (between < 20f)
                        {
                            shallow++;
                        }
                    }
                }
            }

            int folded = 0;
            for (int n = 0; n < network.Nodes.Count; n++)
            {
                if (!IsStarShaped(network.Nodes[n]))
                {
                    folded++;
                }
            }

            int unreachable = CountUnreachable(network);

            int steps = 0;
            float worstStep = 0f;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                float startDrop = Mathf.Abs(
                    edge.Path.GetPositionAtDistance(0f).y - network.Nodes[edge.FromNode].Position.y);
                float endDrop = Mathf.Abs(
                    edge.Path.GetPositionAtDistance(edge.Length).y - network.Nodes[edge.ToNode].Position.y);

                if (startDrop > 0.05f || endDrop > 0.05f)
                {
                    steps++;
                    worstStep = Mathf.Max(worstStep, Mathf.Max(startDrop, endDrop));
                }
            }

            int holes = CountJunctionHoles(network);

            if (crossings > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {crossings} pair(s) of streets that share no "
                                 + $"junction run within a carriageway of each other, first {worstCrossing}. "
                                 + "The graph is not planar and the block finder will not find blocks.");
            }

            if (shallow > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {shallow} pair(s) of streets meet at under "
                                 + $"20°, tightest {tightestAngle:0} ° at node {tightestNode}. Pad "
                                 + "triangulation gets unreliable there whatever the trims do.");
            }

            if (folded > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {folded} junction pad(s) are not star-shaped "
                                 + "about their node, so the fan triangulation has folded through itself. "
                                 + "The trims at those nodes are too short for the angle between the "
                                 + "streets.");
            }

            if (unreachable > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {unreachable} node(s) cannot be driven to "
                                 + "from the trunk road. A disconnected quarter is one no player will "
                                 + "ever see.");
            }

            if (steps > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {steps} street end(s) are not flush with "
                                 + $"their junction, worst {worstStep:0.00} m. Heights must come from "
                                 + "TownShape.FloorHeight at both ends, not from anywhere else.");
            }

            if (holes > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: nothing solid stands under {holes} junction "
                                 + "sample(s). A pad is missing, or a trim ran past the end of its "
                                 + "street.");
            }

            if (crossings + shallow + folded + unreachable + steps + holes == 0)
            {
                Debug.Log($"[Horizon] Street network: {network.Nodes.Count} nodes and "
                          + $"{network.Edges.Count} streets — planar, connected, every pad convex about "
                          + $"its node and flush with its streets. Tightest junction {tightestAngle:0} ° "
                          + $"at node {tightestNode}.");
            }

            // The corridor sweep, once per street. Half-widths are per-street rather than the trunk
            // road's 1.3 m: that box is over half the width of a 5.2 m alley, and a check that fires on
            // every kerb is a check nobody reads.
            int blockedStreets = 0;
            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                float halfWidth = Mathf.Min(1.3f, edge.HalfWidth - 0.6f);

                if (!CorridorIsClear(edge.Path, halfWidth, 3f,
                        edge.TrimStart, edge.Length - edge.TrimEnd, out float at, out string by))
                {
                    blockedStreets++;
                    Debug.LogWarning($"[Horizon] Street {i} ({edge.Kind}): something solid stands in the "
                                     + $"carriageway at {at:0} m along it, against '{by}'.");
                }
            }

            if (blockedStreets == 0)
            {
                Debug.Log($"[Horizon] Street corridors: all {network.Edges.Count} streets driveable end "
                          + "to end.");
            }

            return tightestNode;
        }

        /// <summary>Logs how long a phase of the build took, and restarts the clock for the next one.</summary>
        private static void Phase(System.Diagnostics.Stopwatch clock, string what)
        {
            Debug.Log($"[Horizon] Build phase: {what} took {clock.ElapsedMilliseconds} ms.");
            clock.Restart();
        }

        /// <summary>
        /// Leaves an empty marker in the scene at the junction with the tightest angle between its
        /// streets, so the preview renderer can point a camera at it without knowing anything about the
        /// graph.
        ///
        /// The alternative was for the preview to rebuild the network and re-derive the worst node, which
        /// means a second copy of that reasoning that can drift from this one. A named transform in the
        /// scene is the cheapest possible channel between the two tools, and it shows up in the hierarchy
        /// where someone looking for the awkward junction would want it.
        /// </summary>
        private static void MarkWorstJunction(Transform parent, StreetNetwork network, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= network.Nodes.Count)
            {
                return;
            }

            var marker = new GameObject("TownWorstJunction");
            marker.transform.SetParent(parent, false);
            marker.transform.position = network.Nodes[nodeIndex].Position;
        }

        /// <summary>
        /// Whether the church can actually be seen from the pass, in metres of hillside in the way.
        ///
        /// <para>The whole claim this milestone rests on is that the town reads from the road above, and
        /// that claim is testable: walk the sight line from each viewpoint to the tip of the spire,
        /// sample the height field every ten metres, and report the worst amount by which the ground
        /// stands above the line. Placing a landmark by eye and checking it in a render means checking it
        /// from wherever the render happened to stand.</para>
        /// </summary>
        private static void ValidateLandmarkVisibility(
            MountainField field, RoadCourse course, RoadPath path, TownPlan plan)
        {
            Vector3 spire = Vector3.zero;
            bool found = false;

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                if (plan.Plots[i].Kind == TownPlotKind.Church)
                {
                    spire = plan.Plots[i].Position + Vector3.up * LandmarkMeshes.ChurchHeight;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Viewpoint)
                {
                    continue;
                }

                Vector3 from = path.GetPositionAtDistance(
                    Mathf.Clamp(feature.StartDistance, 0f, path.Length)) + Vector3.up * 1.5f;

                float span = Vector3.Distance(from, spire);
                float worst = 0f;
                float worstAt = 0f;

                for (float t = 0.05f; t <= 0.95f; t += 10f / Mathf.Max(1f, span))
                {
                    Vector3 on = Vector3.Lerp(from, spire, t);
                    float ground = field.HeightAt(on.x, on.z);

                    if (ground - on.y > worst)
                    {
                        worst = ground - on.y;
                        worstAt = t * span;
                    }
                }

                if (worst <= 0f)
                {
                    Debug.Log($"[Horizon] Landmark: the spire is clear from '{feature.Name}', "
                              + $"{span:0} m away.");
                    continue;
                }

                Debug.Log($"[Horizon] Landmark: from '{feature.Name}' at {span:0} m, the ground stands "
                          + $"{worst:0.0} m into the sight line to the spire, worst at {worstAt:0} m "
                          + "along it.");
            }
        }

        /// <summary>
        /// What the face walk found: how many blocks, how big, and which quarter each belongs to.
        ///
        /// The block count is the first number to look at when the layout table changes. A grid of three
        /// streets crossed by five should produce about eight blocks; anything far off that means a
        /// street the table thinks joins something it does not, and no picture would say so.
        /// </summary>
        private static void ReportBlocks(IReadOnlyList<TownBlock> blocks)
        {
            if (blocks.Count == 0)
            {
                Debug.LogWarning("[Horizon] Town blocks: the face walk found none. Either the layout "
                                 + "table is a tree with no closed rings in it, or the bearings the walk "
                                 + "turns on are not sorted.");
                return;
            }

            var byQuarter = new int[5];
            float total = 0f;
            float largest = 0f;

            for (int i = 0; i < blocks.Count; i++)
            {
                byQuarter[(int)blocks[i].Quarter]++;
                total += blocks[i].Area;
                largest = Mathf.Max(largest, blocks[i].Area);
            }

            Debug.Log($"[Horizon] Town blocks: {blocks.Count} enclosing {total / 10000f:0.0} ha, largest "
                      + $"{largest / 10000f:0.00} ha — {byQuarter[(int)TownQuarter.OldTown]} old town, "
                      + $"{byQuarter[(int)TownQuarter.Housing]} housing, "
                      + $"{byQuarter[(int)TownQuarter.Market]} market, "
                      + $"{byQuarter[(int)TownQuarter.Industry]} industry, "
                      + $"{byQuarter[(int)TownQuarter.Green]} green.");
        }

        /// <summary>Closest the two paths come to each other in plan, metres.</summary>
        private static float NearestApproach(IRoadPath a, IRoadPath b)
        {
            const float step = 6f;
            float best = float.MaxValue;

            for (float alongA = 0f; alongA <= a.Length; alongA += step)
            {
                Vector3 pointA = a.GetPositionAtDistance(Mathf.Min(alongA, a.Length));

                for (float alongB = 0f; alongB <= b.Length; alongB += step)
                {
                    Vector3 pointB = b.GetPositionAtDistance(Mathf.Min(alongB, b.Length));

                    float dx = pointA.x - pointB.x;
                    float dz = pointA.z - pointB.z;
                    best = Mathf.Min(best, dx * dx + dz * dz);
                }
            }

            return Mathf.Sqrt(best);
        }

        /// <summary>
        /// Whether a pad outline winds consistently about its node — consecutive cross products all of
        /// one sign. A fan triangulation from the centre is valid exactly when this holds.
        /// </summary>
        private static bool IsStarShaped(StreetNode node)
        {
            if (node.PadOutline == null || node.PadOutline.Length < 3)
            {
                return true;
            }

            int sign = 0;

            for (int i = 0; i < node.PadOutline.Length; i++)
            {
                Vector3 a = node.PadOutline[i] - node.Position;
                Vector3 b = node.PadOutline[(i + 1) % node.PadOutline.Length] - node.Position;

                float cross = a.x * b.z - a.z * b.x;
                if (Mathf.Abs(cross) < 0.0001f)
                {
                    continue;
                }

                int here = cross > 0f ? 1 : -1;
                if (sign == 0)
                {
                    sign = here;
                }
                else if (here != sign)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Nodes a breadth-first walk from the trunk-road entrances never reaches.</summary>
        private static int CountUnreachable(StreetNetwork network)
        {
            var seen = new bool[network.Nodes.Count];
            var queue = new Queue<int>();

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                if (network.Nodes[i].OnTrunkRoad)
                {
                    seen[i] = true;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                StreetNode node = network.Nodes[queue.Dequeue()];

                for (int i = 0; i < node.Degree; i++)
                {
                    int other = network.Edges[node.Edges[i]].Other(node.Index);
                    if (!seen[other])
                    {
                        seen[other] = true;
                        queue.Enqueue(other);
                    }
                }
            }

            int unreachable = 0;
            for (int i = 0; i < seen.Length; i++)
            {
                if (!seen[i])
                {
                    unreachable++;
                }
            }

            return unreachable;
        }

        /// <summary>
        /// Drops a ray on every node centre and every trim point and counts the ones that find nothing.
        ///
        /// The corridor sweep would eventually notice a missing pad, but only if it happened to sample
        /// inside the hole. This asks directly, at the places a hole can actually be.
        /// </summary>
        private static int CountJunctionHoles(StreetNetwork network)
        {
            Physics.SyncTransforms();
            int holes = 0;

            for (int n = 0; n < network.Nodes.Count; n++)
            {
                StreetNode node = network.Nodes[n];
                if (node.OnTrunkRoad)
                {
                    continue;
                }

                if (!HasGroundAt(node.Position))
                {
                    holes++;
                }

                for (int i = 0; i < node.Degree; i++)
                {
                    StreetEdge edge = network.Edges[node.Edges[i]];
                    bool atStart = edge.FromNode == node.Index;
                    float at = atStart ? edge.TrimStart : edge.Length - edge.TrimEnd;

                    if (!HasGroundAt(edge.Path.GetPositionAtDistance(at)))
                    {
                        holes++;
                    }
                }
            }

            return holes;
        }

        private static bool HasGroundAt(Vector3 expected)
        {
            return Physics.Raycast(expected + Vector3.up * 3f, Vector3.down,
                       out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)
                   && Mathf.Abs(hit.point.y - expected.y) < 0.5f;
        }

        /// <summary>
        /// The corridor sweep over one span of one path, reporting rather than logging so a caller can
        /// summarise forty of them.
        /// </summary>
        private static bool CorridorIsClear(
            IRoadPath path, float halfWidth, float clearance, float from, float to,
            out float firstAt, out string firstBy)
        {
            const float step = 2f;
            const float floorLift = 0.35f;

            firstAt = 0f;
            firstBy = null;

            var halfExtents = new Vector3(Mathf.Max(0.3f, halfWidth), clearance * 0.5f, step * 0.5f);
            var hits = new Collider[8];

            for (float distance = from; distance <= to; distance += step)
            {
                Vector3 centre = path.GetPositionAtDistance(distance);
                Vector3 forward = path.GetDirectionAtDistance(distance);
                if (forward.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                int count = Physics.OverlapBoxNonAlloc(
                    centre + Vector3.up * (floorLift + clearance * 0.5f), halfExtents, hits,
                    Quaternion.LookRotation(forward, Vector3.up), ~0, QueryTriggerInteraction.Ignore);

                if (count == 0)
                {
                    continue;
                }

                firstAt = distance;
                firstBy = hits[0] != null ? hits[0].gameObject.name : "unknown";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The materials for a tile's plants, in the order the mesh's submeshes ended up in.
        ///
        /// Empty submeshes are dropped when the mesh is built, so this cannot just be a fixed array — a tile
        /// above the tree line has boulders and nothing else.
        /// </summary>
        private static Material[] PlantMaterials(PrototypeMaterials materials, VegetationStats stats)
        {
            var result = new Material[stats.Submeshes.Count];

            for (int i = 0; i < stats.Submeshes.Count; i++)
            {
                switch (stats.Submeshes[i])
                {
                    case PlantMeshes.BarkSubmesh:
                        result[i] = materials.Bark;
                        break;
                    case PlantMeshes.ConiferSubmesh:
                        result[i] = materials.Conifer;
                        break;
                    case PlantMeshes.BroadleafSubmesh:
                        result[i] = materials.Broadleaf;
                        break;
                    case PlantMeshes.UndergrowthSubmesh:
                        result[i] = materials.Undergrowth;
                        break;
                    default:
                        result[i] = materials.Rock;
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Summarises the vegetation and checks the two things that would be invisible in the editor until
        /// they were a problem: something growing on the carriageway, and a tile heavy enough to cost frames.
        /// </summary>
        private static void ReportVegetation(
            VegetationStats stats,
            in VegetationShape vegetationShape,
            in RoadShape roadShape,
            VegetationContext context,
            int heaviestTile,
            string heaviestTileName)
        {
            if (stats.Plants == 0)
            {
                Debug.LogWarning("[Horizon] Vegetation: nothing grew anywhere. Check the clearances and the "
                                 + "clump threshold in VegetationShape.");
                return;
            }

            float treeLine = context.LowestElevation
                             + (context.SummitElevation - context.LowestElevation) * vegetationShape.TreeLineHeight;

            Debug.Log($"[Horizon] Vegetation: {stats.Conifers} conifers, {stats.Broadleaves} broadleaves, "
                      + $"{stats.Shrubs} shrubs, {stats.Tufts} grass tufts, {stats.Boulders} boulders, "
                      + $"{stats.Snags} snags — {stats.Triangles} triangles, heaviest tile "
                      + $"{heaviestTileName} at {heaviestTile}. Tree line around {treeLine:0} m.");

            float minimum = roadShape.OuterHalfWidth + 1f;
            if (stats.ClosestToRoad < minimum)
            {
                Debug.LogWarning($"[Horizon] Vegetation: something stands {stats.ClosestToRoad:0.0} m from the "
                                 + $"centreline, inside the {minimum:0.0} m the carriageway and its shoulders "
                                 + "occupy.");
            }

            if (stats.ClosestToStreet < 0f)
            {
                Debug.LogWarning($"[Horizon] Vegetation: something is growing {-stats.ClosestToStreet:0.0} m "
                                 + "inside the paved edge of a town street. MountainField.DistanceToRoad "
                                 + "answers for the trunk road only, so the town's streets have to reach "
                                 + "the scatter through VegetationContext or nothing keeps plants off "
                                 + "them.");
            }
            else if (stats.ClosestToStreet < float.MaxValue)
            {
                Debug.Log($"[Horizon] Vegetation: nearest plant to a town street stands "
                          + $"{stats.ClosestToStreet:0.0} m clear of the paving.");
            }

            if (heaviestTile > vegetationShape.MaxTrianglesPerTile)
            {
                Debug.LogWarning($"[Horizon] Vegetation: {heaviestTileName} carries {heaviestTile} triangles, "
                                 + $"over the {vegetationShape.MaxTrianglesPerTile} budget. Raise the cell "
                                 + "sizes or lower FarDensity in VegetationShape.");
            }

            ReportWindingFlips("Vegetation", stats.Flips);
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

                // Collision is the bore, the portal faces and the pillars — not the outer skin. The rock
                // flank sits on terrain that is already solid, so as a collider it adds nothing but a large
                // concave surface next to the road for the car to snag on.
                Mesh collision = TunnelBuilder.BuildCollision(path, roadShape, feature, field, name + "_Collision");
                collision = HorizonAssetUtility.ReplaceAsset(
                    collision, $"{GeneratedFolder}/{name}_Collision.asset");

                // Material order follows the Submesh constants on TunnelBuilder.
                CreateMeshObject(root.transform, name, mesh,
                    new[] { materials.Rock, materials.Concrete }, addCollider: true, markStatic: true,
                    collisionMesh: collision);

                built++;
            }

            Debug.Log($"[Horizon] Built {built} covered section(s).");
        }

        /// <summary>
        /// Drives a box the size of a generous vehicle envelope along the whole course and reports anything
        /// solid in the way.
        ///
        /// This exists because <see cref="ValidateRoadClearance"/> cannot answer the question that actually
        /// matters. That one tests <see cref="MountainField.HeightAt"/>, skips every covered stretch, runs
        /// before the tunnels are built, and has no notion of clearance above the road — so a tunnel massif
        /// standing across the carriageway passed it without a murmur, and the first anyone knew of it was
        /// driving into it. Asking physics is the only check that covers geometry nobody thought of.
        ///
        /// Must run after every geometry builder and before the car is placed, or the car is the obstruction.
        ///
        /// <para><paramref name="halfWidth"/> and <paramref name="clearance"/> are arguments rather than
        /// constants because the same check now runs over forty town streets as well as the pass. The
        /// 1.3 m box that is right for a 9 m trunk carriageway is over half the width of a 5.2 m alley,
        /// and a check that fires on every kerb is a check that gets ignored.</para>
        /// </summary>
        private static void ValidateDriveableCorridor(
            IRoadPath path, string what, float halfWidth, float clearance)
        {
            const float step = 2f;

            // Above the asphalt, so the road surface and the shoulder are not themselves hits.
            const float floorLift = 0.35f;

            var halfExtents = new Vector3(halfWidth, clearance * 0.5f, step * 0.5f);
            var hits = new Collider[8];
            float length = path.Length;

            Physics.SyncTransforms();

            // Canary. An edit-mode overlap query that finds nothing may mean the corridor is clear, or it may
            // mean no collider was registered and the check never ran at all. Those look identical in a log,
            // so ask a question the answer to which must be "yes": a box sunk into the carriageway has to hit
            // the road.
            Vector3 canaryAt = path.GetPositionAtDistance(length * 0.5f);
            int canary = Physics.OverlapBoxNonAlloc(
                canaryAt + Vector3.down * 0.5f, new Vector3(halfWidth, 0.6f, 1f), hits,
                Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

            if (canary == 0)
            {
                Debug.LogWarning($"[Horizon] Driveable corridor ({what}): the check could not run — a box inside the "
                                 + "road surface hit nothing, so no collider was queryable. This is not a "
                                 + "clear corridor, it is no answer.");
                return;
            }

            int blocked = 0;
            float firstAt = 0f;
            string firstBy = null;

            for (float distance = 0f; distance <= length; distance += step)
            {
                Vector3 center = path.GetPositionAtDistance(distance);
                Vector3 forward = path.GetDirectionAtDistance(distance);
                if (forward.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                Vector3 boxCenter = center + Vector3.up * (floorLift + clearance * 0.5f);
                var rotation = Quaternion.LookRotation(forward, Vector3.up);

                int count = Physics.OverlapBoxNonAlloc(
                    boxCenter, halfExtents, hits, rotation, ~0, QueryTriggerInteraction.Ignore);

                if (count == 0)
                {
                    continue;
                }

                blocked++;
                if (firstBy == null)
                {
                    firstAt = distance;
                    firstBy = hits[0] != null ? hits[0].gameObject.name : "unknown";
                }
            }

            if (blocked == 0)
            {
                Debug.Log($"[Horizon] Driveable corridor ({what}): clear over {length:0} m.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Driveable corridor ({what}): something solid stands in the carriageway at {blocked} "
                + $"of "
                + $"the {Mathf.CeilToInt(length / step)} sampled points. First at {firstAt:0} m along the "
                + $"course, against '{firstBy}'.");
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
            bool markStatic = true,
            StaticEditorFlags? staticFlags = null,
            Mesh collisionMesh = null)
        {
            var meshObject = new GameObject(name);
            meshObject.transform.SetParent(parent, false);

            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;

            if (addCollider)
            {
                // Usually the rendered mesh is also the collider. Where the two differ — the tunnels — what
                // you can see and what you can hit are genuinely different questions.
                meshObject.AddComponent<MeshCollider>().sharedMesh = collisionMesh != null ? collisionMesh : mesh;
            }

            if (markStatic)
            {
                // Generated world geometry never moves, so let Unity batch and light-bake it. The car
                // obviously must not be marked static. Vegetation overrides this: see BuildTerrainTiles.
                GameObjectUtility.SetStaticEditorFlags(meshObject, staticFlags
                    ?? (StaticEditorFlags.BatchingStatic
                        | StaticEditorFlags.ContributeGI
                        | StaticEditorFlags.OccluderStatic
                        | StaticEditorFlags.OccludeeStatic));
            }

            return meshObject;
        }
    }
}
