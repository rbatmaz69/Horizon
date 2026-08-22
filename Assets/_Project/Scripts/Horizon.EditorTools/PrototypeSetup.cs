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
            CreateVehicleConfigs();
            CreateTimeOfDayProfile();

            // Start from a throwaway scene so the temporary objects used to author the prefab never
            // touch whatever the user had open.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Before the prefab and well before the Bootstrap scene, because the garage page puts these
            // sprites into buttons as it builds them. They need no prefab — only CarMeshBuilder — so
            // here is the earliest they can be made. The debug renders at the end of this method are a
            // different thing: those are for looking at, and they need the finished car.
            CarPreviewRenderer.RenderUiThumbnails();

            GameObject vehiclePrefab = BuildVehiclePrefab();
            if (vehiclePrefab == null)
            {
                return;
            }

            List<SpawnPoint> spawns = BuildWorldScene(vehiclePrefab);
            BuildBootstrapScene(spawns);
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

            /// <summary>The white shaft of a roadside delineator post.</summary>
            public readonly Material Delineator;

            /// <summary>
            /// The reflector panel on a delineator. Unlit, so it holds its brightness once the sun has
            /// gone and the fog has swallowed everything else — which is when the posts are carrying the
            /// whole sense of speed on their own.
            /// </summary>
            public readonly Material DelineatorReflector;

            public readonly Material Grass;

            /// <summary>
            /// The terrain, tinted per vertex. One slot per tile instead of two.
            ///
            /// <para>Grass and rock were two materials because rock-versus-grass is chosen per triangle
            /// by slope, and a category had to be a submesh. Neither was textured, so both are now a
            /// vertex colour on the shader the buildings already use — and every tile in the world stops
            /// paying for a rock face it may not have.</para>
            /// </summary>
            public readonly Material TerrainTint;
            public readonly Material Rock;
            public readonly Material Lane;
            public readonly Material Footway;
            public readonly Material WindowDay;
            public readonly Material WindowNight;
            public readonly Material LampNight;

            /// <summary>
            /// A filling station's sign face. Unlit and bright, and it never changes.
            ///
            /// <para>Every other lit thing in the world is a pair — dark by day, glowing by night — and
            /// <c>TownLights</c> swaps between them. A sign is the one thing on a forecourt that is
            /// meant to look the same at noon as at midnight, so it is registered with nothing and
            /// simply stays lit. Unlit so it holds its brightness once the sun is down and the fog has
            /// swallowed everything else, which is the same argument <c>DelineatorReflector</c> makes
            /// — and it is exactly when you most want to know there is fuel ahead.</para>
            /// </summary>
            public readonly Material SignFace;
            public readonly Material TailNight;

            /// <summary>An unlit traffic-light lens, and the three lit ones indexed by state.</summary>
            public readonly Material SignalDark;

            public readonly Material[] SignalLenses;

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

            /// <summary>Vertex-tinted, at the road's smoothness. For paving that carries no atlas.</summary>
            public readonly Material RoadTint;

            /// <summary>Vertex-tinted and glossy. Every lake, river and sea shares it.</summary>
            public readonly Material Water;

            /// <summary>
            /// The eight paints the player picks between, in <see cref="CarPaintPalette"/> order. Slot 0
            /// is <see cref="CarBody"/> — the same asset, not a copy, so the default car is unchanged.
            /// </summary>
            public readonly Material[] CarPaints;

            public readonly Material CarBody;
            public readonly Material Tyre;
            public readonly Material CarGlass;
            public readonly Material CarRim;
            public readonly Material LightFront;
            public readonly Material LightRear;
            public readonly Material Smoke;

            /// <summary>
            /// The grit hanging in the air that the car flies past at speed. Its colour is written from
            /// the fog every frame, so what is set here is only a starting point.
            /// </summary>
            public readonly Material AirRush;

            /// <summary>
            /// Tyre smoke. Lighter and far less transparent than the exhaust plume: burnt rubber is
            /// near-white and thick, and reusing the exhaust's grey made a drift look like a misfire.
            /// </summary>
            public readonly Material TyreSmoke;

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
                TerrainTint = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_TerrainTint.mat", "M_TerrainTint", 0.08f);

                // The same vertex-tinted shader at the road's smoothness rather than the terrain's.
                // Asphalt at 0.08 is asphalt that does not catch the low sun this game is mostly lit
                // by, and next to a carriageway at 0.34 it reads as a patch of something else.
                RoadTint = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_RoadTint.mat", "M_RoadTint", 0.34f);

                // Water, on the same vertex-tinted shader and glossier than anything else in the
                // world. 0.55 is what puts the low sun on it — this game is lit at dusk more often
                // than not, and still water that does not catch that light reads as a hole.
                Water = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_Water.mat", "M_Water", 0.55f);
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

                // Warm near-white, and under 1 on every channel unlike the lamps above: those are light
                // sources and are allowed to blow out, this is a painted panel catching the day. It has
                // to read against a bright sky as well as against a black one.
                SignFace = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_SignFace.mat", "M_SignFace",
                    new Color(0.98f, 0.94f, 0.86f));

                // A lit tail lamp, which is not the same thing as M_LightRear: that one is the *off*
                // state of the player's car, animated by a property block. An ambient car has no block —
                // it takes a whole material — and a pair of dark red rectangles is what a car looks like
                // with its lights off, which after dark is wrong.
                TailNight = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_TailNight.mat", "M_TailNight",
                    new Color(1.35f, 0.12f, 0.07f));

                // Traffic lights. Unlit like everything above, and for a further reason of their own: a
                // signal has to read at noon as well as at midnight, and a lit lens that took the sun
                // into account would be a dark hole on the one side of every junction the sun is behind.
                //
                // Over one in the brightest channel, because these are the only things in the world that
                // are meant to be a light source rather than a surface — under bloom that is what makes a
                // green look like it is on rather than like it is painted green.
                SignalDark = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_SignalDark.mat", "M_SignalDark",
                    new Color(0.09f, 0.09f, 0.10f));

                // Indexed by TrafficSignalState: red, amber, green.
                SignalLenses = new[]
                {
                    HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                        MaterialsFolder + "/M_SignalRed.mat", "M_SignalRed",
                        new Color(1.70f, 0.14f, 0.09f)),
                    HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                        MaterialsFolder + "/M_SignalAmber.mat", "M_SignalAmber",
                        new Color(1.75f, 0.90f, 0.10f)),
                    HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                        MaterialsFolder + "/M_SignalGreen.mat", "M_SignalGreen",
                        new Color(0.20f, 1.60f, 0.42f)),
                };

                // Seven body colours against ten body shapes, and the count is the whole reason it is
                // seven. The two share no factor, so shape and colour drift against each other and a
                // pairing only repeats after seventy cars — a pool of ninety-six is very nearly all
                // distinct. It was six against five for the same reason; ten and six share a factor of
                // two and would have quietly halved the variety back to thirty at the moment the garage
                // doubled, which is exactly the kind of regression nobody looks for.
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

                    // The seventh, added when the garage went from five shapes to ten. Warm and dull, so
                    // it sits between the sand and the maroon without giving the set a second light
                    // colour to compete with the bone.
                    HorizonAssetUtility.LoadOrCreateMaterial(
                        MaterialsFolder + "/M_TrafficRust.mat", "M_TrafficRust",
                        new Color(0.46f, 0.31f, 0.23f), 0.53f, 0.1f),
                };
                Concrete = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Concrete.mat", "M_Concrete", new Color(0.52f, 0.51f, 0.49f), 0.20f);

                GuardRail = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_GuardRail.mat", "M_GuardRail", new Color(0.66f, 0.68f, 0.70f), 0.55f, 0.6f);

                // Not pure white: a delineator is weathered plastic, and at the density these are placed
                // a true white reads as a row of lights down the verge in daylight.
                Delineator = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_Delineator.mat", "M_Delineator",
                    new Color(0.88f, 0.88f, 0.85f), 0.25f);

                DelineatorReflector = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_DelineatorReflector.mat", "M_DelineatorReflector",
                    new Color(1f, 0.93f, 0.72f));
                // The palette owns M_CarBody now, as its first entry, so the orange the car has always
                // worn is created exactly once and by one table. CarBody stays as a named handle to it
                // because a good deal of code — and the traffic material resolver — reads it that way.
                CarPaints = CarPaintPalette.LoadOrCreate();
                CarBody = CarPaints[0];
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

                TyreSmoke = HorizonAssetUtility.LoadOrCreateParticleMaterial(
                    MaterialsFolder + "/M_TyreSmoke.mat", "M_TyreSmoke", smokeTexture,
                    new Color(0.88f, 0.87f, 0.85f, 0.62f));

                AirRush = HorizonAssetUtility.LoadOrCreateParticleMaterial(
                    MaterialsFolder + "/M_AirRush.mat", "M_AirRush", smokeTexture,
                    new Color(0.9f, 0.9f, 0.92f, 0.45f));
            }
        }

        /// <summary>
        /// Makes sure a handling asset exists for every body, creating only the missing ones.
        ///
        /// <para>A freshly created config carries <c>Version = 0</c>, which counts as stale, so
        /// <see cref="LoadVehicleConfig"/> stamps it with its profile's preset the first time it is read.
        /// Every body added after the fastback therefore arrives correctly tuned without this function
        /// knowing anything about what a van weighs — see <see cref="VehicleConfigPresets"/>.</para>
        /// </summary>
        private static void CreateVehicleConfigs()
        {
            for (int i = 0; i < VehicleConfigPresets.All.Length; i++)
            {
                HorizonAssetUtility.LoadOrCreate(
                    VehicleConfigPresets.All[i].AssetPath,
                    ScriptableObject.CreateInstance<VehicleConfig>);
            }
        }

        /// <summary>
        /// Re-loads one body's vehicle config from disk.
        ///
        /// This exists because an asset reference does **not** survive
        /// <c>EditorSceneManager.NewScene(..., Single)</c>: after the scene switch the managed
        /// wrapper no longer resolves, and assigning it through a SerializedProperty silently writes
        /// null — no exception, no warning, just a broken prefab. So every function that switches
        /// scenes loads the assets it needs afterwards, by path, rather than receiving them as
        /// arguments from before the switch.
        /// </summary>
        private static VehicleConfig LoadVehicleConfig(string profile)
        {
            string path = VehicleConfigPresets.PathFor(profile);
            if (path == null)
            {
                Debug.LogError($"[Horizon] No vehicle config is registered for body '{profile}'. "
                               + "Add it to VehicleConfigPresets.All.");
                return null;
            }

            VehicleConfig config = AssetDatabase.LoadAssetAtPath<VehicleConfig>(path);

            // A rebuild must not silently construct the car from an asset whose numbers were chosen
            // under meanings the code has since changed. VehicleConfigReset owns that judgement — it is
            // a version stamp, not a guess at which values look wrong.
            VehicleConfigReset.ResetIfStale(config, profile, path);

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
        /// Builds the vehicle: ten generated low-poly bodies on one chassis, and four generated wheels
        /// on pivots.
        ///
        /// The physics side is untouched by the shape of the art — the raycast wheels work off the
        /// anchors and the config, so a body mesh can be replaced freely without retuning handling.
        ///
        /// <para><b>Ten bodies, one of everything else.</b> Only the shell, its lamps, its pipes, its
        /// collider box and its handling asset are per body; the Rigidbody, the wheels, the anchors, the
        /// audio graph and the cover probe are shared, because every silhouette is drawn around the same
        /// running gear. <see cref="VehicleBodySet"/> is what swaps between them at run time, and the
        /// note on that class says why this is one prefab rather than ten.</para>
        ///
        /// <para>The audio graph being shared is why the body set holds a reference to
        /// <c>EngineAudio</c>: the note is per config rather than per object, so it has to be rebuilt on
        /// a swap instead of simply being switched on with the shell.</para>
        /// </summary>
        private static GameObject BuildVehiclePrefab()
        {
            // Loaded here, after Rebuild's scene switch, not passed in from before it.
            var materials = new PrototypeMaterials();

            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.PlayerProfiles;

            var configs = new VehicleConfig[profiles.Length];
            for (int i = 0; i < profiles.Length; i++)
            {
                configs[i] = LoadVehicleConfig(profiles[i].Name);
                if (configs[i] == null)
                {
                    Debug.LogError($"[Horizon] Could not load the vehicle config for "
                                   + $"'{profiles[i].Name}'. Aborting prefab build.");
                    return null;
                }
            }

            // The fastback, and what the car is until the player says otherwise. Everything shared below
            // — the Rigidbody's mass, the anchor drop, the wheel in the pivots — is seeded from it, and
            // every one of those is rewritten by VehicleBodySet.Select the moment another body is
            // picked. None of them is a choice; they are the state the prefab happens to be saved in.
            VehicleConfig config = configs[0];

            var root = new GameObject("Vehicle_Prototype");

            var body = root.AddComponent<Rigidbody>();
            body.mass = config.Mass;

            // Collider spans the body shell. It only matters for hitting scenery — the wheels are
            // raycasts, so this box has no say in how the car drives.
            //
            // Derived from the station table rather than typed out beside it. The four numbers used to be
            // literals carrying a note to re-derive them whenever the silhouette moved, which is a note
            // that only works if somebody reads it — and with five silhouettes there is no single set of
            // literals that could be right anyway. VehicleBodySet rewrites this box on every swap.
            BoxCollider collider = root.AddComponent<BoxCollider>();
            Bounds hull = CarMeshBuilder.HullBounds(profiles[0]);
            collider.center = hull.center;
            collider.size = hull.size;

            // --- The ten bodies, all built, one left showing.
            var bodiesRoot = new GameObject("Bodies");
            bodiesRoot.transform.SetParent(root.transform, false);

            var bodyObjects = new GameObject[profiles.Length];
            var bodyBeams = new Light[profiles.Length][];
            var bodyBounds = new Bounds[profiles.Length];
            var bodyWheels = new Mesh[profiles.Length];

            for (int i = 0; i < profiles.Length; i++)
            {
                CarMeshBuilder.CarProfile profile = profiles[i];

                Mesh mesh = HorizonAssetUtility.ReplaceAsset(
                    CarMeshBuilder.BuildBody(profile, $"CarBodyMesh_{profile.Name}"),
                    $"{GeneratedFolder}/CarBodyMesh_{profile.Name}.asset");

                // Material order must match the Submesh constants in CarMeshBuilder. Slot 0 is the paint
                // and is the one VehicleBodySet rewrites; the other four are the same on every car.
                bodyObjects[i] = CreateMeshObject(
                    bodiesRoot.transform,
                    $"Body_{profile.Name}",
                    mesh,
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

                // Lamps and pipes parent to the body, not to the chassis, so deactivating a body takes
                // its beams and its smoke with it. A headlight left behind on the root would keep
                // shining out of whatever car was showing.
                bodyBeams[i] = CreateHeadlights(bodyObjects[i].transform, profile);
                CreateExhaustEmitters(bodyObjects[i].transform, materials, profile);

                bodyBounds[i] = CarMeshBuilder.HullBounds(profile);

                // One wheel per car, not one per prefab. Built with its axle on X so the controller can
                // write the pivot's rotation directly as spin plus steer, with no correcting child
                // transform, and wide enough to stand slightly proud of the arch — which is what makes
                // the stance read.
                bodyWheels[i] = HorizonAssetUtility.ReplaceAsset(
                    CarMeshBuilder.BuildWheel(
                        profile.WheelRadius, profile.TyreWidth, 18, $"WheelMesh_{profile.Name}",
                        profile.RimFraction, profile.Rim),
                    $"{GeneratedFolder}/WheelMesh_{profile.Name}.asset");

                bodyObjects[i].SetActive(i == 0);
            }

            GameObject bodyObject = bodyObjects[0];
            Light[] headlights = bodyBeams[0];

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

            // The continuous pipe note, on its own source because it is a different sound in a different
            // place from the engine: less spatialised than the engine and more than the one-shots, which
            // is roughly where a tailpipe sits relative to a chase camera.
            AudioSource exhaustToneSource = CreateAudioSource(root.transform, "Audio_ExhaustTone", 0.18f);

            AudioSource tyreSource = CreateAudioSource(root.transform, "Audio_Tyres", 0.1f);

            // The turbo sits with the engine and at the engine's spatial blend, because that is where it
            // is: a compressor is bolted to the exhaust manifold, and putting its whistle anywhere else
            // makes the car sound like it is being followed by a kettle.
            AudioSource turboSource = CreateAudioSource(root.transform, "Audio_Turbo", 0.25f);

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
                serialized.FindProperty("exhaustToneSource").objectReferenceValue = exhaustToneSource;
                serialized.FindProperty("tyreSource").objectReferenceValue = tyreSource;
                serialized.FindProperty("turboSource").objectReferenceValue = turboSource;
                serialized.FindProperty("engineReverb").objectReferenceValue = reverb;
                serialized.FindProperty("cover").objectReferenceValue = cover;
            });

            // A silent layer is invisible until someone drives the car and notices something missing,
            // which for the exhaust means noticing an absence of a noise that only happens on a hard
            // shift. Cheaper to assert it here.
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "engineSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "engineLoadSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "exhaustSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "exhaustToneSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "tyreSource");
            HorizonAssetUtility.AssertReferenceAssigned(engineAudio, "turboSource");

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
            var wheelFilters = new MeshFilter[4];

            // The default body's wheel, in all four pivots. VehicleBodySet.Select puts the right one in
            // whenever the shell changes — the pivots are on the chassis, so unlike the beams and the
            // tailpipes they do not travel with the body they belong to.
            Mesh wheelMesh = bodyWheels[0];

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

                MeshFilter filter = pivot.AddComponent<MeshFilter>();
                filter.sharedMesh = wheelMesh;
                wheelFilters[i] = filter;

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

            // The tank, and the two references pointed at each other. The controller asks the tank
            // whether there is fuel before it will accept a throttle command; the tank asks the
            // controller what work the engine is doing. Both are explicit rather than resolved in Awake,
            // which is the habit everything else on this prefab follows.
            FuelTank fuelTank = root.AddComponent<FuelTank>();
            HorizonAssetUtility.Configure(fuelTank, serialized =>
                serialized.FindProperty("vehicle").objectReferenceValue = controller);

            HorizonAssetUtility.Configure(controller, serialized =>
                serialized.FindProperty("fuel").objectReferenceValue = fuelTank);

            HorizonAssetUtility.AssertReferenceAssigned(fuelTank, "vehicle");
            HorizonAssetUtility.AssertReferenceAssigned(controller, "fuel");

            // On the chassis rather than on a body, unlike the tailpipes: the wheels belong to the car
            // and do not change when the garage swaps a shell over them. Built after the controller so
            // the reference can be explicit instead of resolved in Awake.
            CreateTyreSmoke(root.transform, materials);

            // Wired here rather than with the rest of VehicleLights, because the controller does not
            // exist yet at that point. VehicleLights falls back to a GetComponentInParent in Awake, but
            // an explicit reference is one less thing to be surprised by.
            HorizonAssetUtility.Configure(lights, serialized =>
                serialized.FindProperty("controller").objectReferenceValue = controller);

            // --- The garage. Last, because it needs the collider, the controller and the lights.
            VehicleBodySet bodySet = root.AddComponent<VehicleBodySet>();
            HorizonAssetUtility.Configure(bodySet, serialized =>
            {
                SerializedProperty array = serialized.FindProperty("bodies");
                array.arraySize = profiles.Length;

                for (int i = 0; i < profiles.Length; i++)
                {
                    SerializedProperty element = array.GetArrayElementAtIndex(i);

                    element.FindPropertyRelative("Name").stringValue = profiles[i].Name;
                    element.FindPropertyRelative("Root").objectReferenceValue = bodyObjects[i];
                    element.FindPropertyRelative("Renderer").objectReferenceValue =
                        bodyObjects[i].GetComponent<MeshRenderer>();
                    element.FindPropertyRelative("Config").objectReferenceValue = configs[i];
                    element.FindPropertyRelative("ColliderCenter").vector3Value = bodyBounds[i].center;
                    element.FindPropertyRelative("ColliderSize").vector3Value = bodyBounds[i].size;
                    element.FindPropertyRelative("WheelMesh").objectReferenceValue = bodyWheels[i];

                    SerializedProperty beams = element.FindPropertyRelative("Headlights");
                    beams.arraySize = bodyBeams[i].Length;
                    for (int beam = 0; beam < bodyBeams[i].Length; beam++)
                    {
                        beams.GetArrayElementAtIndex(beam).objectReferenceValue = bodyBeams[i][beam];
                    }
                }

                HorizonAssetUtility.SetObjectArray(serialized, "paints", materials.CarPaints);
                HorizonAssetUtility.SetObjectArray(serialized, "wheelFilters", wheelFilters);

                serialized.FindProperty("controller").objectReferenceValue = controller;
                serialized.FindProperty("lights").objectReferenceValue = lights;
                serialized.FindProperty("hull").objectReferenceValue = collider;
                serialized.FindProperty("engineAudio").objectReferenceValue = engineAudio;
            });

            // The four that would fail silently: without the collider the car keeps the fastback's box
            // whatever it is wearing, without the controller or the lights a swap changes the shape and
            // nothing else, and without the audio every car keeps the last one's engine note.
            HorizonAssetUtility.AssertReferenceAssigned(bodySet, "controller");
            HorizonAssetUtility.AssertReferenceAssigned(bodySet, "lights");
            HorizonAssetUtility.AssertReferenceAssigned(bodySet, "hull");
            HorizonAssetUtility.AssertReferenceAssigned(bodySet, "engineAudio");

            HorizonAssetUtility.EnsureFolder(PrefabsFolder);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, VehiclePrefabPath);
            Object.DestroyImmediate(root);

            // Check the saved asset too, not just the scene instance it was built from.
            HorizonAssetUtility.AssertReferenceAssigned(prefab.GetComponent<VehicleController>(), "config");
            HorizonAssetUtility.AssertReferenceAssigned(prefab.GetComponent<VehicleBodySet>(), "hull");

            ReportBodies(profiles, configs, bodyBounds, materials.CarPaints.Length);
            return prefab;
        }

        /// <summary>
        /// What the garage came out as: one line per body, and a check that the derivation still agrees
        /// with the car everybody has been driving.
        ///
        /// <para>Every other builder in this file reports itself, and for the same reason: a collider
        /// that does not match its bodywork is not visible anywhere until a car goes through a tunnel
        /// wall, and by then nobody is looking at the shape of a box. A line in the log is where that
        /// gets noticed.</para>
        /// </summary>
        /// <summary>
        /// How high off the road the highest-riding body sits, metres.
        ///
        /// <para>Read off the profiles rather than off the configs, for the same reason
        /// <c>CarPreviewRenderer</c> does: the profile is where the number is authored, and the config
        /// is a copy of it that a stale asset can be behind.</para>
        /// </summary>
        private static float TallestRideHeight()
        {
            float tallest = 0f;

            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.PlayerProfiles;
            for (int i = 0; i < profiles.Length; i++)
            {
                tallest = Mathf.Max(tallest, profiles[i].RideHeight);
            }

            return tallest;
        }

        /// <summary>
        /// The tallest kerb <c>TownStreetBuilder</c> builds, metres. Restated here because the check it
        /// feeds is about the car rather than about the street, and a car that cannot mount a kerb is a
        /// bug in the vehicle whichever file the number lives in.
        /// </summary>
        private const float TallestKerb = 0.17f;

        private static void ReportBodies(
            CarMeshBuilder.CarProfile[] profiles,
            VehicleConfig[] configs,
            Bounds[] bounds,
            int paintCount)
        {
            var report = new System.Text.StringBuilder();
            report.Append($"[Horizon] {profiles.Length} player bodies in {paintCount} paints:");

            for (int i = 0; i < profiles.Length; i++)
            {
                VehicleConfig config = configs[i];

                CarMeshBuilder.CarProfile profile = profiles[i];

                report.Append($"\n  {profile.Name,-10} {bounds[i].size.z:0.00} x {bounds[i].size.x:0.00} "
                              + $"x {bounds[i].size.y:0.00} m, collider centre "
                              + $"({bounds[i].center.x:0.00}, {bounds[i].center.y:0.00}, "
                              + $"{bounds[i].center.z:0.00}), {config.Mass:0} kg, {config.DrivenAxle} drive, "
                              + $"{config.MaxTorqueNm:0} Nm, top {config.TopSpeed * 3.6f:0} km/h");

                // The stance and the furniture, on their own line. Ride height is the number the whole
                // station table is quoted against, so a car whose config and profile have drifted apart
                // shows up here as two numbers that disagree rather than as a wheel through an arch that
                // nobody notices until they look at the thing side-on.
                // The arch gap is printed as what was asked for and what the beltline cap allowed,
                // because those two differ and only the second one is what the player sees.
                float frontGap = CarMeshBuilder.ArchClearanceAt(profile, CarMeshBuilder.WheelBaseHalf);
                float rearGap = CarMeshBuilder.ArchClearanceAt(profile, -CarMeshBuilder.WheelBaseHalf);

                report.Append($"\n  {string.Empty,-10} rides {profile.RideHeight:0.00} m on a "
                              + $"{profile.WheelRadius * 2f:0.00} m {profile.Rim} wheel "
                              + $"(rim {profile.RimFraction:0.00}), gap {frontGap:0.000}/{rearGap:0.000} m "
                              + $"of {profile.ArchGap:0.00} asked, "
                              + $"roof {bounds[i].max.y + profile.RideHeight:0.00} m up, "
                              + $"{profile.TailLamps} tail, {profile.HeadLamps} face, "
                              + $"{profile.ExhaustCount}x{profile.ExhaustRadius * 2f:0.00} m pipe");

                // What the bumper clears once the springs have taken the car's weight, which is the
                // number that decides whether it can drive up a kerb. Quoted rather than the box's own
                // corner because the box is measured at full droop and the car never is.
                float sag = config.Mass * 9.81f * 0.25f / Mathf.Max(1f, config.SuspensionStiffness);
                float bumper = profile.RideHeight + bounds[i].min.y - sag;

                report.Append($"\n  {string.Empty,-10} bumper {bumper:0.00} m over the road at rest "
                              + $"({sag * 100f:0} cm of sag)");

                if (bumper < TallestKerb + 0.03f)
                {
                    Debug.LogWarning(
                        $"[Horizon] {profile.Name}'s hull clears {bumper:0.00} m once it has settled, and "
                        + $"the town's tallest kerb is {TallestKerb:0.00} m. This car will plant its nose "
                        + "in one and stop. Raise CarMeshBuilder.ColliderGroundClearance.");
                }

                if (Mathf.Min(frontGap, rearGap) < profile.ArchGap - 0.015f)
                {
                    Debug.LogWarning(
                        $"[Horizon] {profile.Name} asked for {profile.ArchGap:0.00} m of arch gap and got "
                        + $"{Mathf.Min(frontGap, rearGap):0.000} m: BuildRing caps every opening at "
                        + "belt - 0.08, so the beltline over that axle is what is holding it back. Raise "
                        + "the beltline there, not the gap.");
                }

                if (Mathf.Abs(config.WheelRadius - profile.WheelRadius) > 0.001f
                    || Mathf.Abs(config.SuspensionRestLength - profile.SuspensionRestLength) > 0.001f)
                {
                    Debug.LogWarning(
                        $"[Horizon] {profile.Name}'s config rides on {config.WheelRadius:0.00} m over "
                        + $"{config.SuspensionRestLength:0.00} m of travel, and its body is lofted around "
                        + $"{profile.WheelRadius:0.00} over {profile.SuspensionRestLength:0.00}. The asset "
                        + "is stale — bump VehicleConfig.CurrentVersion or run Tools > Horizon > Reset "
                        + "Vehicle Configs to code defaults.");
                }
            }

            Debug.Log(report.ToString());

            // The fastback's box was four literals for the whole life of the project. Checked rather
            // than trusted: if a change to the station table or to the derivation moves it, this is the
            // line that says so, and the alternative is finding out from a car that no longer fits its
            // own collider.
            //
            // The height and centre are 22 cm off the original pair, and deliberately: the box no longer
            // reaches down to the sill. See CarMeshBuilder.ColliderGroundClearance — a hull measured
            // honestly off the bodywork cannot get over a kerb. Width and length are untouched, and they
            // are the two that would mean the silhouette had moved.
            Bounds fastback = bounds[0];
            var wasCenter = new Vector3(0f, 0.127f, -0.11f);
            var wasSize = new Vector3(2.26f, 1.13f, 4.74f);

            if (Vector3.Distance(fastback.center, wasCenter) > 0.01f
                || Vector3.Distance(fastback.size, wasSize) > 0.01f)
            {
                Debug.LogWarning(
                    $"[Horizon] The fastback's collider has moved: centre {fastback.center} size "
                    + $"{fastback.size}, against the {wasCenter} / {wasSize} it carried as literals. "
                    + "That is correct if the station table changed and a bug if it did not.");
            }
        }

        private static List<SpawnPoint> BuildWorldScene(GameObject vehiclePrefab)
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

            VehicleConfig config = LoadVehicleConfig("Fastback");

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

            // --- The country road on into the Ebental, carrying on where the pass runs out. Built with
            // the pass's own cross-section rather than a wider one: it is the same class of road, and a
            // change of width at the join would read as a change of country.
            var ebentalPathObject = new GameObject("EbentalRoadPath");
            ebentalPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath ebentalPath = ebentalPathObject.AddComponent<RoadPath>();

            RoadCourse ebentalCourse = EbentalCourse.Build();
            ebentalPath.SetControlPoints(ebentalCourse.ControlPoints);
            ReportCourse(ebentalCourse, ebentalPath, "Ebental road");

            Mesh ebentalMesh = RoadMeshBuilder.BuildRoad(ebentalPath, roadShape, "EbentalRoadMesh");
            ebentalMesh = HorizonAssetUtility.ReplaceAsset(
                ebentalMesh, GeneratedFolder + "/EbentalRoadMesh.asset");

            GameObject ebentalObject = CreateMeshObject(worldRoot.transform, "EbentalRoad", ebentalMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder });

            WorldChunk ebentalChunk = ebentalObject.AddComponent<WorldChunk>();
            ebentalChunk.RecalculateBounds();
            ebentalChunk.SetBounds(ebentalChunk.Center, 100000f);

            // The region the country road runs through, hung off the road itself rather than off a box
            // of coordinates — see LandRegion for why a rectangle here would recolour a hairpin of the
            // pass. Everything that gives the Ebental its own look reads this.
            LandRegion ebental = LandRegion.Ebental(ebentalPath);

            // --- On over the Kalkgrat and down the Steilufer, carrying on where the Ebental runs out.
            // Same cross-section again, for the reason the Ebental keeps the pass's: a change of width
            // at a join reads as a change of country, and none of these is a different class of road.
            var kalkgratPathObject = new GameObject("KalkgratRoadPath");
            kalkgratPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath kalkgratPath = kalkgratPathObject.AddComponent<RoadPath>();

            RoadCourse kalkgratCourse = KalkgratCourse.Build();
            kalkgratPath.SetControlPoints(kalkgratCourse.ControlPoints);
            ReportCourse(kalkgratCourse, kalkgratPath, "Kalkgrat road");

            Mesh kalkgratMesh = RoadMeshBuilder.BuildRoad(kalkgratPath, roadShape, "KalkgratRoadMesh");
            kalkgratMesh = HorizonAssetUtility.ReplaceAsset(
                kalkgratMesh, GeneratedFolder + "/KalkgratRoadMesh.asset");

            GameObject kalkgratObject = CreateMeshObject(worldRoot.transform, "KalkgratRoad",
                kalkgratMesh, new[] { materials.RoadSurface, materials.RoadShoulder });

            WorldChunk kalkgratChunk = kalkgratObject.AddComponent<WorldChunk>();
            kalkgratChunk.RecalculateBounds();
            kalkgratChunk.SetBounds(kalkgratChunk.Center, 100000f);

            // --- The coast road along the Meerenge, the crossing, and the first of the far shore.
            var meerengePathObject = new GameObject("MeerengeRoadPath");
            meerengePathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath meerengePath = meerengePathObject.AddComponent<RoadPath>();

            RoadCourse meerengeCourse = MeerengeCourse.Build();
            meerengePath.SetControlPoints(meerengeCourse.ControlPoints);
            ReportCourse(meerengeCourse, meerengePath, "Meerenge road");

            Mesh meerengeMesh = RoadMeshBuilder.BuildRoad(meerengePath, roadShape, "MeerengeRoadMesh");
            meerengeMesh = HorizonAssetUtility.ReplaceAsset(
                meerengeMesh, GeneratedFolder + "/MeerengeRoadMesh.asset");

            GameObject meerengeObject = CreateMeshObject(worldRoot.transform, "MeerengeRoad",
                meerengeMesh, new[] { materials.RoadSurface, materials.RoadShoulder });

            WorldChunk meerengeChunk = meerengeObject.AddComponent<WorldChunk>();
            meerengeChunk.RecalculateBounds();
            meerengeChunk.SetBounds(meerengeChunk.Center, 100000f);

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

            // --- The coast road, carrying on where the motorway runs out at its western tip.
            var coastPathObject = new GameObject("CoastRoadPath");
            coastPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath coastPath = coastPathObject.AddComponent<RoadPath>();

            RoadCourse coastCourse = CoastCourse.Build();
            coastPath.SetControlPoints(coastCourse.ControlPoints);
            ReportCourse(coastCourse, coastPath, "Coast road");

            Mesh coastMesh = RoadMeshBuilder.BuildRoad(coastPath, roadShape, "CoastRoadMesh");
            coastMesh = HorizonAssetUtility.ReplaceAsset(
                coastMesh, GeneratedFolder + "/CoastRoadMesh.asset");

            GameObject coastObject = CreateMeshObject(worldRoot.transform, "CoastRoad", coastMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder });

            WorldChunk coastChunk = coastObject.AddComponent<WorldChunk>();
            coastChunk.RecalculateBounds();
            coastChunk.SetBounds(coastChunk.Center, 100000f);

            // --- Seeburg's axis, crossing the coast road where it runs out. Never paved: like Hochstadt's
            // arterial it is a coordinate system and a height datum, and what is driven along it is the
            // waterfront boulevard in the town's own layout table.
            var seeburgAxisObject = new GameObject("SeeburgAxis");
            seeburgAxisObject.transform.SetParent(worldRoot.transform, false);
            RoadPath seeburgAxis = seeburgAxisObject.AddComponent<RoadPath>();

            RoadCourse seeburgCourse = SeeburgCourse.Build();
            seeburgAxis.SetControlPoints(seeburgCourse.ControlPoints);

            BuildMotorwayMerge(worldRoot.transform, out float rampCapOnMedian, out float rampMergeOnMedian,
                motorwayPath, westbound, motorwayShape, roadShape, linkPath, materials);
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

            // Seeburg hangs off its own axis rather than off the coast road, for the reason
            // HochstadtCourse gives: town-local coordinates fold on a bend, and the coast road's are
            // 320 m. The trunk shape handed in is the pass's, not the motorway's — the axis stands in
            // for a country road, and TownShape.Seeburg measures its plot clearances against that.
            TownNetworkSpec seeburgLayout = SeeburgLayout.Build();

            // Resolved once, here, because everything downstream wants the index and the table only
            // gives a name — see SeeburgLayout.GatewayNodeName. A second Build() would be a second
            // table, and the two would agree until they did not.
            int seeburgGateway = seeburgLayout.IndexOfNode(SeeburgLayout.GatewayNodeName);

            if (seeburgGateway < 0)
            {
                Debug.LogError($"[Horizon] Seeburg's layout has no node named "
                               + $"'{SeeburgLayout.GatewayNodeName}'. The coast road has nothing to hand "
                               + "its traffic over to, so its cars will reach the sea and turn round.");
            }

            TownBuild seeburg = PrepareTown(
                "Seeburg", seeburgLayout, seeburgAxis, TownShape.Seeburg,
                worldRoot.transform, roadShape, terrainShape, levelSamples);

            var towns = new[] { talheim, hochstadt, seeburg };

            // --- The filling stations, resolved here and built much later.
            //
            // Here, and not with the rest of the roadside furniture, for exactly the reason the towns are
            // split across the field: a forecourt needs level ground, and the only way to get level
            // ground is to tell MountainField about it before it is built. Afterwards there is nothing
            // left to tell — and nothing would report the failure either, because the apron mesh is laid
            // from the course rather than from the terrain, so it would come out perfectly flat and
            // hovering over a hillside.
            //
            // The motorway is asked twice, once per carriageway. It is one course serving two roads, so
            // each takes only the stations that declared its own side.
            var fuelStations = new List<FuelStationMeshes.StationSite>(8);
            fuelStations.AddRange(FuelStationBuilder.Sites(path, course, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(ebentalPath, ebentalCourse, roadShape));
            fuelStations.AddRange(
                FuelStationBuilder.Sites(westbound, motorwayCourse, motorwayShape, -1f));
            fuelStations.AddRange(
                FuelStationBuilder.Sites(eastbound, motorwayCourse, motorwayShape, 1f));
            fuelStations.AddRange(FuelStationBuilder.Sites(coastPath, coastCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(kalkgratPath, kalkgratCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(meerengePath, meerengeCourse, roadShape));

            // Every carriageway in the world, so a pad can be kept off all of them and not merely off
            // the one it belongs to. The pass is the case that matters: its switchbacks stack legs
            // forty metres apart in plan and fifteen in height, so a platform at the summit is directly
            // over the road below it.
            var padRoads = new[]
            {
                new FuelStationBuilder.NearbyRoad(path, roadShape, "the pass"),
                new FuelStationBuilder.NearbyRoad(ebentalPath, roadShape, "the Ebental road"),
                new FuelStationBuilder.NearbyRoad(westbound, motorwayShape, "the westbound carriageway"),
                new FuelStationBuilder.NearbyRoad(eastbound, motorwayShape, "the eastbound carriageway"),
                new FuelStationBuilder.NearbyRoad(linkPath, roadShape, "the motorway link"),
                new FuelStationBuilder.NearbyRoad(coastPath, roadShape, "the coast road"),
                new FuelStationBuilder.NearbyRoad(kalkgratPath, roadShape, "the Kalkgrat road"),
                new FuelStationBuilder.NearbyRoad(meerengePath, roadShape, "the Meerenge road"),
            };

            for (int i = 0; i < fuelStations.Count; i++)
            {
                FuelStationMeshes.StationSite site = fuelStations[i];

                // Two metres of tolerance: a pad is levelled at its own carriageway's height and the
                // road runs on a grade through it, so its far end is legitimately a little below the
                // near one. Anything past that is a different road.
                if (FuelStationBuilder.PadBuriesRoad(
                        site, padRoads, 2f, terrainShape.VergeWidth, out string buried, out float deep))
                {
                    Debug.LogError($"[Horizon] '{site.Name}' cannot stand here. Its forecourt has to be "
                                   + $"levelled, and doing that drops {deep:0.0} m of ground onto "
                                   + $"{buried} — which puts a wall across a carriageway that nothing "
                                   + "else in this build will complain about. Move it in the course "
                                   + "table, or take it out.");
                }

                FuelStationBuilder.AddPadSamples(site, levelSamples, padRoads);
            }

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

                // Without this line the Ebental road is a ribbon laid over nothing: tiles are listed
                // around whatever the field calls a road, so the corridor — and the ground the Auensee
                // is dug into — arrives with the road rather than as a region somebody adds by hand.
                new MountainField.FieldRoad(ebentalPath),

                new MountainField.FieldRoad(westbound, motorwayCourse),
                new MountainField.FieldRoad(eastbound, motorwayCourse),
                new MountainField.FieldRoad(linkPath),

                // The coast road is in here for the terrain as much as for the shelf: tiles are listed
                // around whatever the field calls a road, so the corridor out to the water — and the
                // ground the sea will be dug into — arrives with the road rather than as a region
                // somebody has to remember to add.
                new MountainField.FieldRoad(coastPath),

                // Both of these hand over their course as well as their path, and both need to. The
                // Kalkgrat has a viaduct across a ravine and the Meerenge has the crossing, and without
                // the course the field knows about neither — it would carry the valley floor up to the
                // Schluchtbrücke's deck, and lay a sixty-metre causeway across the strait.
                new MountainField.FieldRoad(kalkgratPath, kalkgratCourse),
                new MountainField.FieldRoad(meerengePath, meerengeCourse),
            };

            var field = new MountainField(roads, terrainShape, 4f, levelSamples);
            Phase(clock, $"height field ({levelSamples.Count} level samples)");

            // The water, straight after the field and before anything reads a height from it.
            //
            // The order is the load-bearing part. Every surface is derived by sampling the rim of its
            // own basin on the *natural* ground, so the resolve has to happen while the field still
            // has no basins in it; the moment SetWater returns, every later query — terrain, plants,
            // town plots, the traffic bake — sees the dug version. A consumer that sampled before this
            // line would be working from ground that no longer exists, which is a row of trees
            // standing in a lake.
            // The corridor bodies, plus the one that has to be worked out from a village rather than
            // written down beside it — see WaterShape.BesideTown for what typing it cost last time.
            var waterPlans = new List<WaterPlan>(WaterShape.Corridor);

            waterPlans.Add(WaterShape.BesideTown(
                "Talheimer See",
                talheim.Network,
                talheim.Footprint,
                path,
                (MountainPassCourse.TownStartDistance + MountainPassCourse.TownEndDistance) * 0.5f,
                TownShape.Default.AcrossInner,
                // Small, because the room between Talheim's last plot and the edge of the terrain
                // corridor is about 110 m and the bank has to fit inside it too. A tarn, not a lake.
                radius: 35f,
                bankEase: 25f,
                depth: 4f,
                freeboard: 4f,
                // Plot depth plus its garden plus the fence line — the outermost thing Talheim ever
                // puts down beside a street is about thirty metres from the kerb.
                plotReach: 32f,
                out string lakeSite));

            Debug.Log($"[Horizon] Water siting: {lakeSite}");

            // And the sea, and the harbour dug into it.
            //
            // Both are measured off Seeburg's axis rather than off the coast road, which is a change
            // from when there was nothing at the water but a parking apron. The axis is the waterfront:
            // it is where the shoreline has to be parallel to, where the town's floor is derived from,
            // and the one line both the sea's level and the harbour's have to agree with. Deriving them
            // from the road that arrives instead would be deriving them from something perpendicular to
            // everything that matters.
            Vector3 waterfront = seeburgAxis.GetPositionAtDistance(SeeburgCourse.GatewayAlong);

            // Seaward is the axis' left, because TownShape.ToWorld puts positive across to its right and
            // Seeburg's positive across is inland — see SeeburgCourse.
            Vector3 seaward = -seeburgAxis.GetRightAtDistance(SeeburgCourse.GatewayAlong);

            // Three and a half metres under the waterfront. Two would read better, but three is the
            // least a road may stand clear of water anywhere in this build, and a rule the newest town
            // is exempt from is not a rule.
            float seaLevel = waterfront.y - SeeburgCourse.SeaFreeboard;

            Vector2 seaCentre = Flat(
                waterfront + seaward * (SeeburgCourse.ShoreOffset + SeeburgCourse.SeaRadius));

            waterPlans.Add(WaterPlan.Sea(
                "Westmeer",
                seaCentre,
                radius: SeeburgCourse.SeaRadius,
                bankEase: SeeburgCourse.SeaBankEase,
                depth: SeeburgCourse.SeaDepth,
                surfaceY: seaLevel,
                // Untied from the radius, which is the whole reason the shoreline can be this straight
                // and the water still go dark within sight of the beach. See WaterBody.BedScale.
                bedScale: SeeburgCourse.SeaBedScale));

            // The harbour. A capping body rather than a second sea, so it digs the basin out of the land
            // it reaches and leaves the deeper of the two where it lies over the sea's own bed — see
            // WaterPlan.Basin for what two overlapping seas do to each other instead.
            Vector3 basinAt = seeburgAxis.GetPositionAtDistance(SeeburgCourse.BasinAlong)
                              + seaward * -SeeburgCourse.BasinAcross;

            // Sized so its landward rim stands this far out from the axis. The harbour geometry is laid
            // against the same figure, because a quay wall that is not on the edge of the basin is a
            // wall in the water or a wall in a field.
            float basinRimAcross = -SeeburgCourse.BasinAcross - SeeburgCourse.BasinRadius;

            waterPlans.Add(WaterPlan.Basin(
                "Seeburger Hafen",
                Flat(basinAt),
                radius: SeeburgCourse.BasinRadius,
                bankEase: SeeburgCourse.BasinBankEase,
                depth: SeeburgCourse.BasinDepth,
                surfaceY: seaLevel));

            // Every road that carries a bridge a river could be laid across, not just the motorway.
            var bridgeRoads = new[]
            {
                new WaterPlanner.BridgeRoad(motorwayPath, motorwayCourse),
                new WaterPlanner.BridgeRoad(meerengePath, meerengeCourse),
            };

            // And the Meerenge. A river by every mechanic that matters — a spine, a half-width, and a
            // place taken from the bridge over it — which is why it is not a second Sea. The difference
            // is load-bearing: a sea *sets* the ground under it and a river only caps it, and the ground
            // under this crossing is the coarse field interpolating a deck sixty metres up. The cap
            // shaves that hump away; setting would drag the banks down with it.
            //
            // Everything here is read from the course rather than typed beside it, so the water follows
            // the crossing whenever the crossing is retuned. See MeerengeCourse for each number.
            waterPlans.Add(WaterPlan.River(
                "Boğaz",
                MeerengeCourse.BridgeName,
                halfWidth: MeerengeCourse.ChannelHalfWidth,
                bankEase: MeerengeCourse.ChannelBankEase,
                reach: MeerengeCourse.ChannelReach,
                skewDegrees: MeerengeCourse.ChannelSkew,
                depth: MeerengeCourse.ChannelDepth,
                freeboard: MeerengeCourse.ChannelFreeboard,
                bedScale: MeerengeCourse.ChannelBedScale));

            WaterBody[] waters = WaterPlanner.Resolve(
                waterPlans, field, bridgeRoads, out string waterReport);

            field.SetWater(waters);
            ValidateWater(waters, field, roads, towns);

            Debug.Log($"[Horizon] Water: {waters.Length} bodies.{waterReport}");

            BuildWaterHazard(worldRoot.transform, waters);


            ValidateRoadClearance(path, roadShape, field, course);
            ValidateRoadClearance(ebentalPath, roadShape, field, ebentalCourse, "Ebental");
            ValidateRoadClearance(kalkgratPath, roadShape, field, kalkgratCourse, "Kalkgrat");
            ValidateRoadClearance(meerengePath, roadShape, field, meerengeCourse, "Meerenge");
            ValidateRoadClearance(westbound, motorwayShape, field, motorwayCourse, "Westbound");
            ValidateRoadClearance(eastbound, motorwayShape, field, motorwayCourse, "Eastbound");
            ValidateBridges(westbound, field, motorwayCourse);
            ValidateBridges(kalkgratPath, field, kalkgratCourse);
            // The second half of every town: street meshes onto the finished terrain, then blocks and
            // plots seated on it.
            // The lens renderers, in the same counts-to-offsets shape TownLights uses. Declared before
            // the town loop because BuildStreetMeshes fills them, and read after it by
            // WireTrafficSignals.
            var signalRenderers = new List<MeshRenderer>();
            var signalSlotStart = new List<int> { 0 };
            var signalSlots = new List<int>();
            var signalLenses = new List<int>();

            int plots = 0;
            for (int i = 0; i < towns.Length; i++)
            {
                PlanTown(towns[i], field, terrainShape, materials,
                    signalRenderers, signalSlotStart, signalSlots, signalLenses);
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

            // The sea's own share of terrain, out past the corridor and past Seeburg's basin.
            //
            // Nothing here is about ground: it is about how far the water reaches, and the water only
            // exists where a tile does. With the corridor alone the sea ran out about 320 m from the
            // beach — inside the fog, inside the 600 m far plane, and therefore a visible dark edge with
            // the sky behind it. A square box rather than a band because Bounds are axis-aligned and the
            // shore runs at whatever angle the town does; the corners cost a few tiles that are then
            // skipped for being under deep water.
            //
            // Centred on the middle of the waterfront rather than on one point of it, and half again as
            // wide as it was: the horizon has to hold from either end of a seven-hundred-metre front
            // now, not just from a parking apron.
            Vector3 frontMiddle = seeburgAxis.GetPositionAtDistance(
                (SeeburgCourse.CityStart + SeeburgCourse.CityEnd) * 0.5f);

            var seaBand = new Bounds(
                frontMiddle + seaward * 320f,
                new Vector3(1500f, 200f, 1500f));

            // The Meerenge's own band, for the same reason and sized by the same question: how far does
            // the water have to reach before its edge is behind the fog rather than in front of it.
            //
            // Not the whole channel, and that is the saving. It runs 1900 m either side of the deck,
            // but only the southern half has a road beside it — the corniche — and north of the crossing
            // nothing can be seen past the fog wall anyway. A band over the full 3800 m would be four
            // hundred tiles of water nobody ever gets within a kilometre of.
            //
            // Shifted towards the corniche rather than centred on the deck, for that reason. Positive
            // across at the crossing points down the channel towards the coast road, because the deck
            // runs square to the water.
            const float ShownTowardsCorniche = 2700f;
            const float ShownBeyondCrossing = 700f;

            Vector3 crossingMiddle = meerengePath.GetPositionAtDistance(MeerengeCourse.CrossingMiddle);
            Vector3 downTheChannel = meerengePath.GetRightAtDistance(MeerengeCourse.CrossingMiddle);

            var straitSection = new Vector3(
                MeerengeCourse.ChannelHalfWidth * 2f + MeerengeCourse.ChannelBankEase * 2f + 500f,
                200f,
                0f);

            var straitBand = new Bounds(crossingMiddle, straitSection);
            straitBand.Encapsulate(
                new Bounds(crossingMiddle + downTheChannel * ShownTowardsCorniche, straitSection));
            straitBand.Encapsulate(
                new Bounds(crossingMiddle - downTheChannel * ShownBeyondCrossing, straitSection));

            BuildTerrainTiles(worldRoot.transform, path, roadShape, course, field, terrainShape,
                towns, materials, litRenderers, litSlotStart, litSlots, litSlotGroups,
                new[] { seaBand, straitBand },
                new[]
                {
                    new MountainField.FieldRoad(ebentalPath, ebentalCourse),
                    new MountainField.FieldRoad(kalkgratPath, kalkgratCourse),
                    new MountainField.FieldRoad(meerengePath, meerengeCourse),
                },
                ebental, ebentalPath,
                ForecourtCentres(fuelStations));
            ValidateLandmarks(field, course, path, talheim.Plan);
            MarkTownLandmarks(worldRoot.transform, talheim.Network, talheim.Plan);
            Phase(clock, "terrain, vegetation and buildings");

            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);
            BuildDelineatorPosts(worldRoot.transform, path, roadShape, field, course, materials);

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
            BuildDelineatorPosts(worldRoot.transform, westbound, motorwayShape, field, motorwayCourse,
                materials, "MotorwayWest");
            BuildGuardRails(worldRoot.transform, eastbound, motorwayShape, field, motorwayCourse,
                materials, "MotorwayEast");
            BuildDelineatorPosts(worldRoot.transform, eastbound, motorwayShape, field, motorwayCourse,
                materials, "MotorwayEast");
            BuildMedianBarrier(worldRoot.transform, motorwayPath, motorwayShape, motorwayCourse, materials);

            BuildGuardRails(worldRoot.transform, linkPath, roadShape, field, linkCourse,
                materials, "MotorwayLink");

            // The Ebental road gets both, and the posts are the half that matters. It has one blind
            // crest with an 84-degree left immediately behind it, and a line of posts running round
            // that bend is the only thing standing between the driver and a corner they cannot see the
            // exit of — see EbentalCourse.CrestFallGrade.
            BuildGuardRails(worldRoot.transform, ebentalPath, roadShape, field, ebentalCourse,
                materials, "EbentalRoad");

            BuildDelineatorPosts(worldRoot.transform, ebentalPath, roadShape, field, ebentalCourse,
                materials, "EbentalRoad");

            // The Kalkgrat gets a tunnel, a gallery, a viaduct and both kinds of roadside furniture.
            // The posts earn their place here more than anywhere else in the world: seven hairpins down
            // a cliff, and on the outside of every one of them the ground simply stops.
            BuildCoveredSections(worldRoot.transform, kalkgratPath, roadShape, kalkgratCourse, field,
                materials);

            BuildBridges(worldRoot.transform, kalkgratPath, roadShape, field, kalkgratCourse,
                materials, "KalkgratRoad");

            BuildGuardRails(worldRoot.transform, kalkgratPath, roadShape, field, kalkgratCourse,
                materials, "KalkgratRoad");

            BuildDelineatorPosts(worldRoot.transform, kalkgratPath, roadShape, field, kalkgratCourse,
                materials, "KalkgratRoad");

            // The Meerenge gets the two cape bores and the crossing. The rails and posts read
            // IsBridged, which now reports a suspension span as well as a viaduct, so neither of them
            // walks out over the water — the parapet on the deck is the structure's own.
            BuildCoveredSections(worldRoot.transform, meerengePath, roadShape, meerengeCourse, field,
                materials);

            BuildSuspensionBridges(worldRoot.transform, meerengePath, roadShape, field, meerengeCourse,
                MeerengeCourse.Crossing, materials, "MeerengeRoad");

            BuildGuardRails(worldRoot.transform, meerengePath, roadShape, field, meerengeCourse,
                materials, "MeerengeRoad");

            BuildDelineatorPosts(worldRoot.transform, meerengePath, roadShape, field, meerengeCourse,
                materials, "MeerengeRoad");

            ValidateSuspensionBridges(meerengePath, field, meerengeCourse, MeerengeCourse.Crossing);

            // --- The filling stations. After the terrain, because the slab sits on ground that has to
            // exist first — and after the guard rails, so that the rails have already read IsForecourt
            // and left the frontage open before anything is standing on it.
            BuildFuelStations(worldRoot.transform, fuelStations, field, materials,
                litRenderers, litSlotStart, litSlots, litSlotGroups);

            BuildFillingStations(worldRoot.transform, fuelStations);

            // --- Seeburg's harbour. After the water, because every height in it is measured off the
            // surface that was resolved there, and after the terrain, because the promenade rail is laid
            // on ground that has to exist first.
            BuildHarbour(worldRoot.transform, seeburgAxis, field, terrainShape, materials,
                seeburg.Network, basinAt, seaward, seaLevel, basinRimAcross,
                litRenderers, litSlotStart, litSlots, litSlotGroups);
            BuildDelineatorPosts(worldRoot.transform, linkPath, roadShape, field, linkCourse,
                materials, "MotorwayLink");

            TrafficNetwork routes = BuildTraffic(worldRoot.transform, towns, path, roadShape, materials,
                litRenderers, litSlotStart, litSlots, litSlotGroups,
                motorwayPath, motorwayShape, AutobahnCourse.CarriagewayOffset,
                System.Array.IndexOf(towns, hochstadt), HochstadtLayout.GatewayNode,
                linkPath, roadShape, rampCapOnMedian, rampMergeOnMedian,
                coastPath, roadShape, System.Array.IndexOf(towns, seeburg), seeburgGateway,
                ebentalPath, roadShape);

            // After the routes exist, because the phase the lenses show is read off the same asset the
            // traffic obeys — which is the whole reason a light cannot be green at a junction cars are
            // stopping at.
            WireTrafficSignals(worldRoot.transform, routes, materials,
                signalRenderers, signalSlotStart, signalSlots, signalLenses);

            // After both, so one component carries the town's windows and the traffic's lamps.
            WireTownLights(worldRoot.transform, litRenderers, litSlotStart, litSlots, litSlotGroups,
                materials);

            ValidateFuelStations(
                (path, course, roadShape, "the pass", 0f),
                (ebentalPath, ebentalCourse, roadShape, "the Ebental road", 0f),
                (westbound, motorwayCourse, motorwayShape, "the westbound carriageway", -1f),
                (eastbound, motorwayCourse, motorwayShape, "the eastbound carriageway", 1f),
                (coastPath, coastCourse, roadShape, "the coast road", 0f),
                (kalkgratPath, kalkgratCourse, roadShape, "the Kalkgrat road", 0f),
                (meerengePath, meerengeCourse, roadShape, "the Meerenge road", 0f));

            // After every builder and before the car exists — otherwise the car is the obstruction.
            ValidateDriveableCorridor(path, "the pass", 1.3f, 4f);
            ValidateDriveableCorridor(ebentalPath, "the Ebental road", 1.3f, 4f);
            ValidateDriveableCorridor(westbound, "the westbound carriageway", 1.3f, 4f);
            ValidateDriveableCorridor(eastbound, "the eastbound carriageway", 1.3f, 4f);
            ValidateDriveableCorridor(linkPath, "the motorway link", 1.3f, 4f);
            ValidateDriveableCorridor(coastPath, "the coast road", 1.3f, 4f);
            ValidateDriveableCorridor(kalkgratPath, "the Kalkgrat road", 1.3f, 4f);
            ValidateDriveableCorridor(meerengePath, "the Meerenge road", 1.3f, 4f);
            ReportCourse(seeburgCourse, seeburgAxis, "Seeburg axis");
            Phase(clock, "validation");
            int worstJunction = ValidateStreetNetwork(talheim.Network, path, roadShape);
            MarkWorstJunction(worldRoot.transform, talheim.Network, worstJunction);
            ValidateStreetNetwork(hochstadt.Network, arterialPath, motorwayShape,
                HochstadtLayout.GatewayNode);
            ValidateStreetNetwork(seeburg.Network, seeburgAxis, roadShape, seeburgGateway);

            // --- Streaming.
            var streamingObject = new GameObject("Streaming");
            WorldStreamer streamer = streamingObject.AddComponent<WorldStreamer>();
            WorldStreamingDriver driver = streamingObject.AddComponent<WorldStreamingDriver>();
            HorizonAssetUtility.Configure(driver, serialized =>
                serialized.FindProperty("streamer").objectReferenceValue = streamer);

            // Counted after every builder and before the car, at the streamer's own radius and again at
            // the first pressure valve, so the question "would 450 m help, and by how much" is answered
            // in the log rather than by trying it.
            List<Vector3> stations = DrawCallStations(
                path, motorwayPath, arterialPath, seeburgAxis, ebentalPath,
                kalkgratPath, meerengePath);
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

            BuildSpeedAtmosphere(atmosphereObject.transform, timeOfDay, materials);

            // --- Vehicle, dropped onto the road among the houses rather than at the start of the course.
            // The arrival road in front of the town is 700 m of scenery to drive *back* along, not
            // something to make the player sit through before anything happens.
            float spawnDistance = MountainPassCourse.TownStartDistance + 45f;
            Vector3 spawnDirection = path.GetDirectionAtDistance(spawnDistance);
            // The *tallest* body's, not the fastback's, and that is the whole reason this is a loop.
            // A spawn point is a fixed position in a baked scene and the player may arrive at it in any
            // of the ten — so it has to clear the one that rides highest. Placed at the off-roader's
            // height a hatchback drops 17 cm onto its springs, which is a settle nobody notices; placed
            // at the hatchback's, the off-roader starts with its wheels inside the tarmac and its first
            // physics step is a launch.
            float rideHeight = TallestRideHeight() + 0.05f;

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

            // Where the player may choose to begin. Worked out here, where the paths are, and handed to
            // the Bootstrap scene — the menu that offers them lives there and has no way to ask a road
            // anything.
            List<SpawnPoint> spawns = BuildSpawnTable(
                path, roadShape, motorwayPath, motorwayShape, arterialPath, seeburgAxis,
                ebentalPath, ebentalCourse, kalkgratPath, meerengePath, meerengeCourse, rideHeight);

            EditorSceneManager.SaveScene(scene, WorldScenePath);
            return spawns;
        }

        /// <summary>
        /// The places the start menu offers, each measured off the road it stands on.
        ///
        /// <para>Every one is a distance along a course rather than a coordinate, so they follow the
        /// roads when those are retuned — the same reason the spawn point itself has always been
        /// computed rather than typed. A place that drifted into a hillside because a bend was opened
        /// out would be a bug nobody would look for here.</para>
        /// </summary>
        private static List<SpawnPoint> BuildSpawnTable(
            RoadPath pass,
            in RoadShape passShape,
            RoadPath motorway,
            in RoadShape motorwayShape,
            RoadPath arterial,
            RoadPath seeburgAxis,
            RoadPath ebental,
            RoadCourse ebentalCourse,
            RoadPath kalkgrat,
            RoadPath meerenge,
            RoadCourse meerengeCourse,
            float rideHeight)
        {
            var spawns = new List<SpawnPoint>(9);

            void Add(string name, IRoadPath path, float distance, float across, float lift)
            {
                float at = Mathf.Clamp(distance, 0f, path.Length);

                Vector3 forward = path.GetDirectionAtDistance(at);
                Vector3 position = path.GetPositionAtDistance(at)
                                   + path.GetRightAtDistance(at) * across
                                   + Vector3.up * lift;

                spawns.Add(new SpawnPoint(name, position, Quaternion.LookRotation(forward, Vector3.up)));
            }

            // In the right-hand lane in each case, not astride the centre line.
            float passLane = passShape.HalfWidth * 0.5f;

            Add("Talheim", pass, MountainPassCourse.TownStartDistance + 45f, passLane, rideHeight);

            // The summit, found by walking the course for its highest point rather than by a distance
            // somebody counted — the switchback stack is retuned often enough that a literal would rot.
            Add("Passhöhe", pass, HighestDistance(pass), passLane, rideHeight);

            // On the eastbound carriageway at the interchange, pointing at the city.
            Add("Autobahn", motorway, AutobahnCourse.JunctionDistance,
                AutobahnCourse.CarriagewayOffset + motorwayShape.HalfWidth * 0.5f, rideHeight);

            // On the boulevard, a little inside the city gate so the skyline is ahead rather than
            // overhead.
            Add("Hochstadt", arterial, 120f, 4f, rideHeight);

            // On Seeburg's waterfront, a little past the harbour so the quay and the moles are in the
            // mirror rather than behind the camera. The right-hand lane here is the inland one, so the
            // water is out of the driver's window from the moment the scene loads.
            Add("Seeburg", seeburgAxis, SeeburgCourse.BasinAlong + 60f, 4f, rideHeight);

            // On the Ebental crest, facing the way the road falls away from it. Chosen over the lake or
            // the valley floor because it is the one place on that road where both halves of it are
            // visible at once — and because arriving there is what the rest of the road is arranged
            // around.
            //
            // Taken from the viewpoint the course already marks there, not from HighestDistance: this
            // road's highest point is its first metre, where it comes off the pass at 37 m, and the
            // crest is a local rise of eighteen metres a third of the way along. A summit walk would
            // put the player back at the join facing away from everything.
            Add("Ebental", ebental, ViewpointDistance(ebentalCourse, "Hochwiese"), passLane, rideHeight);

            // At the tunnel mouth on the Kalkgrat, facing the reveal. Everything about that road is
            // arranged around this one frame, so it is the place to arrive at — and, less romantically,
            // it is where anybody tuning the descent, the strait or the bridge needs to start, which
            // would otherwise mean driving eleven kilometres from Talheim first.
            Add("Kalkgrat", kalkgrat, KalkgratCourse.RevealDistance + 30f, passLane, rideHeight);

            // On the corniche, at the bay, with the water out of the right-hand window.
            Add("Küstenstraße", meerenge, ViewpointDistance(meerengeCourse, "Steilbucht") - 220f,
                passLane, rideHeight);

            // On the deck, a third of the way across — near enough to the western tower that it is
            // overhead and far enough that the eastern one is in the windscreen.
            Add("Boğaz Köprüsü", meerenge,
                MeerengeCourse.CrossingStart + MeerengeCourse.StructureLength * 0.33f,
                passLane, rideHeight);

            return spawns;
        }

        /// <summary>
        /// How the ground report decides a sample is shore rather than town floor. The same pair
        /// <c>TerrainTileBuilder</c> tints sand with, so the two agree about where the beach is.
        /// </summary>
        private const float ShoreFreeboard = 3f;

        /// <summary>See <see cref="ShoreFreeboard"/>.</summary>
        private const float ShoreReach = 18f;

        /// <summary>
        /// Seeburg's harbour: quay, moles, lighthouse, pontoons, boats and the promenade rail.
        ///
        /// <para><b>One mesh and one chunk for the lot.</b> The four opaque submeshes merge into one on
        /// the vertex-tint material the buildings already use, so the whole harbour is two draw calls —
        /// the second being the lantern, which needs a material of its own because <c>TownLights</c>
        /// swaps it after dusk.</para>
        ///
        /// <para><b>Not a town plot, which is why it is here and not in TownPlanner.</b> Every plot in
        /// the world is placed against a street frontage. A quay wall is placed against the edge of a
        /// dredged basin and a mole against open water, so this belongs with the guard rails and the
        /// bridges: things laid along a line the world already has.</para>
        /// </summary>
        private static void BuildHarbour(
            Transform parent,
            RoadPath axis,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials,
            StreetNetwork streets,
            Vector3 basinAt,
            Vector3 seaward,
            float seaLevel,
            float basinRimAcross,
            List<MeshRenderer> litRenderers,
            List<int> litSlotStart,
            List<int> litSlots,
            List<int> litSlotGroups)
        {
            // The quay's paving is the town's own floor at the basin's rim, less the shelf drop the field
            // applies to every levelled sample. Derived rather than sampled, because sampling the ground
            // there reads the bank that has just been dug into it.
            float quayY = axis.GetPositionAtDistance(SeeburgCourse.BasinAlong).y
                          + SeeburgCourse.FloorRiseAt(basinRimAcross)
                          - terrainShape.RoadShelfDrop;

            var site = new HarbourMeshes.HarbourSite(
                basinAt,
                SeeburgCourse.BasinRadius,
                -seaward,
                seaLevel,
                seaLevel - SeeburgCourse.BasinDepth,
                quayY,
                // Where the open sea's waterline crosses, measured from the basin's centre. The moles
                // start there, because an arm that starts anywhere else starts in the water.
                -SeeburgCourse.BasinAcross - SeeburgCourse.ShoreOffset);

            var buffer = new VegetationMeshBuffer(HarbourMeshes.SubmeshCount);
            HarbourMeshes.AddHarbour(buffer, site);

            // The rail sits just outside the boulevard's footway. Read off the street's own cross-section
            // rather than typed, so it stays on the kerb line if the boulevard is ever widened.
            // The same figure twice: it is how far outside the kerb the rail nominally stands, and it is
            // the margin the clearance test holds it to. Two numbers here means every post on a dead
            // straight stretch counts as blocked and swings out for nothing — which is what happened.
            const float railClearance = 1.2f;

            float railAcross = -(TownStreetShape.For(
                TownStreetKind.Boulevard, terrainShape.RoadShelfDrop).HalfOuter + railClearance);

            HarbourMeshes.AddPromenade(buffer, axis, field, streets,
                SeeburgCourse.CityStart + 30f, SeeburgCourse.CityEnd - 30f, railAcross, railClearance,
                // How far the rail may lean out to get round a pad before it gives up and leaves a gap.
                // Six metres, because the beach begins about twenty out from the boulevard's centreline
                // and the rail nominally stands fourteen.
                6f,
                out float worstSwing, out int railGaps);

            buffer.MergeTinted(HarbourMeshes.Tints());

            var used = new List<int>(HarbourMeshes.SubmeshCount);
            Mesh mesh = buffer.ToMesh("SeeburgHarbourMesh", used);

            if (mesh == null)
            {
                Debug.LogWarning("[Horizon] Seeburg harbour: nothing was built.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/SeeburgHarbourMesh.asset");

            var harbourMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                harbourMaterials[i] = used[i] == HarbourMeshes.LanternSubmesh
                    ? materials.WindowDay
                    : materials.BuildingTint;
            }

            GameObject harbour = CreateMeshObject(parent, "SeeburgHarbour", mesh, harbourMaterials);

            WorldChunk chunk = harbour.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();

            int lanternSlot = used.IndexOf(HarbourMeshes.LanternSubmesh);
            if (lanternSlot >= 0)
            {
                litRenderers.Add(harbour.GetComponent<MeshRenderer>());
                litSlots.Add(lanternSlot);
                litSlotGroups.Add((int)LitGroup.Lamps);
                litSlotStart.Add(litSlots.Count);
            }

            Debug.Log($"[Horizon] Seeburg harbour: {mesh.triangles.Length / 3} triangles in "
                      + $"{used.Count} draw call(s) — a {SeeburgCourse.BasinRadius:0} m basin with its rim "
                      + $"{basinRimAcross:0} m off the waterfront, quay at {quayY:0.0} m over water at "
                      + $"{seaLevel:0.0} m, and a {HarbourMeshes.LighthouseHeight:0} m light on the mole "
                      + $"head. The promenade rail leans out up to {worstSwing:0.0} m to clear the "
                      + $"paving and breaks for it at {railGaps} of its posts.");
        }

        /// <summary>A world position as plan coordinates. Water is authored in X and Z.</summary>
        private static Vector2 Flat(Vector3 at)
        {
            return new Vector2(at.x, at.z);
        }

        /// <summary>Distance along a path of its highest point, sampled every 10 m.</summary>
        /// <summary>
        /// Where a named viewpoint sits along its course.
        ///
        /// <para>Used instead of a literal for the same reason every other spawn is a distance rather
        /// than a coordinate: the viewpoint is placed by the walk that builds the road, so retuning the
        /// road moves both together. Returns the middle of the course if the name is not there, which
        /// is a visible wrong answer rather than a spawn at the origin.</para>
        /// </summary>
        private static float ViewpointDistance(RoadCourse course, string name)
        {
            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];

                if (feature.Kind == RoadFeatureKind.Viewpoint && feature.Name == name)
                {
                    return feature.StartDistance;
                }
            }

            Debug.LogWarning($"[Horizon] No viewpoint named '{name}' on this course, so the spawn that "
                             + "wanted it has been put halfway along instead.");

            return course.PlannedLength * 0.5f;
        }

        private static float HighestDistance(IRoadPath path)
        {
            float best = 0f;
            float highest = float.MinValue;

            for (float at = 0f; at <= path.Length; at += 10f)
            {
                float y = path.GetPositionAtDistance(at).y;
                if (y > highest)
                {
                    highest = y;
                    best = at;
                }
            }

            return best;
        }

        private static void BuildBootstrapScene(IReadOnlyList<SpawnPoint> spawns)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("GameBootstrap");
            GameBootstrap bootstrap = root.AddComponent<GameBootstrap>();
            DriveInputRouter router = root.AddComponent<DriveInputRouter>();
            DriveDebugOverlay overlay = root.AddComponent<DriveDebugOverlay>();
            QualityDirector quality = root.AddComponent<QualityDirector>();

            BuildQualityLevels(quality);

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
            var spawnNames = new string[spawns.Count];
            for (int i = 0; i < spawns.Count; i++)
            {
                spawnNames[i] = spawns[i].Name;
            }

            TouchUiSetup.UiParts ui = TouchUiSetup.Build(root, router, spawnNames);

            // Now that the start screen and the quality director exist, GameBootstrap can be told about
            // them. Wired explicitly rather than left to the FindFirstObjectByType fallbacks in Awake —
            // those are there for a scene somebody assembled by hand, not for generated output.
            HorizonAssetUtility.Configure(bootstrap, serialized =>
            {
                serialized.FindProperty("worldSceneName").stringValue = WorldSceneName;
                serialized.FindProperty("inputRouter").objectReferenceValue = router;
                serialized.FindProperty("qualityDirector").objectReferenceValue = quality;
                serialized.FindProperty("startScreen").objectReferenceValue = ui.StartScreen;
            });

            HorizonAssetUtility.Configure(ui.StartScreen, serialized =>
                serialized.FindProperty("quality").objectReferenceValue = quality);

            // The table itself goes onto the PauseMenu the UI build just created.
            if (ui.Menu != null)
            {
                HorizonAssetUtility.Configure(ui.Menu, serialized =>
                {
                    SerializedProperty array = serialized.FindProperty("spawnPoints");
                    array.arraySize = spawns.Count;

                    for (int i = 0; i < spawns.Count; i++)
                    {
                        SerializedProperty element = array.GetArrayElementAtIndex(i);
                        element.FindPropertyRelative("Name").stringValue = spawns[i].Name;
                        element.FindPropertyRelative("Position").vector3Value = spawns[i].Position;
                        element.FindPropertyRelative("Rotation").quaternionValue = spawns[i].Rotation;
                    }
                });
            }

            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
        }

        /// <summary>
        /// The three quality settings, as the numbers they actually move.
        ///
        /// <para>Balanced is the game as it has always been — 650/820/220 on the streamer and the full
        /// pool of 96 traffic cars are the values those components carry as their own defaults, so the
        /// middle setting changes nothing and the other two are honestly a step either side of it.</para>
        ///
        /// <para>Low pulls the streaming radius in first, which the build log already names as the lever
        /// to reach for, then caps traffic to a quarter, drops the sun's shadow pass and asks for 30 fps.
        /// Together those are most of the frame budget on a weak phone, and none of them touches the URP
        /// asset — see <see cref="QualityDirector"/> for why that matters.</para>
        /// </summary>
        private static void BuildQualityLevels(QualityDirector quality)
        {
            HorizonAssetUtility.Configure(quality, serialized =>
            {
                SerializedProperty levels = serialized.FindProperty("levels");
                levels.arraySize = 3;

                void Set(
                    int index, string name,
                    float streamLoad, float streamUnload, float streamMargin,
                    int trafficBudget, float trafficLoad, float trafficRecycle,
                    bool shadows, bool exhaust, bool tyreSmoke, bool airRush, int frameRate)
                {
                    SerializedProperty level = levels.GetArrayElementAtIndex(index);
                    level.FindPropertyRelative("Name").stringValue = name;
                    level.FindPropertyRelative("StreamLoadRadius").floatValue = streamLoad;
                    level.FindPropertyRelative("StreamUnloadRadius").floatValue = streamUnload;
                    level.FindPropertyRelative("StreamPhysicsMargin").floatValue = streamMargin;
                    level.FindPropertyRelative("TrafficBudget").intValue = trafficBudget;
                    level.FindPropertyRelative("TrafficLoadRadius").floatValue = trafficLoad;
                    level.FindPropertyRelative("TrafficRecycleRadius").floatValue = trafficRecycle;
                    level.FindPropertyRelative("SunShadows").boolValue = shadows;
                    level.FindPropertyRelative("ExhaustParticles").boolValue = exhaust;
                    level.FindPropertyRelative("TyreSmokeParticles").boolValue = tyreSmoke;
                    level.FindPropertyRelative("AirRushParticles").boolValue = airRush;
                    level.FindPropertyRelative("TargetFrameRate").intValue = frameRate;
                }

                // Low keeps the tyre smoke while losing the exhaust, which looks like the wrong way
                // round until you ask what each one is for. The tailpipe plume is atmosphere and runs
                // constantly; tyre smoke is feedback — it is how the player sees that the car has let
                // go — and taking that away on a weak phone would remove information rather than
                // decoration. It also costs nothing at all until something actually slides.
                Set((int)QualityPreset.Low, "Low",
                    380f, 500f, 140f, 24, 320f, 460f, false, false, true, false, 30);

                Set((int)QualityPreset.Balanced, "Balanced",
                    650f, 820f, 220f, 56, 650f, 900f, true, true, true, true, 60);

                Set((int)QualityPreset.High, "High",
                    820f, 1000f, 260f, TrafficPoolSize, 800f, 1050f, true, true, true, true, 60);
            });
        }

        /// <summary>
        /// The acceleration lane that carries the link road onto the motorway.
        ///
        /// <para>Everything about where it goes is <b>measured off the two roads</b> rather than written
        /// down here: which side the link lies on, which way a driver coming off it is pointing, where
        /// along the carriageway its end cap falls, and how far out its paving reaches. Every one of
        /// those is derivable, every one of them moves when either course is retuned, and a literal for
        /// any of them is a wedge that quietly ends up on the wrong side or three hundred metres up the
        /// road the next time somebody changes a radius.</para>
        /// </summary>
        private static void BuildMotorwayMerge(
            Transform parent,
            out float capOnMedian,
            out float mergeOnMedian,
            IRoadPath motorwayPath,
            IRoadPath carriageway,
            in RoadShape motorwayShape,
            in RoadShape linkShape,
            IRoadPath linkPath,
            PrototypeMaterials materials)
        {
            // The link's end cap, found on the carriageway rather than assumed to be at JunctionDistance.
            // That distance is measured along the *median* line, and an offset path through a curve has a
            // different arc length — using it directly would put the wedge tens of metres off through the
            // interchange's bend.
            Vector3 cap = linkPath.GetPositionAtDistance(0f);
            float atDistance = NearestDistanceOn(carriageway, cap);

            // The same two places expressed on the median line, because that is the frame the lane graph
            // is built in: TrafficNetworkBuilder samples the median and offsets to a carriageway, so a
            // distance taken along the carriageway would be several metres out by the time it had been
            // through the interchange's bend.
            capOnMedian = NearestDistanceOn(motorwayPath, cap);

            // Assigned up front so the empty-mesh path below still leaves both defined. A negative merge
            // distance is what tells TrafficNetworkBuilder there is no interchange to cut lanes for.
            mergeOnMedian = -1f;

            Vector3 right = carriageway.GetRightAtDistance(atDistance);
            Vector3 forward = carriageway.GetDirectionAtDistance(atDistance);

            Vector3 toLink = cap - carriageway.GetPositionAtDistance(atDistance);
            toLink.y = 0f;

            float side = Mathf.Sign(Vector3.Dot(toLink, right));

            // A driver reaches the cap travelling *down* the link — from the pass towards the motorway —
            // so their heading there is the reverse of the course's own tangent at distance zero.
            Vector3 arriving = -linkPath.GetDirectionAtDistance(0f);
            float travelSign = Mathf.Sign(Vector3.Dot(arriving, forward));

            // Out to the link's far paved edge, so the wedge covers the ramp's full width and the strip
            // of gravel between the two roads with it. That strip is the whole complaint.
            float lateral = Mathf.Abs(Vector3.Dot(toLink, right));
            float mouthWidth = lateral + linkShape.HalfWidth - motorwayShape.HalfWidth;

            var buffer = new VegetationMeshBuffer(MotorwayMergeBuilder.MergeSubmeshCount);

            MotorwayMergeBuilder.Append(
                carriageway, motorwayShape, atDistance, mouthWidth, side, travelSign, buffer);

            buffer.MergeTinted(MotorwayMergeBuilder.SurfaceTints());

            var used = new List<int>(MotorwayMergeBuilder.MergeSubmeshCount);
            Mesh mesh = buffer.ToMesh("MotorwayMergeMesh", used);
            if (mesh == null)
            {
                Debug.LogWarning("[Horizon] The motorway merge came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/MotorwayMergeMesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = materials.RoadTint;
            }

            GameObject merge = CreateMeshObject(parent, "MotorwayMerge", mesh, meshMaterials);

            WorldChunk chunk = merge.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            // Measured through the carriageway and mapped back, rather than added to capOnMedian: the
            // two paths have different arc lengths through a bend, and this is the point three lanes are
            // going to be cut at.
            Vector3 mergeEnd = carriageway.GetPositionAtDistance(
                Mathf.Clamp(atDistance + travelSign * MotorwayMergeBuilder.TotalLength,
                    0f, carriageway.Length));

            mergeOnMedian = NearestDistanceOn(motorwayPath, mergeEnd);

            ValidateMergeSeam(carriageway, motorwayShape, linkPath, linkShape, atDistance, side, mouthWidth);

            Debug.Log($"[Horizon] Motorway merge: {MotorwayMergeBuilder.TotalLength:0} m of acceleration "
                      + $"lane on the {(side < 0f ? "left" : "right")} of the carriageway, opening "
                      + $"{mouthWidth:0.0} m wide at the link's cap and closing to nothing. The ramp's "
                      + $"paving and the {lateral - motorwayShape.HalfWidth - linkShape.HalfWidth:0.0} m "
                      + "of gravel between the two roads are both under it now.");
        }

        /// <summary>
        /// <summary>
        /// Bakes the water into something the running game can test a car against.
        ///
        /// <para>The runtime cannot ask <see cref="MountainField"/> anything — it is a build-time object
        /// that takes a fifth of a second to construct and is not in a player build at all — so the two
        /// numbers that decide whether a car is in the water travel into the scene as plain data.</para>
        /// </summary>
        private static void BuildWaterHazard(Transform parent, WaterBody[] waters)
        {
            if (waters.Length == 0)
            {
                return;
            }

            var hazardObject = new GameObject("WaterHazard");
            hazardObject.transform.SetParent(parent, false);

            var patches = new List<WaterHazard.Patch>(waters.Length);

            for (int i = 0; i < waters.Length; i++)
            {
                WaterBody body = waters[i];

                patches.Add(new WaterHazard.Patch
                {
                    Name = body.Name,
                    Spine = (Vector2[])body.Spine.Clone(),
                    HalfWidth = body.HalfWidth,
                    SurfaceY = body.SurfaceY,
                });
            }

            WaterHazard hazard = hazardObject.AddComponent<WaterHazard>();
            hazard.SetPatches(patches);
            EditorUtility.SetDirty(hazard);

            Debug.Log($"[Horizon] Water hazard: {patches.Count} bodies baked into the scene. A car under "
                      + "any of them ploughs, and is put back on the nearest road.");
        }

        /// <summary>
        /// Checks that no body of water reaches a road.
        ///
        /// <para><b>The failure this exists for is a drowned carriageway, and it is silent.</b> A
        /// surface is solved from the rim of its own basin, which says nothing at all about what else
        /// is nearby; a lake placed a little too close to the pass would come out perfectly level,
        /// perfectly shaded, and half a metre over the tarmac. Nothing else in the build would object
        /// — the road mesh is laid from the course, not from the ground.</para>
        ///
        /// <para>Sampled along every carriageway rather than at the water's edge: the question is not
        /// how big the lake is, it is whether any drivable metre of road sits below a surface.</para>
        /// </summary>
        private static void ValidateWater(
            WaterBody[] waters,
            MountainField field,
            MountainField.FieldRoad[] roads,
            IReadOnlyList<TownBuild> towns)
        {
            // Three metres of the road standing clear. Less than that and a verge is a beach.
            const float clearance = 3f;

            float worst = float.MaxValue;
            string worstWater = null;
            string worstWhere = null;

            string overWater = null;
            string overWhere = null;

            void Walk(IRoadPath road, string what, RoadCourse course = null)
            {
                if (road == null)
                {
                    return;
                }

                for (float at = 0f; at <= road.Length; at += 10f)
                {
                    // A span is allowed to cross water, and both rivers exist precisely because one
                    // does. What is checked below is the road that is *on the ground*.
                    if (course != null && course.IsBridged(at))
                    {
                        continue;
                    }

                    Vector3 on = road.GetPositionAtDistance(at);

                    for (int w = 0; w < waters.Length; w++)
                    {
                        WaterBody body = waters[w];

                        if (!body.Near(on.x, on.z) || body.DistanceOutside(on.x, on.z) > body.BankEase)
                        {
                            continue;
                        }

                        // Standing over open water, at any height at all.
                        //
                        // <b>Height clearance does not cover this and the difference is not academic.</b>
                        // The first Talheimer See passed the check below at 4.3 m and came out with a
                        // street and six houses on a plinth in the middle of it: the basin was dug out
                        // from under the village, the paving stayed where the layout put it, and every
                        // number in the build was healthy. A viaduct is allowed to cross water. A village
                        // street is not, because nothing built it a deck.
                        if (overWater == null && body.DistanceOutside(on.x, on.z) <= 0f)
                        {
                            overWater = body.Name;
                            overWhere = $"{at:0} m along {what}";
                        }

                        float above = on.y - body.SurfaceY;
                        if (above < worst)
                        {
                            worst = above;
                            worstWater = body.Name;
                            worstWhere = $"{at:0} m along {what}";
                        }
                    }
                }
            }

            for (int r = 0; r < roads.Length; r++)
            {
                Walk(roads[r].Path, $"trunk road {r}", roads[r].Course);
            }

            // And every street in every town.
            //
            // Leaving these out is what let a lake drown Talheim: the check walked the four trunk roads,
            // said nothing, and the build finished with a street and half a dozen houses standing in
            // water. The towns are where the ground is flattest and therefore where a lake is most
            // tempting to place, so they are exactly the roads this needs to see.
            if (towns != null)
            {
                for (int t = 0; t < towns.Count; t++)
                {
                    StreetNetwork network = towns[t].Network;
                    if (network == null)
                    {
                        continue;
                    }

                    for (int e = 0; e < network.Edges.Count; e++)
                    {
                        Walk(network.Edges[e].Path, $"{towns[t].Name} street {e}");
                    }
                }
            }

            if (overWater != null)
            {
                Debug.LogWarning(
                    $"[Horizon] '{overWater}' has open water under the carriageway at {overWhere}. "
                    + "The road is standing over a basin that was dug out from beneath it, whatever "
                    + "height it is standing at. Move the body clear of the road in plan.");
            }

            if (worstWater == null)
            {
                Debug.Log("[Horizon] Water clearance: no body reaches a road at all.");
                return;
            }

            if (worst < clearance)
            {
                Debug.LogWarning(
                    $"[Horizon] '{worstWater}' comes within {worst:0.0} m of the carriageway at "
                    + $"{worstWhere}, and {clearance:0} m is the least a road may stand clear of water. "
                    + "Move it, shrink it, or deepen the basin it was solved against.");
                return;
            }

            Debug.Log($"[Horizon] Water clearance: the nearest road stands {worst:0.0} m above "
                      + $"'{worstWater}', at {worstWhere}.");
        }

        /// <summary>
        /// Measures the step a wheel crosses where the ramp's paving meets the merge.
        ///
        /// <para><b>This is the one seam in the interchange that nothing else can check.</b> The wedge's
        /// inner edge is exact by construction — it is built in the carriageway's own frame, so it
        /// cannot disagree with the road it joins. Its outer edge at the cap is a different matter: that
        /// is where two separately graded courses meet, and the whole reason the previous fix in this
        /// area existed was that they were 0.45 m apart in height without anything noticing. A step there
        /// is a raycast wheel dropping at whatever speed the ramp delivers.</para>
        ///
        /// <para>Sampled across the ramp's paved width rather than at its centre, because the two roads
        /// meet along a line and a difference in cross-fall shows at the edges and nowhere else.</para>
        /// </summary>
        private static void ValidateMergeSeam(
            IRoadPath carriageway,
            in RoadShape motorwayShape,
            IRoadPath linkPath,
            in RoadShape linkShape,
            float atDistance,
            float side,
            float mouthWidth)
        {
            Vector3 capCenter = linkPath.GetPositionAtDistance(0f);
            Vector3 capRight = linkPath.GetRightAtDistance(0f);

            Vector3 wayCenter = carriageway.GetPositionAtDistance(atDistance);
            Vector3 wayRight = carriageway.GetBankedRightAtDistance(
                atDistance, motorwayShape.MaxBankDegrees, motorwayShape.FullBankRadius);

            Vector3 up = Vector3.Cross(carriageway.GetDirectionAtDistance(atDistance), wayRight).normalized;
            if (up.y < 0f)
            {
                up = -up;
            }

            float worst = 0f;
            float worstAcross = 0f;

            const int samples = 17;
            for (int i = 0; i < samples; i++)
            {
                // Across the ramp's paving, from one edge to the other.
                float across = Mathf.Lerp(-linkShape.HalfWidth, linkShape.HalfWidth, i / (float)(samples - 1));

                // The ramp's own surface there. Its rings carry a crown, but the seam is about the two
                // surfaces' base planes, which is what the edges of both sit on.
                Vector3 onLink = capCenter + capRight * across + Vector3.up * linkShape.SurfaceLift;

                // The wedge at the same point, measured out from the carriageway's paved edge along its
                // own banked frame.
                float outward = Mathf.Abs(Vector3.Dot(onLink - wayCenter, wayRight));
                float width = outward - motorwayShape.HalfWidth;

                if (width < 0f || width > mouthWidth)
                {
                    continue;
                }

                Vector3 onWedge = wayCenter
                                  + wayRight * (side * (motorwayShape.HalfWidth + width))
                                  + up * (motorwayShape.SurfaceLift + MotorwayMergeBuilder.Lift);

                float step = Mathf.Abs(onWedge.y - onLink.y);
                if (step > worst)
                {
                    worst = step;
                    worstAcross = across;
                }
            }

            // A tenth of the tyre's radius. Below that a raycast wheel rides over it as a bump; above it
            // the suspension takes the whole step in one physics tick.
            const float tolerable = 0.04f;

            if (worst > tolerable)
            {
                Debug.LogWarning(
                    $"[Horizon] The ramp meets the merge with a {worst * 100f:0} cm step, worst "
                    + $"{worstAcross:0.0} m across its width. A wheel crosses that at ramp speed. The two "
                    + "roads are graded by different courses — see AutobahnCourse.MotorwayGradeAtJunction, "
                    + "which is what keeps them level with each other.");
                return;
            }

            Debug.Log($"[Horizon] Merge seam: the ramp meets the acceleration lane within "
                      + $"{worst * 1000f:0} mm across its whole width.");
        }

        /// <summary>
        /// Distance along a path of the point nearest <paramref name="to"/>. Coarse sweep, then halving
        /// windows — the same shape as <c>PauseMenu.TryNearestRoad</c>, and for the same reason: this
        /// runs once at build time, so a few thousand distance checks cost nothing, and sampling every
        /// metre of eight kilometres would be a hundred thousand.
        /// </summary>
        private static float NearestDistanceOn(IRoadPath path, Vector3 to)
        {
            const float coarse = 20f;

            float best = 0f;
            float bestSqr = float.MaxValue;

            int steps = Mathf.Max(1, Mathf.CeilToInt(path.Length / coarse));
            for (int i = 0; i <= steps; i++)
            {
                float at = path.Length * i / steps;
                float sqr = (path.GetPositionAtDistance(at) - to).sqrMagnitude;

                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = at;
                }
            }

            for (float window = coarse * 0.5f; window > 0.05f; window *= 0.5f)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float at = Mathf.Clamp(best + window * sign, 0f, path.Length);
                    float sqr = (path.GetPositionAtDistance(at) - to).sqrMagnitude;

                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = at;
                    }
                }
            }

            return best;
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

            /// <summary>
            /// Which of this town's junctions have lights, and which phase each approach is on.
            ///
            /// <para>Held here because <b>two</b> bakes read it and they must read the same one: the
            /// street mesh builds the heads from it, and the traffic bake writes the phase onto the lanes
            /// from it. Recomputing it in the second place would be silent when it drifted — heads over
            /// lanes that obey a different light.</para>
            /// </summary>
            public TrafficSignalPlan Signals;

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

                // After the trims, because a signal head stands at the trim point and the stop line is
                // painted from it. Talheim gets an empty plan without being asked about: its layout
                // holds no boulevard and no city street, which is the rule, so a village is unlit by
                // being a village rather than by a flag somebody has to remember to set.
                Signals = TrafficSignalPlan.Build(network),
            };
        }

        /// <summary>Everything that needs the finished terrain: blocks, quarters and plots.</summary>
        private static void PlanTown(
            TownBuild town,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials,
            List<MeshRenderer> signalRenderers,
            List<int> signalSlotStart,
            List<int> signalSlots,
            List<int> signalLenses)
        {
            BuildStreetMeshes(town.StreetsRoot, town.Network, town.Trunk, RoadShape.Default,
                town.Shape, field, terrainShape, materials, town.Name, town.Signals,
                signalRenderers, signalSlotStart, signalSlots, signalLenses);

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
        private static Light[] CreateHeadlights(Transform parent, in CarMeshBuilder.CarProfile profile)
        {
            // Seated off the profile rather than at the fastback's (±0.47, 0.20, 2.05): a van's face is
            // most of a metre taller, and a beam emitted at a fastback's lamp height starts inside it.
            Vector3[] offsets = CarMeshBuilder.HeadlightSeats(profile);

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
        /// <summary>
        /// The world's own response to speed: the fog layer and the grit hanging in the air.
        ///
        /// <para>Parented to the Atmosphere object rather than to the car, and that is the point. The
        /// grit is emitted in world space and left standing still, so it is the car passing it that
        /// makes the motion — hang it off the vehicle and it would travel along and read as a effect
        /// stuck to the windscreen.</para>
        ///
        /// <para>The vehicle reference is left empty deliberately: the car the player drives is spawned
        /// by the bootstrap and swapped by the garage, so the component finds it at runtime rather than
        /// holding a reference to whichever body happened to exist at build time.</para>
        /// </summary>
        private static void BuildSpeedAtmosphere(
            Transform parent, TimeOfDayController timeOfDay, PrototypeMaterials materials)
        {
            var rushObject = new GameObject("AirRush");
            rushObject.transform.SetParent(parent, false);

            ParticleSystem particles = rushObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = particles.main;
            main.duration = 1f;
            main.loop = true;

            // Long enough that a speck placed 75 m ahead is still there when the car reaches it: at
            // 65 m/s that is a little over a second, and the rest is the tail as it falls behind.
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);

            // Almost still. The whole effect is the car's own motion, so anything more than a drift
            // here starts competing with it.
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = new Color(0.9f, 0.9f, 0.92f, 0.45f);
            main.gravityModifier = 0.02f;

            // 90 a second living up to 2.2 seconds is 198 in the air at once.
            main.maxParticles = 220;

            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = false;

            // Faded in and out rather than popped: a speck that appears in front of the car is a speck
            // the eye reads as a glitch, however brief.
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
                    new GradientAlphaKey(1f, 0.25f),
                    new GradientAlphaKey(1f, 0.7f),
                    new GradientAlphaKey(0f, 1f),
                });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = rushObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = materials.AirRush;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            SpeedAtmosphere speed = parent.gameObject.AddComponent<SpeedAtmosphere>();
            HorizonAssetUtility.Configure(speed, serialized =>
            {
                serialized.FindProperty("atmosphere").objectReferenceValue = timeOfDay;
                serialized.FindProperty("rush").objectReferenceValue = particles;
            });

            HorizonAssetUtility.AssertReferenceAssigned(speed, "atmosphere");
            HorizonAssetUtility.AssertReferenceAssigned(speed, "rush");
        }

        /// <summary>
        /// The single world-space emitter every tyre smokes into.
        ///
        /// <para>Not parented per wheel and not four systems — see <see cref="TyreSmoke"/> for why.
        /// Emission is off because the component emits by hand at the contact patches; leaving the rate
        /// on would put a second, wheel-less plume under the car.</para>
        /// </summary>
        private static void CreateTyreSmoke(Transform parent, PrototypeMaterials materials)
        {
            var emitterObject = new GameObject("TyreSmoke");
            emitterObject.transform.SetParent(parent, false);
            emitterObject.transform.localPosition = Vector3.zero;

            ParticleSystem particles = emitterObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = particles.main;
            main.duration = 1f;
            main.loop = true;

            // Long-lived, because tyre smoke hangs. A drift that leaves nothing behind it reads as a
            // puff of dust, and the trail down the road is most of the effect.
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2f);

            // Slow, and mostly supplied per-particle by the component. What is left here is the spread.
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.9f);
            main.startSize = 0.6f;
            main.startColor = new Color(1f, 1f, 1f, 0.5f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            // Rises, but barely: hot rubber smoke drifts up far more slowly than it spreads out.
            main.gravityModifier = -0.02f;
            // 260, and it is the arithmetic rather than a round number: four tyres alight at 35 a
            // second each, living 1.6 seconds on average, is 224 in the air at once. Under that and the
            // cloud thins out at exactly the moment the car is most sideways.
            main.maxParticles = 260;

            // World space is not optional here. The whole point is that the cloud stays on the road
            // where it was made while the car drives away from it.
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = false;

            // Billows out as it ages, which is what separates smoke from dust.
            ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.EaseInOut(0f, 0.55f, 1f, 2.6f));

            ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.86f, 0.86f, 0.88f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0f, 1f),
                });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = emitterObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = materials.TyreSmoke;
            renderer.sortingFudge = 20f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            TyreSmoke tyreSmoke = emitterObject.AddComponent<TyreSmoke>();

            VehicleController vehicle = parent.GetComponent<VehicleController>();
            if (vehicle != null)
            {
                HorizonAssetUtility.Configure(tyreSmoke, serialized =>
                    serialized.FindProperty("vehicle").objectReferenceValue = vehicle);

                HorizonAssetUtility.AssertReferenceAssigned(tyreSmoke, "vehicle");
            }
        }

        private static void CreateExhaustEmitters(
            Transform parent, PrototypeMaterials materials, in CarMeshBuilder.CarProfile profile)
        {
            Vector3[] outlets = CarMeshBuilder.ExhaustOutletsFor(profile);

            for (int i = 0; i < outlets.Length; i++)
            {
                // Numbered rather than named left and right: a car may have one pipe, two or four, and
                // two objects called Exhaust_L under one parent is a wiring mistake waiting to be made.
                var emitterObject = new GameObject($"Exhaust_{i}");
                emitterObject.transform.SetParent(parent, false);
                emitterObject.transform.localPosition = outlets[i];
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
            string what,
            TrafficSignalPlan signals,
            List<MeshRenderer> signalRenderers,
            List<int> signalSlotStart,
            List<int> signalSlots,
            List<int> signalLenses)
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

                // Lane lines, on the wide kinds only. Between the trim points rather than past them:
                // a dash running into a junction pad is a dash lying across a turning circle.
                TownStreetBuilder.AppendMarkings(
                    edge.Path, edge.Shape, edge.Kind,
                    edge.TrimStart, edge.Length - edge.TrimEnd,
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

            // Traffic lights. The masts and heads go into the buffer above and are merged away to
            // nothing; only the lenses need a renderer of their own, because they change colour.
            var lensBuffer = new VegetationMeshBuffer(TrafficSignalMeshes.LensSubmeshCount);

            var signalViewFrom = Vector3.zero;
            var signalViewAt = Vector3.zero;
            float widestApproach = -1f;

            if (signals != null)
            {
                for (int i = 0; i < signals.ApproachCount; i++)
                {
                    signals.GetApproach(i, out int node, out int edge, out int group);
                    TrafficSignalMeshes.AppendApproach(
                        network, node, edge, group, buffer, lensBuffer,
                        out Vector3 from, out Vector3 look);

                    // The widest approach in the town, so the preview station lands on the boulevard
                    // rather than on whichever grid street happened to be listed first. Only from an
                    // approach that actually produced geometry — AppendApproach hands back zeroes for an
                    // edge with no path, and a station built from those looks at the world origin from
                    // the world origin, which LookRotation cannot even express.
                    float width = network.Edges[edge].HalfWidth;
                    if (width > widestApproach && from != look)
                    {
                        widestApproach = width;
                        signalViewFrom = from;
                        signalViewAt = look;
                    }
                }
            }

            // Five categories, one draw call. Surface, kerb, footway, verge and paint were five flat
            // untextured materials, which is five draw calls per town for information that is only ever
            // a colour — the same case the buildings and the terrain already answered this way.
            buffer.MergeTinted(TownStreetBuilder.SurfaceTints());

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

            BuildSignalLenses(parent, lensBuffer, materials, what,
                signalRenderers, signalSlotStart, signalSlots, signalLenses);

            if (widestApproach > 0f)
            {
                // A camera station where a driver meets the town's most important light. Placed from the
                // geometry rather than from typed coordinates, for the reason TrafficView is aimed at an
                // agent: a station that cannot follow the bake is a station that quietly ends up
                // photographing a field.
                var station = new GameObject("SignalView");
                station.transform.SetParent(parent, false);
                station.transform.SetPositionAndRotation(
                    signalViewFrom,
                    Quaternion.LookRotation(signalViewAt - signalViewFrom, Vector3.up));
            }

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

        /// <summary>
        /// Hangs the signal lenses on an object of their own.
        ///
        /// <para><b>Not on the street mesh, and that is the whole reason this method exists.</b> The
        /// street object carries <c>SetBounds(centre, 100000f)</c> so a town seen from the pass above is
        /// never streamed out from under itself — which means every submesh on it is submitted whenever
        /// any part of the town is in frustum, from three kilometres away. Six lens submeshes on that
        /// renderer would be six draw calls paid for from the top of the mountain. On their own object
        /// with their own bounds, they are culled like anything else.</para>
        /// </summary>
        private static void BuildSignalLenses(
            Transform parent,
            VegetationMeshBuffer lensBuffer,
            PrototypeMaterials materials,
            string what,
            List<MeshRenderer> signalRenderers,
            List<int> signalSlotStart,
            List<int> signalSlots,
            List<int> signalLenses)
        {
            if (lensBuffer.IsEmpty)
            {
                return;
            }

            var used = new List<int>(TrafficSignalMeshes.LensSubmeshCount);
            Mesh mesh = lensBuffer.ToMesh(what + "SignalsMesh", used);
            if (mesh == null)
            {
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{what}SignalsMesh.asset");

            // Every slot starts dark. TrafficSignals lights the right ones on its first frame, and a
            // mesh built with the lit material in it would show all three colours at once in any shot
            // taken before that.
            var lensMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                lensMaterials[i] = materials.SignalDark;
            }

            // No collider: a lens is 20 cm of quad three metres in the air, and the mast under it is part
            // of the street mesh, which has one.
            GameObject lensObject = CreateMeshObject(
                parent, what + "Signals", mesh, lensMaterials, addCollider: false);

            var lensChunk = lensObject.AddComponent<WorldChunk>();
            lensChunk.RecalculateBounds();
            EditorUtility.SetDirty(lensChunk);

            MeshRenderer lensRenderer = lensObject.GetComponent<MeshRenderer>();
            lensRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            signalRenderers.Add(lensRenderer);

            // Which slot ended up holding which group's red, amber or green. Looked up rather than
            // assumed, because ToMesh compacts empty submeshes away — the same reason the traffic pool
            // asks where its headlights went instead of using a literal.
            for (int i = 0; i < used.Count; i++)
            {
                signalSlots.Add(i);
                signalLenses.Add(used[i]);
            }

            signalSlotStart.Add(signalSlots.Count);

            Debug.Log($"[Horizon] {what} signals: {mesh.triangles.Length / 3} lens triangles in "
                      + $"{used.Count} draw call(s), on their own chunk.");
        }

        /// <summary>
        /// The materials for the street mesh, in the order its submeshes survived compaction.
        ///
        /// <para>After <c>MergeTinted</c> there is normally one — every tinted category rides in the
        /// vertices on <c>Horizon/VertexTintLit</c>. The switch survives because compaction is what
        /// decides which slot is which, and a mesh with nothing tinted in it would still need naming.</para>
        /// </summary>
        private static Material[] StreetMaterials(PrototypeMaterials materials, List<int> used)
        {
            var result = new Material[used.Count];

            for (int i = 0; i < used.Count; i++)
            {
                result[i] = materials.TerrainTint;
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
            int drowned = 0;

            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += step)
            {
                for (float across = shape.AcrossInner; across <= shape.AcrossOuter; across += step)
                {
                    Vector3 point = TownShape.ToWorld(path, shape, along, across);

                    float here = field.HeightAt(point.x, point.z);
                    float ahead = field.HeightAt(point.x + step, point.z);
                    float beside = field.HeightAt(point.x, point.z + step);

                    // Sea bed and shore are not ground this report has anything to say about.
                    //
                    // <b>A coastal town's basin deliberately reaches past its own shoreline</b> — see
                    // SeeburgCourse.Seaward for why it has to — so several hundred metres of every
                    // sweep here is under water, and the band between that and the promenade is the
                    // beach and the quay bank. Measured against the planned floor those are eleven
                    // metres of error and a forty-five percent slope, and both figures then trip the
                    // warnings below: 'the level samples are not reaching MountainField', about ground
                    // that was levelled correctly and then dug out on purpose.
                    //
                    // The shore test is the one TerrainTileBuilder already uses to decide where to tint
                    // the ground sand, so what is skipped here is exactly what comes out as beach.
                    // Talheim and Hochstadt skip nothing, because neither of them touches water.
                    if (field.IsUnderWater(point.x, point.z, here, 0.5f)
                        || field.IsShore(point.x, point.z, here, ShoreFreeboard, ShoreReach))
                    {
                        drowned++;
                        continue;
                    }

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
                      + $"{steepFraction * 100f:0.0} % of the basin over 8 %"
                      + (drowned > 0 ? $", {drowned} samples skipped as sea bed or shore." : "."));

            if (steepFraction > 0.06f || steepest > 0.30f)
            {
                // Where the worst sample is and what stands around it, because the sentence above names
                // two causes and cannot tell them apart. A profile across the point separates them at a
                // glance: level samples that never arrived leave the ground far below the planned floor
                // and following the hillside, while a pitch too coarse leaves it on the floor and
                // corrugated between the shelves. A third answer turns up as often as either — the point
                // is out on the skirt rings, which are meant to be steep and are not buildable ground in
                // the first place.
                Vector3 worst = TownShape.ToWorld(path, shape, steepestAlong, steepestAcross);
                float floor = TownShape.FloorHeight(path, shape, steepestAlong, steepestAcross)
                              - terrainShape.RoadShelfDrop;

                var profile = new System.Text.StringBuilder();
                for (float across = steepestAcross - step * 2f;
                     across <= steepestAcross + step * 2f;
                     across += step)
                {
                    Vector3 at = TownShape.ToWorld(path, shape, steepestAlong, across);
                    bool folded = TownShape.IsBeyondFold(path, shape, steepestAlong, across);
                    profile.Append($" {across:0}:{field.HeightAt(at.x, at.z):0.0}"
                                   + $"/{field.ShelfDistance(at.x, at.z):0}"
                                   + (folded ? "*" : string.Empty));
                }

                Debug.LogWarning(
                    "[Horizon] Town ground has too little buildable area. The level samples from "
                    + "TownShape.BuildLevelSamples are either not reaching MountainField, or their grid "
                    + "pitch is too coarse for the shelves to merge — it has to stay under twice "
                    + $"MountainField.Verge, which is {Mathf.Max(terrainShape.VergeWidth, terrainShape.CellSize * 2f):0} m."
                    + $" Worst point at ({worst.x:0}, {worst.z:0}), ground {field.HeightAt(worst.x, worst.z):0.0} m "
                    + $"against a planned floor of {floor:0.0} m, {field.Verge:0} m of verge. Profile "
                    + $"across (m:height/shelf distance, * = the mapping refused the sample as beyond "
                    + $"the fold):{profile}");
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
            List<int> townSlotGroups,
            IReadOnlyList<Bounds> waterBands,
            IReadOnlyList<MountainField.FieldRoad> otherRoads,
            LandRegion region,
            IRoadPath avenueRoad,
            IReadOnlyList<Vector3> forecourts)
        {
            // One region per settlement rather than one big box round the lot: the corridor is widened
            // where a town is, and a rectangle spanning both would drag in every tile of open country
            // between them. The water bands are more of the same, and there is a list of them rather
            // than one because the two seas are at opposite ends of the world and a box containing both
            // would contain everything in between.
            int bandCount = waterBands != null ? waterBands.Count : 0;
            var extraRegions = new Bounds[towns.Count + bandCount];
            for (int i = 0; i < towns.Count; i++)
            {
                extraRegions[i] = towns[i].Footprint;
            }

            for (int i = 0; i < bandCount; i++)
            {
                extraRegions[towns.Count + i] = waterBands[i];
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

            // The pass is handed in on its own because the tree line is measured against it — see the
            // note on the constructor. Every other road is in for its viewpoints, which have to be kept
            // clear of trees or they are lay-bys with a hedge in front of them.
            var vegetationContext = new VegetationContext(
                path, course, vegetationShape, settlements, otherRoads,
                region != null ? avenueRoad : null, forecourts);
            var vegetationTotal = new VegetationStats();
            int heaviestTile = 0;
            string heaviestTileName = "none";

            // How much of a tile came out as shore, read back off the finished mesh rather than counted
            // while it was built. The tint is one line inside the tile builder's own colour choice and
            // it would be a poor trade to grow that method's signature by an out-parameter to say so.
            static int CountShore(Mesh mesh)
            {
                Color32[] colours = mesh.colors32;
                int sand = 0;

                for (int i = 0; i < colours.Length; i += 3)
                {
                    Color32 colour = colours[i];

                    if (colour.r == TerrainTileBuilder.SandTint.r
                        && colour.g == TerrainTileBuilder.SandTint.g
                        && colour.b == TerrainTileBuilder.SandTint.b)
                    {
                        sand++;
                    }
                }

                return sand;
            }

            var townTotals = new TownStats[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                townTotals[i] = new TownStats();
            }

            int waterTiles = 0;
            int waterTriangleTotal = 0;
            int shoreTriangles = 0;
            int drownedTiles = 0;

            for (int i = 0; i < tiles.Count; i++)
            {
                TerrainTileKey key = tiles[i];
                string name = $"Terrain_{key.Column}_{key.Row}";

                GameObject tileObject;

                if (TerrainTileBuilder.IsDrowned(field, terrainShape, key))
                {
                    // Water only. The bed here is under eight metres of opaque surface, so the ground,
                    // its collider and everything that would have been scattered on it are all work
                    // done for a view nobody has. See TerrainTileBuilder.IsDrowned.
                    tileObject = new GameObject(name);
                    tileObject.transform.SetParent(terrainRoot.transform, false);
                    drownedTiles++;
                }
                else
                {
                    Mesh mesh = TerrainTileBuilder.BuildTile(key, field, terrainShape, name, region);
                    totalTriangles += mesh.triangles.Length / 3;
                    shoreTriangles += CountShore(mesh);

                    mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}.asset");

                    tileObject = CreateMeshObject(
                        terrainRoot.transform, name, mesh, new[] { materials.TerrainTint });
                }

                Mesh water = WaterTileBuilder.BuildTile(
                    key, field, terrainShape, field.Water, name + "_Water", out int waterTriangles);

                if (water != null)
                {
                    water = HorizonAssetUtility.ReplaceAsset(water, $"{GeneratedFolder}/{name}_Water.asset");

                    // No collider. What the car hits is the basin the terrain already carries; the
                    // surface is something to look at and something WaterHazard tests against by
                    // height, and a mesh collider on it would be a sheet of glass on every lake.
                    CreateMeshObject(tileObject.transform, name + "_Water", water,
                        new[] { materials.Water }, addCollider: false);

                    waterTiles++;
                    waterTriangleTotal += waterTriangles;
                }

                Mesh plants = VegetationBuilder.BuildTile(
                    key, field, terrainShape, vegetationShape, vegetationContext,
                    name + "_Plants", out VegetationStats stats, region);

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

            if (waterTiles > 0)
            {
                Debug.Log($"[Horizon] Water surfaces: {waterTriangleTotal} triangles over {waterTiles} "
                          + $"of {tiles.Count} tiles, on one shared material. Hung under the tiles they "
                          + "sit on, so they stream with them.");

                if (shoreTriangles > 0)
                {
                    Debug.Log($"[Horizon] Shoreline: {shoreTriangles} terrain triangles tinted sand, "
                              + $"{shoreTriangles * 100f / Mathf.Max(1, totalTriangles):0.0} % of the "
                              + "terrain — no extra draw call, no extra material.");
                }
                else
                {
                    Debug.LogWarning("[Horizon] Every body of water meets grass directly: not one terrain "
                                     + "triangle came out sand. Either the shore band is thinner than a "
                                     + "cell, or the banks are steeper than the height it reaches.");
                }
            }
            else if (field.Water.Count > 0)
            {
                Debug.LogWarning("[Horizon] There are bodies of water in the field and not one tile "
                                 + "carries a surface. Either every basin fell outside the terrain "
                                 + "corridor, or the surfaces are being solved above their own banks.");
            }

            Debug.Log($"[Horizon] Terrain: {tiles.Count} tiles of {tileSize:0} m, "
                      + $"{totalTriangles} triangles total, corridor {terrainShape.CorridorWidth:0} m "
                      + $"plus {tiles.Count - baseline} for the town basins and the sea band. "
                      + $"{drownedTiles} of them carry water and no ground.");

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
        private static TrafficNetwork BuildTraffic(
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
            float carriagewayOffset,
            int highwayEndTown,
            int highwayEndNode,
            IRoadPath link,
            RoadShape linkShape,
            float rampCapDistance,
            float rampMergeDistance,
            IRoadPath coast,
            RoadShape coastShape,
            int coastEndTown,
            int coastEndNode,
            IRoadPath country,
            RoadShape countryShape)
        {
            var networks = new StreetNetwork[towns.Count];

            // Parallel to the networks, and the *same instances* the street meshes built their heads
            // from. Recomputing them here would compile and would be silent when it drifted.
            var plans = new TrafficSignalPlan[towns.Count];

            for (int i = 0; i < towns.Count; i++)
            {
                networks[i] = towns[i].Network;
                plans[i] = towns[i].Signals;
            }

            if (networks.Length == 0 || networks[0].Edges.Count == 0)
            {
                return null;
            }

            // Generated, not Settings. It is a ScriptableObject like VehicleConfig, but it is derived
            // output rather than something anyone tunes — regenerate it and every edit is gone — so it
            // belongs where the meshes are and under the orphan report that watches them.
            TrafficNetwork routes = TrafficNetworkBuilder.Build(
                networks, trunk, trunkShape, highway, highwayShape, carriagewayOffset,
                highwayEndTown, highwayEndNode,
                link, linkShape, rampCapDistance, rampMergeDistance, plans,
                coast, coastShape, coastEndTown, coastEndNode,
                country, countryShape);
            routes = HorizonAssetUtility.ReplaceAsset(routes, GeneratedFolder + "/TrafficNetwork.asset");

            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.TrafficProfiles;

            var bodies = new Mesh[profiles.Length];
            var bodyTriangles = new int[profiles.Length];

            // Which submesh constants each body actually kept. Traffic bodies fold their glass into the
            // paint and drop the empty slot, so nothing below may assume where a lamp ended up.
            var bodySlots = new List<int>[profiles.Length];

            for (int i = 0; i < profiles.Length; i++)
            {
                bodySlots[i] = new List<int>(CarMeshBuilder.BodySubmeshCount);

                Mesh shape = CarMeshBuilder.BuildTrafficBody(profiles[i], bodySlots[i]);
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

                List<int> slots = bodySlots[i % bodies.Length];

                MeshRenderer renderer = carObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = TrafficMaterials(
                    materials, slots, materials.TrafficBodies[i % materials.TrafficBodies.Length]);

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

                // Looked up, not assumed. These were the literals 2 and 3, which was true while the body
                // kept all five of its submeshes — and folding the glass away moves every slot behind it
                // down one, so the fixed version would light the taillights as headlights and the wheels
                // as taillights. Nothing would have said so: a build log cannot see a car that is wrong
                // after dark.
                int headlight = slots.IndexOf(CarMeshBuilder.HeadlightSubmesh);
                int taillight = slots.IndexOf(CarMeshBuilder.TaillightSubmesh);

                if (headlight >= 0 || taillight >= 0)
                {
                    litRenderers.Add(renderer);

                    if (headlight >= 0)
                    {
                        litSlots.Add(headlight);
                        litSlotGroups.Add((int)LitGroup.Headlights);
                    }

                    if (taillight >= 0)
                    {
                        litSlots.Add(taillight);
                        litSlotGroups.Add((int)LitGroup.Taillights);
                    }

                    litSlotStart.Add(litSlots.Count);
                }
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

            return routes;
        }

        /// <summary>
        /// Hangs the component that lights the lenses on the world root.
        ///
        /// <para>It is handed the routes rather than a cycle length of its own: the phase is a function
        /// on the baked asset, and both this and <c>TrafficDirector</c> evaluate it. A timer here that
        /// the director asked for would be the same number arrived at twice, with a frame between
        /// them.</para>
        /// </summary>
        private static void WireTrafficSignals(
            Transform parent,
            TrafficNetwork routes,
            PrototypeMaterials materials,
            List<MeshRenderer> renderers,
            List<int> slotStart,
            List<int> slots,
            List<int> lenses)
        {
            if (routes == null || renderers.Count == 0)
            {
                return;
            }

            var host = new GameObject("TrafficSignals");
            host.transform.SetParent(parent, false);

            TrafficSignals signals = host.AddComponent<TrafficSignals>();

            HorizonAssetUtility.Configure(signals, serialized =>
            {
                serialized.FindProperty("network").objectReferenceValue = routes;

                HorizonAssetUtility.SetObjectArray(serialized, "renderers", renderers.ToArray());
                SetIntArray(serialized, "slotStart", slotStart);
                SetIntArray(serialized, "slots", slots);
                SetIntArray(serialized, "slotLens", lenses);

                serialized.FindProperty("darkMaterial").objectReferenceValue = materials.SignalDark;
                HorizonAssetUtility.SetObjectArray(serialized, "lensMaterials", materials.SignalLenses);
            });

            HorizonAssetUtility.AssertReferenceAssigned(signals, "network");
        }

        /// <summary>
        /// The materials for one traffic car, in the order its submeshes survived compaction.
        ///
        /// <para>Chrome takes the tyre material rather than the rim's: the reduced body puts its wheels
        /// in that submesh, which the detail pass would otherwise have filled with exhausts. Four wheels
        /// for no extra draw call, because the slot is submitted regardless.</para>
        /// </summary>
        private static Material[] TrafficMaterials(
            PrototypeMaterials materials, List<int> slots, Material body)
        {
            var result = new Material[slots.Count];

            for (int i = 0; i < slots.Count; i++)
            {
                switch (slots[i])
                {
                    case CarMeshBuilder.HeadlightSubmesh:
                    case CarMeshBuilder.TaillightSubmesh:
                        result[i] = materials.WindowDay;
                        break;

                    case CarMeshBuilder.ChromeSubmesh:
                        result[i] = materials.Tyre;
                        break;

                    case CarMeshBuilder.GlassSubmesh:
                        result[i] = materials.CarGlass;
                        break;

                    default:
                        result[i] = body;
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// How many ambient cars there are. Fixed at build; the director never changes it.
        ///
        /// Ninety-six. Sixty-four was set for the motorway alone; the city added ten kilometres of
        /// street and two hundred lanes to the network, and the pool is shared — so the same number of
        /// cars spread over half again as much road made both places thinner.
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
        private const int TrafficPoolSize = 96;

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
        private static List<Vector3> DrawCallStations(
            RoadPath path, RoadPath motorway, RoadPath arterial, RoadPath seeburgAxis,
            RoadPath ebental, RoadPath kalkgrat, RoadPath meerenge)
        {
            var stations = new List<Vector3>(22);

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

            // The two towns that are not on the pass. Neither was sampled before, which meant the budget
            // was measured everywhere except the heaviest place in the world — Hochstadt's core carries
            // 239k triangles of tower and perimeter block, against Talheim's 49k.
            if (arterial != null)
            {
                stations.Add(arterial.GetPositionAtDistance(arterial.Length * 0.15f));
                stations.Add(arterial.GetPositionAtDistance(arterial.Length * 0.5f));
            }

            if (ebental != null)
            {
                // The crest and the lake loop. Open country, so these are the cheapest stations in the
                // report — which is the point of having them: if the Ebental road is anywhere near the
                // budget, something has gone wrong with the vegetation rather than with the buildings.
                stations.Add(ebental.GetPositionAtDistance(ebental.Length * 0.5f));
                stations.Add(ebental.GetPositionAtDistance(ebental.Length * 0.7f));
            }

            if (kalkgrat != null)
            {
                // The reveal and the middle of the descent. The first is the single most expensive frame
                // on the road — a tunnel mouth, a coastline, a strait and a bridge arriving together —
                // and it is the one place where being over budget would be felt rather than measured.
                stations.Add(kalkgrat.GetPositionAtDistance(
                    Mathf.Min(KalkgratCourse.RevealDistance, kalkgrat.Length)));
                stations.Add(kalkgrat.GetPositionAtDistance(kalkgrat.Length * 0.8f));
            }

            if (meerenge != null)
            {
                // The corniche, and the middle of the deck. The crossing never streams out, so the deck
                // is where its cost is unavoidable and therefore where it has to be counted.
                stations.Add(meerenge.GetPositionAtDistance(meerenge.Length * 0.25f));
                stations.Add(meerenge.GetPositionAtDistance(
                    Mathf.Min(MeerengeCourse.CrossingMiddle, meerenge.Length)));
            }

            if (seeburgAxis != null)
            {
                // On the waterfront at the harbour, and back at the market square. The first is the view
                // the town is built for; the second is where its own buildings stand thickest.
                stations.Add(seeburgAxis.GetPositionAtDistance(SeeburgCourse.BasinAlong));
                stations.Add(seeburgAxis.GetPositionAtDistance(SeeburgCourse.GatewayAlong)
                             + seeburgAxis.GetRightAtDistance(SeeburgCourse.GatewayAlong) * 150f);
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

            var callsAt = new int[stations.Count];

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

                callsAt[s] = calls;

                if (calls > worst)
                {
                    worst = calls;
                    worstStation = s;
                    worstChunks = resident;
                }
            }

            // The three heaviest, not only the heaviest. One number says whether the world is over
            // budget; three say <i>where</i>, which is the question anyone reading this is actually
            // asking — and with stations in every settlement it is now a number per place rather than a
            // number for the world.
            var order = new int[stations.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            System.Array.Sort(order, (a, b) => callsAt[b].CompareTo(callsAt[a]));

            var heaviest = new System.Text.StringBuilder();
            for (int i = 0; i < order.Length && i < 3; i++)
            {
                heaviest.Append(i == 0 ? " Heaviest: " : ", ");
                heaviest.Append(
                    $"({stations[order[i]].x:0}, {stations[order[i]].z:0}) {callsAt[order[i]]}");
            }

            Debug.Log($"[Horizon] Draw calls at loadRadius {loadRadius:0} m: worst of "
                      + $"{stations.Count} stations is {worst} over {worstChunks} chunks plus "
                      + $"{unchunked} always resident, at station {worstStation + 1} "
                      + $"({stations[worstStation].x:0}, {stations[worstStation].z:0}). "
                      + "Upper bound — no culling, no batcher merging. Confirm on device."
                      + heaviest + ".");

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
                // Scaled with the plot, because the mesh is: TownPlan.Plot.Scale multiplies every offset
                // in the recipe through PlantPlacement, so a collider box left at recipe size would put a
                // fifty-metre minaret behind a thirty-metre one you can drive through.
                float scale = plot.Scale;

                for (int b = 0; b < boxes.Length; b++)
                {
                    BuildingBox box = boxes[b];
                    BoxCollider collider = holder.AddComponent<BoxCollider>();
                    collider.center = new Vector3(
                        box.OffsetX * scale, box.Height * 0.5f * scale, box.OffsetZ * scale);
                    collider.size = new Vector3(
                        box.HalfWidth * 2f * scale, box.Height * scale, box.HalfDepth * 2f * scale);
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
            StreetNetwork network, RoadPath trunk, in RoadShape trunkShape, int entry = -1)
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

            int unreachable = CountUnreachable(network, entry);

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
                      + $"{byQuarter[(int)TownQuarter.Commercial]} commercial, "
                      + $"{byQuarter[(int)TownQuarter.Harbour]} harbour.");
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

        /// <summary>
        /// Nodes a breadth-first walk from the town's entrances never reaches.
        ///
        /// <para>An entrance is a node marked <c>OnTrunkRoad</c> — a bell-mouth onto the road the town
        /// hangs off — or <paramref name="entry"/>. The second exists because Hochstadt has no
        /// bell-mouths at all: its arterial is a coordinate axis rather than a paved road, and what
        /// arrives there is the motorway, at one gateway node. Without it the walk starts nowhere and
        /// reports the entire city as unreachable, which is true of the trunk road and says nothing
        /// about the city.</para>
        /// </summary>
        private static int CountUnreachable(StreetNetwork network, int entry = -1)
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

            if (entry >= 0 && entry < network.Nodes.Count && !seen[entry])
            {
                seen[entry] = true;
                queue.Enqueue(entry);
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

            // The region's own, counted apart. An avenue that failed to plant is invisible in a total —
            // five hundred trees against a hundred thousand is a rounding error — and it is the one
            // thing in the world whose absence nothing else would report.
            Debug.Log($"[Horizon] Ebental: {stats.Poplars} avenue poplars, {stats.FruitTrees} fruit trees, "
                      + $"{stats.WallRuns} field boundaries, {stats.HayBales} bales.");

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
        /// Builds the delineator posts. Resident with the road for the same reason the guard rails are:
        /// they are one draw call, and a stretch of road that loses its posts loses the thing the eye
        /// was reading its speed from.
        /// </summary>
        /// <summary>
        /// Every filling station in the world: one object, one mesh asset and one chunk each.
        ///
        /// <para><b>One chunk per station, not one for the lot.</b> They are kilometres apart, and a
        /// single chunk covering all of them has bounds the streamer can never put down — so every
        /// forecourt in the world would stay resident, invisibly, at a cost nothing in the build would
        /// report.</para>
        ///
        /// <para><b>Colliders on</b>, unlike the guard rails and the delineator posts. Those are markers
        /// and a car should pass through them rather than lean on them; a canopy column, a pump and a
        /// shop wall are things it should hit, and the apron is a surface it has to be able to drive onto
        /// at all.</para>
        ///
        /// <para>The lit slot is looked up in what <c>ToMesh</c> actually kept rather than taken from its
        /// constant — see <c>FuelStationBuilder.Build</c>, and <c>BuildHarbour</c>, which does the same
        /// for its lantern and for the same reason.</para>
        /// </summary>
        private static void BuildFuelStations(
            Transform parent,
            IReadOnlyList<FuelStationMeshes.StationSite> sites,
            MountainField field,
            PrototypeMaterials materials,
            List<MeshRenderer> litRenderers,
            List<int> litSlotStart,
            List<int> litSlots,
            List<int> litSlotGroups)
        {
            if (sites == null || sites.Count == 0)
            {
                Debug.LogWarning("[Horizon] No filling stations were placed, so the fuel model has "
                                 + "nowhere to send anybody. Check the AddFuelStation calls on the "
                                 + "courses.");
                return;
            }

            // One entry per submesh, in FuelStationMeshes' own order. The first four are tinted and
            // MergeTinted folds them into the lowest of them, so only the first is ever really used;
            // the last two are the untinted slots and keep their own materials.
            var stationMaterials = new[]
            {
                materials.RoadTint,      // Apron
                materials.BuildingTint,  // Structure
                materials.BuildingTint,  // Trim
                materials.RoadTint,      // Marking — paint on a road, so road smoothness if ever untinted
                materials.WindowDay,     // Lit — TownLights swaps this after dusk
                materials.SignFace,      // Sign — registered with nothing, bright always
            };

            int triangles = 0;
            float worstRelief = 0f;
            string worstAt = string.Empty;
            int signed = 0;
            float nearestSign = float.MaxValue;
            float furthestSign = 0f;

            for (int i = 0; i < sites.Count; i++)
            {
                FuelStationMeshes.StationSite site = sites[i];

                // The one hazard the sign resolver could not test: it runs before the height field
                // exists, so whether a spot is standing in a lake is a question only answerable here.
                // The coast road passes close enough to the water that this is a real case, not a guard.
                if (site.Sign.Exists
                    && field.IsUnderWater(site.Sign.Foot.x, site.Sign.Foot.z, site.Sign.Foot.y, 1.5f))
                {
                    Debug.LogWarning($"[Horizon] '{site.Name}' wanted its advance sign "
                                     + $"{site.Sign.Distance:0} m back, and that lands in water. Built "
                                     + "without one.");

                    site = site.WithoutSign();
                }

                var used = new List<int>(FuelStationMeshes.SubmeshCount);
                Mesh mesh = FuelStationBuilder.Build(site, $"FuelStation{Slug(site.Name)}Mesh", used);

                if (mesh == null)
                {
                    Debug.LogError($"[Horizon] '{site.Name}' produced no geometry at all.");
                    continue;
                }

                triangles += mesh.triangles.Length / 3;

                mesh = HorizonAssetUtility.ReplaceAsset(
                    mesh, $"{GeneratedFolder}/FuelStation{Slug(site.Name)}Mesh.asset");

                // The tinted submeshes merged into the lowest of them, so the slot list is short and its
                // order is whatever survived. Materials are indexed the same way.
                var slotMaterials = new Material[used.Count];
                for (int m = 0; m < used.Count; m++)
                {
                    slotMaterials[m] = stationMaterials[used[m]];
                }

                GameObject station = CreateMeshObject(
                    parent, "FuelStation" + Slug(site.Name), mesh, slotMaterials);

                // The sign, as a child, so it shares the station's chunk — the two are one place, and
                // RecalculateBounds below walks children, which grows the radius to take it in. Built
                // before that call for exactly that reason.
                var signUsed = new List<int>(FuelStationMeshes.SubmeshCount);
                Mesh signMesh = FuelStationBuilder.BuildAdvanceSign(
                    site, $"FuelSign{Slug(site.Name)}Mesh", signUsed);

                if (signMesh != null)
                {
                    signMesh = HorizonAssetUtility.ReplaceAsset(
                        signMesh, $"{GeneratedFolder}/FuelSign{Slug(site.Name)}Mesh.asset");

                    triangles += signMesh.triangles.Length / 3;

                    var signMaterials = new Material[signUsed.Count];
                    for (int m = 0; m < signUsed.Count; m++)
                    {
                        signMaterials[m] = stationMaterials[signUsed[m]];
                    }

                    CreateMeshObject(station.transform, "AdvanceSign", signMesh, signMaterials,
                        addCollider: false);

                    signed++;
                    nearestSign = Mathf.Min(nearestSign, site.Sign.Distance);
                    furthestSign = Mathf.Max(furthestSign, site.Sign.Distance);
                }
                else
                {
                    Debug.LogWarning($"[Horizon] '{site.Name}' has no advance sign: nowhere between "
                                     + "250 and 600 m back is clear of a bore, a span, another "
                                     + "station's frontage and a bend under 90 m. It can only be found "
                                     + "by someone already level with it.");
                }

                WorldChunk chunk = station.AddComponent<WorldChunk>();
                chunk.RecalculateBounds();

                int litSlot = used.IndexOf(FuelStationMeshes.LitSubmesh);
                if (litSlot >= 0)
                {
                    // Windows and not Lamps, and the difference is not cosmetic: Lamps' day material is
                    // M_Lane, the road's own asphalt, because a lamp's pool of light has to vanish into
                    // the carriageway when it is switched off. Applied to a shop window it paints it
                    // tarmac — which is what it did, on every station, from sunrise to dusk.
                    litRenderers.Add(station.GetComponent<MeshRenderer>());
                    litSlots.Add(litSlot);
                    litSlotGroups.Add((int)LitGroup.Windows);
                    litSlotStart.Add(litSlots.Count);
                }

                // What the pad actually came out like. This is the one number that says whether the
                // level samples reached the field, and it is silent otherwise: the slab is laid from the
                // course, so it is flat whatever the ground under it is doing.
                float relief = PadRelief(field, site);
                if (relief > worstRelief)
                {
                    worstRelief = relief;
                    worstAt = site.Name;
                }
            }

            string signs = signed == 0
                ? "none carries an advance sign"
                : $"{signed} of {sites.Count} carry an advance sign, at "
                  + $"{nearestSign:0}–{furthestSign:0} m";

            Debug.Log($"[Horizon] Filling stations: {sites.Count} built, {triangles} triangles. Worst pad "
                      + $"relief {worstRelief:0.00} m, at '{worstAt}'. {signs}.");

            if (worstRelief > 0.6f)
            {
                Debug.LogError($"[Horizon] '{worstAt}' stands on ground that still falls {worstRelief:0.00} m "
                               + "across its own apron. Its level samples are not reaching MountainField "
                               + "— check that FuelStationBuilder.AddPadSamples runs before the field is "
                               + "constructed, not after. The slab is laid from the course, so it will "
                               + "look perfectly flat while it hovers.");
            }
        }

        /// <summary>
        /// Checks that every filling station stands somewhere a filling station can stand, and reports
        /// how far a driver can get between them.
        ///
        /// <para>Each of these is a failure that would otherwise be silent. A forecourt on a viaduct
        /// comes out perfectly built with nothing underneath it. One overhanging the carriageway is two
        /// mesh colliders a few centimetres apart, which is a car thrown into the air at the moment it
        /// touches the seam. A station on a 9.5 % hairpin is a station the car rolls off. And a gap
        /// longer than a tank is the map stranding somebody, which is the one thing this feature must
        /// never do.</para>
        /// </summary>
        private static void ValidateFuelStations(
            params (IRoadPath Path, RoadCourse Course, RoadShape Shape, string Where, float Side)[] roads)
        {
            int counted = 0;
            float worstGap = 0f;
            string worstGapOn = string.Empty;

            for (int r = 0; r < roads.Length; r++)
            {
                (IRoadPath road, RoadCourse course, RoadShape shape, string where, float side) = roads[r];

                if (road == null || course == null)
                {
                    continue;
                }

                // Walked in course order so the gaps below are gaps along the road, and the two ends
                // count: what matters is the longest a driver can go without passing a pump, and the
                // start of a road is somewhere a driver can be.
                float previous = 0f;
                bool any = false;

                for (int i = 0; i < course.Features.Count; i++)
                {
                    RoadFeature feature = course.Features[i];

                    if (feature.Kind != RoadFeatureKind.FuelStation)
                    {
                        continue;
                    }

                    if (side != 0f && !Mathf.Approximately(feature.Side, side))
                    {
                        continue;
                    }

                    counted++;
                    any = true;

                    float at = Mathf.Clamp(feature.StartDistance, 0f, road.Length);

                    if (course.IsBridged(at, 40f) || course.IsCoveredOrNear(at, 40f))
                    {
                        Debug.LogError($"[Horizon] '{feature.Name}' at {at:0} m on {where} sits on a "
                                       + "bridge or inside a bore. There is no ground under either to "
                                       + "build a forecourt on — move it in the course table.");
                    }

                    float radius = road.GetRadiusAtDistance(at, 20f);
                    if (radius < 120f)
                    {
                        Debug.LogWarning($"[Horizon] '{feature.Name}' at {at:0} m on {where} is on a "
                                         + $"{radius:0} m radius. A forecourt wants straight road either "
                                         + "side of it; under about 120 m the apron starts cutting the "
                                         + "corner it is built beside.");
                    }

                    // Grade over the apron's own length. The pad is levelled, so a steep road here does
                    // not tilt the slab — it makes the step where the slab meets the verge, and that
                    // step is what a car has to drive over to get in.
                    float back = Mathf.Clamp(at - FuelStationMeshes.ApronHalfLength, 0f, road.Length);
                    float ahead = Mathf.Clamp(at + FuelStationMeshes.ApronHalfLength, 0f, road.Length);
                    float rise = Mathf.Abs(
                        road.GetPositionAtDistance(ahead).y - road.GetPositionAtDistance(back).y);
                    float grade = ahead > back ? rise / (ahead - back) * 100f : 0f;

                    if (grade > 3f)
                    {
                        Debug.LogWarning($"[Horizon] '{feature.Name}' at {at:0} m on {where} is on a "
                                         + $"{grade:0.0} % grade. The pad is poured level, so that is the "
                                         + "step the entry ramp has to swallow at one end of it.");
                    }

                    // Clear of its own carriageway. The apron's near edge is one verge gap out, and the
                    // check is that the gap is a gap rather than an overlap.
                    if (FuelStationMeshes.ApronHalfDepth <= 0f || shape.OuterHalfWidth <= 0f)
                    {
                        Debug.LogError($"[Horizon] '{feature.Name}' has no road width to stand clear of.");
                    }

                    float gap = at - previous;
                    if (gap > worstGap)
                    {
                        worstGap = gap;
                        worstGapOn = where;
                    }

                    previous = at;
                }

                float tail = road.Length - previous;
                if (any && tail > worstGap)
                {
                    worstGap = tail;
                    worstGapOn = where;
                }
                else if (!any && road.Length > worstGap)
                {
                    worstGap = road.Length;
                    worstGapOn = where + " (which has none at all)";
                }
            }

            // Quoted against hard driving, not against a cruise. A tank cruised gently covers 147 km,
            // which is six times the whole world's road network and would make any gap look harmless —
            // and cruising gently is not what anyone is doing when they run out.
            Debug.Log($"[Horizon] Filling stations: {counted} on the courses. Longest stretch without one "
                      + $"is {worstGap / 1000f:0.00} km, on {worstGapOn} — against a tank that covers "
                      + "about 20 km driven hard and 147 km cruised.");

            // Six kilometres, and it is no longer a stranding threshold — at 20 km of hard-driving range
            // the map cannot strand anybody any more. What it now measures is whether the world feels
            // inhabited: a road you can drive for six kilometres without passing a pump is a road with
            // nothing on it, which is a different complaint and still worth hearing.
            if (worstGap > 6000f)
            {
                Debug.LogWarning($"[Horizon] {worstGap / 1000f:0.0} km on {worstGapOn} without a pump. "
                                 + "Not far enough to strand anyone since the burn was slowed, but far "
                                 + "enough that the road reads as empty. Add a station, or accept it "
                                 + "deliberately.");
            }
        }

        /// <summary>
        /// Bakes the pump positions in for the runtime.
        ///
        /// <para>Baked rather than derived, exactly as the water is: the courses that know where the
        /// stations go are build-time objects and are not in a player build at all.</para>
        ///
        /// <para>The point recorded is the <b>pumps</b>, not the middle of the forecourt — offset out
        /// from the centre by the same 1.5 m the canopy is. Parking at a pump is what makes a station
        /// somewhere you arrive at rather than a stretch of road that happens to refill you, and a reach
        /// measured from the middle of the slab would let a car fuel from the far corner of it.</para>
        /// </summary>
        private static void BuildFillingStations(
            Transform parent, IReadOnlyList<FuelStationMeshes.StationSite> sites)
        {
            if (sites == null || sites.Count == 0)
            {
                return;
            }

            var pumpsObject = new GameObject("FillingStations");
            pumpsObject.transform.SetParent(parent, false);

            var records = new List<FillingStations.Station>(sites.Count);

            for (int i = 0; i < sites.Count; i++)
            {
                FuelStationMeshes.StationSite site = sites[i];

                records.Add(new FillingStations.Station
                {
                    Name = site.Name,
                    Pumps = site.Centre + site.Outward * 1.5f,

                    // 9 m: comfortably more than the 5 m island so nobody has to be precise about where
                    // they stop, and well short of the apron's 17 m half-depth so that being on the
                    // forecourt is not the same as being at a pump.
                    Radius = 9f,

                    // The slab, so the runtime can tell "on a forecourt" from "at a pump" — see the
                    // note on Station.Centre for why that has to be a rectangle. Baked as numbers rather
                    // than read back out of FuelStationMeshes at run time: this record is a bake, and a
                    // bake that reaches into a builder's constants changes meaning when the builder does.
                    Centre = site.Centre,
                    Forward = site.Forward,
                    Outward = site.Outward,
                    HalfLength = FuelStationMeshes.ApronHalfLength,
                    HalfDepth = FuelStationMeshes.ApronHalfDepth,
                });
            }

            FillingStations pumps = pumpsObject.AddComponent<FillingStations>();
            pumps.SetStations(records);
            EditorUtility.SetDirty(pumps);

            Debug.Log($"[Horizon] Filling stations: {records.Count} sets of pumps baked in. Stopping "
                      + "within 9 m of one fills the tank.");
        }

        /// <summary>
        /// Just the centres, for the vegetation scatter.
        ///
        /// <para>The scatter needs a point and a radius and nothing else, and it lives in a module that
        /// has never heard of a filling station. Handing it the whole site would mean teaching it
        /// what one is.</para>
        /// </summary>
        private static List<Vector3> ForecourtCentres(
            IReadOnlyList<FuelStationMeshes.StationSite> sites)
        {
            var centres = new List<Vector3>(sites.Count);

            for (int i = 0; i < sites.Count; i++)
            {
                centres.Add(sites[i].Centre);
            }

            return centres;
        }

        /// <summary>How far the finished ground rises and falls under one forecourt, metres.</summary>
        private static float PadRelief(MountainField field, in FuelStationMeshes.StationSite site)
        {
            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (float a = -FuelStationMeshes.ApronHalfLength;
                 a <= FuelStationMeshes.ApronHalfLength;
                 a += 4f)
            {
                for (float d = -FuelStationMeshes.ApronHalfDepth;
                     d <= FuelStationMeshes.ApronHalfDepth;
                     d += 4f)
                {
                    Vector3 at = site.Centre + site.Forward * a + site.Outward * d;
                    float y = field.HeightAt(at.x, at.z);

                    lowest = Mathf.Min(lowest, y);
                    highest = Mathf.Max(highest, y);
                }
            }

            return highest - lowest;
        }

        /// <summary>A station's name as an asset-name fragment: letters and digits only.</summary>
        private static string Slug(string name)
        {
            var built = new System.Text.StringBuilder(name.Length);

            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsLetterOrDigit(name[i]))
                {
                    built.Append(name[i]);
                }
            }

            return built.ToString();
        }

        private static void BuildDelineatorPosts(
            Transform parent,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            PrototypeMaterials materials,
            string label = "")
        {
            Mesh mesh = DelineatorPostBuilder.Build(path, roadShape, field, course);
            if (mesh == null)
            {
                Debug.Log($"[Horizon] No delineator posts on {Where(label)}.");
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, $"{GeneratedFolder}/Delineator{label}Mesh.asset");

            // No collider, for the same reason the guard rails have none: a post is a marker, not
            // something the car should be able to lean on.
            CreateMeshObject(parent, "Delineators" + label, mesh,
                new[] { materials.Delineator, materials.DelineatorReflector },
                addCollider: false, markStatic: true);

            Debug.Log($"[Horizon] Delineator posts on {Where(label)}: {triangles} triangles.");
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
        /// <summary>
        /// The suspension crossing, on a chunk that never unloads.
        ///
        /// <para>Unlike a viaduct, and that is the only difference in this method. A viaduct is a
        /// hundred metres of deck in a valley and is nothing to anybody standing more than a few hundred
        /// metres away; this is the tallest structure in the world and the thing the whole leg is built
        /// to arrive at, so it takes the treatment the road ribbons take rather than the one the filling
        /// stations take. Streaming it out would be the towers vanishing from the coast road.</para>
        /// </summary>
        private static void BuildSuspensionBridges(
            Transform parent,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            in SuspensionShape shape,
            PrototypeMaterials materials,
            string label)
        {
            var used = new List<int>();
            Mesh mesh = SuspensionBridgeBuilder.Build(
                path, roadShape, field, course, shape, used, "Suspension" + label);

            if (mesh == null)
            {
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, $"{GeneratedFolder}/Suspension{label}Mesh.asset");

            var slots = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                if (used[i] == SuspensionBridgeBuilder.LampSubmesh)
                {
                    // The one always-bright material in the build, and the beacons share it with the
                    // filling station signs for the reason recorded there: a lit group swaps a day
                    // material for a night one, and neither of these two things is ever unlit.
                    slots[i] = materials.SignFace;
                }
                else if (used[i] == SuspensionBridgeBuilder.SteelSubmesh)
                {
                    slots[i] = materials.GuardRail;
                }
                else
                {
                    slots[i] = materials.Concrete;
                }
            }

            GameObject bridge = CreateMeshObject(parent, "SuspensionBridges" + label, mesh, slots,
                addCollider: false, markStatic: true);

            WorldChunk chunk = bridge.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            Debug.Log($"[Horizon] Suspension bridges on {Where(label)}: {triangles} triangles, "
                      + $"{used.Count} material slot(s), never streamed out.");
        }

        /// <summary>
        /// Measures a suspension crossing against the four things that make it one, and says so.
        ///
        /// <para><see cref="ValidateBridges"/> does not cover this. It asks whether there is air under
        /// the deck, which a causeway with towers on it would pass the moment
        /// <c>MountainField.BridgeHeadroom</c> carved its nine metres. The questions here are different:
        /// is there <i>water</i> under it, is there enough air over that water to be a shipping channel
        /// rather than a jetty, does the cable stay above the parapet it is meant to be holding up, and
        /// is the deck actually level.</para>
        ///
        /// <para>All four have the same failure mode, which is why they are worth measuring: each one
        /// produces a structure that builds without complaint and is wrong in a way only a photograph
        /// would show.</para>
        /// </summary>
        private static void ValidateSuspensionBridges(
            IRoadPath path,
            MountainField field,
            RoadCourse course,
            in SuspensionShape shape)
        {
            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Suspension)
                {
                    continue;
                }

                const float step = 20f;

                int stations = 0;
                int overWater = 0;
                float leastAir = float.MaxValue;
                float leastOverWater = float.MaxValue;
                float deepestGround = 0f;

                for (float at = feature.StartDistance; at <= feature.EndDistance; at += step)
                {
                    Vector3 deck = path.GetPositionAtDistance(at);

                    float air = deck.y - field.HeightAt(deck.x, deck.z);
                    leastAir = Mathf.Min(leastAir, air);
                    deepestGround = Mathf.Max(deepestGround, air);
                    stations++;

                    if (TryWaterUnder(field, deck.x, deck.z, out float surface))
                    {
                        overWater++;
                        leastOverWater = Mathf.Min(leastOverWater, deck.y - surface);
                    }
                }

                Vector3 west = path.GetPositionAtDistance(feature.StartDistance);
                Vector3 east = path.GetPositionAtDistance(feature.EndDistance);
                float grade = (east.y - west.y) / Mathf.Max(1f, feature.Length) * 100f;

                float headroom = shape.TowerRise - shape.CableSag - BridgeBuilder.ParapetHeight;
                float mainSpan = feature.Length - 2f * shape.SideSpan;

                Debug.Log($"[Horizon] Suspension bridge '{feature.Name}': {feature.Length:0} m of "
                          + $"structure, {mainSpan:0} m between the towers, {shape.TowerRise:0} m of "
                          + $"tower over a deck {leastAir:0} to {deepestGround:0} m above the ground, "
                          + $"water under {overWater} of {stations} stations"
                          + (overWater > 0 ? $" with {leastOverWater:0.0} m of air over it" : string.Empty)
                          + $", cable clearing the parapet by {headroom:0.0} m at mid-span, deck at "
                          + $"{grade:0.00} %.");

                if (overWater == 0)
                {
                    Debug.LogError(
                        $"[Horizon] '{feature.Name}' has no water under any part of it. A suspension "
                        + "bridge over dry land is nine hundred metres of cable spent on a field. Either "
                        + "the channel's plan names a different bridge, or WaterPlanner was not given "
                        + "the road this span is on.");
                }
                else if (leastOverWater < 40f)
                {
                    Debug.LogWarning(
                        $"[Horizon] '{feature.Name}' passes {leastOverWater:0.0} m over the water at its "
                        + "lowest, which is a jetty rather than a shipping channel and puts the towers "
                        + "out of proportion with the thing they are standing in. The deck's height comes "
                        + "from the ramp grade on the course; the water's comes from its own rim.");
                }

                // The cable is slung sag below the tower heads, so this is what is left over the rail at
                // mid-span. Negative means the hangers are pushing up.
                if (headroom < 3f)
                {
                    Debug.LogWarning(
                        $"[Horizon] '{feature.Name}': the main cable comes within {headroom:0.0} m of the "
                        + "parapet at mid-span. Sag is a tenth of the main span by convention, so either "
                        + "the towers are too short for the span or the span is too long for the towers.");
                }

                if (Mathf.Abs(grade) > 1f)
                {
                    Debug.LogWarning(
                        $"[Horizon] '{feature.Name}' falls {grade:0.00} % across the span. A suspension "
                        + "deck is level: every height in the structure is measured off it, and a grade "
                        + "here tilts the towers' own datum with it.");
                }
            }
        }

        /// <summary>The surface of whatever open water stands at a point, if any does.</summary>
        private static bool TryWaterUnder(MountainField field, float x, float z, out float surface)
        {
            surface = 0f;

            for (int i = 0; i < field.Water.Count; i++)
            {
                WaterBody body = field.Water[i];

                if (body.Near(x, z) && body.DistanceOutside(x, z) <= 0f)
                {
                    surface = body.SurfaceY;
                    return true;
                }
            }

            return false;
        }

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
