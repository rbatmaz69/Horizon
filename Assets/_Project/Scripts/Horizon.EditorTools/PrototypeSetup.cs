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
            public readonly Material Lane;
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
                // village lane with a dashed centre line reads as a main road.
                Lane = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Lane.mat", "M_Lane", new Color(0.27f, 0.27f, 0.29f), 0.30f);

                // A palette, because URP/Lit reads no vertex colours and the building meshes carry no UVs
                // — a per-house tint has to be a per-house material. Warm plaster tones, the kind an
                // alpine village is actually rendered and limewashed in.
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

                // Unlit, both of them. VillageLights swaps between the two on the window submesh at dusk
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

            // --- Village lanes. Laid out before the terrain, because they are what flattens the ground it
            // will stand on. Each gets its own RoadPath — the component carries no singleton, so several
            // in a scene are fine — and its own narrower ribbon.
            VillageShape villageShape = VillageShape.Default;
            List<RoadCourse> laneCourses = VillageBuilder.LayOutLanes(path, villageShape);
            RoadPath[] lanePaths = BuildVillageLanes(
                worldRoot.transform, laneCourses, villageShape, materials, out RoadShape[] laneShapes);

            BuildVillageJunctions(worldRoot.transform, path, lanePaths, laneShapes, roadShape,
                villageShape, materials);

            // One field, shared: the terrain is built from it, the guard rails ask it where the ground falls
            // away, and the tunnel bodies use it to bury their feet. Building a second would be slow and
            // could disagree with the first.
            //
            // The village's floor is levelled by handing the field a grid of level samples over the
            // village footprint. The lanes alone will not do it — they level a 24 m ribbon each and leave
            // the ground between them untouched, which measured 22 m of relief. See
            // VillageBuilder.BuildLevelSamples.
            List<Vector3> levelSamples = VillageBuilder.BuildLevelSamples(path, villageShape);
            for (int i = 0; i < lanePaths.Length; i++)
            {
                // The lanes as well as the apron. The apron's heights come from the main road, so without
                // this a lane running its own grade ends up standing on a plinth.
                VillageBuilder.AddPathSamples(lanePaths[i], 6f, levelSamples);
            }

            var field = new MountainField(path, terrainShape, 4f, levelSamples);

            ValidateRoadClearance(path, roadShape, field, course);
            ReportVillageGround(field, path, villageShape);

            // Planned after the field exists, because the plots are seated on the finished terrain.
            VillagePlan villagePlan = VillageBuilder.Plan(
                path, lanePaths, field, terrainShape, villageShape);

            BuildTerrainTiles(worldRoot.transform, path, roadShape, course, field, terrainShape,
                villageShape, villagePlan, materials);
            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);

            // After every builder and before the car exists — otherwise the car is the obstruction.
            ValidateDriveableCorridor(path, "the pass");
            for (int i = 0; i < lanePaths.Length; i++)
            {
                // The lanes too. Checking only the main road is why a fence could have stood in a lane
                // and the build would have called the world clear.
                ValidateDriveableCorridor(lanePaths[i], $"lane {i}");
            }

            ValidateVillageStreets(path, lanePaths, roadShape);

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
        /// <summary>
        /// Builds a RoadPath and a ribbon for every village lane, and hands back the paths so the height
        /// field can be given them.
        /// </summary>
        private static RoadPath[] BuildVillageLanes(
            Transform parent,
            List<RoadCourse> courses,
            in VillageShape villageShape,
            PrototypeMaterials materials,
            out RoadShape[] laneShapes)
        {
            if (courses == null || courses.Count == 0)
            {
                laneShapes = new RoadShape[0];
                return new RoadPath[0];
            }

            var root = new GameObject("VillageLanes");
            root.transform.SetParent(parent, false);

            // A lane is the pass's cross-section, narrowed and stripped of its markings. The centre line
            // lives in a baked texture, so a plain untextured surface is also the cheapest way to not have
            // one — a village lane with a dashed centre line would read as a main road.
            laneShapes = new RoadShape[courses.Count];

            RoadShape laneShape = RoadShape.Default;
            laneShape.HalfWidth = villageShape.LaneHalfWidth;
            laneShape.ShoulderWidth = 0.6f;
            laneShape.Crown = 0.05f;
            laneShape.MaxBankDegrees = 0f;

            var paths = new RoadPath[courses.Count];

            for (int i = 0; i < courses.Count; i++)
            {
                string name = $"Lane_{i}";
                laneShapes[i] = laneShape;

                var laneObject = new GameObject(name);
                laneObject.transform.SetParent(root.transform, false);

                RoadPath lane = laneObject.AddComponent<RoadPath>();
                lane.SetControlPoints(courses[i].ControlPoints);
                paths[i] = lane;

                Mesh mesh = RoadMeshBuilder.BuildRoad(lane, laneShape, name + "Mesh");
                mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}Mesh.asset");

                CreateMeshObject(laneObject.transform, name + "_Surface", mesh,
                    new[] { materials.Lane, materials.RoadShoulder });
            }

            Debug.Log($"[Horizon] Village lanes: {courses.Count}, {LaneLength(courses):0} m of street.");
            return paths;
        }

        /// <summary>
        /// A throat where each lane leaves a road, so the two surfaces run into one another instead of
        /// stopping near each other. RoadMeshBuilder leaves every ribbon end open, so there is nothing to
        /// attach to and this has to be its own geometry.
        ///
        /// Only the lanes that actually leave the main road get one — the back lane and the connector meet
        /// other lanes, whose narrow ribbons already overlap enough not to show a step.
        /// </summary>
        private static void BuildVillageJunctions(
            Transform parent,
            RoadPath main,
            RoadPath[] lanes,
            RoadShape[] laneShapes,
            in RoadShape roadShape,
            in VillageShape villageShape,
            PrototypeMaterials materials)
        {
            if (lanes.Length == 0)
            {
                return;
            }

            var root = new GameObject("VillageJunctions");
            root.transform.SetParent(parent, false);

            float side = Mathf.Sign(villageShape.LaneSide == 0f ? -1f : villageShape.LaneSide);
            float[] branchAt = { villageShape.FirstLaneAt, villageShape.SecondLaneAt };

            int built = 0;
            for (int i = 0; i < branchAt.Length && i < lanes.Length; i++)
            {
                Mesh mesh = VillageRoadBuilder.Build(
                    main, branchAt[i], lanes[i], roadShape, laneShapes[i], side,
                    villageShape.JunctionThroat, $"Junction_{i}");

                if (mesh == null)
                {
                    continue;
                }

                mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/Junction_{i}.asset");
                CreateMeshObject(root.transform, $"Junction_{i}", mesh, new[] { materials.Lane });
                built++;
            }

            Debug.Log($"[Horizon] Village junctions: {built} built.");
        }

        private static float LaneLength(List<RoadCourse> courses)
        {
            float total = 0f;
            for (int i = 0; i < courses.Count; i++)
            {
                total += courses[i].PlannedLength;
            }

            return total;
        }

        /// <summary>
        /// Measures how flat the village floor actually came out, as a number rather than an impression.
        ///
        /// The whole village rests on the claim that running the lanes through the height field levels the
        /// ground between them. If that claim is wrong the houses stand on a 22 % slope, and it is far
        /// cheaper to read that here than to build forty of them and look at a picture.
        /// </summary>
        private static void ReportVillageGround(MountainField field, RoadPath path, in VillageShape shape)
        {
            const float step = 8f;

            float side = Mathf.Sign(shape.LaneSide == 0f ? -1f : shape.LaneSide);
            float depth = shape.LaneLength;

            float lowest = float.MaxValue;
            float highest = float.MinValue;
            float steepest = 0f;
            int samples = 0;

            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += step)
            {
                Vector3 centre = path.GetPositionAtDistance(Mathf.Clamp(along, 0f, path.Length));
                Vector3 right = path.GetRightAtDistance(Mathf.Clamp(along, 0f, path.Length));

                for (float across = 10f; across <= depth; across += step)
                {
                    Vector3 point = centre + right * (across * side);

                    float here = field.HeightAt(point.x, point.z);
                    float ahead = field.HeightAt(point.x + step, point.z);
                    float beside = field.HeightAt(point.x, point.z + step);

                    // Relative to the road beside it, not to sea level — the valley approach climbs 1.5 %,
                    // so absolute height says nothing about whether the ground is buildable.
                    float relative = here - centre.y;
                    lowest = Mathf.Min(lowest, relative);
                    highest = Mathf.Max(highest, relative);

                    steepest = Mathf.Max(steepest, Mathf.Abs(ahead - here) / step);
                    steepest = Mathf.Max(steepest, Mathf.Abs(beside - here) / step);
                    samples++;
                }
            }

            if (samples == 0)
            {
                return;
            }

            Debug.Log($"[Horizon] Village ground: {samples} samples over {depth:0} m of depth, "
                      + $"{lowest:0.0} m to {highest:0.0} m relative to the road, "
                      + $"steepest {steepest * 100f:0} %.");

            if (highest - lowest > 6f || steepest > 0.25f)
            {
                Debug.LogWarning(
                    "[Horizon] Village ground is not buildable. The level samples from "
                    + "VillageBuilder.BuildLevelSamples are either not reaching MountainField, or their "
                    + "grid pitch is too coarse for the shelves to merge — it has to stay under twice "
                    + $"TerrainShape.VergeWidth, which is {TerrainShape.Default.VergeWidth:0} m.");
            }
        }

        private static void BuildTerrainTiles(
            Transform parent,
            RoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            MountainField field,
            in TerrainShape terrainShape,
            in VillageShape villageShape,
            VillagePlan villagePlan,
            PrototypeMaterials materials)
        {
            List<TerrainTileKey> tiles = TerrainTileBuilder.ListTiles(field, terrainShape, terrainShape.CorridorWidth);

            var terrainRoot = new GameObject("Terrain");
            terrainRoot.transform.SetParent(parent, false);

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            int totalTriangles = 0;

            VegetationShape vegetationShape = VegetationShape.Default;
            var vegetationContext = new VegetationContext(
                path, course, vegetationShape, villagePlan,
                villageShape.PlotClearance, villageShape.TreeKeepOut);
            var vegetationTotal = new VegetationStats();
            int heaviestTile = 0;
            string heaviestTileName = "none";

            var villageTotal = new VillageStats();
            var villageRenderers = new List<MeshRenderer>();
            var villageWindowSlots = new List<int>();

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

                Mesh buildings = VillageBuilder.BuildTile(
                    key, terrainShape, villageShape, villagePlan, name + "_Village",
                    out VillageStats villageStats);

                if (buildings != null)
                {
                    buildings = HorizonAssetUtility.ReplaceAsset(
                        buildings, $"{GeneratedFolder}/{name}_Village.asset");

                    // Houses keep OccluderStatic, unlike the trees. A village street is the one place in
                    // this world where occlusion culling has something solid to work with.
                    //
                    // No MeshCollider on the merged mesh: it would be a large concave collider full of
                    // window ledges and fence rails for the car to snag on, the same reason the tunnel
                    // skin was taken out of collision. Each plot gets a box below instead.
                    GameObject villageObject = CreateMeshObject(
                        tileObject.transform, name + "_Village", buildings,
                        VillageMaterials(materials, villageStats),
                        addCollider: false, markStatic: true,
                        staticFlags: StaticEditorFlags.BatchingStatic
                                     | StaticEditorFlags.OccluderStatic
                                     | StaticEditorFlags.OccludeeStatic);

                    int windowSlot = villageStats.Submeshes.IndexOf(BuildingMeshes.WindowSubmesh);
                    if (windowSlot >= 0)
                    {
                        villageRenderers.Add(villageObject.GetComponent<MeshRenderer>());
                        villageWindowSlots.Add(windowSlot);
                    }

                    AddPlotColliders(villageObject.transform, key, terrainShape, villagePlan);
                    villageTotal.Add(villageStats);
                }

                // After the plants and the houses, never before: the chunk takes its radius from the
                // renderers below it, and anything standing proud of the terrain bounds would pop in and
                // out on its own.
                WorldChunk chunk = tileObject.AddComponent<WorldChunk>();
                chunk.RecalculateBounds();
                EditorUtility.SetDirty(chunk);
            }

            Debug.Log($"[Horizon] Terrain: {tiles.Count} tiles of {tileSize:0} m, "
                      + $"{totalTriangles} triangles total, corridor {terrainShape.CorridorWidth:0} m.");

            ReportVegetation(vegetationTotal, vegetationShape, roadShape, vegetationContext,
                heaviestTile, heaviestTileName);

            ReportVillage(villageTotal, villageShape, villagePlan);
            WireVillageLights(parent, villageRenderers, villageWindowSlots, materials);
        }

        /// <summary>
        /// Hangs one VillageLights on the world root, holding every renderer that owns a window submesh.
        ///
        /// One component for the whole village rather than one per tile: every window in the place lights
        /// at the same instant, so there is nothing to be gained by deciding it thirty times over.
        /// </summary>
        private static void WireVillageLights(
            Transform parent,
            List<MeshRenderer> renderers,
            List<int> windowSlots,
            PrototypeMaterials materials)
        {
            if (renderers.Count == 0)
            {
                return;
            }

            var lightsObject = new GameObject("VillageLights");
            lightsObject.transform.SetParent(parent, false);

            VillageLights lights = lightsObject.AddComponent<VillageLights>();
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

        private static void ReportVillage(VillageStats stats, in VillageShape shape, VillagePlan plan)
        {
            if (plan == null || stats.Houses + stats.Windmills == 0)
            {
                Debug.LogWarning("[Horizon] Village: nothing was built. Check VillageShape's extent against "
                                 + "the course, and that the plots landed inside terrain tiles.");
                return;
            }

            Debug.Log($"[Horizon] Village: {stats.Houses} houses, {stats.Windmills} windmill, "
                      + $"{stats.Barns} barns, {stats.Sawmills} sawmills, {stats.Fences} fences, "
                      + $"{stats.Lamps} lamps, {stats.Cars} parked cars — {stats.Triangles} triangles "
                      + $"over {plan.Footprint.size.x:0} x {plan.Footprint.size.z:0} m.");

            if (stats.Triangles > shape.MaxTrianglesPerTile * 4)
            {
                Debug.LogWarning($"[Horizon] Village: {stats.Triangles} triangles is heavier than expected. "
                                 + "Raise PlotSpacing or lower the plot count in VillageShape.");
            }

            ReportWindingFlips("Village", stats.Flips);
        }

        /// <summary>
        /// Whether any face had to be turned round on its way into a mesh buffer.
        ///
        /// Zero is the only acceptable answer and it costs nothing to ask. The correction happens either
        /// way, so a non-zero count is never a broken build — it is a builder whose vertex order has drifted
        /// from the direction it claims the face looks in, and it is worth knowing on the run that
        /// introduces it rather than three stages later. Silent self-correction is how the mirrored
        /// placement basis went unnoticed until every wall in the village was inside out.
        ///
        /// <para>This is the only automated winding check there is, and that is a deliberate stopping point
        /// rather than a gap waiting to be filled. Four attempts were made at a second one that inspects the
        /// finished meshes — crossing parity on a <c>MeshCollider</c>, the same in exact Möller–Trumbore,
        /// per-face front visibility, and two-culling-mode ray sampling from outside the mesh bounds. Every
        /// one of them reported nine of ten wall submeshes riddled with holes on a village that renders
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
        private static Material[] VillageMaterials(PrototypeMaterials materials, VillageStats stats)
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
                    // Starts dark. VillageLights swaps in the lit one after sunset.
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
            VillagePlan plan)
        {
            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                VillagePlan.Plot plot = plan.Plots[i];

                if (plot.Position.x < originX || plot.Position.x >= originX + tileSize
                    || plot.Position.z < originZ || plot.Position.z >= originZ + tileSize)
                {
                    continue;
                }

                bool tall = plot.Kind == VillagePlotKind.Windmill;
                bool wide = plot.Kind == VillagePlotKind.Barn;

                float halfWidth = tall ? 4.5f : wide ? 7.5f : 5.6f;
                float halfDepth = tall ? 4.5f : wide ? 5.5f : 4.8f;
                float height = tall ? 16f : wide ? 8f : 6f;

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
        /// Checks the village streets against the two things that have actually gone wrong with them.
        ///
        /// Both bugs this catches shipped and survived two rounds of looking at screenshots, because a
        /// lane running the wrong way and a lane standing on a plinth both look plausible in a foggy
        /// render. Neither survives a number.
        ///
        /// 1. **No lane may cross the main carriageway.** A sign error in the turn-off heading sent both
        ///    lanes straight across it, coplanar to within five millimetres — z-fighting over the full
        ///    width of the road the player drives on.
        /// 2. **Junctions between lanes must be flush.** The back lane took its endpoint heights from a
        ///    helper that returns y = 0, so it met the side lanes with steps of 0.42 m and 0.98 m.
        /// </summary>
        private static void ValidateVillageStreets(RoadPath main, RoadPath[] lanes, in RoadShape roadShape)
        {
            if (lanes == null || lanes.Length == 0)
            {
                return;
            }

            const float step = 4f;
            const float flushTolerance = 0.05f;

            int crossings = 0;
            float worstCrossing = float.MaxValue;
            int steps = 0;
            float worstStep = 0f;

            for (int i = 0; i < lanes.Length; i++)
            {
                RoadPath lane = lanes[i];

                for (float along = 0f; along <= lane.Length; along += step)
                {
                    Vector3 point = lane.GetPositionAtDistance(Mathf.Min(along, lane.Length));

                    // Skip the first few metres: a lane is *meant* to touch the main road where it joins.
                    if (along < roadShape.OuterHalfWidth * 3f)
                    {
                        continue;
                    }

                    float toMain = PlanDistanceToPath(main, point, 8f);
                    if (toMain < roadShape.OuterHalfWidth)
                    {
                        crossings++;
                        worstCrossing = Mathf.Min(worstCrossing, toMain);
                    }
                }

                // Every other lane end that lands on this one has to meet it at the same height.
                for (int j = 0; j < lanes.Length; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    foreach (float end in new[] { 0f, lanes[j].Length })
                    {
                        Vector3 at = lanes[j].GetPositionAtDistance(end);
                        float plan = PlanDistanceToPath(lane, at, 4f, out float height);

                        if (plan > 2f)
                        {
                            continue;
                        }

                        float drop = Mathf.Abs(height - at.y);
                        if (drop > flushTolerance)
                        {
                            steps++;
                            worstStep = Mathf.Max(worstStep, drop);
                        }
                    }
                }
            }

            if (crossings > 0)
            {
                Debug.LogWarning($"[Horizon] Village streets: a lane runs across the main carriageway at "
                                 + $"{crossings} sampled points, closest {worstCrossing:0.0} m from the "
                                 + $"centreline against a {roadShape.OuterHalfWidth:0.0} m half-width. "
                                 + "Check the sign of the turn-off heading in VillageBuilder.LayOutLanes.");
            }

            if (steps > 0)
            {
                Debug.LogWarning($"[Horizon] Village streets: {steps} lane junction(s) are not flush, worst "
                                 + $"{worstStep:0.00} m. Lane endpoints must take their height from the "
                                 + "course they join, not from a plan-space heading vector.");
            }

            if (crossings == 0 && steps == 0)
            {
                Debug.Log($"[Horizon] Village streets: {lanes.Length} lanes clear of the carriageway and "
                          + "flush at every junction.");
            }
        }

        private static float PlanDistanceToPath(IRoadPath path, Vector3 point, float step)
        {
            return PlanDistanceToPath(path, point, step, out float _);
        }

        /// <summary>
        /// Plan distance from a point to a path, and the path's height at the nearest point.
        ///
        /// A walk rather than a lookup because there is no inverse projection anywhere in the codebase
        /// and this runs a few thousand times at edit time, not per frame.
        /// </summary>
        private static float PlanDistanceToPath(IRoadPath path, Vector3 point, float step, out float height)
        {
            float best = float.MaxValue;
            height = point.y;

            for (float along = 0f; along <= path.Length; along += step)
            {
                Vector3 at = path.GetPositionAtDistance(Mathf.Min(along, path.Length));

                float dx = at.x - point.x;
                float dz = at.z - point.z;
                float plan = Mathf.Sqrt(dx * dx + dz * dz);

                if (plan < best)
                {
                    best = plan;
                    height = at.y;
                }
            }

            return best;
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
        /// </summary>
        private static void ValidateDriveableCorridor(RoadPath path, string what)
        {
            const float step = 2f;

            // Wider and far taller than the car (2.00 x 1.39 m). The point is to fail early on anything that
            // would even feel close, not to certify the exact hull.
            const float halfWidth = 1.3f;
            const float clearance = 4f;

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
