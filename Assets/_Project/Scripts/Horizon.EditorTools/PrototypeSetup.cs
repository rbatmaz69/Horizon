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

            /// <summary>Four-lane markings for one carriageway of the motorway.</summary>
            public readonly Material MotorwaySurface;
            public readonly Material Concrete;
            public readonly Material GuardRail;
            public readonly Material Grass;
            public readonly Material Rock;
            public readonly Material Lane;
            public readonly Material Footway;
            public readonly Material WindowDay;
            public readonly Material WindowNight;
            public readonly Material LampNight;
            public readonly Material TailNight;
            public readonly Material[] TrafficBodies;

            /// <summary>
            /// One material for every opaque face of every building, whatever colour it is.
            ///
            /// Horizon/VertexTintLit multiplies its base colour by the vertex colour, and the palette is
            /// written into the mesh — so this is white, and the town is as colourful as it ever was on
            /// one draw call instead of ten. See BuildingMeshes.OpaqueTints.
            /// </summary>
            public readonly Material BuildingTint;

            /// <summary>
            /// The same one material, for the foliage: bark, conifer, broadleaf and undergrowth in one
            /// draw call instead of four. Separate from BuildingTint only because plants want a
            /// different smoothness from plaster.
            /// </summary>
            public readonly Material FoliageTint;
            public readonly Material CarBody;
            public readonly Material Tyre;
            public readonly Material CarGlass;
            public readonly Material CarRim;
            public readonly Material LightFront;
            public readonly Material LightRear;
            public readonly Material Smoke;

            /// <summary>
            /// The exhaust flame. Additive, so it brightens what it is over rather than being pasted on
            /// top — which is the difference between fire and an orange sticker, and the reason it can
            /// glow at dusk and vanish into a noon sky.
            /// </summary>
            public readonly Material Flame;

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

                // A second atlas rather than a wider road wearing the first. The markings are painted in
                // normalised coordinates across the asphalt, so stretching the two-lane texture over a
                // 15 m carriageway would give four lanes' worth of road with one lane line down the
                // middle of it — correct-looking geometry that says the wrong thing.
                RoadShape motorwayShape = RoadShape.Autobahn;

                Texture2D motorwayTexture = HorizonAssetUtility.LoadOrCreateTexture(
                    ProjectRoot + "/Art/T_MotorwaySurface.png",
                    () => RoadTextureBuilder.BuildSurface(motorwayShape, 4),
                    anisoLevel: 8);

                MotorwaySurface = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_MotorwaySurface.mat", "M_MotorwaySurface", Color.white, 0.34f, 0f,
                    motorwayTexture);

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
                BuildingTint = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_BuildingTint.mat", "M_BuildingTint", 0.12f);
                FoliageTint = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_FoliageTint.mat", "M_FoliageTint", 0.06f);

                // Unlit, all of them. TownLights swaps between day and night on the lit-glass and lamp
                // submeshes at dusk and dawn — no keyword, no property block, and nothing written to a
                // material at runtime.
                WindowDay = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_WindowDay.mat", "M_WindowDay", new Color(0.20f, 0.23f, 0.27f));
                WindowNight = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_WindowNight.mat", "M_WindowNight",
                    new Color(1.55f, 1.25f, 0.72f));

                // Brighter and whiter than a window, because a sodium lamp is not a living room. There is
                // deliberately no M_LampDay: the lamp submesh takes the *street's own* material by day,
                // which is the only way a pool of light on the carriageway can vanish exactly rather than
                // to within a shade — see TownMaterials.
                LampNight = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_LampNight.mat", "M_LampNight",
                    new Color(1.90f, 1.72f, 1.28f));

                // A lit tail lamp, which is not the same thing as M_LightRear: that one is the *off*
                // state of the player's car, animated by a property block. An ambient car has no block —
                // it takes a whole material — and a pair of dark red rectangles is what a car looks like
                // with its lights off, which after dark is wrong.
                TailNight = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_TailNight.mat", "M_TailNight",
                    new Color(1.35f, 0.12f, 0.07f));

                // Six body colours against five body shapes, which is the whole reason there are six.
                // The two counts share no factor, so shape and colour drift against each other and a
                // pairing only repeats after thirty cars — a pool of twenty-four has no two alike. Make
                // them equal and you get five combinations shown five times each, which looks more like a
                // bug than one colour would.
                //
                // All muted against the player's orange: ambient traffic that pulls the eye is traffic
                // that has stopped being ambient.
                TrafficBodies = new[]
                {
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficSlate.mat", "M_TrafficSlate",
                        new Color(0.33f, 0.36f, 0.40f), 0.55f, 0.1f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficSand.mat", "M_TrafficSand",
                        new Color(0.72f, 0.66f, 0.52f), 0.52f, 0.1f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficMoss.mat", "M_TrafficMoss",
                        new Color(0.34f, 0.42f, 0.34f), 0.55f, 0.1f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficMaroon.mat", "M_TrafficMaroon",
                        new Color(0.38f, 0.20f, 0.20f), 0.54f, 0.1f),

                    // The one light colour in the set, and the one that reads at distance in fog — a van
                    // in off-white is the only ambient car you can pick out at four hundred metres.
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficBone.mat", "M_TrafficBone",
                        new Color(0.78f, 0.77f, 0.72f), 0.50f, 0.1f),
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficNavy.mat", "M_TrafficNavy",
                        new Color(0.22f, 0.27f, 0.36f), 0.56f, 0.1f),
                };
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
                Flame = HorizonAssetUtility.LoadOrCreateParticleMaterial(
                    MaterialsFolder + "/M_ExhaustFlame.mat", "M_ExhaustFlame", smokeTexture,
                    new Color(1f, 0.66f, 0.26f, 1f), additive: true);

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
            VehicleConfig config = AssetDatabase.LoadAssetAtPath<VehicleConfig>(VehicleConfigPath);

            // A rebuild must not silently construct the car from an asset whose numbers were chosen
            // under meanings the code has since changed. VehicleConfigReset owns that judgement — it is
            // a version stamp, not a guess at which values look wrong.
            VehicleConfigReset.ResetIfStale(config);

            return config;
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
            // Follows the shell: 2.26 m across the arches, a sill at -0.59 and a crowned roof at 0.69,
            // running from a tail cap at -2.48 to a nose at 2.26. Re-derived every time the station
            // table moves — a collider left behind after a reshape is the kind of thing nothing
            // complains about until a tunnel does.
            collider.center = new Vector3(0f, 0.05f, -0.11f);
            collider.size = new Vector3(2.26f, 1.28f, 4.74f);

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

            // The loaded voice sits on the same object family and the same spatial blend as the other,
            // because the two are one engine crossfaded — any difference in placement would be audible
            // as the sound moving when you put your foot down.
            AudioSource engineLoadSource = CreateAudioSource(root.transform, "Audio_EngineLoad", 0.25f);

            // The exhaust is behind you and the tyres are under you, so neither is worth spatialising as
            // much as the engine: at this camera distance it only smears them.
            AudioSource exhaustSource = CreateAudioSource(root.transform, "Audio_Exhaust", 0.15f);
            exhaustSource.loop = false;
            exhaustSource.volume = 1f;

            AudioSource tyreSource = CreateAudioSource(root.transform, "Audio_Tyres", 0.1f);

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
                serialized.FindProperty("engineLoadSource").objectReferenceValue = engineLoadSource;
                serialized.FindProperty("exhaustSource").objectReferenceValue = exhaustSource;
                serialized.FindProperty("tyreSource").objectReferenceValue = tyreSource;
                serialized.FindProperty("engineReverb").objectReferenceValue = reverb;
                serialized.FindProperty("cover").objectReferenceValue = cover;
            });

            // A silent layer is invisible until someone drives the car and notices something missing,
            // which for the exhaust means noticing an absence of a noise that only happens on a hard
            // shift. Cheaper to assert it here.
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "engineSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "engineLoadSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "exhaustSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "tyreSource");

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

            // --- The motorway. One authored median line, two carriageways offset from it, and a link
            // road down to the foot of the pass. The centreline is never paved.
            RoadShape motorwayShape = RoadShape.Autobahn;

            var motorwayPathObject = new GameObject("MotorwayPath");
            motorwayPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath motorwayPath = motorwayPathObject.AddComponent<RoadPath>();

            RoadCourse motorwayCourse = AutobahnCourse.Build();
            motorwayPath.SetControlPoints(motorwayCourse.ControlPoints);
            ReportCourse(motorwayCourse, motorwayPath, "Motorway");

            var linkPathObject = new GameObject("MotorwayLinkPath");
            linkPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath linkPath = linkPathObject.AddComponent<RoadPath>();

            RoadCourse linkCourse = AutobahnCourse.BuildLink();
            linkPath.SetControlPoints(linkCourse.ControlPoints);

            var westbound = new OffsetRoadPath(motorwayPath, -AutobahnCourse.CarriagewayOffset);
            var eastbound = new OffsetRoadPath(motorwayPath, AutobahnCourse.CarriagewayOffset);

            BuildCarriageway(worldRoot.transform, "CarriagewayWest", westbound, motorwayShape, materials);
            BuildCarriageway(worldRoot.transform, "CarriagewayEast", eastbound, motorwayShape, materials);

            Mesh linkMesh = RoadMeshBuilder.BuildRoad(linkPath, roadShape, "MotorwayLinkMesh");
            linkMesh = HorizonAssetUtility.ReplaceAsset(
                linkMesh, GeneratedFolder + "/MotorwayLinkMesh.asset");

            GameObject linkObject = CreateMeshObject(worldRoot.transform, "MotorwayLink", linkMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder });

            WorldChunk linkChunk = linkObject.AddComponent<WorldChunk>();
            linkChunk.RecalculateBounds();
            linkChunk.SetBounds(linkChunk.Center, 100000f);
            EditorUtility.SetDirty(roadChunk);

            // --- The city's arterial. Never paved: it is a coordinate axis and a height datum, which is
            // all a town's trunk road has to be. What the player drives on through Hochstadt is the
            // boulevard in its layout table, sitting on this line.
            var arterialObject = new GameObject("ArterialPath");
            arterialObject.transform.SetParent(worldRoot.transform, false);
            RoadPath arterialPath = arterialObject.AddComponent<RoadPath>();

            RoadCourse arterialCourse = HochstadtCourse.Build();
            arterialPath.SetControlPoints(arterialCourse.ControlPoints);

            // --- The settlements. Talheim on the pass, Hochstadt on the motorway's arterial.
            //
            // Both are prepared here and planned after the height field, and that split is not a style
            // choice — see TownBuild. Their street centrelines are what their ground is levelled to, so
            // every one of them has to exist before the field; their plots are seated on the finished
            // terrain mesh, so none of them can be planned until after it.
            var levelSamples = new List<Vector3>();

            TownBuild talheim = PrepareTown(
                "Talheim", TalheimLayout.Build(), path, TownShape.Default,
                worldRoot.transform, roadShape, terrainShape, levelSamples);

            TownBuild hochstadt = PrepareTown(
                "Hochstadt", HochstadtLayout.Build(), arterialPath, TownShape.Hochstadt,
                worldRoot.transform, motorwayShape, terrainShape, levelSamples);

            var towns = new[] { talheim, hochstadt };
            Phase(clock, "roads and street networks");

            // One field, shared: the terrain is built from it, the guard rails ask it where the ground falls
            // away, and the tunnel bodies use it to bury their feet. Building a second would be slow and
            // could disagree with the first.
            //
            // Every carriageway in the world, in one field. Two fields would each carve a mountain from
            // their own road and disagree where the two came near, leaving a step down the seam.
            //
            // The motorway's carriageways are handed their course as well as their path, which is what
            // lets the field leave a valley standing under a viaduct instead of filling it in — see
            // MountainField.carriesShelf. The pass and the link have no bridges and pass none.
            var roads = new[]
            {
                new MountainField.FieldRoad(path),
                new MountainField.FieldRoad(westbound, motorwayCourse),
                new MountainField.FieldRoad(eastbound, motorwayCourse),
                new MountainField.FieldRoad(linkPath),
            };

            var field = new MountainField(roads, terrainShape, 4f, levelSamples);
            Phase(clock, $"height field ({levelSamples.Count} level samples)");


            ValidateRoadClearance(path, roadShape, field, course);
            ValidateRoadClearance(westbound, motorwayShape, field, motorwayCourse, "Westbound");
            ValidateRoadClearance(eastbound, motorwayShape, field, motorwayCourse, "Eastbound");
            ValidateBridges(westbound, field, motorwayCourse);
            // The second half of every town: street meshes onto the finished terrain, then blocks and
            // plots seated on it.
            int plots = 0;
            for (int i = 0; i < towns.Length; i++)
            {
                PlanTown(towns[i], field, terrainShape, materials);
                plots += towns[i].Plan.Plots.Count;
            }

            Phase(clock, $"street meshes, blocks and parcels ({plots} plots)");

            // The counts-to-offsets shape TownLights reads: one start per renderer plus a terminator,
            // and a flat run of (slot, group) pairs behind it. The town tiles fill it first and the
            // traffic pool adds to it, because every window, lamp and headlight in the world is decided
            // by one component reading one sun — see TownLights.
            var litRenderers = new List<MeshRenderer>();
            var litSlotStart = new List<int> { 0 };
            var litSlots = new List<int>();
            var litSlotGroups = new List<int>();

            BuildTerrainTiles(worldRoot.transform, path, roadShape, course, field, terrainShape,
                towns, materials, litRenderers, litSlotStart, litSlots, litSlotGroups);
            ValidateLandmarks(field, course, path, talheim.Plan);
            MarkTownLandmarks(worldRoot.transform, talheim.Network, talheim.Plan);
            Phase(clock, "terrain, vegetation and buildings");

            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);

            // --- Motorway structures. Per carriageway, because a divided road has two of everything:
            // two bores through a spur, two decks over a valley, two sets of verge rails. Only the
            // barrier down the middle is single, and it runs on the median line the carriageways were
            // offset from.
            // One bore over the whole road rather than one per carriageway, and the driveable-corridor
            // check is what settled it. TunnelBuilder sweeps a massif 80 m across around whatever path
            // it is given, and the two carriageways are 21 m apart — so a bore built on each buried the
            // other one in rock, at 92 sampled points of the westbound carriageway. Real motorway
            // tunnels are twin bores because they are driven through rock from either end; this one is
            // a single span wide enough to cover both, which is the shape the tool can actually build.
            RoadShape boreShape = motorwayShape;
            boreShape.HalfWidth = AutobahnCourse.CarriagewayOffset + motorwayShape.OuterHalfWidth;

            BuildCoveredSections(worldRoot.transform, motorwayPath, boreShape, motorwayCourse, field,
                materials, "Motorway");

            BuildBridges(worldRoot.transform, westbound, motorwayShape, field, motorwayCourse,
                materials, "West");
            BuildBridges(worldRoot.transform, eastbound, motorwayShape, field, motorwayCourse,
                materials, "East");

            BuildGuardRails(worldRoot.transform, westbound, motorwayShape, field, motorwayCourse,
                materials, "MotorwayWest");
            BuildGuardRails(worldRoot.transform, eastbound, motorwayShape, field, motorwayCourse,
                materials, "MotorwayEast");
            BuildMedianBarrier(worldRoot.transform, motorwayPath, motorwayShape, motorwayCourse, materials);

            BuildGuardRails(worldRoot.transform, linkPath, roadShape, field, linkCourse,
                materials, "MotorwayLink");

            BuildTraffic(worldRoot.transform, towns, path, roadShape, materials,
                litRenderers, litSlotStart, litSlots, litSlotGroups,
                motorwayPath, motorwayShape, AutobahnCourse.CarriagewayOffset);

            // After both, so one component carries the town's windows and the traffic's lamps.
            WireTownLights(worldRoot.transform, litRenderers, litSlotStart, litSlots, litSlotGroups,
                materials);

            // After every builder and before the car exists — otherwise the car is the obstruction.
            ValidateDriveableCorridor(path, "the pass", 1.3f, 4f);
            ValidateDriveableCorridor(westbound, "the westbound carriageway", 1.3f, 4f);
            ValidateDriveableCorridor(eastbound, "the eastbound carriageway", 1.3f, 4f);
            ValidateDriveableCorridor(linkPath, "the motorway link", 1.3f, 4f);
            Phase(clock, "validation");
            int worstJunction = ValidateStreetNetwork(talheim.Network, path, roadShape);
            MarkWorstJunction(worldRoot.transform, talheim.Network, worstJunction);
            ValidateStreetNetwork(hochstadt.Network, arterialPath, motorwayShape);

            // --- Streaming.
            var streamingObject = new GameObject("Streaming");
            WorldStreamer streamer = streamingObject.AddComponent<WorldStreamer>();
            WorldStreamingDriver driver = streamingObject.AddComponent<WorldStreamingDriver>();
            HorizonAssetUtility.Configure(driver, serialized =>
                serialized.FindProperty("streamer").objectReferenceValue = streamer);

            // Counted after every builder and before the car, at the streamer's own radius and again at
            // the first pressure valve, so the question "would 450 m help, and by how much" is answered
            // in the log rather than by trying it.
            List<Vector3> stations = DrawCallStations(path, motorwayPath);
            ReportDrawCallBudget(worldRoot.transform, stations, streamer.LoadRadius);
            ReportDrawCallBudget(worldRoot.transform, stations, 450f);

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

            // The controls the player actually sees. On the Bootstrap object rather than in the world
            // scene, so they survive the additive load and exist before there is anything to drive.
            TouchUiSetup.Build(root, router);

            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
        }

        /// <summary>
        /// One carriageway of the motorway: the ordinary road ribbon, built off an offset path and
        /// wearing the four-lane atlas.
        ///
        /// <para>Nothing here is motorway-specific except which two assets it is handed, which is the
        /// point — <see cref="RoadMeshBuilder"/> needed no changes at all to build a divided road.</para>
        /// </summary>
        private static void BuildCarriageway(
            Transform parent,
            string name,
            IRoadPath path,
            in RoadShape shape,
            PrototypeMaterials materials)
        {
            Mesh mesh = RoadMeshBuilder.BuildRoad(path, shape, name + "Mesh");
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}Mesh.asset");

            GameObject carriageway = CreateMeshObject(parent, name, mesh,
                new[] { materials.MotorwaySurface, materials.RoadShoulder });

            // Never unloads, for the same reason the pass road does not: it is a thin ribbon, and the
            // car is by definition standing on one of them.
            WorldChunk chunk = carriageway.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);
        }

        /// <summary>
        /// One settlement, carried across the build in the two halves it has to be built in.
        ///
        /// <para>The world cannot simply build one town and then the next, and the reason is
        /// <see cref="MountainField"/>. A town's street centrelines are what its ground is levelled to,
        /// so every town's centrelines have to exist <i>before</i> the field is made; but a town's plots
        /// are seated on the finished terrain mesh, so they have to be planned <i>after</i> it. One field
        /// for the world is not an optimisation — two would each carve a mountain from their own roads
        /// and disagree along the seam between them.</para>
        ///
        /// <para>So a town is prepared, the field is built from all of them at once, and then each is
        /// planned. This is what holds the first half's answers until the second half needs them.</para>
        /// </summary>
        private sealed class TownBuild
        {
            public string Name;
            public IRoadPath Trunk;
            public TownShape Shape;
            public StreetNetwork Network;
            public Transform StreetsRoot;
            public Bounds Footprint;

            // Filled by PlanTown, after the field exists.
            public StreetIndex Index;
            public List<TownBlock> Blocks;
            public TownPlan Plan;
        }

        /// <summary>
        /// Everything about a town that has to happen before the height field: the graph, its trims, and
        /// the level samples the ground will be flattened to.
        /// </summary>
        private static TownBuild PrepareTown(
            string name,
            TownNetworkSpec layout,
            IRoadPath trunk,
            in TownShape preset,
            Transform worldRoot,
            in RoadShape trunkShape,
            in TerrainShape terrainShape,
            List<Vector3> levelSamples)
        {
            // Sized to the layout before anything is validated against it. The basin's extent used to be
            // a hand-set number that the table was trusted to stay inside of, and it did not: Talheim's
            // crescent and the lane out to it were authored past the levelled floor, so their paving
            // stood over hillside. See TownShape.CoverLayout.
            TownShape shape = TownShape.CoverLayout(preset, layout, terrainShape.RoadShelfDrop);

            ValidateTownMapping(trunk, shape, name);

            var streetsRoot = new GameObject(name + "Streets");
            streetsRoot.transform.SetParent(worldRoot, false);

            StreetNetwork network = StreetNetwork.Build(
                trunk, shape, layout, streetsRoot.transform, terrainShape.RoadShelfDrop);

            StreetJunctionBuilder.ResolveTrims(network, trunkShape.OuterHalfWidth);

            // After the trims and before anything is built: a square's paved edge runs from one trim
            // point to the other, because between the trim point and the node the ground belongs to the
            // junction pad.
            network.BuildSquareInteriors();

            // The town's floor is levelled by handing the field a grid of level samples over the whole
            // basin. The streets alone will not do it — a road levels a 24 m ribbon either side and
            // leaves the ground between them untouched, which measured 22 m of relief on the village
            // that was here before.
            int before = levelSamples.Count;
            levelSamples.AddRange(TownShape.BuildLevelSamples(trunk, shape));

            for (int i = 0; i < network.Edges.Count; i++)
            {
                // Every street centreline, on top of the basin grid. Belt and braces rather than a
                // correction — both come from FloorHeight — but it is what makes the shelf follow a
                // street exactly rather than to within half a sample pitch.
                TownPlanner.AddPathSamples(network.Edges[i].Path, 8f, levelSamples);
            }

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                levelSamples.Add(network.Nodes[i].Position);
            }

            // From this town's own samples only, so two towns do not share one enormous footprint with
            // the empty country between them inside it.
            var mine = levelSamples.GetRange(before, levelSamples.Count - before);

            return new TownBuild
            {
                Name = name,
                Trunk = trunk,
                Shape = shape,
                Network = network,
                StreetsRoot = streetsRoot.transform,
                Footprint = TownShape.Footprint(mine, shape.CorridorMargin),
            };
        }

        /// <summary>Everything that needs the finished terrain: blocks, quarters and plots.</summary>
        private static void PlanTown(
            TownBuild town,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials)
        {
            BuildStreetMeshes(town.StreetsRoot, town.Network, town.Trunk, RoadShape.Default,
                town.Shape, field, terrainShape, materials, town.Name);

            ValidateStreetClearance(town.Network, field, terrainShape);
            ReportPadWinding(town.Network);
            ReportTownGround(field, town.Trunk, terrainShape, town.Shape, town.Name);

            town.Index = new StreetIndex(town.Network);
            town.Blocks = town.Network.FindBlocks(out int[] blockOfHalfEdge);
            ReportBlocks(town.Blocks, town.Name);

            town.Plan = TownPlanner.Plan(
                town.Network, town.Index, field, terrainShape, town.Shape, town.Trunk,
                town.Blocks, blockOfHalfEdge);
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
        private static void ReportCourse(RoadCourse course, RoadPath path, string what = "Pass")
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
                $"[Horizon] {what} course: {length:0} m long, {elevationGain:0} m of elevation, "
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

                ParticleSystem flame = CreateExhaustFlame(emitterObject.transform, materials);

                ExhaustSmoke smoke = emitterObject.AddComponent<ExhaustSmoke>();
                HorizonAssetUtility.Configure(smoke, serialized =>
                    serialized.FindProperty("flame").objectReferenceValue = flame);

                HorizonAssetUtility.AssertReferenceAssigned(smoke, "flame");
            }
        }

        /// <summary>
        /// The flame at a tailpipe: a burst emitter that does nothing at all until the exhaust lights.
        ///
        /// <para>Emission by burst rather than by rate, so the particle system is idle for almost all of
        /// its life and <c>ExhaustSmoke</c> decides the moments. It sits <i>beside</i> the smoke rather
        /// than replacing it, because the plume is always there and the flame is punctuation on it.</para>
        ///
        /// <para>Local simulation space, unlike the smoke. Smoke is left behind by a moving car and so
        /// lives in world space; a flame lasts a tenth of a second and belongs to the pipe, and in world
        /// space it would smear into a streak at anything above walking pace.</para>
        /// </summary>
        private static ParticleSystem CreateExhaustFlame(Transform parent, PrototypeMaterials materials)
        {
            var flameObject = new GameObject("Flame");
            flameObject.transform.SetParent(parent, false);

            ParticleSystem particles = flameObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = particles.main;
            main.duration = 1f;
            main.loop = false;
            main.playOnAwake = false;

            // A tenth of a second. Long enough to see, short enough that it is over before the eye can
            // decide it is a particle system.
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.07f, 0.16f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 13f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.30f);
            main.startColor = new Color(1f, 0.62f, 0.20f, 1f);
            main.gravityModifier = 0f;
            main.maxParticles = 48;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;

            // Nothing over time; every particle comes from an Emit call.
            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 13f;
            shape.radius = 0.03f;

            // White hot at the pipe, orange in the middle, gone. Fading the alpha to nothing matters
            // more than usual on an additive material, which has no other way to disappear.
            ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
            color.enabled = true;
            var burn = new Gradient();
            burn.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.75f), 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0.45f),
                    new GradientColorKey(new Color(0.75f, 0.16f, 0.03f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.35f),
                    new GradientAlphaKey(0f, 1f),
                });
            color.color = new ParticleSystem.MinMaxGradient(burn);

            ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.35f));

            var renderer = flameObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = materials.Flame;
            renderer.sortingFudge = 10f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return particles;
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
            IRoadPath trunk,
            in RoadShape trunkShape,
            in TownShape townShape,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials,
            string what)
        {
            if (network.Edges.Count == 0)
            {
                Debug.LogWarning($"[Horizon] {what} streets: the layout table produced no streets at all.");
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
                    field, terrainShape, buffer);
            }

            // Counted in three, because "five thousand faces are backwards" says nothing about which
            // builder to go and read. One number per builder is the difference between a tripwire and a
            // hunt.
            int ribbonFlips = buffer.FlipCount;
            var flipsBefore = (int[])buffer.FlipCountBySubmesh.Clone();

            int pads = 0;
            int mouths = 0;

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                if (network.Nodes[i].OnTrunkRoad)
                {
                    continue;
                }

                StreetJunctionBuilder.AppendPad(network, i, field, terrainShape, buffer);
                pads++;
            }

            int padFlips = buffer.FlipCount - ribbonFlips;
            var padFlipsBySubmesh = new int[buffer.FlipCountBySubmesh.Length];
            for (int i = 0; i < padFlipsBySubmesh.Length; i++)
            {
                padFlipsBySubmesh[i] = buffer.FlipCountBySubmesh[i] - flipsBefore[i];
            }

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                if (!network.Nodes[i].OnTrunkRoad)
                {
                    continue;
                }

                StreetJunctionBuilder.AppendTrunkMouth(
                    network, i, trunk, trunkShape, network.Nodes[i].AlongTrunk,
                    field, terrainShape, buffer);
                mouths++;
            }

            int mouthFlips = buffer.FlipCount - ribbonFlips - padFlips;

            for (int i = 0; i < network.Squares.Count; i++)
            {
                StreetJunctionBuilder.AppendSquare(network.Squares[i], buffer);
            }

            int squareFlips = buffer.FlipCount - ribbonFlips - padFlips - mouthFlips;

            var used = new List<int>(TownStreetBuilder.StreetSubmeshCount);
            Mesh mesh = buffer.ToMesh(what + "StreetsMesh", used);
            if (mesh == null)
            {
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{what}StreetsMesh.asset");

            GameObject streetObject = CreateMeshObject(
                parent, "Streets", mesh, StreetMaterials(materials, used));

            WorldChunk chunk = streetObject.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);
            EditorUtility.SetDirty(chunk);

            Debug.Log($"[Horizon] {what} streets: {network.Nodes.Count} nodes, {network.Edges.Count} "
                      + $"streets, {network.TotalLength:0} m, {pads} junction pads and {mouths} trunk "
                      + $"mouths — {mesh.triangles.Length / 3} triangles in {used.Count} draw calls.");

            for (int i = 0; i < network.Squares.Count; i++)
            {
                TownSquare square = network.Squares[i];
                Debug.Log($"[Horizon] Square '{square.Name}': {square.Edges.Length} edges, "
                          + $"{square.Area:0} m² paved, centre at "
                          + $"({square.Centre.x:0}, {square.Centre.z:0}).");
            }

            ReportWindingFlips("Town street ribbons", ribbonFlips);
            ReportWindingFlips("Town junction pads", padFlips, padFlipsBySubmesh);
            ReportWindingFlips("Town trunk mouths", mouthFlips);
            ReportWindingFlips("Town squares", squareFlips);
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
                    case TownStreetBuilder.VergeSubmesh:
                        result[i] = materials.Grass;
                        break;
                    default:
                        result[i] = materials.Footway;
                        break;
                }
            }

            return result;
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
        private static void ValidateTownMapping(IRoadPath path, in TownShape shape, string what)
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
                Debug.Log($"[Horizon] {what} mapping: town-local coordinates hold, tightest scale "
                          + $"{worst:0.00} at {worstAlong:0} m along.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] {what} mapping folds: the along-axis is squeezed to {worst:0.00} of its length "
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
            IRoadPath path,
            in TerrainShape terrainShape,
            in TownShape shape,
            string what)
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

            Debug.Log($"[Horizon] {what} ground: {samples} samples over "
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
            IReadOnlyList<TownBuild> towns,
            PrototypeMaterials materials,
            List<MeshRenderer> townRenderers,
            List<int> townSlotStart,
            List<int> townSlots,
            List<int> townSlotGroups)
        {
            // One region per settlement rather than one big box round the lot: the corridor is widened
            // where a town is, and a rectangle spanning both would drag in every tile of open country
            // between them.
            var extraRegions = new Bounds[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                extraRegions[i] = towns[i].Footprint;
            }

            List<TerrainTileKey> tiles = TerrainTileBuilder.ListTiles(
                field, terrainShape, terrainShape.CorridorWidth, extraRegions);

            var terrainRoot = new GameObject("Terrain");
            terrainRoot.transform.SetParent(parent, false);

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            int totalTriangles = 0;

            VegetationShape vegetationShape = VegetationShape.Default;

            var settlements = new VegetationContext.TownSource[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                settlements[i] = new VegetationContext.TownSource(
                    towns[i].Plan, towns[i].Network,
                    towns[i].Shape.PlotClearance, towns[i].Shape.TreeKeepOut);
            }

            var vegetationContext = new VegetationContext(
                path, course, vegetationShape, settlements);
            var vegetationTotal = new VegetationStats();
            int heaviestTile = 0;
            string heaviestTileName = "none";

            var townTotals = new TownStats[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                townTotals[i] = new TownStats();
            }

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

                // Every settlement gets its own mesh on this tile. Two towns never share one,
                // even where their tiles would overlap: the merged submesh layout is per town
                // and TownLights looks its lit slots up in that layout.
                for (int s = 0; s < towns.Count; s++)
                {
                    TownBuild town = towns[s];

                    Mesh buildings = TownPlanner.BuildTile(
                        key, terrainShape, town.Shape, town.Plan, $"{name}_{town.Name}",
                        out TownStats townStats);

                    if (buildings != null)
                    {
                        buildings = HorizonAssetUtility.ReplaceAsset(
                            buildings, $"{GeneratedFolder}/{name}_{town.Name}.asset");

                        // Houses keep OccluderStatic, unlike the trees. A town street is the one place in
                        // this world where occlusion culling has something solid to work with.
                        //
                        // No MeshCollider on the merged mesh: it would be a large concave collider full of
                        // window ledges and fence rails for the car to snag on, the same reason the tunnel
                        // skin was taken out of collision. Each plot gets a box below instead.
                        GameObject townObject = CreateMeshObject(
                            tileObject.transform, $"{name}_{town.Name}", buildings,
                            TownMaterials(materials, townStats),
                            addCollider: false, markStatic: true,
                            staticFlags: StaticEditorFlags.BatchingStatic
                                         | StaticEditorFlags.OccluderStatic
                                         | StaticEditorFlags.OccludeeStatic);

                        // Looked up rather than assumed, because empty submeshes are dropped when the tile
                        // mesh is built: the lit glass is not in slot 7 on a tile that has no ochre walls.
                        int litSlot = townStats.Submeshes.IndexOf(BuildingMeshes.WindowLitSubmesh);
                        int lampSlot = townStats.Submeshes.IndexOf(BuildingMeshes.LampLitSubmesh);

                        if (litSlot >= 0 || lampSlot >= 0)
                        {
                            townRenderers.Add(townObject.GetComponent<MeshRenderer>());

                            if (litSlot >= 0)
                            {
                                townSlots.Add(litSlot);
                                townSlotGroups.Add((int)LitGroup.Windows);
                            }

                            if (lampSlot >= 0)
                            {
                                townSlots.Add(lampSlot);
                                townSlotGroups.Add((int)LitGroup.Lamps);
                            }

                            townSlotStart.Add(townSlots.Count);
                        }

                        AddPlotColliders(townObject.transform, key, terrainShape, town.Plan);
                        townTotals[s].Add(townStats);
                    }
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

            for (int i = 0; i < towns.Count; i++)
            {
                ReportTown(townTotals[i], towns[i].Shape, towns[i].Plan, towns[i].Name);
            }
        }

        /// <summary>
        /// Bakes the traffic routes and builds the pool of cars that drive them.
        ///
        /// <para>Everything is made here rather than at runtime: the routes are an asset, the cars are
        /// instantiated once, and the director never constructs anything. That is the whole reason
        /// ambient traffic fits the mobile budget at all — see <c>TrafficDirector</c>.</para>
        ///
        /// <para>Returns the agents' renderers and which material slot carries their lamps, so the same
        /// <c>TownLights</c> that lights the town's windows lights their headlights too. A car with
        /// <c>Light</c> components would be two more realtime lights each against a four-per-object
        /// budget, twenty-eight for the pool; the swap costs nothing and is already written.</para>
        /// </summary>
        private static void BuildTraffic(
            Transform parent,
            IReadOnlyList<TownBuild> towns,
            RoadPath trunk,
            in RoadShape trunkShape,
            PrototypeMaterials materials,
            List<MeshRenderer> litRenderers,
            List<int> litSlotStart,
            List<int> litSlots,
            List<int> litSlotGroups,
            IRoadPath highway,
            RoadShape highwayShape,
            float carriagewayOffset)
        {
            var networks = new StreetNetwork[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                networks[i] = towns[i].Network;
            }

            if (networks.Length == 0 || networks[0].Edges.Count == 0)
            {
                return;
            }

            // Generated, not Settings. It is a ScriptableObject like VehicleConfig, but it is derived
            // output rather than something anyone tunes — regenerate it and every edit is gone — so it
            // belongs where the meshes are and under the orphan report that watches them.
            TrafficNetwork routes = TrafficNetworkBuilder.Build(
                networks, trunk, trunkShape, highway, highwayShape, carriagewayOffset);
            routes = HorizonAssetUtility.ReplaceAsset(routes, GeneratedFolder + "/TrafficNetwork.asset");

            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.TrafficProfiles;

            var bodies = new Mesh[profiles.Length];
            var bodyTriangles = new int[profiles.Length];

            for (int i = 0; i < profiles.Length; i++)
            {
                Mesh shape = CarMeshBuilder.BuildTrafficBody(profiles[i]);
                bodyTriangles[i] = shape.triangles.Length / 3;
                bodies[i] = HorizonAssetUtility.ReplaceAsset(
                    shape, $"{GeneratedFolder}/TrafficCarMesh_{profiles[i].Name}.asset");
            }

            var root = new GameObject("Traffic");
            root.transform.SetParent(parent, false);

            var cars = new Transform[TrafficPoolSize];
            var renderers = new MeshRenderer[TrafficPoolSize];

            for (int i = 0; i < TrafficPoolSize; i++)
            {
                // Shape and colour are indexed separately and the two counts share no factor, so a pool
                // of twenty-four holds no two identical cars. See PrototypeMaterials.TrafficBodies.
                Mesh body = bodies[i % bodies.Length];

                var carObject = new GameObject($"{TrafficCarPrefix}{i}");
                carObject.transform.SetParent(root.transform, false);

                carObject.AddComponent<MeshFilter>().sharedMesh = body;

                MeshRenderer renderer = carObject.AddComponent<MeshRenderer>();
                // Chrome takes the tyre material rather than the rim's: the reduced body puts its wheels
                // in that submesh, which the detail pass would otherwise have filled with exhausts. Four
                // wheels for no extra draw call, because the slot was being submitted regardless.
                renderer.sharedMaterials = new[]
                {
                    materials.TrafficBodies[i % materials.TrafficBodies.Length],
                    materials.CarGlass,
                    materials.WindowDay,
                    materials.WindowDay,
                    materials.Tyre,
                };

                // Shadows off. Fourteen extra shadow casters is a second pass over the whole town for
                // silhouettes that are under a car's own body anyway at the angle the sun sits at here.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // Kinematic, because the director writes the transform directly. Without a Rigidbody at
                // all the collider would be static geometry that teleports, which PhysX handles by not
                // noticing — and VehicleController raycasts on groundMask ~0, so the player's wheels
                // would find these before its bumper did.
                Rigidbody agentBody = carObject.AddComponent<Rigidbody>();
                agentBody.isKinematic = true;
                agentBody.useGravity = false;
                agentBody.interpolation = RigidbodyInterpolation.Interpolate;

                // From the mesh it is wrapping, not from a literal. The box used to be one hand-measured
                // size for one hand-built car; with five shapes on the road a fixed box is a van you can
                // drive through the roof of and a hatchback with half a metre of invisible bumper.
                BoxCollider collider = carObject.AddComponent<BoxCollider>();
                collider.center = body.bounds.center;
                collider.size = body.bounds.size;

                cars[i] = carObject.transform;
                renderers[i] = renderer;

                PlaceOnRoute(routes, carObject.transform, i);

                // Slots 2 and 3 are the lamp submeshes, and the traffic body always emits both, so
                // these are fixed rather than looked up the way a town tile's are.
                litRenderers.Add(renderer);
                litSlots.Add(CarMeshBuilder.HeadlightSubmesh);
                litSlotGroups.Add((int)LitGroup.Headlights);
                litSlots.Add(CarMeshBuilder.TaillightSubmesh);
                litSlotGroups.Add((int)LitGroup.Taillights);
                litSlotStart.Add(litSlots.Count);
            }

            // A camera station behind the first car, looking the way it faces. Aimed at an agent rather
            // than at a coordinate for the same reason the square's station is: it follows whatever the
            // bake actually produced, so a shot of the traffic cannot quietly become a shot of an empty
            // street when the lane numbering changes.
            var view = new GameObject("TrafficView");
            view.transform.SetParent(parent, false);

            Vector3 behind = cars[0].position - cars[0].forward * 13f + Vector3.up * 2.2f;
            view.transform.SetPositionAndRotation(
                behind, Quaternion.LookRotation(cars[0].position + Vector3.up * 0.6f - behind, Vector3.up));

            var directorObject = new GameObject("TrafficDirector");
            directorObject.transform.SetParent(parent, false);

            TrafficDirector director = directorObject.AddComponent<TrafficDirector>();
            HorizonAssetUtility.Configure(director, serialized =>
            {
                serialized.FindProperty("network").objectReferenceValue = routes;
                HorizonAssetUtility.SetObjectArray(serialized, "cars", cars);
                HorizonAssetUtility.SetObjectArray(serialized, "renderers", renderers);

                // From the mesh builder, so reshaping the body cannot leave the traffic riding at a
                // height nothing else believes in.
                serialized.FindProperty("rideHeight").floatValue = CarMeshBuilder.TrafficRideHeight;
            });

            HorizonAssetUtility.AssertReferenceAssigned(director, "network");

            ReportTraffic(routes, profiles, bodies, bodyTriangles);
        }

        /// <summary>
        /// How many ambient cars there are. Fixed at build; the director never changes it.
        ///
        /// Sixty-four, up from twenty-four, because of the motorway: eight lanes over eight kilometres is
        /// four times the road the pass and the town have between them, and the whole point of it is
        /// traffic dense enough to thread through. Twenty-four cars spread over the new network is one
        /// car every kilometre and a half.
        ///
        /// <para>What makes that affordable is not this number but where the cars are. The director
        /// weights its lane choice by length and now searches a candidate lane for the point nearest the
        /// player, so nearly the whole pool sits inside the load radius rather than scattered over
        /// twenty kilometres of empty road. Sixty-four cars near you is dense; sixty-four cars spread
        /// evenly would be invisible.</para>
        ///
        /// <para>The draw-call report does not know that, and should not: it counts every material slot
        /// on a renderer no <c>WorldChunk</c> owns and calls the total an upper bound. At five slots a
        /// car the pool is 320 always-resident slots against the old 120 — a real increase, and the
        /// number to watch, but still under the 400 the report warns at.</para>
        ///
        /// <para>If it does start warning, the cheapest lever is not this constant: it is the glass
        /// submesh in <c>CarMeshBuilder.BuildTrafficBody</c>. A traffic car is seen in motion at a
        /// distance, where a separate glass material buys a highlight nobody resolves, and folding it
        /// into the body would take the pool to 256 without removing a single car. That is deliberately
        /// not done yet — it is a mesh change made to fix a number that has not gone wrong.</para>
        /// </summary>
        private const int TrafficPoolSize = 64;

        /// <summary>
        /// Stands one car on the routes, spread evenly over every lane that is a road rather than a turn.
        ///
        /// <para>The director does this again in <c>Awake</c>, so this matters for exactly one thing —
        /// and it is not a small one. <c>Awake</c> does not run at edit time, so without it the saved
        /// scene holds fourteen cars stacked on top of each other at the world origin, a kilometre off
        /// the road, and every preview render and every look at the scene shows them there. A build that
        /// leaves the scene in a state nobody would ship is a build with a bug in it, whatever happens
        /// on Play.</para>
        ///
        /// <para>Evenly rather than randomly, which is the one place the two differ usefully: a stride
        /// through the lane list puts a car on every part of the network, so a preview shows traffic
        /// where the camera happens to be pointing.</para>
        /// </summary>
        private static void PlaceOnRoute(TrafficNetwork routes, Transform car, int index)
        {
            var drivenLanes = new List<int>(routes.LaneCount);
            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.NodeOf(lane) < 0 && routes.LengthOf(lane) > 8f)
                {
                    drivenLanes.Add(lane);
                }
            }

            if (drivenLanes.Count == 0)
            {
                return;
            }

            // A stride that shares no factor with the count walks the whole list rather than revisiting
            // a handful of lanes.
            int chosen = drivenLanes[index * 7 % drivenLanes.Count];

            routes.GetLane(chosen, routes.LengthOf(chosen) * 0.5f,
                out Vector3 position, out Vector3 forward);

            car.SetPositionAndRotation(
                position + Vector3.up * CarMeshBuilder.TrafficRideHeight,
                Quaternion.LookRotation(forward, Vector3.up));
        }

        /// <summary>What the bake produced, and whether the routes are actually connected.</summary>
        private static void ReportTraffic(
            TrafficNetwork routes,
            CarMeshBuilder.CarProfile[] profiles,
            Mesh[] bodies,
            int[] bodyTriangles)
        {
            int streets = 0;
            int trunkLanes = 0;
            int highwayLanes = 0;
            int connectors = 0;
            int deadEnds = 0;
            float total = 0f;
            float trunkTotal = 0f;
            float highwayTotal = 0f;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                total += routes.LengthOf(lane);

                switch (routes.KindOf(lane))
                {
                    case TrafficLaneKind.Street:
                        streets++;
                        break;

                    case TrafficLaneKind.Trunk:
                        trunkLanes++;
                        trunkTotal += routes.LengthOf(lane);
                        break;

                    case TrafficLaneKind.Highway:
                        highwayLanes++;
                        highwayTotal += routes.LengthOf(lane);
                        break;

                    default:
                        connectors++;
                        break;
                }

                if (routes.ExitCount(lane) == 0)
                {
                    deadEnds++;
                }
            }

            // Per shape, not one average. Five silhouettes off one loft can differ by a factor of two in
            // cost without anything looking wrong, and a mean would hide the one that had run away.
            int poolTriangles = 0;
            for (int i = 0; i < TrafficPoolSize; i++)
            {
                poolTriangles += bodyTriangles[i % bodyTriangles.Length];
            }

            Debug.Log($"[Horizon] Traffic: {streets} street lanes, {trunkLanes} trunk lanes "
                      + $"({trunkTotal:0} m), {highwayLanes} motorway lanes ({highwayTotal:0} m) and "
                      + $"{connectors} turn connectors, {total:0} m of route, "
                      + $"{TrafficPoolSize} cars over {profiles.Length} body types "
                      + $"({poolTriangles} triangles in total).");

            for (int i = 0; i < profiles.Length; i++)
            {
                Bounds bounds = bodies[i].bounds;
                int howMany = TrafficPoolSize / profiles.Length
                              + (i < TrafficPoolSize % profiles.Length ? 1 : 0);

                Debug.Log($"[Horizon] Traffic body '{profiles[i].Name}': {bodyTriangles[i]} triangles, "
                          + $"{bounds.size.z:0.00} x {bounds.size.x:0.00} x {bounds.size.y:0.00} m, "
                          + $"roof {bounds.max.y + CarMeshBuilder.TrafficRideHeight:0.00} m above the "
                          + $"road — {howMany} of them in the pool.");
            }

            if (trunkLanes == 0)
            {
                Debug.LogWarning("[Horizon] Traffic: no lanes on the trunk road. The pass is where the "
                                 + "player spends nearly all of their time, and it is empty — check that "
                                 + "the trunk nodes in the layout table carry an AlongTrunk.");
            }

            if (deadEnds > 0)
            {
                Debug.LogWarning($"[Horizon] Traffic: {deadEnds} lane(s) lead nowhere. A car reaching one "
                                 + "stops on it and stays there, which reads as a broken-down car that "
                                 + "never gets towed. Every lane should at least be able to turn round.");
            }
        }

        /// <summary>
        /// Hangs one TownLights on the world root, holding every renderer that owns a lit submesh.
        ///
        /// One component for the whole town rather than one per tile: every window in the place lights
        /// at the same instant, so there is nothing to be gained by deciding it thirty times over.
        ///
        /// <para>The lamps' day material is <c>M_Lane</c> itself rather than a copy of its colour. A pool
        /// of light on the carriageway has to be invisible by day, and "the same material" is the only
        /// version of that which cannot drift when somebody retints the road.</para>
        /// </summary>
        private static void WireTownLights(
            Transform parent,
            List<MeshRenderer> renderers,
            List<int> slotStart,
            List<int> slots,
            List<int> slotGroups,
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

                SetIntArray(serialized, "slotStart", slotStart);
                SetIntArray(serialized, "slots", slots);
                SetIntArray(serialized, "slotGroup", slotGroups);

                // Indexed by LitGroup, so the order of these two arrays is the order of that enum.
                // Headlights and tail lamps take the dark window material by day, which is what an
                // unlit lens is: the same near-black glass every window in the town starts as.
                HorizonAssetUtility.SetObjectArray(serialized, "dayMaterials",
                    new[]
                    {
                        materials.WindowDay, materials.Lane,
                        materials.WindowDay, materials.WindowDay,
                    });
                HorizonAssetUtility.SetObjectArray(serialized, "nightMaterials",
                    new[]
                    {
                        materials.WindowNight, materials.LampNight,
                        materials.LampNight, materials.TailNight,
                    });
            });
        }

        private static void SetIntArray(SerializedObject serialized, string name, List<int> values)
        {
            SerializedProperty property = serialized.FindProperty(name);
            property.arraySize = values.Count;

            for (int i = 0; i < values.Count; i++)
            {
                property.GetArrayElementAtIndex(i).intValue = values[i];
            }
        }

        private static void ReportTown(TownStats stats, in TownShape shape, TownPlan plan, string what)
        {
            if (plan == null || stats.Houses + stats.Windmills == 0)
            {
                Debug.LogWarning($"[Horizon] {what}: nothing was built. Check TownShape's extent against "
                                 + "the course, and that the plots landed inside terrain tiles.");
                return;
            }

            Debug.Log($"[Horizon] {what}: {stats.Houses} houses, {stats.Towers} towers, "
                      + $"{stats.Blocks} blocks, {stats.Mosques} mosque, "
                      + $"{stats.TownHalls} town hall, {stats.Fountains} fountain, "
                      + $"{stats.Stalls} market stalls, {stats.Windmills} windmill, "
                      + $"{stats.Barns} barns, {stats.Sawmills} sawmills, {stats.Fences} fences, "
                      + $"{stats.Lamps} lamps with {stats.Pools} ground pools, {stats.Cars} parked cars "
                      + $"— {stats.Triangles} triangles "
                      + $"over {plan.Footprint.size.x:0} x {plan.Footprint.size.z:0} m.");

            if (stats.Triangles > shape.MaxTrianglesPerTile * 4)
            {
                Debug.LogWarning($"[Horizon] {what}: {stats.Triangles} triangles is heavier than expected. "
                                 + "Open out the spacing in TownPlanner's quarter table, or raise its "
                                 + "vacancy.");
            }

            ReportGlassSplit(stats);
            ReportWindingFlips("Town", stats.Flips);
        }

        /// <summary>
        /// What fraction of the town's glass will light after dark, as a number.
        ///
        /// <para>This is the check the night render cannot do for you. If the per-quarter
        /// <c>litChance</c> never reached the panes — the plot's value dropped somewhere between the
        /// planner and <see cref="BuildingMeshes.GlassSubmesh"/>, or a default of 0.5 stood in for it —
        /// the town still lights up, still looks like a town at night, and is simply wrong in a way no
        /// screenshot distinguishes from right. The expected figure is a weighted average of the
        /// quarters, which lands well under a half.</para>
        /// </summary>
        private static void ReportGlassSplit(TownStats stats)
        {
            int total = stats.LitGlass + stats.DarkGlass;
            if (total == 0)
            {
                Debug.LogWarning("[Horizon] Town glass: no window panes at all, lit or dark. Either "
                                 + "nothing was built or the glass submeshes have been renumbered "
                                 + "without BuildingMeshes.GlassSubmesh being told.");
                return;
            }

            float lit = stats.LitGlass / (float)total;
            Debug.Log($"[Horizon] Town glass: {lit * 100f:0.0} % of {total} pane triangles light after "
                      + "dark. Expect roughly 25-45 %, weighted across the quarters.");

            if (lit > 0.46f || lit < 0.12f)
            {
                Debug.LogWarning(
                    $"[Horizon] Town glass is {lit * 100f:0.0} % lit, which is outside the range the "
                    + "quarter table asks for (0.15 industry to 0.60 market). Around 50 % in particular "
                    + "is the signature of litChance never arriving and every pane being rolled at a "
                    + "flat half — check TownPlan.Plot.LitChance reaches BuildingMeshes.AddHouse.");
            }
        }

        /// <summary>
        /// How many draw calls stand resident around each of a set of viewpoints, worst case first.
        ///
        /// <para>This exists because the twelve-submesh budget is the one decision in the town most
        /// likely to have to be undone, and an opinion about it is worth nothing. Every submesh a tile
        /// keeps is a draw call; empty-submesh compaction stops helping in a core where every variant
        /// appears on every tile; and at <c>loadRadius</c> 650 with 168 m tiles there are twenty-odd
        /// tiles resident, each carrying terrain, vegetation and buildings. The arithmetic is easy to get
        /// wrong in either direction, so it is counted instead.</para>
        ///
        /// <para><b>An upper bound, and deliberately so.</b> It counts every material slot of every
        /// resident renderer, with no frustum culling, no occlusion culling and no SRP Batcher merging —
        /// none of which can be evaluated at edit time. The real number comes from the Frame Debugger on a
        /// real mid-range Android; this is the tripwire that says whether it is worth going and looking.
        /// Buildings are marked <c>OccluderStatic</c>, so the delivered figure in a dense core should be
        /// materially below this one, and at the open edges of the town it will not be.</para>
        ///
        /// <para>The levers, in order, if this says twelve is too many: drop
        /// <c>WorldStreamer.loadRadius</c> to about 450 and lean on the fog, which is already tuned to
        /// hide the 600 m far plane; merge the wall palette to two; then vertex colours and one custom
        /// shader, which collapses a tile's buildings to about three calls. See
        /// <see cref="BuildingMeshes.SubmeshCount"/>.</para>
        /// </summary>
        /// <summary>
        /// Where to stand to count draw calls: the five preview stations up the climb, plus three
        /// through the town.
        ///
        /// The same five as <see cref="WorldPreviewRenderer"/> deliberately — the shots and the numbers
        /// should be about the same places — three more in the town, because the town is where the
        /// answer is in most doubt, and three on the motorway.
        ///
        /// <para>The motorway ones were added with the road itself and are not optional. The worst
        /// station in this world is in the town, so the report was answering "how bad does it get"
        /// correctly and saying nothing at all about a road carrying most of the traffic pool — which
        /// is a different question with a different answer, since out there the resident count is the
        /// cars and almost nothing else.</para>
        /// </summary>
        private static List<Vector3> DrawCallStations(RoadPath path, RoadPath motorway)
        {
            var stations = new List<Vector3>(11);

            float[] fractions = { 0.06f, 0.30f, 0.55f, 0.78f, 0.95f };
            for (int i = 0; i < fractions.Length; i++)
            {
                stations.Add(path.GetPositionAtDistance(path.Length * fractions[i]));
            }

            float start = MountainPassCourse.TownStartDistance;
            float end = MountainPassCourse.TownEndDistance;
            stations.Add(path.GetPositionAtDistance(Mathf.Min(start, path.Length)));
            stations.Add(path.GetPositionAtDistance(Mathf.Min((start + end) * 0.5f, path.Length)));
            stations.Add(path.GetPositionAtDistance(Mathf.Min(end, path.Length)));

            if (motorway != null)
            {
                // The interchange, and open road well clear of it in both directions.
                stations.Add(motorway.GetPositionAtDistance(
                    Mathf.Min(AutobahnCourse.JunctionDistance, motorway.Length)));
                stations.Add(motorway.GetPositionAtDistance(motorway.Length * 0.15f));
                stations.Add(motorway.GetPositionAtDistance(motorway.Length * 0.85f));
            }

            return stations;
        }

        private static void ReportDrawCallBudget(
            Transform worldRoot, IReadOnlyList<Vector3> stations, float loadRadius)
        {
            WorldChunk[] chunks = worldRoot.GetComponentsInChildren<WorldChunk>(true);
            if (chunks.Length == 0 || stations == null || stations.Count == 0)
            {
                return;
            }

            // Counted once per chunk rather than once per chunk per station: the renderer walk is the
            // expensive half, and a chunk's material count does not depend on where you stand.
            var callsPerChunk = new int[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
            {
                Renderer[] renderers = chunks[i].GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    callsPerChunk[i] += renderers[r].sharedMaterials.Length;
                }
            }

            // And everything the streamer never sees. Ambient traffic is deliberately not chunked — it
            // migrates between tiles every few seconds — so it is resident wherever you stand, and a
            // budget that walked chunks alone would silently leave seventy material slots out of the
            // number it exists to report.
            int unchunked = CountUnchunkedMaterials(worldRoot);

            int worst = 0;
            int worstStation = 0;
            int worstChunks = 0;

            for (int s = 0; s < stations.Count; s++)
            {
                int calls = unchunked;
                int resident = 0;

                for (int i = 0; i < chunks.Length; i++)
                {
                    if (chunks[i].DistanceTo(stations[s]) >= loadRadius)
                    {
                        continue;
                    }

                    calls += callsPerChunk[i];
                    resident++;
                }

                if (calls > worst)
                {
                    worst = calls;
                    worstStation = s;
                    worstChunks = resident;
                }
            }

            Debug.Log($"[Horizon] Draw calls at loadRadius {loadRadius:0} m: worst of "
                      + $"{stations.Count} stations is {worst} over {worstChunks} chunks plus "
                      + $"{unchunked} always resident, at station {worstStation + 1} "
                      + $"({stations[worstStation].x:0}, {stations[worstStation].z:0}). "
                      + "Upper bound — no culling, no batcher merging. Confirm on device.");

            if (worst > 400)
            {
                Debug.LogWarning(
                    $"[Horizon] {worst} resident material slots is past what a mid-range Android will "
                    + "hold at 60 fps even after culling. Pull WorldStreamer.loadRadius in first — it is "
                    + "one field and the fog already hides the result — before touching the submesh "
                    + "budget. See BuildingMeshes.SubmeshCount for the order of the levers.");
            }
        }

        /// <summary>
        /// Material slots on renderers that no <see cref="WorldChunk"/> owns, and which are therefore
        /// drawn wherever the player stands.
        /// </summary>
        private static int CountUnchunkedMaterials(Transform worldRoot)
        {
            Renderer[] renderers = worldRoot.GetComponentsInChildren<Renderer>(true);
            int calls = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].GetComponentInParent<WorldChunk>(true) == null)
                {
                    calls += renderers[i].sharedMaterials.Length;
                }
            }

            return calls;
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
        private static void ReportWindingFlips(string what, int flips, int[] bySubmesh = null)
        {
            if (flips <= 0)
            {
                return;
            }

            // Named by strip where the caller can say. A junction emits its carriageway, its kerb faces,
            // its footways and its grass through one method, and which of the four is backwards is the
            // whole of the question.
            string where = string.Empty;
            if (bySubmesh != null)
            {
                string[] names = { "carriageway", "kerb faces", "footways", "grass" };

                for (int i = 0; i < bySubmesh.Length && i < names.Length; i++)
                {
                    if (bySubmesh[i] > 0)
                    {
                        where += $" {bySubmesh[i]} in the {names[i]};";
                    }
                }
            }

            Debug.LogWarning($"[Horizon] {what}: {flips} faces were wound backwards and were corrected at "
                             + $"build time.{where} The helper that emitted them disagrees with its own "
                             + "outward direction — the geometry is right, the code that wrote it is not.");
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

                if (submesh == BuildingMeshes.WindowLitSubmesh)
                {
                    // Dark by day, and deliberately the same dark as the glass that never lights — a
                    // window you can tell will light later is a window with a bulb painted on it.
                    // TownLights swaps this one after sunset.
                    result[i] = materials.WindowDay;
                }
                else if (submesh == BuildingMeshes.LampLitSubmesh)
                {
                    // The street's own material, so the pool of light on the carriageway is not merely
                    // close to the road colour by day but is the road colour, to the last digit. The
                    // lantern head goes dark grey with it, which is what an unlit lantern is.
                    result[i] = materials.Lane;
                }
                else
                {
                    // Everything else — three walls, three roofs, the dark glass, the trim, the gardens
                    // and the accent — arrives here as one submesh carrying its colours in its vertices.
                    // See BuildingMeshes.OpaqueTints.
                    result[i] = materials.BuildingTint;
                }
            }

            return result;
        }

        /// <summary>
        /// One box of a building's collision: a half-extent pair, a height, and where it sits in the
        /// building's own frame.
        /// </summary>
        private readonly struct BuildingBox
        {
            public readonly float HalfWidth;
            public readonly float HalfDepth;
            public readonly float Height;

            /// <summary>Offset from the plot's origin, in the building's frame. +Z faces the street.</summary>
            public readonly float OffsetX;

            public readonly float OffsetZ;

            public BuildingBox(float halfWidth, float halfDepth, float height,
                float offsetX = 0f, float offsetZ = 0f)
            {
                HalfWidth = halfWidth;
                HalfDepth = halfDepth;
                Height = height;
                OffsetX = offsetX;
                OffsetZ = offsetZ;
            }
        }

        /// <summary>
        /// What a kind of building collides as: one box, or two where one would be a lie.
        ///
        /// <para>A table rather than the ladder of ternaries this replaces. The ladder was three
        /// expressions each carrying every kind's number, so adding the town hall meant editing three
        /// lines in three places and there was no way to read one building's collision without reading
        /// all of them.</para>
        ///
        /// <para><b>The mosque gets two.</b> A single box around the hall and the minaret together would
        /// enclose the courtyard between them and wall off ground the player can see straight through —
        /// which is why the previous version simply left the minaret out, leaving thirty-three metres of
        /// tower you could drive through. Two boxes says the true thing.</para>
        ///
        /// <para>Stalls and fountains get one small box each. They are the only things in the town at
        /// bumper height in the middle of an open space, so they are also the only things a driver will
        /// actually try to hit.</para>
        ///
        /// <para>The plan for this stage also called for terraces to collide per run rather than per unit,
        /// one box spanning the row. There are no terraces yet — the parcelling stage that produces them
        /// has not run — so there is nothing here to merge, and a <c>Terrace</c> entry with no producer
        /// would be a line of code that has never once executed.</para>
        /// </summary>
        private static BuildingBox[] ColliderFor(TownPlotKind kind, in TownPlan.Plot plot)
        {
            switch (kind)
            {
                case TownPlotKind.Mosque:
                    return new[]
                    {
                        new BuildingBox(9.5f, 9.5f, 15f),
                        new BuildingBox(2.2f, 2.2f, 31f, 7.4f, -7.4f),
                    };

                case TownPlotKind.TownHall:
                    return new[] { new BuildingBox(12f, 8f, 15.5f) };

                case TownPlotKind.Windmill:
                    return new[] { new BuildingBox(4.5f, 4.5f, 16f) };

                case TownPlotKind.Barn:
                    return new[] { new BuildingBox(7.5f, 5.5f, 8f) };

                case TownPlotKind.Sawmill:
                    return new[] { new BuildingBox(5.6f, 4.8f, 6f) };

                case TownPlotKind.Fountain:
                    return new[] { new BuildingBox(3.2f, 3.2f, 1f) };

                case TownPlotKind.Stall:
                    return new[] { new BuildingBox(2.1f, 1.4f, 2.6f) };

                default:
                    return new[] { new BuildingBox(5.6f, 4.8f, 6f) };
            }
        }

        /// <summary>
        /// The box colliders for every plot standing on one terrain tile.
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

                BuildingBox[] boxes = ColliderFor(plot.Kind, plot);

                var holder = new GameObject($"Plot_{i}");
                holder.transform.SetParent(parent, false);
                holder.transform.position = plot.Position;
                holder.transform.rotation = Quaternion.Euler(0f, plot.Yaw, 0f);

                // Several colliders on one object rather than a child each: a BoxCollider carries its own
                // centre, so a second box needs no second transform, and the tile ends up with one
                // GameObject per building however many boxes that building takes.
                for (int b = 0; b < boxes.Length; b++)
                {
                    BuildingBox box = boxes[b];
                    BoxCollider collider = holder.AddComponent<BoxCollider>();
                    collider.center = new Vector3(box.OffsetX, box.Height * 0.5f, box.OffsetZ);
                    collider.size = new Vector3(box.HalfWidth * 2f, box.Height, box.HalfDepth * 2f);
                }

                GameObjectUtility.SetStaticEditorFlags(holder, StaticEditorFlags.BatchingStatic);
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
            var foldedNodes = new List<int>(4);
            for (int n = 0; n < network.Nodes.Count; n++)
            {
                if (!IsStarShaped(network.Nodes[n]))
                {
                    folded++;

                    // Named, not just counted. "Six pads are folded" sends you reading every node in the
                    // table; six node indices send you to six lines of it.
                    if (foldedNodes.Count < 12)
                    {
                        foldedNodes.Add(n);
                    }
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
            float worstGradient = VergeGradient(
                network, out int steepStreets, out int groundless, out int groundlessStreets);

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
                                 + $"about their node — nodes {string.Join(", ", foldedNodes)} — so the "
                                 + "fan triangulation has folded through itself. The trims at those nodes "
                                 + "are too short for the angle between the streets.");
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

            if (steepStreets > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: the ground falls away from {steepStreets} "
                                 + $"street(s) too steeply to drive back up, worst {worstGradient:0.00}. "
                                 + "A street is not a plateau: widen the verge, or find out why the "
                                 + "terrain beside it is not where the shelf should have put it.");
            }

            if (groundless > 0)
            {
                Debug.LogWarning($"[Horizon] Street network: {groundless} verge sample(s) on "
                                 + $"{groundlessStreets} street(s) have no ground under them at all. That "
                                 + "paving is standing off the edge of the levelled basin with daylight "
                                 + "under its kerb — TownShape.CoverLayout should have sized the basin to "
                                 + "cover it, so either the layout reaches past the town's along-extent "
                                 + "or the terrain corridor is not being built that far out.");
            }

            if (crossings + shallow + folded + unreachable + steps + holes + steepStreets + groundless == 0)
            {
                Debug.Log($"[Horizon] Street network: {network.Nodes.Count} nodes and "
                          + $"{network.Edges.Count} streets — planar, connected, every pad convex about "
                          + "its node, flush with its streets and standing on levelled ground. Tightest "
                          + $"junction {tightestAngle:0} ° at node {tightestNode}, steepest verge "
                          + $"{worstGradient:0.00}.");
            }

            // The corridor sweep, once per street. Half-widths are per-street rather than the trunk
            // road's 1.3 m: that box is over a fifth of a 6.2 m alley, and a check that fires on
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
        /// Drops an empty at the market square and another at the mosque, and checks that the chunk
        /// carrying the minaret is big enough to know it is there.
        ///
        /// <para>The markers are for the preview renderer, for the same reason <c>TownWorstJunction</c> is:
        /// a camera aimed at a coordinate copied out of the layout table is a camera aimed at where the
        /// square used to be. Finding the object by name means the shot follows the thing.</para>
        ///
        /// <para>The chunk check is worth its five lines. <see cref="WorldChunk.RecalculateBounds"/> walks
        /// renderers, and the minaret is thirty-three metres of a tile whose terrain is flat — so the
        /// radius depends entirely on the town mesh's bounds being included, and if it were not, the
        /// tallest thing in the world would pop out of existence at close range while its own hillside
        /// stayed drawn.</para>
        /// </summary>
        private static void MarkTownLandmarks(Transform parent, StreetNetwork network, TownPlan plan)
        {
            for (int i = 0; i < network.Squares.Count; i++)
            {
                TownSquare square = network.Squares[i];

                var marker = new GameObject(i == 0 ? "TownSquare" : $"TownSquare_{i}");
                marker.transform.SetParent(parent, false);
                marker.transform.position = square.Centre;

                if (i == 0)
                {
                    MarkSquareView(parent, network, square, plan);
                }
            }

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                if (plan.Plots[i].Kind != TownPlotKind.Mosque)
                {
                    continue;
                }

                Vector3 at = plan.Plots[i].Position;

                var marker = new GameObject("TownLandmark");
                marker.transform.SetParent(parent, false);
                marker.transform.position = at;

                CheckChunkCovers(parent, at + Vector3.up * LandmarkMeshes.MinaretHeight, "the minaret");
                break;
            }
        }

        /// <summary>
        /// An empty standing in the square at eye height, aimed at the town hall.
        ///
        /// <para>A camera station rather than a landmark, and it is here rather than in the preview
        /// renderer because this is the only code that knows which edge came out uphill and where the
        /// hall ended up. The first version guessed a fixed offset from the centroid and put the camera
        /// outside the square behind somebody's garden fence — a shot of a market place taken from the
        /// wrong side of the street, which looked plausible enough to nearly keep.</para>
        ///
        /// <para>Set back from the middle towards the low side, so the hall is across the open space
        /// rather than overhead.</para>
        /// </summary>
        private static void MarkSquareView(
            Transform parent, StreetNetwork network, TownSquare square, TownPlan plan)
        {
            StreetEdge uphill = network.Edges[square.Edges[square.UphillEdge]];
            Vector3 towards = uphill.Path.GetPositionAtDistance(uphill.Length * 0.5f) - square.Centre;
            towards.y = 0f;

            float reach = towards.magnitude;
            if (reach < 1f)
            {
                return;
            }

            Vector3 station = square.Centre - towards.normalized * (reach * 0.55f);
            station.y = square.PavedHeightAt(station.x, station.z) + 1.7f;

            // The hall if there is one, otherwise the middle of the uphill edge — which is where it would
            // have been.
            Vector3 look = square.Centre + towards;
            for (int i = 0; i < plan.Plots.Count; i++)
            {
                if (plan.Plots[i].Kind == TownPlotKind.TownHall)
                {
                    look = plan.Plots[i].Position + Vector3.up * 7f;
                    break;
                }
            }

            var marker = new GameObject("TownSquareView");
            marker.transform.SetParent(parent, false);
            marker.transform.SetPositionAndRotation(
                station, Quaternion.LookRotation(look - station, Vector3.up));
        }

        /// <summary>Whether the chunk nearest a point actually contains it, bounds and all.</summary>
        private static void CheckChunkCovers(Transform root, Vector3 point, string what)
        {
            WorldChunk[] chunks = root.GetComponentsInChildren<WorldChunk>(true);

            WorldChunk nearest = null;
            float best = float.MaxValue;

            for (int i = 0; i < chunks.Length; i++)
            {
                // Skip the road and the streets, whose radius is set to 100 km so they never unload:
                // they contain everything and would answer the question for free.
                if (chunks[i].Radius > 10000f)
                {
                    continue;
                }

                float distance = Vector3.Distance(chunks[i].Center, point);
                if (distance < best)
                {
                    best = distance;
                    nearest = chunks[i];
                }
            }

            if (nearest == null)
            {
                return;
            }

            if (best <= nearest.Radius)
            {
                Debug.Log($"[Horizon] Streaming: {what} sits {best:0} m from its chunk's centre, inside "
                          + $"a {nearest.Radius:0} m radius.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Streaming: {what} stands {best:0} m from the centre of {nearest.name}, whose "
                + $"radius is only {nearest.Radius:0} m. WorldChunk.RecalculateBounds walks renderers, so "
                + "either it ran before this geometry was parented or the geometry is not under it — "
                + "and the result is the tallest thing in the world popping out at close range.");
        }

        /// <summary>
        /// Whether one thing can actually be seen from another, in metres of hillside in the way.
        ///
        /// <para>The whole claim this milestone rests on is that the town reads from the road above, and
        /// that claim is testable: walk the sight line, sample the height field every ten metres, and
        /// report the worst amount by which the ground stands above the line. Placing a landmark by eye
        /// and checking it in a render means checking it from wherever the render happened to stand.</para>
        ///
        /// <para>Two points and a label rather than a landmark and a course, because there is more than
        /// one landmark now and there is no reason this should know what any of them are. It answers a
        /// question about geometry; deciding which geometry to ask about belongs to the caller.</para>
        /// </summary>
        private static void ValidateLandmarkVisibility(
            MountainField field, Vector3 from, Vector3 to, string what)
        {
            float span = Vector3.Distance(from, to);
            if (span < 1f)
            {
                return;
            }

            float worst = 0f;
            float worstAt = 0f;

            // From 5 % to 95 %: at the far end the line is inside the landmark's own tile, where the
            // shelf under the town is legitimately above the line to a point 33 m up in the air, and at
            // the near end it is inside the road shelf the camera is standing on.
            for (float t = 0.05f; t <= 0.95f; t += 10f / span)
            {
                Vector3 on = Vector3.Lerp(from, to, t);
                float ground = field.HeightAt(on.x, on.z);

                if (ground - on.y > worst)
                {
                    worst = ground - on.y;
                    worstAt = t * span;
                }
            }

            if (worst <= 0f)
            {
                Debug.Log($"[Horizon] Landmark: {what} is clear, {span:0} m away.");
                return;
            }

            Debug.Log($"[Horizon] Landmark: {what} at {span:0} m — the ground stands {worst:0.0} m into "
                      + $"the sight line, worst at {worstAt:0} m along it.");

            if (worst > 6f)
            {
                Debug.LogWarning(
                    $"[Horizon] {what}: {worst:0.0} m of hillside in the way is enough to hide it. The "
                    + "landmark is placed on the highest ground the basin has, so the fix is the "
                    + "sight line rather than the building — either the viewpoint is looking over a "
                    + "shoulder of the pass, or the town has drifted behind one.");
            }
        }

        /// <summary>
        /// Runs the sight line to the town's tallest thing from every viewpoint on the course and from
        /// three stations on the climb.
        ///
        /// <para>The viewpoints are where the player is invited to stop and look, so those are the lines
        /// that have to be clear. The three climb stations are the answer to the obvious objection — that
        /// a viewpoint is chosen and could have been chosen to flatter the answer — and they are spread
        /// over the part of the course that faces back down the valley.</para>
        /// </summary>
        private static void ValidateLandmarks(
            MountainField field, RoadCourse course, RoadPath path, TownPlan plan)
        {
            Vector3 finial = Vector3.zero;
            bool found = false;

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                if (plan.Plots[i].Kind == TownPlotKind.Mosque)
                {
                    finial = plan.Plots[i].Position + Vector3.up * LandmarkMeshes.MinaretHeight;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Debug.LogWarning("[Horizon] Landmark: no mosque in the plan, so the claim that the town "
                                 + "reads from the pass cannot be tested at all.");
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

                ValidateLandmarkVisibility(field, from, finial, $"the minaret from '{feature.Name}'");
            }

            float[] stations = { 0.28f, 0.42f, 0.56f };
            for (int i = 0; i < stations.Length; i++)
            {
                float at = path.Length * stations[i];
                Vector3 from = path.GetPositionAtDistance(at) + Vector3.up * 1.5f;

                ValidateLandmarkVisibility(
                    field, from, finial, $"the minaret from the climb at {at:0} m");
            }
        }

        /// <summary>
        /// What the face walk found: how many blocks, how big, and which quarter each belongs to.
        ///
        /// The block count is the first number to look at when the layout table changes. A grid of three
        /// streets crossed by five should produce about eight blocks; anything far off that means a
        /// street the table thinks joins something it does not, and no picture would say so.
        /// </summary>
        private static void ReportBlocks(IReadOnlyList<TownBlock> blocks, string what)
        {
            if (blocks.Count == 0)
            {
                Debug.LogWarning($"[Horizon] {what} blocks: the face walk found none. Either the layout "
                                 + "table is a tree with no closed rings in it, or the bearings the walk "
                                 + "turns on are not sorted.");
                return;
            }

            var byQuarter = new int[System.Enum.GetValues(typeof(TownQuarter)).Length];
            float total = 0f;
            float largest = 0f;

            for (int i = 0; i < blocks.Count; i++)
            {
                byQuarter[(int)blocks[i].Quarter]++;
                total += blocks[i].Area;
                largest = Mathf.Max(largest, blocks[i].Area);
            }

            Debug.Log($"[Horizon] {what} blocks: {blocks.Count} enclosing {total / 10000f:0.0} ha, largest "
                      + $"{largest / 10000f:0.00} ha — {byQuarter[(int)TownQuarter.OldTown]} old town, "
                      + $"{byQuarter[(int)TownQuarter.Housing]} housing, "
                      + $"{byQuarter[(int)TownQuarter.Market]} market, "
                      + $"{byQuarter[(int)TownQuarter.Industry]} industry, "
                      + $"{byQuarter[(int)TownQuarter.Green]} green, "
                      + $"{byQuarter[(int)TownQuarter.Downtown]} downtown, "
                      + $"{byQuarter[(int)TownQuarter.Commercial]} commercial.");
        }

        /// <summary>
        /// How steeply the ground falls away from the edge of a street's paving, worst case.
        ///
        /// <para>A step down off a street is not the same problem as a step up onto one. Driving off is
        /// always possible; driving back on means a raycast wheel has to climb whatever is there, and a
        /// vertical half-metre is a wall. So what matters is not the height difference — the town sits in
        /// a basin and some of it is genuinely on a slope — but the <b>gradient</b>: half a metre over a
        /// verge is a ramp, and half a metre over nothing is a kerb you cannot mount.</para>
        ///
        /// <para>Nothing else was measuring this. The corridor sweep looks for solid things <i>in</i> the
        /// carriageway, and a street standing on a plinth has a perfectly clear one.</para>
        /// </summary>
        private static float VergeGradient(
            StreetNetwork network, out int steepStreets, out int groundless, out int groundlessStreets)
        {
            const float allowed = 0.6f;

            float worst = 0f;
            float worstDetail = 0f;
            string detail = null;
            string firstGroundless = null;
            steepStreets = 0;
            groundless = 0;
            groundlessStreets = 0;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                float run = edge.Shape.VergeWidth + 0.5f;
                float edgeWorst = 0f;
                int edgeGroundless = 0;

                for (float along = edge.TrimStart; along <= edge.Length - edge.TrimEnd; along += 12f)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        float sign = s == 0 ? -1f : 1f;

                        Vector3 paved = TownStreetBuilder.PointAcross(
                            edge.Path, edge.Shape, along, edge.HalfOuter * sign,
                            edge.Shape.SurfaceLift + edge.Shape.KerbHeight);

                        Vector3 beside = TownStreetBuilder.PointAcross(
                            edge.Path, edge.Shape, along, (edge.HalfOuter + run) * sign, 0f);

                        if (!Physics.Raycast(beside + Vector3.up * 8f, Vector3.down,
                                out RaycastHit hit, 16f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            // Nothing at all under the probe, which this used to skip in silence — and it
                            // is not the absence of a measurement, it is the worst answer there is. A
                            // street whose verge has no ground beneath it is standing off the edge of the
                            // levelled basin with daylight under its kerb, and no gradient computed from
                            // the samples that *did* hit will ever say so.
                            edgeGroundless++;
                            groundless++;
                            firstGroundless ??= $"street {i} ({edge.Kind}) at {along:0} m along, "
                                                + $"{run:0.0} m out on its "
                                                + (sign < 0f ? "left" : "right");
                            continue;
                        }

                        float gradient = (paved.y - hit.point.y) / run;
                        edgeWorst = Mathf.Max(edgeWorst, gradient);

                        if (gradient > worstDetail)
                        {
                            worstDetail = gradient;
                            detail = $"street {i} at {along:0} m: paving {paved.y:0.00}, ground "
                                     + $"{hit.point.y:0.00} at {run:0.0} m out, on "
                                     + $"'{hit.collider.gameObject.name}'";
                        }
                    }
                }

                if (edgeWorst > allowed)
                {
                    steepStreets++;
                }

                if (edgeGroundless > 0)
                {
                    groundlessStreets++;
                }

                worst = Mathf.Max(worst, edgeWorst);
            }

            if (detail != null && worst > allowed)
            {
                Debug.Log($"[Horizon] Steepest verge — {detail}.");
            }

            if (firstGroundless != null)
            {
                Debug.Log($"[Horizon] First verge sample with no ground under it — {firstGroundless}.");
            }

            return worst;
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

                // A trunk node's own centre stands on the trunk road's carriageway, which is a different
                // mesh and always there. Its street's trim point is not: that is where the bell-mouth has
                // to hand over to the ribbon, and it is exactly the seam a mouth built to the wrong reach
                // would leave open.
                if (!node.OnTrunkRoad && !HasGroundAt(node.Position))
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

                for (int i = 0; i < count; i++)
                {
                    if (hits[i] == null || IsTraffic(hits[i]))
                    {
                        continue;
                    }

                    firstAt = distance;
                    firstBy = hits[i].gameObject.name;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a collider in the carriageway is an ambient car rather than something built there.
        ///
        /// <para>The corridor sweep is looking for <i>scenery</i> in the road — a wall parcelled onto a
        /// street mouth, a boulder scattered into a hairpin. A car standing on a lane is not that; it is
        /// the thing the lane exists for. This is the same exemption the player's own car already gets,
        /// which it gets by being spawned after the sweep runs — an ordering the ambient pool cannot use,
        /// because its cars have to be standing on the routes before the scene is saved.</para>
        ///
        /// <para>Without it the sweep reported ten town streets as blocked, every one of them against a
        /// <c>TrafficCar_</c>, and a check that cries wolf ten times is a check nobody reads the eleventh
        /// time — which is the time it would have been a wall.</para>
        /// </summary>
        private static bool IsTraffic(Component collider)
        {
            return collider.gameObject.name.StartsWith(TrafficCarPrefix, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Name prefix of the ambient cars. Read by the corridor sweep, so the two cannot drift apart
        /// without the sweep quietly starting to report traffic as obstructions again.
        /// </summary>
        private const string TrafficCarPrefix = "TrafficCar_";

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
                    case PlantMeshes.RockSubmesh:
                        // The one that keeps its own material: dry matte stone against wet foliage.
                        result[i] = materials.Rock;
                        break;
                    default:
                        // Bark, conifer, broadleaf and undergrowth arrive as one submesh carrying their
                        // colours in its vertices — merged into the lowest of the four, which is bark's
                        // index. See PlantMeshes.FoliageTints.
                        result[i] = materials.FoliageTint;
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
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            MountainField field,
            PrototypeMaterials materials,
            string label = "")
        {
            var root = new GameObject("CoveredSections" + label);
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

                string name = $"{feature.Kind}_{feature.Name}{label}";
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

            Debug.Log($"[Horizon] Built {built} covered section(s) on {Where(label)}.");
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
        /// 1.3 m box that is right for a 10.5 m trunk carriageway is over a fifth of a 6.2 m alley, and a
        /// check that fires on every kerb is a check that gets ignored.</para>
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
            //
            // Centred on the path and 1.5 m tall either way rather than sunk half a metre: the original
            // box spanned -1.1 to +0.1 about the centreline, which straddled the pass's asphalt and its
            // shelf by a few centimetres and missed the motorway's entirely — a wider carriageway has a
            // higher crown and a deeper shoulder, and the box fell through the gap between them. A
            // canary that reports "no answer" because the road is shaped differently is worse than no
            // canary, because it is indistinguishable from the failure it exists to catch.
            Vector3 canaryAt = path.GetPositionAtDistance(length * 0.5f);
            int canary = Physics.OverlapBoxNonAlloc(
                canaryAt, new Vector3(halfWidth, 1.5f, 1f), hits,
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

                // Ambient cars are parked on their lanes at edit time and are of course standing in the
                // road — that is where cars go. The street check already exempts them; this one did not,
                // and only got away with it while there were few enough that none happened to be sitting
                // on the stretch being measured.
                Collider blocker = null;
                for (int i = 0; i < count && blocker == null; i++)
                {
                    if (hits[i] != null && !IsTraffic(hits[i]))
                    {
                        blocker = hits[i];
                    }
                }

                if (blocker == null)
                {
                    continue;
                }

                blocked++;
                if (firstBy == null)
                {
                    firstAt = distance;
                    firstBy = blocker.gameObject.name;
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
        /// <summary>
        /// Whether the terrain stays under the town's paving — measured against the terrain <b>mesh</b>,
        /// not against the height field.
        ///
        /// <para>That distinction is the entire reason this is a separate check from
        /// <see cref="ValidateRoadClearance"/>. The trunk road's asphalt stands 0.53 m above its shelf, so
        /// the field and the mesh can disagree by the metre or two they do on a slope and the road is
        /// still clear. A town street stands <b>8 cm</b> above its shelf — <c>TownStreetShape.SurfaceLift</c>
        /// is deliberately negative so the streets are not plateaux — and the mesh is a linear
        /// interpolation of the field across twelve-metre cells, which <c>TerrainTileBuilder.SampleSurface</c>
        /// records as being up to a fifth of a metre out. Eight centimetres of clearance against twenty of
        /// error is grass growing through the road, and the field-based check cannot see it because the
        /// field is not what is drawn.</para>
        ///
        /// <para>Sampled at the gutters and across the carriageway rather than down the centreline: the
        /// crown lifts the middle by 6 cm, so the centre is the last place the ground would break through
        /// and the gutter is the first.</para>
        /// </summary>
        /// <summary>
        /// Which junction pads emit a backwards kerb or footway face, and where on the pad.
        ///
        /// <para>The flip counter says how many and, since it was split by submesh, which strip. Neither
        /// says <i>where</i>, and two plausible-sounding explanations for these seven — grass laid across
        /// the street mouths, then slivers too short to have a normal — were both wrong, at a rebuild
        /// each. This replicates the two winding tests without emitting anything, so the answer is a node
        /// index and a bearing rather than another hypothesis.</para>
        /// </summary>
        private static void ReportPadWinding(StreetNetwork network)
        {
            var found = new List<string>(8);

            for (int n = 0; n < network.Nodes.Count; n++)
            {
                StreetNode node = network.Nodes[n];
                if (node.PadGutter == null || node.PadKerbedAfter == null)
                {
                    continue;
                }

                int count = node.PadGutter.Length;

                for (int i = 0; i < count; i++)
                {
                    int next = (i + 1) % count;
                    if (!node.PadKerbedAfter[i])
                    {
                        continue;
                    }

                    CheckFace(node, n, i, "kerb",
                        node.PadGutter[next], node.PadGutter[i], node.PadKerbTop[i], found);
                    CheckFace(node, n, i, "kerb",
                        node.PadGutter[next], node.PadKerbTop[i], node.PadKerbTop[next], found);
                    CheckFace(node, n, i, "footway",
                        node.PadKerbTop[next], node.PadKerbTop[i], node.PadOutline[i], found);
                    CheckFace(node, n, i, "footway",
                        node.PadKerbTop[next], node.PadOutline[i], node.PadOutline[next], found);
                }
            }

            if (found.Count > 0)
            {
                Debug.Log($"[Horizon] Pad winding: {string.Join(" | ", found)}");
            }
        }

        private static void CheckFace(
            StreetNode node, int index, int span, string strip, Vector3 a, Vector3 b, Vector3 c,
            List<string> into)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, Vector3.up) >= 0f)
            {
                return;
            }

            Vector3 radial = a - node.Position;
            float bearing = Mathf.Atan2(radial.x, radial.z) * Mathf.Rad2Deg;

            into.Add($"node {index} (degree {node.Degree}, {node.PadGutter.Length} pad points) span "
                     + $"{span} {strip}, bearing {bearing:0}°, |n| {normal.magnitude:0.0000}");
        }

        private static void ValidateStreetClearance(
            StreetNetwork network, MountainField field, in TerrainShape terrainShape)
        {
            const float step = 2f;

            int breaches = 0;
            int streets = 0;
            float worst = 0f;
            string worstWhere = null;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                float[] offsets =
                {
                    -edge.HalfWidth, -edge.HalfWidth * 0.5f, 0f,
                    edge.HalfWidth * 0.5f, edge.HalfWidth,
                };

                bool breached = false;

                for (float along = edge.TrimStart; along <= edge.Length - edge.TrimEnd; along += step)
                {
                    for (int k = 0; k < offsets.Length; k++)
                    {
                        // At the gutter's own height, ignoring the crown: the crown only ever helps, and
                        // a check that counted it would pass a street the ground breaks through at its
                        // edges.
                        Vector3 paved = TownStreetBuilder.PointAcross(
                            edge.Path, edge.Shape, along, offsets[k], edge.Shape.SurfaceLift);

                        TerrainTileBuilder.SampleSurface(field, terrainShape, paved.x, paved.z,
                            out Vector3 ground, out Vector3 _);

                        float intrusion = ground.y - paved.y;
                        if (intrusion <= 0.005f)
                        {
                            continue;
                        }

                        breaches++;
                        breached = true;

                        if (intrusion > worst)
                        {
                            worst = intrusion;
                            worstWhere = $"street {i} ({edge.Kind}) at {along:0} m along, "
                                         + $"{offsets[k]:0.0} m across";
                        }
                    }
                }

                if (breached)
                {
                    streets++;
                }
            }

            if (breaches == 0)
            {
                Debug.Log($"[Horizon] Street clearance: the terrain mesh is below the paving on all "
                          + $"{network.Edges.Count} streets.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Street clearance: the terrain mesh stands above the paving at {breaches} "
                + $"sampled point(s) on {streets} street(s), worst {worst:0.00} m — {worstWhere}. That is "
                + "grass growing up through the road. TownStreetShape.SurfaceLift decides how much room "
                + "there is and TerrainShape.CellSize decides how badly the mesh can miss the field it is "
                + "interpolating; the clearance has to exceed the error.");
        }

        /// <summary>
        /// Measures the air under every viaduct and says so.
        ///
        /// <para>The one thing about a bridge that no other check can see. <see cref="ValidateRoadClearance"/>
        /// asks whether the ground is below the road, and a span built straight onto an embankment passes
        /// it perfectly — the ground is below the road, by nothing at all. Piers a metre tall on a solid
        /// floor is the failure this catches, and it is the failure the first two viaducts had before
        /// <c>MountainField.BridgeHeadroom</c> existed.</para>
        /// </summary>
        private static void ValidateBridges(IRoadPath path, MountainField field, RoadCourse course)
        {
            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Bridge)
                {
                    continue;
                }

                float deepest = 0f;
                float shallowest = float.MaxValue;
                const float step = 8f;

                for (float at = feature.StartDistance; at <= feature.EndDistance; at += step)
                {
                    Vector3 deck = path.GetPositionAtDistance(at);
                    float drop = deck.y - field.HeightAt(deck.x, deck.z);

                    deepest = Mathf.Max(deepest, drop);
                    shallowest = Mathf.Min(shallowest, drop);
                }

                Debug.Log($"[Horizon] Bridge '{feature.Name}': {feature.Length:0} m span, "
                          + $"{shallowest:0.0} m to {deepest:0.0} m above the ground under it.");

                // Below this the piers are stubs and the structure reads as a wall along the road.
                if (deepest < 6f)
                {
                    Debug.LogWarning(
                        $"[Horizon] Bridge '{feature.Name}' is at most {deepest:0.0} m above the ground, "
                        + "so it is an embankment with a parapet rather than a viaduct. Either the span "
                        + "is somewhere the terrain was never going to fall away, or "
                        + "MountainField.BridgeHeadroom is not reaching it.");
                }
            }
        }

        private static void ValidateRoadClearance(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string what = "Road")
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
                Debug.Log($"[Horizon] {what} clearance: the terrain is below the carriageway everywhere.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] {what} clearance: terrain stands above the asphalt at {breaches} sampled points. "
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
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            PrototypeMaterials materials,
            string label = "")
        {
            Mesh mesh = GuardRailBuilder.Build(path, roadShape, field, course);
            if (mesh == null)
            {
                Debug.Log($"[Horizon] No guard rails needed on {Where(label)} — nothing is exposed enough.");
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, $"{GeneratedFolder}/GuardRail{label}Mesh.asset");

            // No collider: the rails are visual. Hitting one should not be a wall the car can lean on
            // until the vehicle has a proper collision response, and a concave mesh collider here would
            // catch the car in ways that feel arbitrary.
            CreateMeshObject(parent, "GuardRails" + label, mesh, new[] { materials.GuardRail },
                addCollider: false, markStatic: true);

            Debug.Log($"[Horizon] Guard rails on {Where(label)}: {triangles} triangles.");
        }

        /// <summary>
        /// The barrier down the middle of the motorway. Unlike the verge rails this is unconditional, so
        /// there is no "nothing was exposed enough" case to report.
        /// </summary>
        private static void BuildMedianBarrier(
            Transform parent,
            IRoadPath centre,
            in RoadShape roadShape,
            RoadCourse course,
            PrototypeMaterials materials)
        {
            Mesh mesh = GuardRailBuilder.BuildMedian(centre, roadShape, course);
            if (mesh == null)
            {
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/MedianBarrierMesh.asset");

            CreateMeshObject(parent, "MedianBarrier", mesh, new[] { materials.GuardRail },
                addCollider: false, markStatic: true);

            Debug.Log($"[Horizon] Median barrier: {triangles} triangles.");
        }

        /// <summary>
        /// Every viaduct on a course, as one mesh per carriageway.
        ///
        /// <para>The parapet gets a collider and the rest does not. A car that leaves the deck should
        /// hit something rather than fall through the world, but a concave collider wrapped round piers
        /// forty metres below is a large amount of geometry nothing can ever reach.</para>
        /// </summary>
        private static void BuildBridges(
            Transform parent,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            PrototypeMaterials materials,
            string label)
        {
            var used = new List<int>();
            Mesh mesh = BridgeBuilder.Build(path, roadShape, field, course, used, "Bridge" + label);

            if (mesh == null)
            {
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/Bridge{label}Mesh.asset");

            var slots = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                slots[i] = used[i] == BridgeBuilder.ParapetSubmesh
                    ? materials.GuardRail
                    : materials.Concrete;
            }

            CreateMeshObject(parent, "Bridges" + label, mesh, slots,
                addCollider: false, markStatic: true);

            Debug.Log($"[Horizon] Bridges on {Where(label)}: {triangles} triangles.");
        }

        private static string Where(string label)
        {
            return string.IsNullOrEmpty(label) ? "the pass" : label;
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
