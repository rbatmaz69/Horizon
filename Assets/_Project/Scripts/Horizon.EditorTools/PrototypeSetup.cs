using System.Collections.Generic;
using Horizon.Atmosphere;
using Horizon.Core;
using Horizon.Game;
using Horizon.Input;
using Horizon.Net;
using Horizon.Vehicle;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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

        /// <summary>Internal so the preview tool photographs the map this build wrote, not one of its own.</summary>
        internal const string WorldMapPath = GeneratedFolder + "/WorldMap.asset";
        private const string PrefabsFolder = ProjectRoot + "/Prefabs/Vehicles";

        private const string BootstrapScenePath = ScenesFolder + "/Bootstrap.unity";
        private const string WorldScenePath = ScenesFolder + "/World_MountainPass.unity";
        private const string WorldSceneName = "World_MountainPass";
        private const string VehiclePrefabPath = PrefabsFolder + "/Vehicle_Prototype.prefab";
        private const string VehicleConfigPath = SettingsFolder + "/VehicleConfig_Prototype.asset";
        private const string TimeOfDayProfilePath = SettingsFolder + "/TimeOfDayProfile_Default.asset";

        /// <summary>The sky shader, named once so the material, the check and the log all agree.</summary>
        private const string SkyShaderName = "Horizon/Sky";

        /// <summary>Path of the one sky material. See <c>PrototypeMaterials.Sky</c>.</summary>
        private const string SkyMaterialPath = MaterialsFolder + "/M_Sky.mat";

        /// <summary>Path of the generated cloud field.</summary>
        private const string CloudFieldPath = ProjectRoot + "/Art/Skybox/T_SkyClouds.png";

        /// <summary>
        /// Where the cloud coverage threshold sits at <c>Overcast</c> 0 and at 1.
        ///
        /// <para>Written here rather than only in the shader because the build measures what they mean
        /// against the field's own histogram — see <c>ReportCloudField</c>. The shader's defaults are
        /// the same two numbers; <c>ValidateSky</c> is what says so, because two copies of a number
        /// agree until one of them is edited.</para>
        /// </summary>
        private const float CoverClear = 0.75f;

        /// <summary>See <see cref="CoverClear"/>.</summary>
        private const float CoverFull = 0.15f;

        /// <summary>
        /// How much of the fine field the shader mixes into the broad one.
        ///
        /// <para>Kept here as well as on the material because the coverage report has to threshold the
        /// same field the shader does. Measuring the broad channel alone was the first version, and it
        /// was wrong in a way that mattered: mixing two fields narrows the distribution, so the
        /// percentages came out against a spread the sky never sees. <c>ValidateSky</c> compares the two
        /// copies.</para>
        /// </summary>
        private const float CloudDetailWeight = 0.24f;

        [MenuItem("Tools/Horizon/Rebuild Prototype Scene", priority = 0)]
        public static void Rebuild()
        {
            // Static counters live across an editor session, so a second run in the same one would
            // otherwise report twice the swell it built.
            WaterTileBuilder.ResetSwellCount();

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EnsureFolders();
            HorizonAssetUtility.BeginGeneratedRun();

            taggedCarriageways = 0;
            taggedGround = 0;
            surfaceProbes = 0;
            surfaceProbeMisses = 0;
            surfaceVergeSamples = 0;
            surfaceVergeBuried = 0;

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
            ReportSurfaces();
            BuildBootstrapScene(spawns);
            RegisterScenesInBuildSettings();

            // After both scenes are on disk, because the question it asks is which sky each of them
            // saved and there is no moment before this when both answers exist.
            ValidateSky(LoadTimeOfDayProfile(), TimeOfDayController.DefaultEnvironmentInterval);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            HorizonAssetUtility.ReportOrphanedAssets(GeneratedFolder);

            // Render the preview here, as part of the rebuild. Running it as a separate command invites
            // rendering the previous car by mistake, which is exactly what happened once. The temporary
            // rig dirties the current scene, but that scene is already saved and is replaced below.
            CarPreviewRenderer.Render();

            // And the map, here rather than as a separate command, for the reason above: run on its own
            // it photographs whatever was baked last time, which is the one thing a preview must not do.
            MapPreviewRenderer.Render();

            // Leave the editor in the state you actually want to work in: Bootstrap active, with the
            // world open alongside it. Opening Bootstrap alone looks broken — it holds one object,
            // no camera and no geometry, because the world is loaded at runtime. GameBootstrap skips
            // its additive load when the scene is already open, so Play works either way.
            EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);

            // Last of all, because it photographs whatever is open and this is where Bootstrap is.
            HudPreviewRenderer.Render();

            // Reported at the very end, and the first version was not. It sat beside "Water: N bodies",
            // which counts *plans* — the tiles are built thousands of lines of work later, so the check
            // ran before a single water vertex existed and reported a still world every time. The
            // counter caught the placement of its own report, which is the only thing that would have.
            if (WaterTileBuilder.SwellVertices == 0)
            {
                Debug.LogWarning("[Horizon] Swell: no water vertex carries one, so every body in the "
                               + "world is a still pane. Either the mask is not being written or every "
                               + "body is shallower than the depth the swell fades in over.");
            }
            else
            {
                Debug.Log($"[Horizon] Swell: {WaterTileBuilder.SwellVertices} water vertices move.");
            }

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

            /// <summary>
            /// The circuit's asphalt: a third atlas, and a third for the same reason the motorway got a
            /// second one. Markings are painted in coordinates normalised across the carriageway, so a
            /// two-lane texture stretched over a thirteen-metre track would give a race track with a
            /// dashed line down the middle of it — correct-looking geometry saying the wrong thing.
            /// Asked for with one lane, which is what leaves it with edge lines and nothing between.
            /// </summary>
            public readonly Material CircuitSurface;
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

            /// <summary>
            /// The town's streets. The same shader and the same smoothness as <see cref="TerrainTint"/>,
            /// and a separate asset only so that the rain can tell them apart from a hillside.
            /// </summary>
            public readonly Material TownStreet;
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

            /// <summary>A traffic car's tail lamps under braking. See <c>TrafficDirector</c>.</summary>
            public readonly Material TailBrake;

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

            /// <summary>
            /// A headlamp lens that is on, as a whole material.
            ///
            /// <para>The player's own car has no need of this — <c>VehicleLights</c> drives
            /// <see cref="LightFront"/> through a <c>MaterialPropertyBlock</c> on one submesh of one
            /// object, which is affordable exactly once. Another player's car has no block and gets a
            /// material instead, the same argument <see cref="TailNight"/> already makes for the
            /// ambient traffic. The colour is <see cref="LightFront"/>'s at
            /// <c>VehicleLights.headlightGlow</c>, so the two agree about what a lit lamp looks
            /// like.</para>
            /// </summary>
            public readonly Material HeadLampLit;
            public readonly Material Smoke;

            /// <summary>
            /// The grit hanging in the air that the car flies past at speed. Its colour is written from
            /// the fog every frame, so what is set here is only a starting point.
            /// </summary>
            public readonly Material AirRush;

            /// <summary>Falling water. Stretched billboards, so it wants its own thin bright material.</summary>
            public readonly Material Rain;

            /// <summary>
            /// The sky, for every hour and every weather. Null only if the shader is missing, which
            /// <c>ValidateSky</c> fails the build over.
            /// </summary>
            public readonly Material Sky;

            /// <summary>
            /// The wet counterparts, in the order <see cref="WetRoadMaterials"/> lists the dry ones.
            ///
            /// <para>Whole assets rather than a colour written onto the dry ones at run time, for the
            /// reason <c>WetSurfaces</c> gives: Unity does not roll an asset edit back when Play mode
            /// ends, so a player who tried the rain once would leave M_RoadSurface.mat modified in the
            /// working tree.</para>
            /// </summary>
            public readonly Material[] RoadWet;

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

                RoadShape circuitShape = RoadShape.Circuit;

                Texture2D circuitTexture = HorizonAssetUtility.LoadOrCreateTexture(
                    ProjectRoot + "/Art/T_CircuitSurface.png",
                    () => RoadTextureBuilder.BuildSurface(circuitShape, 1),
                    anisoLevel: 8);

                CircuitSurface = HorizonAssetUtility.LoadOrCreateMaterial(
                    MaterialsFolder + "/M_CircuitSurface.mat", "M_CircuitSurface", Color.white, 0.34f, 0f,
                    circuitTexture);

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

                // <b>The town's streets, at exactly the terrain's smoothness they already had.</b> They
                // shared M_TerrainTint with every hillside in the world, which is why rain darkened
                // every carriageway and left four towns dry: WetSurfaces swaps by material identity, and
                // wetting that one would have wetted the ground as well. An asset of their own is the
                // whole of the fix.
                //
                // 0.08 and not the carriageway's 0.34, deliberately: this change is meant to add a wet
                // state and to leave the dry one pixel-identical, which is checkable in the town frames.
                // Whether a town street should be as glossy as a trunk road when dry is a separate
                // question and a separate look — see RoadTint's own note for the argument it would use.
                TownStreet = HorizonAssetUtility.LoadOrCreateTintMaterial(
                    MaterialsFolder + "/M_TownStreet.mat", "M_TownStreet", 0.08f);

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

                // Under braking, and it has to clear the lit one by enough to read at a distance in a
                // mirror-less game — the only place traffic brake lights are ever seen is from behind on
                // the motorway, at night, several car lengths back. Two and a half times the lit value,
                // which with the bloom now in the stack is the difference between a red rectangle and a
                // lamp. Bright enough to be worth having by day as well, which matters: a car braking at
                // noon used to show nothing at all.
                TailBrake = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_TailBrake.mat", "M_TailBrake",
                    new Color(3.4f, 0.22f, 0.12f));

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

                // M_LightFront's colour times VehicleLights.headlightGlow, which is 2.4. Written out
                // rather than multiplied at a gate for the reason the road widths are: a number that
                // exists only as an expression is a number no comment can be checked against.
                HeadLampLit = HorizonAssetUtility.LoadOrCreateUnlitMaterial(
                    MaterialsFolder + "/M_HeadLampLit.mat", "M_HeadLampLit",
                    new Color(1.49f, 1.44f, 1.20f));

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

                // Cool and pale rather than white, and thin. A raindrop seen against a stormy sky is
                // nearly the colour of the sky, and pure white streaks read as scratches on the lens.
                Rain = HorizonAssetUtility.LoadOrCreateParticleMaterial(
                    MaterialsFolder + "/M_Rain.mat", "M_Rain", smokeTexture,
                    new Color(0.86f, 0.90f, 0.96f, 0.62f));

                // Darker, and only a little smoother. The darkening does nearly all the work and the
                // smoothness is a garnish — which is the opposite of what the first version assumed,
                // and the pictures were unambiguous about it.
                //
                // <b>0.80 turned every road in the world into a mirror of the sky.</b> This project has
                // no reflection probes by budget, so URP's environment reflection is the skybox itself:
                // past about half smoothness the carriageway stops being asphalt and becomes a sheet of
                // blue, the lane markings disappear under it entirely, and the effect does not even stop
                // at a portal — the bore came back with a blue river running through it. A wet road is
                // dark first and shiny second.
                RoadWet = new[]
                {
                    WetVariant(MaterialsFolder + "/M_RoadSurfaceWet.mat", "M_RoadSurfaceWet",
                        RoadSurface, new Color(0.52f, 0.53f, 0.57f), 0.46f),
                    WetVariant(MaterialsFolder + "/M_MotorwaySurfaceWet.mat", "M_MotorwaySurfaceWet",
                        MotorwaySurface, new Color(0.52f, 0.53f, 0.57f), 0.46f),
                    WetVariant(MaterialsFolder + "/M_CircuitSurfaceWet.mat", "M_CircuitSurfaceWet",
                        CircuitSurface, new Color(0.52f, 0.53f, 0.57f), 0.46f),

                    // The verge barely moves. Gravel holds water in it rather than on it, so a shoulder
                    // shining like the carriageway would read as a second lane.
                    WetVariant(MaterialsFolder + "/M_RoadShoulderWet.mat", "M_RoadShoulderWet",
                        RoadShoulder, new Color(0.68f, 0.68f, 0.70f), 0.22f),

                    WetVariant(MaterialsFolder + "/M_LaneWet.mat", "M_LaneWet",
                        Lane, new Color(0.17f, 0.17f, 0.19f), 0.44f),

                    // <b>The town's streets, and the two numbers are derived rather than picked.</b> The
                    // tint is the carriageway's, because darkening is what reads as wet and asphalt,
                    // footway and kerb all darken. The smoothness is not: MergeTinted folds this mesh's
                    // surface, kerb, footway, marking *and grass verge* into one material, so a single
                    // number has to cover paved and unpaved together. 0.38 sits between the
                    // carriageway's 0.46 and the shoulder's 0.22 in about the proportion the
                    // cross-section does — verge, footway, kerb, surface, surface, kerb, footway, verge.
                    //
                    // Giving the verge a null tint would give it a material and the right gloss, and it
                    // would also give every town tile in the world another draw call for two metres of
                    // grass. See VegetationMeshBuffer.MergeTinted on what a null tint means.
                    HorizonAssetUtility.LoadOrCreateTintMaterial(
                        MaterialsFolder + "/M_TownStreetWet.mat", "M_TownStreetWet", 0.38f,
                        new Color(0.52f, 0.53f, 0.57f)),
                };

                Sky = SkyMaterial();
            }

            /// <summary>
            /// The wet twin of a road material: same texture, darker tint, higher smoothness.
            ///
            /// <para><b>The tint is written after the asset is made, and only when it is made.</b>
            /// <c>LoadOrCreateMaterial</c> forces <c>_BaseColor</c> to white whenever a base map is
            /// given, and it is right to — for a dry road the tint multiplies the marking atlas and
            /// anything but white would darken the paint. Here darkening is the point, so the colour has
            /// to go back on afterwards. Only on creation, because that helper returns an existing asset
            /// untouched so hand-retints survive a rebuild, and re-writing the tint every run would make
            /// this the one material in the project that cannot be adjusted.</para>
            /// </summary>
            private static Material WetVariant(
                string assetPath, string name, Material dry, Color tint, float smoothness)
            {
                bool existed = AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null;

                Material wet = HorizonAssetUtility.LoadOrCreateMaterial(
                    assetPath, name, tint, smoothness, 0f,
                    dry != null ? dry.GetTexture("_BaseMap") as Texture2D : null);

                if (!existed && wet != null)
                {
                    wet.SetColor("_BaseColor", tint);
                    EditorUtility.SetDirty(wet);
                }

                return wet;
            }

            /// <summary>
            /// The bad-weather sky: a flat grey gradient, painted rather than simulated.
            ///
            /// <para><b>The procedural sky cannot do overcast, and the first attempt proved it.</b>
            /// <c>Skybox/Procedural</c> models atmospheric scattering, so its one "more weather" knob —
            /// <c>_AtmosphereThickness</c> — means *more air*, and more air means more scattering: the
            /// frame came back with a gold sunset at the horizon and a green zenith over a rainstorm.
            /// Thickness is a sunset knob wearing a weather name. It also keeps taking its colour from
            /// <c>RenderSettings.sun</c>, which at four in the afternoon is warm gold, so no tint on top
            /// could have rescued it.</para>
            ///
            /// <para>What overcast actually is, is a flat low-contrast dome that does not know where the
            /// sun is. That is two colours and a gradient, so it is generated as a small equirectangular
            /// texture and hung on <c>Skybox/Panoramic</c> — 64 × 32, because it is nothing but a
            /// vertical ramp and bilinear filtering does the rest. The horizon is the lighter end, which
            /// is the one thing a real overcast sky reliably does.</para>
            ///
            /// <para><b>It is also what every smooth surface in the world reflects</b>, there being no
            /// reflection probes here, so this is half of why wet asphalt stopped looking like a canal.
            /// </para>
            /// </summary>
            /// <summary>
            /// The one sky: <c>Horizon/Sky</c> over a generated cloud field.
            ///
            /// <para>Replaces both the stock <c>Skybox/Procedural</c> the fair weather used and the
            /// painted grey <c>M_SkyOvercast</c> that stood in for bad weather. Nothing is written to
            /// this material at run time — see <c>TimeOfDayController.PushSky</c> — so every value set
            /// here is an authoring decision and the asset is safe to hand-tune.</para>
            ///
            /// <para>A missing shader is an error rather than a warning, unlike the material this
            /// replaces. That one could fall back on the fair sky and merely look wrong in the rain;
            /// there is no fallback now, and <c>ValidateSky</c> fails the build.</para>
            /// </summary>
            private static Material SkyMaterial()
            {
                const string assetPath = MaterialsFolder + "/M_Sky.mat";

                Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (existing != null)
                {
                    return existing;
                }

                Shader shader = Shader.Find(SkyShaderName);
                if (shader == null)
                {
                    Debug.LogError($"[Horizon] No {SkyShaderName} shader. There is no sky at all without "
                                   + "it — see Art/Shaders/HorizonSky.shader.");
                    return null;
                }

                // Linear, not sRGB. This is a coverage mask that thresholds are compared against, not a
                // colour: imported as sRGB it carries a 2.2 curve and every threshold in the shader
                // lands somewhere other than where the histogram in the build log says it does. The
                // sky still comes out with clouds in it, which is what makes it worth writing down.
                Texture2D clouds = HorizonAssetUtility.LoadOrCreateTexture(
                    ProjectRoot + "/Art/Skybox/T_SkyClouds.png", BuildCloudField,
                    anisoLevel: 4, wrap: true, sRGB: false);

                var sky = new Material(shader) { name = "M_Sky" };
                sky.SetTexture("_CloudTex", clouds);

                AssetDatabase.CreateAsset(sky, assetPath);
                return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            }

            /// <summary>
            /// The cloud field: two octave stacks of tiling value noise, broad in red and fine in green.
            ///
            /// <para><b>Hand-rolled noise, and this reverses the rule <c>SurfaceRelief</c> states for a
            /// different reason than that one does.</b> <c>MountainField</c> and the tile builder are
            /// right to use <c>Mathf.PerlinNoise</c> because they bake once. This bakes once too — but
            /// Unity's Perlin cannot be made to tile at any useful frequency: its permutation repeats
            /// with period 256, so the only sample range that wraps exactly spans 256 lattice cells,
            /// which at 256 pixels is one cell per texel. That is white noise. A periodic integer hash
            /// with a chosen cell period tiles by construction.</para>
            ///
            /// <para>Blue is left empty deliberately. A third field would be a third thing to tune and
            /// the shader has no use for one; a channel that exists and is not read is a channel
            /// somebody will later assume means something.</para>
            /// </summary>
            private static Texture2D BuildCloudField()
            {
                const int size = 256;

                var texture = new Texture2D(size, size, TextureFormat.RGB24, false)
                {
                    name = "T_SkyClouds",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                };

                var pixels = new Color[size * size];

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size;
                        float v = (y + 0.5f) / size;

                        // Broad: the shape of a cloud bank. Three octaves, weighted hard towards the
                        // coarsest — it decides where cloud is at all and the finer two only break its
                        // edge. The first version was 4/8/16 at 0.55/0.30/0.15 and the sky came back
                        // speckled: not clouds but mottling, because equalising the field afterwards
                        // stretches exactly the middle of the histogram the fine octaves live in.
                        float broad = TilingNoise(u, v, 3, 17u) * 0.62f
                                    + TilingNoise(u, v, 6, 23u) * 0.27f
                                    + TilingNoise(u, v, 12, 41u) * 0.11f;

                        // Fine: what the shader mixes in at a second, tighter scale, so a cloud has an
                        // edge at more than one size. One field sampled twice would repeat itself at a
                        // fixed ratio and read as a pattern.
                        float detail = TilingNoise(u, v, 8, 71u) * 0.65f
                                     + TilingNoise(u, v, 16, 89u) * 0.35f;

                        pixels[y * size + x] = new Color(broad, detail, 0f);
                    }
                }

                // <b>Flattened to a uniform distribution before it is written.</b> An octave stack sums
                // to something roughly Gaussian about a half, which puts almost all of its values in a
                // narrow band — so a coverage threshold moved by a tenth swings the sky from scattered
                // cloud to a lid, and the four weathers cannot be spread across it. Measured on the
                // first bake: clear came out at 12 % and hazy, one notch along, at 73 %.
                //
                // Equalising is a monotonic remap, so it moves no cloud and changes no shape — only how
                // the values are spaced. What it buys is that a threshold now means very nearly the
                // share of sky above it, which is what makes the numbers in the log readable and the
                // two constants above choosable rather than guessed at.
                Equalise(pixels, size, broad: true);
                Equalise(pixels, size, broad: false);

                texture.SetPixels(pixels);
                texture.Apply();

                ReportCloudField(pixels, size);

                return texture;
            }

            /// <summary>
            /// Value noise that wraps exactly at 1, with <paramref name="period"/> cells across.
            ///
            /// <para>Quintic fade and integer lattice indices taken modulo the period, so the left edge
            /// and the right edge share their corner values by construction rather than by luck. A
            /// tiling failure shows in the frame as a hard vertical line in the sky at one bearing,
            /// which reads as a rendering bug rather than as a texture one — so nobody looks here.</para>
            /// </summary>
            private static float TilingNoise(float u, float v, int period, uint seed)
            {
                float x = u * period;
                float y = v * period;

                int x0 = Mathf.FloorToInt(x);
                int y0 = Mathf.FloorToInt(y);
                float fx = x - x0;
                float fy = y - y0;

                int xa = ((x0 % period) + period) % period;
                int ya = ((y0 % period) + period) % period;
                int xb = (xa + 1) % period;
                int yb = (ya + 1) % period;

                float c00 = LatticeValue(xa, ya, seed);
                float c10 = LatticeValue(xb, ya, seed);
                float c01 = LatticeValue(xa, yb, seed);
                float c11 = LatticeValue(xb, yb, seed);

                // Quintic rather than smoothstep: C2, so an octave stack has no visible lattice ridges
                // where two cells meet. The same reason SurfaceRelief's fade is quintic, one dimension up.
                float sx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
                float sy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);

                return Mathf.Lerp(Mathf.Lerp(c00, c10, sx), Mathf.Lerp(c01, c11, sx), sy);
            }

            /// <summary>FNV-1a with an avalanche, the shape every other hash in this project uses.</summary>
            private static float LatticeValue(int x, int y, uint seed)
            {
                unchecked
                {
                    uint hash = 2166136261u;

                    hash = (hash ^ (uint)x) * 16777619u;
                    hash = (hash ^ (uint)y) * 16777619u;
                    hash = (hash ^ seed) * 16777619u;
                    hash ^= hash >> 13;
                    hash *= 0x5bd1e995u;
                    hash ^= hash >> 15;

                    return (hash & 0xFFFFFFu) / (float)0x1000000;
                }
            }

            /// <summary>
            /// Replaces every value in one channel by its own rank, so the channel is uniform on 0..1.
            ///
            /// <para>Sorted once and looked up by binary search rather than ranked pairwise: 65 536
            /// values, and the pairwise form is four thousand million comparisons.</para>
            /// </summary>
            private static void Equalise(Color[] pixels, int size, bool broad)
            {
                var values = new float[pixels.Length];

                for (int i = 0; i < pixels.Length; i++)
                {
                    values[i] = broad ? pixels[i].r : pixels[i].g;
                }

                var sorted = (float[])values.Clone();
                System.Array.Sort(sorted);

                for (int i = 0; i < pixels.Length; i++)
                {
                    int rank = System.Array.BinarySearch(sorted, values[i]);

                    if (rank < 0)
                    {
                        rank = ~rank;
                    }

                    float ranked = rank / (float)(sorted.Length - 1);

                    if (broad)
                    {
                        pixels[i].r = ranked;
                    }
                    else
                    {
                        pixels[i].g = ranked;
                    }
                }
            }

            /// <summary>
            /// Measures the field and says what each weather will actually see of it.
            ///
            /// <para><b>A coverage threshold only means "a few scattered clouds" if the histogram puts
            /// the right share above it</b>, and an octave stack is roughly Gaussian about a half — so
            /// the numbers in the shader have to be chosen against a measurement rather than by eye.
            /// This is also the line that catches the two ends of the continuum, which are one number
            /// apart and both invisible: a clear sky with no cloud in it at all, and an overcast sky
            /// that still has holes.</para>
            ///
            /// <para>The seam is measured for the reason <see cref="TilingNoise"/> gives.</para>
            /// </summary>
            private static void ReportCloudField(Color[] pixels, int size)
            {
                // The mixed field, not the broad channel: that is what the shader thresholds, and two
                // fields lerped together are narrower than either of them.
                var broad = new float[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    broad[i] = Mathf.Lerp(pixels[i].r, pixels[i].g, CloudDetailWeight);
                }

                var sorted = (float[])broad.Clone();
                System.Array.Sort(sorted);

                float seam = 0f;
                for (int i = 0; i < size; i++)
                {
                    seam = Mathf.Max(seam, Mathf.Abs(broad[i * size] - broad[i * size + size - 1]));
                    seam = Mathf.Max(seam, Mathf.Abs(broad[i] - broad[(size - 1) * size + i]));
                }

                float Share(float threshold)
                {
                    int over = 0;
                    for (int i = 0; i < broad.Length; i++)
                    {
                        if (broad[i] > threshold)
                        {
                            over++;
                        }
                    }

                    return over * 100f / broad.Length;
                }

                // The four weathers, read off PlayerChoices rather than typed, so this cannot drift away
                // from what the menu actually asks for.
                float clearCover = Mathf.Lerp(CoverClear, CoverFull, PlayerChoices.OvercastFor(WeatherPreset.Clear));
                float hazyCover = Mathf.Lerp(CoverClear, CoverFull, PlayerChoices.OvercastFor(WeatherPreset.Hazy));
                float rainCover = Mathf.Lerp(CoverClear, CoverFull, PlayerChoices.OvercastFor(WeatherPreset.Rain));
                float fullCover = Mathf.Lerp(CoverClear, CoverFull, PlayerChoices.OvercastFor(WeatherPreset.Overcast));

                Debug.Log($"[Horizon] Sky: cloud field {size}x{size}, "
                          + $"p50 {sorted[sorted.Length / 2]:0.00} "
                          + $"p85 {sorted[(int)(sorted.Length * 0.85f)]:0.00} "
                          + $"p98 {sorted[(int)(sorted.Length * 0.98f)]:0.00}, "
                          + $"seam {seam:0.000}. Cover: clear {Share(clearCover):0} %, "
                          + $"hazy {Share(hazyCover):0} %, rain {Share(rainCover):0} %, "
                          + $"overcast {Share(fullCover):0} %.");

                if (seam > 0.02f)
                {
                    Debug.LogWarning($"[Horizon] Sky: the cloud field does not tile — {seam:0.000} of "
                                     + "difference across its own edge. That shows in the frame as a hard "
                                     + "vertical line in the sky at one bearing, which reads as a "
                                     + "rendering fault rather than as a texture one.");
                }

                if (Share(clearCover) < 2f)
                {
                    Debug.LogWarning("[Horizon] Sky: a clear sky has no cloud in it at all. _CoverClear "
                                     + "is above the top of this field's histogram.");
                }

                if (Share(fullCover) < 85f)
                {
                    Debug.LogWarning($"[Horizon] Sky: an overcast sky is only {Share(fullCover):0} % "
                                     + "covered, so it has holes in it. _CoverFull is too high for this "
                                     + "field.");
                }
            }

            /// <summary>The dry road materials, in the order <see cref="RoadWet"/> gives their wet twins.</summary>
            public Material[] WetRoadMaterials => new[]
            {
                RoadSurface, MotorwaySurface, CircuitSurface, RoadShoulder, Lane, TownStreet,
            };
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
            TimeOfDayProfile existing = HorizonAssetUtility.LoadOrCreate(
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

                    ApplySkyDefaults(profile);
                    profile.Version = TimeOfDayProfile.CurrentVersion;

                    return profile;
                });

            // <b>LoadOrCreate deliberately returns an existing asset untouched</b>, which is what lets a
            // hand-tuned gradient survive a rebuild — and it is also why a field added later arrives
            // empty on every checkout that already had the file. An empty Gradient evaluates to black,
            // so the sky would go on dimming through its horizon and be wrong overhead: half working,
            // and nothing anywhere reporting it. Same shape of trap VehicleConfigReset exists for.
            if (existing != null && existing.Version < TimeOfDayProfile.CurrentVersion)
            {
                Debug.Log($"[Horizon] Time of day: healing {TimeOfDayProfilePath} from version "
                          + $"{existing.Version} to {TimeOfDayProfile.CurrentVersion}. Fields the bump "
                          + "introduced are being written; everything else is left as it was.");

                ApplySkyDefaults(existing);
                existing.Version = TimeOfDayProfile.CurrentVersion;
                EditorUtility.SetDirty(existing);
            }

            return existing;
        }

        /// <summary>
        /// The zenith gradient and the ground bounce, written on creation and on a version bump.
        ///
        /// <para>Its own method so the two callers cannot disagree — the create path and the heal path
        /// are the same fields, and a heal that wrote a different violet from the one a fresh checkout
        /// gets would be two worlds depending on how old somebody's repository is.</para>
        ///
        /// <para><b>Every key is under half a unit linear.</b> The tone map is Neutral with a +0.5 stop
        /// lift and bloom's knee opens at 0.63 linear; the sun's disc is the one thing in the sky shader
        /// meant to reach that. The deep violet at dusk against the fog's warm gold is the pair that
        /// makes an evening read as an evening, and it is the reason this is a gradient of its own
        /// rather than something derived from the fog.</para>
        /// </summary>
        private static void ApplySkyDefaults(TimeOfDayProfile profile)
        {
            profile.SkyZenith = HorizonAssetUtility.BuildGradient(
                (0.00f, new Color(0.04f, 0.05f, 0.11f)),   // night, a shade above the fog's own
                (0.27f, new Color(0.34f, 0.42f, 0.62f)),   // dawn, cold and pale
                (0.50f, new Color(0.26f, 0.45f, 0.72f)),   // noon
                (0.75f, new Color(0.30f, 0.30f, 0.50f)),   // dusk: violet over a gold horizon
                (1.00f, new Color(0.04f, 0.05f, 0.11f)));

            profile.GroundBounce = new Color(0.30f, 0.27f, 0.21f);
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

            // The three contact layers. Placed after the engine because they are the same kind of thing
            // and read the same car — see ContactAudio for why all three live on one component.
            //
            // Barely spatialised at all. A crash and a scrape happen at the bodywork, which at chase
            // distance is close enough to the listener that panning them only smears the transient; the
            // rumble is under the wheels, which is where the tyre squeal already sits at 0.1.
            AudioSource impactSource = CreateAudioSource(root.transform, "Audio_Impact", 0.12f);
            impactSource.loop = false;
            impactSource.volume = 1f;

            AudioSource scrapeSource = CreateAudioSource(root.transform, "Audio_Scrape", 0.12f);
            AudioSource rumbleSource = CreateAudioSource(root.transform, "Audio_Rumble", 0.1f);

            // The verge's own voice, crossfaded against the one above it on a single level — see
            // ContactAudio.UpdateRumble for why the pair is one contact patch and not two sounds.
            AudioSource gritSource = CreateAudioSource(root.transform, "Audio_Grit", 0.1f);

            // Fully 2D, which nothing else on this car is. Rain has no position: it is not coming from
            // the engine bay or from under the wheels, it is everywhere the car is, and any spatial
            // blend at all would make it swing around the listener as the camera turns.
            AudioSource rainSource = CreateAudioSource(root.transform, "Audio_Rain", 0f);

            ContactAudio contactAudio = root.AddComponent<ContactAudio>();
            HorizonAssetUtility.Configure(contactAudio, serialized =>
            {
                serialized.FindProperty("impactSource").objectReferenceValue = impactSource;
                serialized.FindProperty("scrapeSource").objectReferenceValue = scrapeSource;
                serialized.FindProperty("rumbleSource").objectReferenceValue = rumbleSource;
                serialized.FindProperty("gritSource").objectReferenceValue = gritSource;
            });

            // Same argument as the engine layers above, and it bites harder here: two of these three are
            // silent until the car touches something it should not, so a missing reference is a layer
            // nobody discovers until the day they crash and hear nothing. The vehicle reference is
            // asserted further down, where the controller exists.
            HorizonAssetUtility.AssertReferenceAssigned(contactAudio, "impactSource");
            HorizonAssetUtility.AssertReferenceAssigned(contactAudio, "scrapeSource");
            HorizonAssetUtility.AssertReferenceAssigned(contactAudio, "rumbleSource");
            HorizonAssetUtility.AssertReferenceAssigned(contactAudio, "gritSource");

            // On the car and not on the camera, for the one reason that matters: VehicleCover is here.
            // A tunnel has to silence the sky, and the upward ray that already fades the engine's reverb
            // is the answer — see RainAudio. Its level is written by WeatherDirector.
            RainAudio rainAudio = root.AddComponent<RainAudio>();
            HorizonAssetUtility.Configure(rainAudio, serialized =>
            {
                serialized.FindProperty("source").objectReferenceValue = rainSource;
                serialized.FindProperty("cover").objectReferenceValue = cover;
            });

            HorizonAssetUtility.AssertReferenceAssigned(rainAudio, "source");
            HorizonAssetUtility.AssertReferenceAssigned(rainAudio, "cover");

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

            // And the contact layers, for the same reason and in the same shape: the sources were made
            // with the rest of the audio, but the car they listen to is only born here.
            HorizonAssetUtility.Configure(contactAudio, serialized =>
                serialized.FindProperty("vehicle").objectReferenceValue = controller);

            HorizonAssetUtility.AssertReferenceAssigned(contactAudio, "vehicle");

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
        /// Builds the cars other players are drawn with: seven of them, parked below the world until
        /// somebody joins.
        ///
        /// <para><b>Baked rather than instantiated, and there was no third option.</b> Nothing in this
        /// project can reach <c>Vehicle_Prototype.prefab</c> at run time — there is no
        /// <c>Resources</c> folder, no Addressables and no serialized reference outside this file — so
        /// "spawn a car when somebody joins" is a loading mechanism rather than a line of code. The
        /// traffic pool has answered the same question for ninety-six cars since it was written.</para>
        ///
        /// <para><b>Each one carries a <see cref="VehicleBodySet"/> with four of its five references
        /// left null.</b> That class checks <c>controller</c>, <c>lights</c>, <c>hull</c> and
        /// <c>engineAudio</c> separately, so a set with only bodies, wheels and paints does exactly
        /// what is wanted here: swap a mesh, swap the tyre, swap the paint, and touch no physics, no
        /// beams and no engine that this car does not have. Ten shells per slot is seventy inactive
        /// GameObjects across the pool and no extra geometry at all — the meshes are the same assets
        /// the player's own car uses.</para>
        ///
        /// <para><b>No <see cref="Light"/>, no collider, no audio.</b> The lamps are material swaps on
        /// the body's own submeshes, which is what makes eight cars affordable against a budget that
        /// allows realtime shadows from the sun and nothing else. The collider is absent because a
        /// snapshot-driven body is kinematic and would win every exchange with the player. And the
        /// silence is the note against <c>EngineAudio</c> applied honestly: the car is the subject of
        /// this game, and a second engine at a distance is the "second thing to listen to" that got
        /// the ambient world audio deleted.</para>
        /// </summary>
        private static RemoteCarPool BuildRemoteCarPool(
            PrototypeMaterials materials, WorldStreamer streamer, Vector3 park)
        {
            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.PlayerProfiles;
            int slotCount = NetProtocol.MaxPeers - 1;

            var bodyMeshes = new Mesh[profiles.Length];
            var wheelMeshes = new Mesh[profiles.Length];
            var configs = new VehicleConfig[profiles.Length];

            for (int i = 0; i < profiles.Length; i++)
            {
                // Loaded, not rebuilt. BuildVehiclePrefab has just written these, and a second copy of
                // the same geometry under a different asset name would double the memory and give the
                // two cars different silhouettes the first time anybody edited one of them.
                bodyMeshes[i] = AssetDatabase.LoadAssetAtPath<Mesh>(
                    $"{GeneratedFolder}/CarBodyMesh_{profiles[i].Name}.asset");
                wheelMeshes[i] = AssetDatabase.LoadAssetAtPath<Mesh>(
                    $"{GeneratedFolder}/WheelMesh_{profiles[i].Name}.asset");
                configs[i] = LoadVehicleConfig(profiles[i].Name);

                if (bodyMeshes[i] == null || wheelMeshes[i] == null)
                {
                    Debug.LogError(
                        $"[Horizon] Remote car pool: no mesh for '{profiles[i].Name}'. It is built by "
                        + "BuildVehiclePrefab, which has to run first.");
                    return null;
                }
            }

            const float trackX = CarMeshBuilder.TrackHalfWidth;
            const float baseZ = CarMeshBuilder.WheelBaseHalf;

            var anchors = new[]
            {
                new Vector3(-trackX, 0f, baseZ),
                new Vector3(trackX, 0f, baseZ),
                new Vector3(-trackX, 0f, -baseZ),
                new Vector3(trackX, 0f, -baseZ),
            };
            var wheelNames = new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" };

            var poolObject = new GameObject("RemoteCars");
            var cars = new RemoteCar[slotCount];

            for (int slot = 0; slot < slotCount; slot++)
            {
                var carObject = new GameObject($"RemoteCar_{slot}");
                carObject.transform.SetParent(poolObject.transform, false);
                carObject.transform.position = park;

                var bodiesRoot = new GameObject("Bodies");
                bodiesRoot.transform.SetParent(carObject.transform, false);

                var bodyObjects = new GameObject[profiles.Length];

                for (int i = 0; i < profiles.Length; i++)
                {
                    bodyObjects[i] = CreateMeshObject(
                        bodiesRoot.transform,
                        $"Body_{profiles[i].Name}",
                        bodyMeshes[i],
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

                    bodyObjects[i].SetActive(i == 0);
                }

                var pivots = new Transform[4];
                var filters = new MeshFilter[4];

                for (int i = 0; i < 4; i++)
                {
                    var pivot = new GameObject(wheelNames[i]);
                    pivot.transform.SetParent(carObject.transform, false);
                    pivot.transform.localPosition =
                        anchors[i] - new Vector3(0f, configs[0].SuspensionRestLength, 0f);
                    pivots[i] = pivot.transform;

                    MeshFilter filter = pivot.AddComponent<MeshFilter>();
                    filter.sharedMesh = wheelMeshes[0];
                    filters[i] = filter;

                    pivot.AddComponent<MeshRenderer>().sharedMaterials =
                        new[] { materials.Tyre, materials.CarRim };
                }

                VehicleBodySet bodySet = carObject.AddComponent<VehicleBodySet>();
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
                        element.FindPropertyRelative("WheelMesh").objectReferenceValue = wheelMeshes[i];
                        element.FindPropertyRelative("Headlights").arraySize = 0;
                    }

                    HorizonAssetUtility.SetObjectArray(serialized, "paints", materials.CarPaints);
                    HorizonAssetUtility.SetObjectArray(serialized, "wheelFilters", filters);

                    // controller, lights, hull and engineAudio are deliberately left null. See the
                    // remarks above, and VehicleBodySet.Select, which checks each of them.
                });

                RemoteCar car = carObject.AddComponent<RemoteCar>();
                car.SetParts(
                    bodySet,
                    pivots,
                    anchors,
                    materials.HeadLampLit,
                    materials.LightFront,
                    materials.LightRear,
                    materials.TailNight,
                    materials.TailBrake);

                EditorUtility.SetDirty(car);
                cars[slot] = car;
            }

            RemoteCarPool pool = poolObject.AddComponent<RemoteCarPool>();
            pool.SetCars(cars, streamer, park);
            EditorUtility.SetDirty(pool);

            ReportRemoteCarPool(pool, profiles.Length, materials.CarPaints.Length);
            return pool;
        }

        /// <summary>
        /// Says what the pool came out as, and complains when any of it is empty.
        ///
        /// <para>A pool with no cars, no bodies or no paints builds without a word, validates cleanly
        /// and plays exactly like one that works right up until somebody joins — which is the failure
        /// shape the snow line and the surface tagging are already warned about here. There is no
        /// picture that answers it either, because an empty pool is invisible by construction.</para>
        /// </summary>
        private static void ReportRemoteCarPool(RemoteCarPool pool, int bodies, int paints)
        {
            int slots = pool != null ? pool.SlotCount : 0;

            Debug.Log($"[Horizon] Remote cars: {slots} slots, {bodies} bodies and {paints} paints each, "
                      + $"for a protocol that carries {NetProtocol.MaxPeers} players "
                      + $"({NetProtocol.MaxPeers - 1} of them somebody else).");

            if (slots == 0 || bodies == 0 || paints == 0)
            {
                Debug.LogWarning(
                    "[Horizon] The remote car pool is empty. Nobody who joins will be visible, and "
                    + "nothing else in the build or the checks will say so.");
                return;
            }

            if (slots < NetProtocol.MaxPeers - 1)
            {
                Debug.LogWarning(
                    $"[Horizon] The pool has {slots} cars but the protocol admits "
                    + $"{NetProtocol.MaxPeers - 1} guests. The last to join would be heard and not seen.");
            }
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
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

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
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk ebentalChunk = ebentalObject.AddComponent<WorldChunk>();
            ebentalChunk.RecalculateBounds();
            ebentalChunk.SetBounds(ebentalChunk.Center, 100000f);

            // The region the country road runs through, hung off the road itself rather than off a box
            // of coordinates — see LandRegion for why a rectangle here would recolour a hairpin of the
            // pass. Everything that gives the Ebental its own look reads this.
            LandRegion ebental = LandRegion.Ebental(ebentalPath);

            // The wood on the pass's lower slopes. Two kilometres of it, on a road that had no region
            // at all until now — the climb begins inside a forest and drives out of it, which is the
            // first thing in this world that a region has been able to say since LandRegion.EndAlong
            // existed. The bounds are the course's own, so retuning the arrival moves the wood with the
            // town rather than leaving it standing over the last houses.
            LandRegion passWood = LandRegion.Forest(
                "Talwald", path, MountainPassCourse.ForestStart, MountainPassCourse.ForestEnd);

            // --- The Stadtfeldstraße: back down to Hochstadt, and the road that closes the ring.
            //
            // Built from the city outwards, so it starts at the boulevard's last node rather than at
            // the end of the arterial — see StadtfeldCourse for why those are 120 m and one working
            // road apart. Same cross-section as its neighbours again, for the reason the Ebental keeps
            // the pass's.
            var stadtfeldPathObject = new GameObject("StadtfeldRoadPath");
            stadtfeldPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath stadtfeldPath = stadtfeldPathObject.AddComponent<RoadPath>();

            RoadCourse stadtfeldCourse = StadtfeldCourse.Build();
            stadtfeldPath.SetControlPoints(stadtfeldCourse.ControlPoints);
            ReportCourse(stadtfeldCourse, stadtfeldPath, "Stadtfeld road");

            Mesh stadtfeldMesh = BuildBranchRoad(
                stadtfeldPath, roadShape, "StadtfeldRoadMesh",
                ebentalPath, roadShape, EbentalCourse.ForkPoint, "Stadtfeld road");
            stadtfeldMesh = HorizonAssetUtility.ReplaceAsset(
                stadtfeldMesh, GeneratedFolder + "/StadtfeldRoadMesh.asset");

            GameObject stadtfeldObject = CreateMeshObject(worldRoot.transform, "StadtfeldRoad",
                stadtfeldMesh, new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk stadtfeldChunk = stadtfeldObject.AddComponent<WorldChunk>();
            stadtfeldChunk.RecalculateBounds();
            stadtfeldChunk.SetBounds(stadtfeldChunk.Center, 100000f);

            // A second Ebental, because a LandRegion binds to exactly one IRoadPath. The two overlap
            // where this road leaves the fork, and that is harmless for the reason the two Anadolus
            // below are harmless: same palette, same tree mix, so RegionFor picking either one gives
            // the same ground. Without it the road would fall outside every region — LandRegion's
            // EdgeReach is 260 m — and a country lane out of a city would come up on the mountain's
            // own grey.
            LandRegion stadtfeld = LandRegion.Ebental(stadtfeldPath);

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
                kalkgratMesh, new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk kalkgratChunk = kalkgratObject.AddComponent<WorldChunk>();
            kalkgratChunk.RecalculateBounds();
            kalkgratChunk.SetBounds(kalkgratChunk.Center, 100000f);

            // The wood on the Kalkgrat's climb, and it is here for what is on the far side of the bore
            // rather than for itself — see KalkgratCourse.ForestStart. It reads RevealDistance, which
            // Build() sets during the walk above, so it must be constructed after the course and not
            // beside the other regions.
            LandRegion kalkgratWood = LandRegion.Forest(
                "Kalkgratwald", kalkgratPath, KalkgratCourse.ForestStart, KalkgratCourse.ForestEnd);

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
                meerengeMesh, new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk meerengeChunk = meerengeObject.AddComponent<WorldChunk>();
            meerengeChunk.RecalculateBounds();
            meerengeChunk.SetBounds(meerengeChunk.Center, 100000f);

            // The far shore, hung off the same road the corniche is on and starting at the eastern
            // anchorage. It has to be a distance along the road rather than a road of its own, because
            // what separates the two countries here is 1250 m of bridge and not a different piece of
            // tarmac — see LandRegion.StartAlong.
            LandRegion anadolu = LandRegion.Anadolu(
                meerengePath, MeerengeCourse.CrossingStart + MeerengeCourse.StructureLength);

            // --- On round the eastern cape to Yalıköy and up into the hills behind it. The bridge now
            // leads somewhere; see YalikoyCourse for why there had to be something here at all.
            var yalikoyPathObject = new GameObject("YalikoyRoadPath");
            yalikoyPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath yalikoyPath = yalikoyPathObject.AddComponent<RoadPath>();

            RoadCourse yalikoyCourse = YalikoyCourse.Build();
            yalikoyPath.SetControlPoints(yalikoyCourse.ControlPoints);
            ReportCourse(yalikoyCourse, yalikoyPath, "Yalıköy road");

            Mesh yalikoyMesh = RoadMeshBuilder.BuildRoad(yalikoyPath, roadShape, "YalikoyRoadMesh");
            yalikoyMesh = HorizonAssetUtility.ReplaceAsset(
                yalikoyMesh, GeneratedFolder + "/YalikoyRoadMesh.asset");

            GameObject yalikoyObject = CreateMeshObject(worldRoot.transform, "YalikoyRoad",
                yalikoyMesh, new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk yalikoyChunk = yalikoyObject.AddComponent<WorldChunk>();
            yalikoyChunk.RecalculateBounds();
            yalikoyChunk.SetBounds(yalikoyChunk.Center, 100000f);

            // A second Anadolu, because a LandRegion binds to exactly one IRoadPath — see
            // LandRegion.RoadProximity. The two overlap at the join and that is harmless here and only
            // here: they are the same region with the same palette and the same tree mix, so RegionFor
            // picking either of them gives the same ground. Two regions that differed could not do this.
            LandRegion yalikoyRegion = LandRegion.Anadolu(yalikoyPath, 0f);

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

            // Both carriageways stop short of both ends, and the terminus paving takes over there. The
            // cut is found on each carriageway by position rather than by subtracting the same number
            // from its own length: a carriageway is an offset of the median and their arc lengths differ
            // through every bend, so the two ribbons and the paving between them would otherwise meet at
            // three different places. Same helper, same reason, as the merge and the forks.
            Vector3 westTaperEnd = motorwayPath.GetPositionAtDistance(
                MotorwayTerminusBuilder.TerminusLength);
            Vector3 eastTaperEnd = motorwayPath.GetPositionAtDistance(
                Mathf.Max(0f, motorwayPath.Length - MotorwayTerminusBuilder.TerminusLength));

            BuildCarriageway(worldRoot.transform, "CarriagewayWest", westbound, motorwayShape, materials,
                NearestDistanceOn(westbound, westTaperEnd), NearestDistanceOn(westbound, eastTaperEnd));
            BuildCarriageway(worldRoot.transform, "CarriagewayEast", eastbound, motorwayShape, materials,
                NearestDistanceOn(eastbound, westTaperEnd), NearestDistanceOn(eastbound, eastTaperEnd));

            Mesh linkMesh = RoadMeshBuilder.BuildRoad(linkPath, roadShape, "MotorwayLinkMesh");
            linkMesh = HorizonAssetUtility.ReplaceAsset(
                linkMesh, GeneratedFolder + "/MotorwayLinkMesh.asset");

            GameObject linkObject = CreateMeshObject(worldRoot.transform, "MotorwayLink", linkMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk linkChunk = linkObject.AddComponent<WorldChunk>();
            linkChunk.RecalculateBounds();
            linkChunk.SetBounds(linkChunk.Center, 100000f);

            // --- The Weissjoch: off the motorway's western leg and nine hundred metres up into the
            // snow. The highest thing in the world by a factor of four and a half, and the only road
            // here that is a dead end on purpose.
            var weissjochPathObject = new GameObject("WeissjochRoadPath");
            weissjochPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath weissjochPath = weissjochPathObject.AddComponent<RoadPath>();

            RoadCourse weissjochCourse = WeissjochCourse.Build();
            weissjochPath.SetControlPoints(weissjochCourse.ControlPoints);
            ReportCourse(weissjochCourse, weissjochPath, "Weissjoch road");

            Mesh weissjochMesh = RoadMeshBuilder.BuildRoad(weissjochPath, roadShape, "WeissjochRoadMesh");
            weissjochMesh = HorizonAssetUtility.ReplaceAsset(
                weissjochMesh, GeneratedFolder + "/WeissjochRoadMesh.asset");

            GameObject weissjochObject = CreateMeshObject(worldRoot.transform, "WeissjochRoad",
                weissjochMesh, new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk weissjochChunk = weissjochObject.AddComponent<WorldChunk>();
            weissjochChunk.RecalculateBounds();
            weissjochChunk.SetBounds(weissjochChunk.Center, 100000f);

            // The first region in the world that decides anything by altitude — a tree line at 460 m and
            // a snow line at 650, both absolute metres. See LandRegion.TreeLineElevation for why they
            // cannot be the fraction of the pass's own climb that every other road here uses.
            LandRegion weissjoch = LandRegion.Weissjoch(weissjochPath);

            // --- The Weissjochring: fourteen and a half kilometres of closed circuit on the shoulder
            // below the col, and its access road down from it. The circuit is the only road in this
            // world that closes on itself, so it is the only one paved as a loop — see
            // RoadCourse.IsClosed for what that changes and why the flag was worth having.
            RoadShape circuitShape = RoadShape.Circuit;

            var ringPathObject = new GameObject("WeissjochringPath");
            ringPathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath ringPath = ringPathObject.AddComponent<RoadPath>();

            RoadCourse ringCourse = WeissjochringCourse.Build();
            ringPath.SetControlPoints(ringCourse.ControlPoints, ringCourse.IsClosed);
            ReportCourse(ringCourse, ringPath, "Weissjochring");

            Mesh ringMesh = RoadMeshBuilder.BuildRoad(ringPath, circuitShape, "WeissjochringMesh");
            ringMesh = HorizonAssetUtility.ReplaceAsset(
                ringMesh, GeneratedFolder + "/WeissjochringMesh.asset");

            GameObject ringObject = CreateMeshObject(worldRoot.transform, "Weissjochring",
                ringMesh, new[] { materials.CircuitSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk ringChunk = ringObject.AddComponent<WorldChunk>();
            ringChunk.RecalculateBounds();
            ringChunk.SetBounds(ringChunk.Center, 100000f);

            var ringAccessObject = new GameObject("WeissjochringAccessPath");
            ringAccessObject.transform.SetParent(worldRoot.transform, false);
            RoadPath ringAccessPath = ringAccessObject.AddComponent<RoadPath>();

            RoadCourse ringAccessCourse = WeissjochringCourse.BuildAccess();
            ringAccessPath.SetControlPoints(ringAccessCourse.ControlPoints);
            ReportCourse(ringAccessCourse, ringAccessPath, "Weissjochring access road");

            Mesh ringAccessMesh = BuildBranchRoad(
                ringAccessPath, roadShape, "WeissjochringAccessMesh",
                ringPath, circuitShape, WeissjochringCourse.JunctionPoint,
                "Weissjochring access road");
            ringAccessMesh = HorizonAssetUtility.ReplaceAsset(
                ringAccessMesh, GeneratedFolder + "/WeissjochringAccessMesh.asset");

            GameObject ringAccessObjectMesh = CreateMeshObject(worldRoot.transform,
                "WeissjochringAccess", ringAccessMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk ringAccessChunk = ringAccessObjectMesh.AddComponent<WorldChunk>();
            ringAccessChunk.RecalculateBounds();
            ringAccessChunk.SetBounds(ringAccessChunk.Center, 100000f);

            // The circuit's own region. Same palette and the same two absolute altitude bands as the
            // mountain it stands on — see LandRegion.Weissjochring for why its densities are not the
            // same, which is a tile-budget decision rather than a taste one.
            LandRegion weissjochring = LandRegion.Weissjochring(ringPath);

            // --- The Bahçe Ring: Istanbul Park, at its own scale, in the empty quadrant beyond the
            // end of Yalıköy, and its access road down from the plateau. The second closed loop in this
            // world and therefore the second road paved as one — see RoadCourse.IsClosed.
            var bahcePathObject = new GameObject("BahceRingPath");
            bahcePathObject.transform.SetParent(worldRoot.transform, false);
            RoadPath bahcePath = bahcePathObject.AddComponent<RoadPath>();

            RoadCourse bahceCourse = BahceRingCourse.Build();
            bahcePath.SetControlPoints(bahceCourse.ControlPoints, bahceCourse.IsClosed);
            ReportCourse(bahceCourse, bahcePath, "Bahçe Ring");

            Mesh bahceMesh = RoadMeshBuilder.BuildRoad(bahcePath, circuitShape, "BahceRingMesh");
            bahceMesh = HorizonAssetUtility.ReplaceAsset(
                bahceMesh, GeneratedFolder + "/BahceRingMesh.asset");

            GameObject bahceObject = CreateMeshObject(worldRoot.transform, "BahceRing",
                bahceMesh, new[] { materials.CircuitSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk bahceChunk = bahceObject.AddComponent<WorldChunk>();
            bahceChunk.RecalculateBounds();
            bahceChunk.SetBounds(bahceChunk.Center, 100000f);

            var bahceAccessObject = new GameObject("BahceRingAccessPath");
            bahceAccessObject.transform.SetParent(worldRoot.transform, false);
            RoadPath bahceAccessPath = bahceAccessObject.AddComponent<RoadPath>();

            RoadCourse bahceAccessCourse = BahceRingCourse.BuildAccess();
            bahceAccessPath.SetControlPoints(bahceAccessCourse.ControlPoints);
            ReportCourse(bahceAccessCourse, bahceAccessPath, "Bahçe Ring access road");

            Mesh bahceAccessMesh = BuildBranchRoad(
                bahceAccessPath, roadShape, "BahceRingAccessMesh",
                bahcePath, circuitShape, BahceRingCourse.JunctionPoint,
                "Bahçe Ring access road");
            bahceAccessMesh = HorizonAssetUtility.ReplaceAsset(
                bahceAccessMesh, GeneratedFolder + "/BahceRingAccessMesh.asset");

            GameObject bahceAccessMeshObject = CreateMeshObject(worldRoot.transform,
                "BahceRingAccess", bahceAccessMesh,
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

            WorldChunk bahceAccessChunk = bahceAccessMeshObject.AddComponent<WorldChunk>();
            bahceAccessChunk.RecalculateBounds();
            bahceAccessChunk.SetBounds(bahceAccessChunk.Center, 100000f);

            // The valley's own region, twice — once against the lap and once against the road that
            // leads to it, which is the pattern LandRegion.Ebental already uses for the Stadtfeld. One
            // region binds to one path, and the blossom has to be growing beside the approach as well
            // as beside the circuit or the layby in it stands in Anadolu's dry scrub. The access road's
            // copy starts partway down, because the change of country happens on the tarmac.
            LandRegion bahceRegion = LandRegion.Bahce(bahcePath);
            LandRegion bahceApproach = LandRegion.Bahce(
                bahceAccessPath, BahceRingCourse.RegionStartAlong);

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
                new[] { materials.RoadSurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

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

            BuildMotorwayMerge(worldRoot.transform, "MotorwayMerge",
                out float rampCapOnMedian, out float rampMergeOnMedian,
                motorwayPath, westbound, motorwayShape, roadShape, linkPath, materials);

            // The Weissjoch's own ramp, three kilometres west of the interchange. The out-params are
            // measured and thrown away: TrafficNetworkBuilder is written for exactly one interchange —
            // one bool, one node, and a lane cut that breaks the nearside carriageway into exactly two
            // pieces — so wiring a second into it is a job of its own. Said plainly rather than hidden:
            // cars stream past this exit and none of them take it.
            BuildMotorwayMerge(worldRoot.transform, "WeissjochMerge",
                out _, out _,
                motorwayPath, westbound, motorwayShape, roadShape, weissjochPath, materials);
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

            // The circuit's paddock, and it has to go in before the field rather than after it. A level
            // area asked for afterwards comes out perfectly flat hovering over a hillside, with nothing
            // complaining — the recorded failure the forecourts already pay for. It needs no tile bounds
            // of its own: it sits on the main straight, so the tiles under it are the road's.
            AddPaddockSamples(levelSamples, terrainShape, Weissjochring);
            AddPaddockSamples(levelSamples, terrainShape, BahceRing);

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

            // Yalıköy hangs off its own seafront road rather than an axis, the way Talheim hangs off the
            // pass: that stretch is a dead straight, so the town-local mapping has no radius to fold
            // against and the village needs no coordinate system of its own. See YalikoyLayout.
            TownBuild yalikoy = PrepareTown(
                YalikoyCourse.TownName, YalikoyLayout.Build(), yalikoyPath, TownShape.Yalikoy,
                worldRoot.transform, roadShape, terrainShape, levelSamples);

            var towns = new[] { talheim, hochstadt, seeburg, yalikoy };

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
            fuelStations.AddRange(FuelStationBuilder.Sites(stadtfeldPath, stadtfeldCourse, roadShape));
            fuelStations.AddRange(
                FuelStationBuilder.Sites(westbound, motorwayCourse, motorwayShape, -1f));
            fuelStations.AddRange(
                FuelStationBuilder.Sites(eastbound, motorwayCourse, motorwayShape, 1f));
            fuelStations.AddRange(FuelStationBuilder.Sites(coastPath, coastCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(weissjochPath, weissjochCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(ringPath, ringCourse, circuitShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(kalkgratPath, kalkgratCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(meerengePath, meerengeCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(yalikoyPath, yalikoyCourse, roadShape));
            fuelStations.AddRange(FuelStationBuilder.Sites(bahcePath, bahceCourse, circuitShape));

            // Every carriageway in the world, so a pad can be kept off all of them and not merely off
            // the one it belongs to. The pass is the case that matters: its switchbacks stack legs
            // forty metres apart in plan and fifteen in height, so a platform at the summit is directly
            // over the road below it.
            var padRoads = new[]
            {
                new FuelStationBuilder.NearbyRoad(path, roadShape, "the pass"),
                new FuelStationBuilder.NearbyRoad(ebentalPath, roadShape, "the Ebental road"),
                new FuelStationBuilder.NearbyRoad(stadtfeldPath, roadShape, "the Stadtfeld road"),
                new FuelStationBuilder.NearbyRoad(westbound, motorwayShape, "the westbound carriageway"),
                new FuelStationBuilder.NearbyRoad(eastbound, motorwayShape, "the eastbound carriageway"),
                new FuelStationBuilder.NearbyRoad(linkPath, roadShape, "the motorway link"),
                new FuelStationBuilder.NearbyRoad(coastPath, roadShape, "the coast road"),
                new FuelStationBuilder.NearbyRoad(weissjochPath, roadShape, "the Weissjoch road"),
                new FuelStationBuilder.NearbyRoad(ringPath, circuitShape, "the Weissjochring"),
                new FuelStationBuilder.NearbyRoad(
                    ringAccessPath, roadShape, "the Weissjochring access road"),
                new FuelStationBuilder.NearbyRoad(kalkgratPath, roadShape, "the Kalkgrat road"),
                new FuelStationBuilder.NearbyRoad(meerengePath, roadShape, "the Meerenge road"),
                new FuelStationBuilder.NearbyRoad(yalikoyPath, roadShape, "the Yalıköy road"),
                new FuelStationBuilder.NearbyRoad(bahcePath, circuitShape, "the Bahçe Ring"),
                new FuelStationBuilder.NearbyRoad(
                    bahceAccessPath, roadShape, "the Bahçe Ring access road"),
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
                new MountainField.FieldRoad(stadtfeldPath),

                new MountainField.FieldRoad(westbound, motorwayCourse),
                new MountainField.FieldRoad(eastbound, motorwayCourse),
                new MountainField.FieldRoad(linkPath),

                // The coast road is in here for the terrain as much as for the shelf: tiles are listed
                // around whatever the field calls a road, so the corridor out to the water — and the
                // ground the sea will be dug into — arrives with the road rather than as a region
                // somebody has to remember to add.
                new MountainField.FieldRoad(coastPath),
                new MountainField.FieldRoad(weissjochPath),
                new MountainField.FieldRoad(ringPath),
                new MountainField.FieldRoad(ringAccessPath),

                // Both of these hand over their course as well as their path, and both need to. The
                // Kalkgrat has a viaduct across a ravine and the Meerenge has the crossing, and without
                // the course the field knows about neither — it would carry the valley floor up to the
                // Schluchtbrücke's deck, and lay a sixty-metre causeway across the strait.
                new MountainField.FieldRoad(kalkgratPath, kalkgratCourse),
                new MountainField.FieldRoad(meerengePath, meerengeCourse),

                // The Yalıköy road has one bore and no bridge, so the course buys it nothing the shelf
                // would not do anyway — but it is here for the same reason the coast road is: the
                // corridor out to the bay, and the ground the bay is dug into, arrive with the road.
                new MountainField.FieldRoad(yalikoyPath, yalikoyCourse),

                new MountainField.FieldRoad(bahcePath),
                new MountainField.FieldRoad(bahceAccessPath),
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

            // --- Yalıköy's bay, and the harbour dug into it.
            //
            // A Sea rather than more of the Boğaz, and the two are kept a cape apart on purpose: a sea
            // *sets* the ground under it while a river only caps it, so two of them over the same water
            // fight and the loser leaves a step across the middle of it. Everything here is measured off
            // the seafront road, which is the one line the village floor, the waterline and the quay all
            // have to agree with.
            Vector3 yalikoyFront = yalikoyPath.GetPositionAtDistance(YalikoyCourse.Waterfront);

            // Seaward is the road's left, because TownShape.ToWorld puts positive across to its right
            // and Yalıköy's positive across is inland — see TownShape.Yalikoy.
            Vector3 yalikoySeaward = -yalikoyPath.GetRightAtDistance(YalikoyCourse.Waterfront);

            float bayLevel = yalikoyFront.y - YalikoyCourse.SeaFreeboard;

            waterPlans.Add(WaterPlan.Sea(
                "Yalı Koyu",
                Flat(yalikoyFront + yalikoySeaward * (YalikoyCourse.ShoreOffset + YalikoyCourse.BayRadius)),
                radius: YalikoyCourse.BayRadius,
                bankEase: YalikoyCourse.BayBankEase,
                depth: YalikoyCourse.BayDepth,
                surfaceY: bayLevel,
                bedScale: YalikoyCourse.BayBedScale));

            // The harbour. A capping body rather than a second sea, for the reason WaterPlan.Basin
            // gives: it digs the basin out of the land it reaches and leaves the deeper of the two where
            // it lies over the bay's own bed, so there is no step at the mouth and no ordering to
            // remember.
            Vector3 yalikoyBasinAt = yalikoyPath.GetPositionAtDistance(YalikoyCourse.BasinAlong)
                                     + yalikoySeaward * -YalikoyCourse.BasinAcross;

            waterPlans.Add(WaterPlan.Basin(
                "Yalıköy Limanı",
                Flat(yalikoyBasinAt),
                radius: YalikoyCourse.BasinRadius,
                bankEase: YalikoyCourse.BasinBankEase,
                depth: YalikoyCourse.BasinDepth,
                surfaceY: bayLevel));

            WaterBody[] waters = WaterPlanner.Resolve(
                waterPlans, field, bridgeRoads, out string waterReport);

            field.SetWater(waters);
            ValidateWater(waters, field, roads, towns);

            Debug.Log($"[Horizon] Water: {waters.Length} bodies.{waterReport}");

            BuildWaterHazard(worldRoot.transform, waters);


            ValidateRoadClearance(path, roadShape, field, course);
            ValidateRoadClearance(ebentalPath, roadShape, field, ebentalCourse, "Ebental");
            ValidateRoadClearance(stadtfeldPath, roadShape, field, stadtfeldCourse, "Stadtfeld");
            ValidateRoadClearance(kalkgratPath, roadShape, field, kalkgratCourse, "Kalkgrat");
            ValidateRoadClearance(meerengePath, roadShape, field, meerengeCourse, "Meerenge");
            ValidateRoadClearance(yalikoyPath, roadShape, field, yalikoyCourse, "Yalıköy");
            ValidateRoadClearance(weissjochPath, roadShape, field, weissjochCourse, "Weissjoch");
            ValidateRoadClearance(ringPath, circuitShape, field, ringCourse, "Weissjochring");
            ValidateCircuitClosure(ringCourse, ringPath, "the Weissjochring");
            ValidateInfieldCoverage(field, terrainShape, ringPath, "the Weissjochring");
            ValidateRoadClearance(
                ringAccessPath, roadShape, field, ringAccessCourse, "Weissjochring access road");
            ValidateRoadClearance(bahcePath, circuitShape, field, bahceCourse, "Bahçe Ring");
            ValidateCircuitClosure(bahceCourse, bahcePath, "the Bahçe Ring");
            ValidateInfieldCoverage(field, terrainShape, bahcePath, "the Bahçe Ring");
            ValidateRoadClearance(
                bahceAccessPath, roadShape, field, bahceAccessCourse, "Bahçe Ring access road");
            ValidateRoadClearance(westbound, motorwayShape, field, motorwayCourse, "Westbound");
            ValidateRoadClearance(eastbound, motorwayShape, field, motorwayCourse, "Eastbound");

            // Two paved roads that were on no list at all. The link carries every car that leaves the
            // motorway for the pass and the coast road is eight kilometres of carriageway; neither had
            // ever been asked whether it sits on the ground.
            ValidateRoadClearance(linkPath, roadShape, field, linkCourse, "Motorway link");
            ValidateRoadClearance(coastPath, roadShape, field, coastCourse, "Coast road");

            // And the other sign of the same question, for every one of them. See ValidateRoadSupport:
            // MountainField averages, so a road that has terrain dropped on it always has a neighbour
            // that has had the ground taken out from under it, and only one of the two was ever printed.
            ValidateRoadSupport(path, roadShape, field, course);
            ValidateRoadSupport(ebentalPath, roadShape, field, ebentalCourse, "Ebental");
            ValidateRoadSupport(stadtfeldPath, roadShape, field, stadtfeldCourse, "Stadtfeld");
            ValidateRoadSupport(kalkgratPath, roadShape, field, kalkgratCourse, "Kalkgrat");
            ValidateRoadSupport(meerengePath, roadShape, field, meerengeCourse, "Meerenge");
            ValidateRoadSupport(yalikoyPath, roadShape, field, yalikoyCourse, "Yalıköy");
            ValidateRoadSupport(weissjochPath, roadShape, field, weissjochCourse, "Weissjoch");
            ValidateRoadSupport(ringPath, circuitShape, field, ringCourse, "Weissjochring");
            ValidateRoadSupport(
                ringAccessPath, roadShape, field, ringAccessCourse, "Weissjochring access road");
            ValidateRoadSupport(bahcePath, circuitShape, field, bahceCourse, "Bahçe Ring");
            ValidateRoadSupport(
                bahceAccessPath, roadShape, field, bahceAccessCourse, "Bahçe Ring access road");
            ValidateRoadSupport(westbound, motorwayShape, field, motorwayCourse, "Westbound");
            ValidateRoadSupport(eastbound, motorwayShape, field, motorwayCourse, "Eastbound");
            ValidateRoadSupport(linkPath, roadShape, field, linkCourse, "Motorway link");
            ValidateRoadSupport(coastPath, roadShape, field, coastCourse, "Coast road");

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

            // And Yalıköy's bay, for the reason the Westmeer has one: water only exists where a tile
            // does, and the corridor is 200 m wide. Without this the bay ended a couple of hundred
            // metres off the quay — well inside the fog and the far plane, so it read as a lagoon with
            // an edge on it rather than as open water running out into haze. Centred on the middle of
            // the front and pushed seaward, so the tiles paid for are the ones anybody can see.
            var bayBand = new Bounds(
                yalikoyFront + yalikoySeaward * 420f,
                new Vector3(1800f, 200f, 1800f));

            BuildTerrainTiles(worldRoot.transform, path, roadShape, course, field, terrainShape,
                towns, materials, litRenderers, litSlotStart, litSlots, litSlotGroups,
                new[] { seaBand, straitBand, bayBand },
                new[]
                {
                    new MountainField.FieldRoad(ebentalPath, ebentalCourse),
                    new MountainField.FieldRoad(stadtfeldPath, stadtfeldCourse),
                    new MountainField.FieldRoad(kalkgratPath, kalkgratCourse),
                    new MountainField.FieldRoad(meerengePath, meerengeCourse),
                    new MountainField.FieldRoad(yalikoyPath, yalikoyCourse),
                    new MountainField.FieldRoad(weissjochPath, weissjochCourse),
                    new MountainField.FieldRoad(ringPath, ringCourse),
                    new MountainField.FieldRoad(ringAccessPath, ringAccessCourse),
                    new MountainField.FieldRoad(bahcePath, bahceCourse),
                    new MountainField.FieldRoad(bahceAccessPath, bahceAccessCourse),
                },

                // Order decides ties: RegionFor takes the first that reaches a tile. Anadolu's two
                // entries come before the Bahçe's so the first few hundred metres of the access road,
                // which leave the end of Yalıköy, stay the dry country they are in — the change of
                // country happens along that road and is BahceRingCourse.RegionStartAlong's business,
                // not the tile grid's.
                new[]
                {
                    // The two woods come first, and the order is load-bearing rather than tidy:
                    // RegionFor takes the first entry that reaches a tile, so a belt listed after a
                    // country would never be seen on any tile they share. Neither of these overlaps a
                    // region that exists today — the pass and the Kalkgrat had none — but they are
                    // written in the position a belt has to be in, because the next one will overlap.
                    passWood, kalkgratWood,
                    ebental, stadtfeld, weissjochring, weissjoch, anadolu, yalikoyRegion,
                    bahceRegion, bahceApproach,
                },
                ebental, ebentalPath,
                ForecourtCentres(fuelStations));
            ValidateLandmarks(field, course, path, talheim.Plan);
            MarkTownLandmarks(worldRoot.transform, talheim.Network, talheim.Plan);
            Phase(clock, "terrain, vegetation and buildings");

            // What share of that phase was the asset database rather than the geometry. The two are the
            // only candidates and a phase timer cannot tell them apart — see
            // HorizonAssetUtility.AssetIoMilliseconds for the guess this replaced.
            Debug.Log($"[Horizon] Asset writes: {HorizonAssetUtility.AssetWrites} so far, "
                      + $"{HorizonAssetUtility.AssetIoMilliseconds / 1000f:0.0} s inside ReplaceAsset.");

            BuildCoveredSections(worldRoot.transform, path, roadShape, course, field, materials);
            BuildGuardRails(worldRoot.transform, path, roadShape, field, course, materials);
            BuildDelineatorPosts(worldRoot.transform, path, roadShape, field, course, materials);

            // --- Motorway structures. Per carriageway, because a divided road has two of everything:
            // two bores through a spur, two decks over a valley, two sets of verge rails. Only the
            // barrier down the middle is single, and it runs on the median line the carriageways were
            // offset from.
            // One bore over the whole road rather than one per carriageway, and the driveable-corridor
            // check is what settled it. TunnelBuilder sweeps a massif at least 80 m across around
            // whatever path it is given, and the two carriageways are a median apart — so a bore built
            // on each buried the other one in rock, at 92 sampled points of the westbound carriageway.
            // Real motorway tunnels are twin bores because they are driven through rock from either
            // end; this one is a single span wide enough to cover both, which is the shape the tool can
            // actually build.
            //
            // This is also the widest bore in the world by a long way, which is why
            // TunnelBuilder.MoundHalfWidthFor exists: forty metres of massif is sized against how far
            // apart a switchback's legs are and has nothing to say about a fifty-metre hole.
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
            BuildMedianBarrier(worldRoot.transform, motorwayPath, motorwayShape, motorwayCourse, materials,
                GuardRailBuilder.MedianEndClearance);

            // The two ends of the motorway, where it stops being one. Built after the barrier because
            // that is the thing the terminus is undoing — see MotorwayTerminusBuilder for what was there
            // before, which was a solid wall across Hochstadt's only gate.
            //
            // The east one hands over to the city's boulevard, so its narrow end is read off
            // TownStreetShape rather than typed here: two copies of a carriageway width would agree
            // until the day the boulevard was retuned, and then the motorway would end in a step down
            // the middle of the city gate.
            RoadShape boulevardShape = roadShape;
            TownStreetShape boulevard = TownStreetShape.For(
                TownStreetKind.Boulevard, terrainShape.RoadShelfDrop);

            boulevardShape.HalfWidth = boulevard.HalfWidth;
            boulevardShape.Crown = boulevard.Crown;
            boulevardShape.SurfaceLift = boulevard.SurfaceLift;

            BuildMotorwayTerminus(worldRoot.transform, "MotorwayTerminusWest", motorwayPath,
                motorwayShape, roadShape, 0f, 1f, materials,
                "the coast road");

            BuildMotorwayTerminus(worldRoot.transform, "MotorwayTerminusEast", motorwayPath,
                motorwayShape, boulevardShape, motorwayPath.Length, -1f, materials,
                "Hochstadt's boulevard");

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

            // The Stadtfeld road: rails and posts of its own, and then the mouth of the fork it leaves
            // the Ebental by.
            //
            // The mouth goes last of the three on purpose. GuardRailBuilder and DelineatorPostBuilder
            // both read RoadCourse.IsJunction and leave 60 m of verge clear either side of the mark, so
            // by the time the throat is laid there is already nothing standing where it goes. Building
            // it first would put paving under a rail that had not yet decided not to be there — the
            // same ordering the filling stations already depend on.
            BuildGuardRails(worldRoot.transform, stadtfeldPath, roadShape, field, stadtfeldCourse,
                materials, "StadtfeldRoad");

            BuildDelineatorPosts(worldRoot.transform, stadtfeldPath, roadShape, field, stadtfeldCourse,
                materials, "StadtfeldRoad");

            BuildTrunkFork(worldRoot.transform, "TrunkFork", ebentalPath, roadShape,
                EbentalCourse.ForkPoint, stadtfeldPath, roadShape, stadtfeldMesh, materials);

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

            ValidateSuspensionBridges(meerengePath, roadShape, field, meerengeCourse,
                MeerengeCourse.Crossing);

            // The Yalıköy road: one cape bore, and rails and posts down the hairpins behind the village
            // where the hillside falls away on the outside of every one of them.
            BuildCoveredSections(worldRoot.transform, yalikoyPath, roadShape, yalikoyCourse, field,
                materials, "YalikoyRoad");

            BuildGuardRails(worldRoot.transform, yalikoyPath, roadShape, field, yalikoyCourse,
                materials, "YalikoyRoad");

            BuildDelineatorPosts(worldRoot.transform, yalikoyPath, roadShape, field, yalikoyCourse,
                materials, "YalikoyRoad");

            // The Weissjoch: a bore through the rock band and an avalanche gallery in the snow above it,
            // and then twenty-eight hairpins' worth of verge furniture. This is the most exposed road in
            // the world by a long way — on the outside of every corner on the stack the ground falls
            // away for the whole height of whatever is still below, so the rails here are not decoration
            // and the posts are the only thing marking a corner in a whiteout.
            BuildCoveredSections(worldRoot.transform, weissjochPath, roadShape, weissjochCourse, field,
                materials, "WeissjochRoad");

            BuildGuardRails(worldRoot.transform, weissjochPath, roadShape, field, weissjochCourse,
                materials, "WeissjochRoad");

            BuildDelineatorPosts(worldRoot.transform, weissjochPath, roadShape, field, weissjochCourse,
                materials, "WeissjochRoad");

            // --- The Weissjochring. Rails on both roads: a circuit cut into a mountainside is the one
            // place in this world where leaving the road is not a rare mistake, and the drop off the
            // downhill rung of a ladder is the whole height of the rung below it. No delineator posts —
            // a marker post every four metres reads as a country road, and the kerbs are what say where
            // the edge of a race track is.
            BuildGuardRails(worldRoot.transform, ringPath, circuitShape, field, ringCourse,
                materials, "Weissjochring");

            BuildGuardRails(worldRoot.transform, ringAccessPath, roadShape, field, ringAccessCourse,
                materials, "WeissjochringAccess");

            BuildDelineatorPosts(worldRoot.transform, ringAccessPath, roadShape, field, ringAccessCourse,
                materials, "WeissjochringAccess");

            BuildKerbs(worldRoot.transform, ringPath, circuitShape, ringCourse, Weissjochring,
                materials);
            BuildPaddock(worldRoot.transform, ringPath, circuitShape, Weissjochring, materials);
            BuildLapTiming(worldRoot.transform, ringPath, circuitShape, Weissjochring, materials);
            BuildStartingGrid(worldRoot.transform, ringPath, circuitShape, Weissjochring,
                TallestRideHeight() + 0.05f);

            // Last of the group, for the reason the Stadtfeld's mouth is: the throat is laid on top of
            // both carriageways, and the rails either side of it have to have decided where to stop
            // before anything is laid over them.
            BuildTrunkFork(worldRoot.transform, "WeissjochringPitFork", ringPath, circuitShape,
                WeissjochringCourse.JunctionPoint, ringAccessPath, roadShape, ringAccessMesh,
                materials);

            // --- The Bahçe Ring, the same group in the same order. Rails here are not about a drop —
            // this is a valley floor — but about the one thing a circuit has that no other road in the
            // world does: a driver deliberately using all of it.
            BuildGuardRails(worldRoot.transform, bahcePath, circuitShape, field, bahceCourse,
                materials, "BahceRing");

            BuildGuardRails(worldRoot.transform, bahceAccessPath, roadShape, field, bahceAccessCourse,
                materials, "BahceRingAccess");

            BuildDelineatorPosts(worldRoot.transform, bahceAccessPath, roadShape, field,
                bahceAccessCourse, materials, "BahceRingAccess");

            BuildKerbs(worldRoot.transform, bahcePath, circuitShape, bahceCourse, BahceRing,
                materials);
            BuildPaddock(worldRoot.transform, bahcePath, circuitShape, BahceRing, materials);
            BuildLapTiming(worldRoot.transform, bahcePath, circuitShape, BahceRing, materials);
            BuildStartingGrid(worldRoot.transform, bahcePath, circuitShape, BahceRing,
                TallestRideHeight() + 0.05f);

            BuildTrunkFork(worldRoot.transform, "BahceRingPitFork", bahcePath, circuitShape,
                BahceRingCourse.JunctionPoint, bahceAccessPath, roadShape, bahceAccessMesh,
                materials);

            // --- The filling stations. After the terrain, because the slab sits on ground that has to
            // exist first — and after the guard rails, so that the rails have already read IsForecourt
            // and left the frontage open before anything is standing on it.
            BuildFuelStations(worldRoot.transform, fuelStations, field, materials,
                litRenderers, litSlotStart, litSlots, litSlotGroups);

            BuildFillingStations(worldRoot.transform, fuelStations);

            // --- Seeburg's harbour. After the water, because every height in it is measured off the
            // surface that was resolved there, and after the terrain, because the promenade rail is laid
            // on ground that has to exist first.
            BuildHarbour(worldRoot.transform, "Seeburg", "Seeburg", seeburgAxis, TownShape.Seeburg,
                field, terrainShape, materials, seeburg.Network, basinAt, seaward, seaLevel,
                SeeburgCourse.BasinAlong, SeeburgCourse.BasinAcross, SeeburgCourse.BasinRadius,
                SeeburgCourse.BasinDepth, SeeburgCourse.ShoreOffset,
                SeeburgCourse.CityStart + 30f, SeeburgCourse.CityEnd - 30f,
                // The rail sits just outside the boulevard's footway. Read off the street's own
                // cross-section rather than typed, so it stays on the kerb line if it is ever widened.
                -(TownStreetShape.For(TownStreetKind.Boulevard, terrainShape.RoadShelfDrop).HalfOuter
                  + 1.2f),
                litRenderers, litSlotStart, litSlots, litSlotGroups);

            // And Yalıköy's, which is the same harbour one climate over. Its waterfront is the driving
            // road rather than a boulevard, so the rail stands off the road's own shoulder.
            BuildHarbour(worldRoot.transform, YalikoyCourse.TownName, "Yalikoy", yalikoyPath,
                TownShape.Yalikoy, field, terrainShape, materials, yalikoy.Network,
                yalikoyBasinAt, yalikoySeaward, bayLevel,
                YalikoyCourse.BasinAlong, YalikoyCourse.BasinAcross, YalikoyCourse.BasinRadius,
                YalikoyCourse.BasinDepth, YalikoyCourse.ShoreOffset,
                YalikoyCourse.CityStart + 30f, YalikoyCourse.CityEnd - 30f,
                -(roadShape.OuterHalfWidth + 1.2f),
                litRenderers, litSlotStart, litSlots, litSlotGroups);
            BuildDelineatorPosts(worldRoot.transform, linkPath, roadShape, field, linkCourse,
                materials, "MotorwayLink");

            TrafficNetwork routes = BuildTraffic(worldRoot.transform, towns, path, roadShape, materials,
                litRenderers, litSlotStart, litSlots, litSlotGroups,
                motorwayPath, motorwayShape, AutobahnCourse.CarriagewayOffset,
                System.Array.IndexOf(towns, hochstadt), HochstadtLayout.GatewayNode,
                linkPath, roadShape, rampCapOnMedian, rampMergeOnMedian,
                coastPath, roadShape, System.Array.IndexOf(towns, seeburg), seeburgGateway,
                ebentalPath, roadShape,
                // On past the Ebental, in the order they are driven. Traffic on the crossing is half of
                // what that structure is for: an empty bridge reads as a monument and a bridge with cars
                // on it reads as a road somebody built for a reason.
                new[]
                {
                    new TrafficNetworkBuilder.OnwardRoad(kalkgratPath, roadShape),
                    new TrafficNetworkBuilder.OnwardRoad(meerengePath, roadShape),
                    // With its town, unlike the two before it: Yalıköy hangs off this very road and
                    // puts four junctions on it. See TrafficNetworkBuilder.OnwardRoad.Town.
                    new TrafficNetworkBuilder.OnwardRoad(
                        yalikoyPath, roadShape, System.Array.IndexOf(towns, yalikoy)),
                });

            // After the routes exist, because the phase the lenses show is read off the same asset the
            // traffic obeys — which is the whole reason a light cannot be green at a junction cars are
            // stopping at.
            WireTrafficSignals(worldRoot.transform, routes, materials,
                signalRenderers, signalSlotStart, signalSlots, signalLenses);

            // After both, so one component carries the town's windows and the traffic's lamps.
            WireTownLights(worldRoot.transform, litRenderers, litSlotStart, litSlots, litSlotGroups,
                materials);

            ValidateFuelStations(
                (path, course, roadShape, "the pass", 0f, false),
                (ebentalPath, ebentalCourse, roadShape, "the Ebental road", 0f, false),
                (stadtfeldPath, stadtfeldCourse, roadShape, "the Stadtfeld road", 0f, false),
                (westbound, motorwayCourse, motorwayShape, "the westbound carriageway", -1f, false),
                (eastbound, motorwayCourse, motorwayShape, "the eastbound carriageway", 1f, false),
                (coastPath, coastCourse, roadShape, "the coast road", 0f, false),
                (kalkgratPath, kalkgratCourse, roadShape, "the Kalkgrat road", 0f, false),
                (meerengePath, meerengeCourse, roadShape, "the Meerenge road", 0f, false),
                (yalikoyPath, yalikoyCourse, roadShape, "the Yalıköy road", 0f, false),
                (weissjochPath, weissjochCourse, roadShape, "the Weissjoch road", 0f, false),

                // The circuit, and the one road here that is told it is a loop. One pump in the paddock
                // is the right number: a lap is fifteen kilometres and a tank driven hard covers a good
                // deal more, so a car that starts a lap full finishes it. Three filling stations round a
                // race track to satisfy a rule written for a country road would be the check wearing the
                // costume of a feature.
                (ringPath, ringCourse, circuitShape, "the Weissjochring", 0f, true),
                (ringAccessPath, ringAccessCourse, roadShape,
                    "the Weissjochring access road", 0f, false),
                (bahcePath, bahceCourse, circuitShape, "the Bahçe Ring", 0f, true),
                (bahceAccessPath, bahceAccessCourse, roadShape,
                    "the Bahçe Ring access road", 0f, false));

            // After every builder and before the car exists — otherwise the car is the obstruction.
            //
            // The box is the car, so it grew with the car — see DriverBoxHalfWidth.
            ValidateDriveableCorridor(path, "the pass", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(ebentalPath, "the Ebental road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(stadtfeldPath, "the Stadtfeld road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(westbound, "the westbound carriageway", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(eastbound, "the eastbound carriageway", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(linkPath, "the motorway link", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(coastPath, "the coast road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(kalkgratPath, "the Kalkgrat road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(meerengePath, "the Meerenge road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(yalikoyPath, "the Yalıköy road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(weissjochPath, "the Weissjoch road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(ringPath, "the Weissjochring", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(ringAccessPath, "the Weissjochring access road", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(bahcePath, "the Bahçe Ring", DriverBoxHalfWidth, 4f);
            ValidateDriveableCorridor(bahceAccessPath, "the Bahçe Ring access road", DriverBoxHalfWidth, 4f);
            ReportCourse(seeburgCourse, seeburgAxis, "Seeburg axis");
            Phase(clock, "validation");
            int worstJunction = ValidateStreetNetwork(talheim.Network, path, roadShape);
            MarkWorstJunction(worldRoot.transform, talheim.Network, worstJunction);
            ValidateStreetNetwork(hochstadt.Network, arterialPath, motorwayShape,
                HochstadtLayout.GatewayNode);
            ValidateStreetNetwork(seeburg.Network, seeburgAxis, roadShape, seeburgGateway);
            ValidateStreetNetwork(yalikoy.Network, yalikoyPath, roadShape);

            // And the question the walk above cannot ask, because it is handed the answer: is there a
            // way in. Measured against the road that actually arrives paved — for Hochstadt that is the
            // eastbound carriageway and not the arterial, which is a coordinate axis with no asphalt on
            // it, and for Seeburg the coast road and not the town's own axis.
            ValidateTownEntry(talheim.Network, path, roadShape, "Talheim");
            ValidateTownEntry(hochstadt.Network, eastbound, motorwayShape, "Hochstadt");
            ValidateTownEntry(seeburg.Network, coastPath, roadShape, "Seeburg");
            ValidateTownEntry(yalikoy.Network, yalikoyPath, roadShape, "Yalıköy");

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
                path, motorwayPath, arterialPath, seeburgAxis, ebentalPath, stadtfeldPath,
                kalkgratPath, meerengePath, yalikoyPath, weissjochPath, ringPath, bahcePath);
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

            // <b>The sky is written into the scene as well as onto the component, and it had never been
            // written at all.</b> This line used to read RenderSettings.skybox and hand whatever it
            // found to a `clearSky` field — which is to say the tool captured Unity's built-in default
            // and put it back, and AssertReferenceAssigned passed on it happily because a built-in
            // default is not null.
            HorizonAssetUtility.Configure(timeOfDay, serialized =>
            {
                serialized.FindProperty("sky").objectReferenceValue = materials.Sky;
            });

            RenderSettings.skybox = materials.Sky;

            // See BuildBootstrapScene for the argument. Written in both, because a scene that is loaded
            // and never made active still has to be correct — one of them being wrong is exactly the
            // kind of thing nobody would find.
            RenderSettings.defaultReflectionResolution = 64;

            HorizonAssetUtility.AssertReferenceAssigned(timeOfDay, "profile");
            HorizonAssetUtility.AssertReferenceAssigned(timeOfDay, "sun");
            HorizonAssetUtility.AssertReferenceAssigned(timeOfDay, "sky");

            BuildSpeedAtmosphere(atmosphereObject.transform, timeOfDay, materials);

            // The tone map, the grade and the bloom. On the Atmosphere object because a tone map is
            // atmosphere rather than an opinion the camera holds — the same argument ImpactEffects and
            // the speed haze are placed by.
            BuildPostProcessing(atmosphereObject.transform);

            // The one wind. On the Atmosphere object because that is what it is, and because every other
            // thing that pushes a value at the whole world from here already lives there.
            atmosphereObject.AddComponent<WindDirector>();

            // The camera's answer to a crash. On the Atmosphere object rather than the camera, because
            // it is the world reacting to the car and not the camera having an opinion of its own —
            // which is the same argument BuildSpeedAtmosphere makes for the grit. Created here and
            // wired further down, once the rig it kicks exists.
            ImpactEffects impacts = atmosphereObject.AddComponent<ImpactEffects>();

            // The other half of what the rig is told about the car: ImpactEffects covers being hit,
            // this covers coming back down. Beside it and wired the same way, and for the reason that
            // class gives — the camera cannot ask a car whether its wheels are on the ground.
            DriveFeel driveFeel = atmosphereObject.AddComponent<DriveFeel>();

            // The phone's own channel. On the Atmosphere object with the other two joins, and it finds
            // the car at run time for the reason they do — the shell is swapped by the garage.
            atmosphereObject.AddComponent<HapticsDirector>();

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

            // --- The cars other players are drawn with. Parked where the traffic pool parks its
            // spares, which is far enough below the world that nothing can see them and no terrain
            // tile has to exist for them.
            BuildRemoteCarPool(materials, streamer, new Vector3(0f, -10000f, 0f));

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

            // Without this the whole post stack is dead, and silently: renderPostProcessing lives on
            // UniversalAdditionalCameraData, a camera built by AddComponent has none, and the property's
            // default is false. Every volume in the world can be correct and every profile can be
            // populated and the frame still comes out raw. Anti-aliasing is left at None because the
            // pipeline asset's MSAA is the cheap answer on a tile GPU; FXAA here would be a second
            // opinion about the same edge.
            UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;

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

            // The rig is wired explicitly and the vehicle is left empty, which is the split
            // BuildSpeedAtmosphere already makes: the camera is in this scene and never replaced, the
            // car's shell is swapped by the garage.
            HorizonAssetUtility.Configure(impacts, serialized =>
                serialized.FindProperty("chaseCamera").objectReferenceValue = chaseCamera);

            HorizonAssetUtility.AssertReferenceAssigned(impacts, "chaseCamera");

            HorizonAssetUtility.Configure(driveFeel, serialized =>
                serialized.FindProperty("chaseCamera").objectReferenceValue = chaseCamera);

            HorizonAssetUtility.AssertReferenceAssigned(driveFeel, "chaseCamera");

            // The rain hangs off the camera rather than off the car, and it is the only effect in this
            // project that does. The grit is emitted into world space ahead of the *car* because the
            // whole point of it is the car passing it; rain falls everywhere and what has to be filled
            // is the frame. Parented here it costs one box of drops whichever way the player is looking,
            // and the simulation stays in world space so they fall straight down rather than being
            // dragged sideways with the rig.
            ParticleSystem rainParticles = BuildRain(cameraObject.transform, materials);

            // The registry of everything that changes when it rains, swept off the finished world by the
            // materials the builders themselves assigned. Must run after every road, street, forecourt
            // and deck exists — which is here, and is why it is not next to the material creation.
            WetSurfaces wetSurfaces = BuildWetSurfaces(worldRoot.transform, materials);

            WeatherDirector weather = atmosphereObject.AddComponent<WeatherDirector>();
            HorizonAssetUtility.Configure(weather, serialized =>
            {
                serialized.FindProperty("rain").objectReferenceValue = rainParticles;
                serialized.FindProperty("surfaces").objectReferenceValue = wetSurfaces;
            });

            // The cover probe is deliberately left empty and found at run time, because it is on the
            // car — the same split BuildSpeedAtmosphere makes and for the same reason.
            HorizonAssetUtility.AssertReferenceAssigned(weather, "rain");
            HorizonAssetUtility.AssertReferenceAssigned(weather, "surfaces");

            timeOfDay.Apply();

            // Rendered here, while the world objects are in the active scene and before it is saved, so
            // the temporary camera never ends up in the saved scene.
            CoursePreviewRenderer.Render(path);

            // Where the player may choose to begin. Worked out here, where the paths are, and handed to
            // the Bootstrap scene — the menu that offers them lives there and has no way to ask a road
            // anything.
            List<SpawnPoint> spawns = BuildSpawnTable(
                path, roadShape, motorwayPath, motorwayShape, arterialPath, seeburgAxis,
                ebentalPath, ebentalCourse, stadtfeldPath, kalkgratPath, meerengePath, meerengeCourse,
                yalikoyPath, weissjochPath, weissjochCourse, ringPath, ringCourse, bahcePath,
                rideHeight);

            // The map, from the same objects everything above was built from. Before the scene is
            // saved, because ReplaceAsset writes to disk and the orphan report at the end of the run
            // watches what was written.
            BuildWorldMap(
                path, roadShape, ebentalPath, stadtfeldPath, kalkgratPath, meerengePath, yalikoyPath,
                coastPath, weissjochPath, westbound, eastbound, motorwayShape, linkPath, motorwayPath,
                course, ebentalCourse, stadtfeldCourse, kalkgratCourse, meerengeCourse, yalikoyCourse,
                coastCourse, weissjochCourse, motorwayCourse, linkCourse, ringPath, ringCourse,
                ringAccessPath, bahcePath, bahceCourse, bahceAccessPath, circuitShape, towns, waters,
                spawns);

            // Last, because it is the only check in this build that asks the *scene* rather than the
            // data — every collider and every tile has to exist before a ray can be cast at one.
            ValidateSurfaces(path, roadShape, course, "Mountain pass");
            ValidateSurfaces(ebentalPath, roadShape, ebentalCourse, "Ebental");
            ValidateSurfaces(stadtfeldPath, roadShape, stadtfeldCourse, "Stadtfeld");
            ValidateSurfaces(kalkgratPath, roadShape, kalkgratCourse, "Kalkgrat");
            ValidateSurfaces(meerengePath, roadShape, meerengeCourse, "Meerenge");
            ValidateSurfaces(yalikoyPath, roadShape, yalikoyCourse, "Yalikoy");
            ValidateSurfaces(weissjochPath, roadShape, weissjochCourse, "Weissjoch");
            ValidateSurfaces(coastPath, roadShape, coastCourse, "Coast road");
            ValidateSurfaces(westbound, motorwayShape, motorwayCourse, "Motorway westbound");
            ValidateSurfaces(eastbound, motorwayShape, motorwayCourse, "Motorway eastbound");
            ValidateSurfaces(ringPath, circuitShape, ringCourse, "Weissjochring");
            ValidateSurfaces(bahcePath, circuitShape, bahceCourse, "Bahce Ring");

            ValidateSurfaceRelief(path, "Mountain pass");
            ValidateSurfaceRelief(eastbound, "Motorway eastbound");
            ValidateSurfaceRelief(ringPath, "Weissjochring");

            ValidatePostStack(camera);
            ValidateAmbient(timeOfDay);

            EditorSceneManager.SaveScene(scene, WorldScenePath);
            return spawns;
        }

        /// <summary>
        /// Walks a carriageway with a real car's wheel spacing and measures what
        /// <see cref="SurfaceRelief"/> does to its suspension.
        ///
        /// <para><b>This is the one feature in the project a picture cannot check.</b> The road looks
        /// pixel-identical with the field and without it — that is the entire point of doing it this way
        /// rather than in the mesh — so the only thing that can say whether it is present, whether it is
        /// too loud, and whether it has locked the wheels together is an arithmetic walk. Same argument
        /// <c>ValidateSurfaces</c> makes for itself, one sense further removed: that one is invisible and
        /// silent, this one is invisible, silent, and leaves the geometry untouched.</para>
        ///
        /// <para>The car is read off the configs on disk rather than typed here, which is
        /// <c>ValidateMergeSeam</c>'s lesson about a rule spelt as a number in a file the car does not
        /// pass through. The worst case is whichever car has the highest damping-to-quarter-mass ratio,
        /// because above the suspension's own resonance the load ripple goes as that and nothing
        /// else.</para>
        /// </summary>
        private static void ValidateSurfaceRelief(IRoadPath path, string name)
        {
            const float Track = CarMeshBuilder.TrackHalfWidth * 2f;
            const float Wheelbase = CarMeshBuilder.WheelBaseHalf * 2f;
            const float Step = 1f / 50f;

            if (!TryWorstCar(out string worstCar, out float mass, out float damping, out float topSpeed))
            {
                Debug.LogWarning($"[Horizon] Relief on {name}: no vehicle config could be read, so the "
                               + "load figures are unmeasured.");
                return;
            }

            float quarterMass = mass * 0.25f;
            float staticLoad = quarterMass * 9.81f;

            float peakRelief = 0f;
            float peakShaft = 0f;
            float peakLoad = 0f;
            Vector3 worstAt = Vector3.zero;
            float worstAlong = 0f;

            // How much of the field the car actually turns into motion, in three parts: heave (all four
            // wheels together), pitch (front pair against rear) and roll (left pair against right).
            //
            // Reported as amplitudes rather than as a correlation, and that is the second thing this
            // check got wrong about itself. A correlation between the front and rear pairs came out at
            // +0.74 to +0.95 on every road in the world and looked like an alarm — but two of the three
            // octaves are deliberately longer than the 3.4 m wheelbase, so all four wheels seeing nearly
            // the same height is a car riding a swell rather than a car stuck in one. The roll figure is
            // always the higher of the two for a reason no tuning will change: a car is narrower than it
            // is long, so the two sides always see more similar ground than the two ends. What actually
            // matters is whether any differential survives, and that is a length in millimetres.
            float heaveSquare = 0f;
            float pitchSquare = 0f;
            float rollSquare = 0f;
            int samples = 0;

            float advance = topSpeed * Step;
            var previous = new float[4];
            var height = new float[4];
            bool hasPrevious = false;

            for (float along = 0f; along < path.Length; along += advance)
            {
                Vector3 centre = path.GetPositionAtDistance(along);
                Vector3 forward = path.GetDirectionAtDistance(along);
                Vector3 right = path.GetRightAtDistance(along);

                for (int i = 0; i < 4; i++)
                {
                    float alongSign = i < 2 ? 1f : -1f;
                    float acrossSign = (i % 2 == 0) ? -1f : 1f;

                    Vector3 at = centre
                               + forward * (alongSign * Wheelbase * 0.5f)
                               + right * (acrossSign * Track * 0.5f);

                    // Asphalt gain: the check measures the carriageway, which is what the amplitudes
                    // were sized against. The verge is louder by construction and is not the case that
                    // decides whether the field is safe.
                    height[i] = SurfaceRelief.HeightAt(at.x, at.z, 1f);

                    peakRelief = Mathf.Max(peakRelief, Mathf.Abs(height[i]));
                }

                float heave = (height[0] + height[1] + height[2] + height[3]) * 0.25f;
                float pitch = (height[0] + height[1]) * 0.5f - (height[2] + height[3]) * 0.5f;
                float roll = (height[0] + height[2]) * 0.5f - (height[1] + height[3]) * 0.5f;

                heaveSquare += heave * heave;
                pitchSquare += pitch * pitch;
                rollSquare += roll * roll;
                samples++;

                if (hasPrevious)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        // The road-induced shaft rate with the body held still, which is the honest
                        // upper bound: the sprung mass never moves faster than the input at these
                        // frequencies.
                        float shaft = Mathf.Abs(height[i] - previous[i]) / Step;
                        peakShaft = Mathf.Max(peakShaft, shaft);

                        float load = Mathf.Abs(height[i] * ReliefStiffness + shaft * damping);
                        if (load > peakLoad)
                        {
                            peakLoad = load;
                            worstAt = centre;
                            worstAlong = along;
                        }
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    previous[i] = height[i];
                }

                hasPrevious = true;
            }

            float scale = samples > 0 ? 1f / samples : 0f;
            float heaveRms = Mathf.Sqrt(heaveSquare * scale) * 1000f;
            float pitchRms = Mathf.Sqrt(pitchSquare * scale) * 1000f;
            float rollRms = Mathf.Sqrt(rollSquare * scale) * 1000f;
            float loadShare = staticLoad > 0f ? peakLoad / staticLoad : 0f;

            Debug.Log($"[Horizon] Relief on {name}: peak {peakRelief * 1000f:0.0} mm, "
                    + $"peak shaft {peakShaft:0.000} m/s ({peakShaft / ReliefDamperClamp * 100f:0.0} % of "
                    + $"the damper clamp), peak load {loadShare * 100f:0.0} % of static on the {worstCar} "
                    + $"at ({worstAt.x:0}, {worstAt.y:0}, {worstAt.z:0}), {worstAlong:0} m along; "
                    + $"heave {heaveRms:0.00} mm rms, pitch {pitchRms:0.00} mm, roll {rollRms:0.00} mm.");

            // The loudest line here, and the reason is the snow line's: a world with no relief in it
            // builds, validates and drives exactly like one that works.
            if (peakRelief < 0.0001f)
            {
                Debug.LogError($"[Horizon] Relief on {name} is flat. The field is off, zero, or being "
                             + "asked at coordinates it happens to be level at — the road has no texture "
                             + "and nothing else in this build would say so.");
            }

            if (peakShaft > ReliefDamperClamp * 0.5f)
            {
                Debug.LogError($"[Horizon] Relief on {name} reaches {peakShaft:0.00} m/s of shaft speed, "
                             + $"over half the {ReliefDamperClamp:0} m/s clamp that exists for kerbs. "
                             + "The amplitude ladder is too loud.");
            }

            if (loadShare > 0.25f)
            {
                Debug.LogWarning($"[Horizon] Relief on {name} moves {loadShare * 100f:0} % of the "
                               + $"{worstCar}'s static wheel load. The grip curve is load-dependent and "
                               + "was tuned on a flat road.");
            }

            // The real failure this replaces a correlation with. A field the car cannot pitch or roll
            // on is one it only heaves on, which reads as the whole car breathing rather than as a road
            // — and it is what a short wavelength landing on the wheelbase or the track would produce.
            if (pitchRms < 0.05f || rollRms < 0.05f)
            {
                Debug.LogWarning($"[Horizon] Relief on {name} gives the car almost no differential to "
                               + $"work with (pitch {pitchRms:0.00} mm, roll {rollRms:0.00} mm against "
                               + $"{heaveRms:0.00} mm of heave). A short wavelength is sitting on the "
                               + $"{Wheelbase:0.00} m wheelbase or the {Track:0.00} m track, so the car "
                               + "moves up and down as a block instead of being unsettled.");
            }
        }

        /// <summary>Spring rate the relief check bills against. Matches the configs' shared value.</summary>
        private const float ReliefStiffness = 42000f;

        /// <summary>Mirrors <c>VehicleController.MaxDamperSpeed</c>, which is private to that class.</summary>
        private const float ReliefDamperClamp = 4f;

        /// <summary>
        /// The car the relief is worst for: the highest damping over quarter mass in the garage.
        ///
        /// <para>Above the suspension's own resonance the load ripple tends to amplitude times frequency
        /// times that ratio, so it is the only ranking that matters and a light car with a firm damper
        /// beats a heavy one every time.</para>
        /// </summary>
        private static bool TryWorstCar(
            out string name, out float mass, out float damping, out float topSpeed)
        {
            name = null;
            mass = 0f;
            damping = 0f;
            topSpeed = 0f;

            float worstRatio = 0f;
            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.PlayerProfiles;

            for (int i = 0; i < profiles.Length; i++)
            {
                VehicleConfig config = LoadVehicleConfig(profiles[i].Name);
                if (config == null || config.Mass <= 0f)
                {
                    continue;
                }

                topSpeed = Mathf.Max(topSpeed, config.TopSpeed);

                float ratio = config.SuspensionDamping / (config.Mass * 0.25f);
                if (ratio > worstRatio)
                {
                    worstRatio = ratio;
                    name = profiles[i].Name;
                    mass = config.Mass;
                    damping = config.SuspensionDamping;
                }
            }

            return name != null && topSpeed > 0f;
        }

        /// <summary>
        /// Checks the one thing about the sky that no picture can show, and three that no picture would
        /// be looked at for.
        ///
        /// <para><b>The line that matters is the one naming both scenes.</b> <c>RenderSettings</c> is
        /// per-scene and <c>GameBootstrap</c> never calls <c>SetActiveScene</c>, so the settings that
        /// render in the game are Bootstrap's — and <c>Rebuild</c> leaves the editor with Bootstrap
        /// active too, which means every preview frame this project takes is already using them. A sky
        /// baked into the world scene alone would therefore photograph perfectly and ship as Unity's
        /// stock blue dome. That is the only fault in this feature a picture actively <i>hides</i>.</para>
        ///
        /// <para><b>The second is the shadowing one.</b> Everything the clock drives is a global uniform
        /// declared outside <c>UnityPerMaterial</c>, because a skybox has no renderer to hang a
        /// <c>MaterialPropertyBlock</c> on and writing the asset would leave it modified in a player's
        /// working tree. If any of those names ever also appears in <c>Properties</c>, the material's
        /// serialized value shadows the global and the sky renders a plausible static dome — which
        /// reads as a wiring fault and sends the reader to the wrong file.</para>
        ///
        /// <para>The third is the profile arriving with an empty gradient after a version bump: the sky
        /// still dims through its horizon and is merely wrong overhead. Half working, reported nowhere.
        /// And the fourth is the pair of coverage constants this file keeps a copy of so that
        /// <c>ReportCloudField</c> can measure them — a copy that agrees until one of them is
        /// edited.</para>
        /// </summary>
        private static void ValidateSky(TimeOfDayProfile profile, float environmentInterval)
        {
            Material sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);

            if (sky == null || sky.shader == null || sky.shader.name != SkyShaderName)
            {
                Debug.LogError($"[Horizon] Sky: {SkyMaterialPath} is missing or is not on "
                               + $"{SkyShaderName}. There is no fallback — the sky this replaced was two "
                               + "materials and both are gone.");
                return;
            }

            // The globals, asserted absent from the material. One HasProperty call each.
            string[] driven =
            {
                "_HorizonSkyHorizon", "_HorizonSkyZenith", "_HorizonSkyCloudLit",
                "_HorizonSkyCloudShade", "_HorizonSun", "_HorizonSunTint",
                "_HorizonSkyDrift", "_HorizonOvercast",
            };

            for (int i = 0; i < driven.Length; i++)
            {
                if (sky.HasProperty(driven[i]))
                {
                    Debug.LogError($"[Horizon] Sky: {driven[i]} is declared in the material as well as "
                                   + "being a global. The serialized value shadows what the controller "
                                   + "pushes, and the sky renders whatever was baked — which looks like "
                                   + "a dome that does not respond to the clock rather than like this.");
                }
            }

            // The three numbers this file keeps a copy of so ReportCloudField can measure what they
            // mean. A copy agrees until one of them is edited, which is the whole reason for the check.
            CheckFloat(sky, "_CoverClear", CoverClear);
            CheckFloat(sky, "_CoverFull", CoverFull);
            CheckFloat(sky, "_CloudDetailWeight", CloudDetailWeight);

            if (profile != null && (profile.SkyZenith == null || profile.SkyZenith.colorKeys.Length == 0))
            {
                Debug.LogError($"[Horizon] Sky: {TimeOfDayProfilePath} has no zenith gradient, so the "
                               + "sky overhead is black at every hour. An empty Gradient evaluates to "
                               + "black and the horizon still works, so this half-works — see "
                               + "TimeOfDayProfile.CurrentVersion for the heal that should have run.");
            }

            // <b>Both scenes, read off the files rather than off RenderSettings.</b> Which scene is
            // active decides what the static API answers for, and that is the entire fault this line
            // exists to catch — so asking it would be asking the thing under test. It also has to run
            // after both scenes are saved, which is why the call is in Rebuild and not in either
            // builder.
            string worldSkybox = SkyboxNameIn(WorldScenePath);
            string bootstrapSkybox = SkyboxNameIn(BootstrapScenePath);

            Debug.Log($"[Horizon] Sky: {sky.name} on {SkyShaderName}; skybox is {worldSkybox} in "
                      + $"World_MountainPass and {bootstrapSkybox} in Bootstrap. Environment reflection "
                      + $"{RenderSettings.defaultReflectionResolution} px, rebuilt at most every "
                      + $"{environmentInterval:0.00} s.");

            if (worldSkybox != sky.name || bootstrapSkybox != sky.name)
            {
                Debug.LogError("[Horizon] Sky: one of the two scenes is not on the new sky. Bootstrap is "
                               + "the active scene at run time — GameBootstrap loads the world "
                               + "additively and never calls SetActiveScene — so if that is the one "
                               + "reading Default-Skybox, the game ships Unity's dome while every "
                               + "preview frame this project takes still looks perfect.");
            }

            static void CheckFloat(Material material, string property, float expected)
            {
                if (!material.HasProperty(property))
                {
                    Debug.LogError($"[Horizon] Sky: the shader has no {property}.");
                    return;
                }

                float actual = material.GetFloat(property);

                if (!Mathf.Approximately(actual, expected))
                {
                    Debug.LogWarning($"[Horizon] Sky: {property} is {actual:0.000} on the material and "
                                     + $"{expected:0.000} in PrototypeSetup, so the coverage percentages "
                                     + "printed above are measured against a threshold the sky does not "
                                     + "use.");
                }
            }
        }

        /// <summary>
        /// Reads a saved scene's skybox material name out of its YAML.
        ///
        /// <para>Out of the file, because the scene in question is not open — and because opening it to
        /// ask would mean closing the one being built. Unity writes the RenderSettings block first, so
        /// this is the top of the file in every scene this project produces.</para>
        /// </summary>
        private static string SkyboxNameIn(string scenePath)
        {
            if (!System.IO.File.Exists(scenePath))
            {
                return "no scene file";
            }

            foreach (string line in System.IO.File.ReadLines(scenePath))
            {
                int at = line.IndexOf("m_SkyboxMaterial:", System.StringComparison.Ordinal);

                if (at < 0)
                {
                    continue;
                }

                // The built-in default is fileID 10304 in the zero GUID, and it is the value that has
                // been sitting in both scenes all along. Named rather than reported as a guid, because
                // "Default-Skybox" is the answer somebody needs and a guid is a lookup.
                if (line.Contains("fileID: 10304"))
                {
                    return "Default-Skybox";
                }

                int guid = line.IndexOf("guid: ", System.StringComparison.Ordinal);

                if (guid < 0)
                {
                    return "none";
                }

                string id = line.Substring(guid + 6, 32);
                string path = AssetDatabase.GUIDToAssetPath(id);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

                return material != null ? material.name : "unknown";
            }

            return "none";
        }

        /// <summary>
        /// Reads the ambient probe back and prints what the engine actually built from the three
        /// Trilight colours.
        ///
        /// <para><b>The claim this exists to check is that Trilight is free and does not move the
        /// world's exposure</b>, and both halves of that are exactly the kind of thing that is easy to
        /// assert and hard to notice being wrong. The colours in this project were chosen against a flat
        /// ambient. If the average moved, every one of them wants looking at again — and nothing would
        /// say so, because a world uniformly ten per cent brighter looks like a world.</para>
        ///
        /// <para>So it sets Flat, evaluates the probe up, sideways and down, sets Trilight, does it
        /// again, and prints both. <c>RenderSettings.ambientProbe</c> is the engine's own answer rather
        /// than this file's arithmetic about what Unity does with three colours, which is the rule
        /// <c>ValidateSurfaces</c> states for itself: ask the thing that will be asked at runtime, not a
        /// second model of it.</para>
        ///
        /// <para>The delta that matters is L0 — the constant term, which is the average over the whole
        /// sphere and therefore the overall brightness. The up/down spread is the feature, so it is
        /// printed and never warned about.</para>
        /// </summary>
        private static void ValidateAmbient(TimeOfDayController timeOfDay)
        {
            if (timeOfDay == null)
            {
                return;
            }

            // Evaluated for a facet pointing at the sky, at the horizon and at the ground. Those are the
            // three the mode is named for and the three a hillside is made of.
            var directions = new[] { Vector3.up, Vector3.forward, Vector3.down };
            var flat = new Color[3];
            var trilight = new Color[3];

            AmbientMode wasMode = RenderSettings.ambientMode;

            // Off the controller, not off RenderSettings. ambientLight is ambientSkyColor under another
            // name, so once the three bands are written there is no flat value left in the scene to read
            // — and taking it from there compares Trilight against its own sky band, which reported the
            // world 44 % darker on a change that moves the mean by nothing.
            Color wasLight = timeOfDay.FlatAmbient;
            Color wasSky = RenderSettings.ambientSkyColor;
            Color wasEquator = RenderSettings.ambientEquatorColor;
            Color wasGround = RenderSettings.ambientGroundColor;

            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = wasLight;
                Sample(directions, flat);

                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = wasSky;
                RenderSettings.ambientEquatorColor = wasEquator;
                RenderSettings.ambientGroundColor = wasGround;
                Sample(directions, trilight);
            }
            finally
            {
                // Whatever this measured, the scene keeps what the controller wrote. A validator that
                // leaves the world in the state its last probe needed is a validator that decides the
                // look.
                RenderSettings.ambientMode = wasMode;
                RenderSettings.ambientSkyColor = wasSky;
                RenderSettings.ambientEquatorColor = wasEquator;
                RenderSettings.ambientGroundColor = wasGround;
            }

            // A probe that did not move is a measurement of nothing, and it would read as a clean pass:
            // drift comes out zero and the warning below never fires. Same rule ValidateSurfaces states
            // about its own rays missing.
            if (flat[0] == trilight[0] && flat[1] == trilight[1] && flat[2] == trilight[2])
            {
                Debug.LogWarning("[Horizon] Ambient: the probe read identically in both modes, so this "
                               + "check measured nothing. Either the mode is not being applied or "
                               + "RenderSettings.ambientProbe did not refresh — do not read the numbers "
                               + "below as a pass.");
            }

            float flatL0 = (flat[0].grayscale + flat[1].grayscale + flat[2].grayscale) / 3f;
            float triL0 = (trilight[0].grayscale + trilight[1].grayscale + trilight[2].grayscale) / 3f;
            float drift = flatL0 > 0.0001f ? Mathf.Abs(triL0 - flatL0) / flatL0 : 0f;

            float up = flat[0].grayscale > 0.0001f ? trilight[0].grayscale / flat[0].grayscale - 1f : 0f;
            float down = flat[2].grayscale > 0.0001f ? trilight[2].grayscale / flat[2].grayscale - 1f : 0f;

            Debug.Log($"[Horizon] Ambient: Trilight sky {Describe(RenderSettings.ambientSkyColor)} "
                    + $"equator {Describe(RenderSettings.ambientEquatorColor)} "
                    + $"ground {Describe(RenderSettings.ambientGroundColor)}. "
                    + $"A facet facing up gains {up * 100f:0}%, one facing down loses "
                    + $"{-down * 100f:0}%. Mean {flatL0:0.000} flat against {triL0:0.000} trilight, "
                    + $"{drift * 100f:0.0}% apart.");

            if (drift > 0.05f)
            {
                Debug.LogWarning($"[Horizon] Ambient: the trilight mean is {drift * 100f:0.0}% off the "
                               + "flat one, so this is a change to how bright the world is and not only "
                               + "to its shape. Every colour in the project was chosen against the flat "
                               + "value. The gains in TimeOfDayController.ApplyAmbient are written to "
                               + "average one — check them before tuning anything else.");
            }

            static void Sample(Vector3[] directions, Color[] into)
            {
                // The probe is built from the ambient settings, and nothing here says when. Asking for
                // it straight after writing them is how a validator ends up measuring the previous
                // mode's answer twice and reporting no difference at all.
                DynamicGI.UpdateEnvironment();

                SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
                var results = new Color[directions.Length];

                probe.Evaluate(directions, results);

                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = results[i];
                }
            }

            static string Describe(Color colour)
            {
                return $"({colour.r:0.00}, {colour.g:0.00}, {colour.b:0.00})";
            }
        }

        /// <summary>
        /// Reports what the post stack actually is, and fails the build on the two ways it can be
        /// switched off without anything looking wrong.
        ///
        /// <para><b>This check exists because the gap it guards was open for the life of the project and
        /// nothing said so.</b> The performance budget specified a tone map and a colour grade from the
        /// beginning; neither scene had a <see cref="Volume"/>, the camera had no
        /// <see cref="UniversalAdditionalCameraData"/>, and the two pipeline assets were pointed at
        /// Unity's leftover <c>SampleSceneProfile</c> — which carries an active bloom at 0.25 and an
        /// active vignette at 0.2 that nobody here authored. That profile is the <i>quality default</i>
        /// layer, underneath every scene volume, so it would have gone on blooming on the Low tier after
        /// the tier switch was built and the switch would have looked broken rather than overridden.</para>
        ///
        /// <para>So the numbers are printed unconditionally rather than only on failure. This file has
        /// paid for a stale document five times now, and a line in the log is what lets the budget in
        /// CLAUDE.md and the asset on disk be compared without opening either.</para>
        /// </summary>
        private static void ValidatePostStack(Camera camera)
        {
            UniversalAdditionalCameraData data = camera != null
                ? camera.GetComponent<UniversalAdditionalCameraData>()
                : null;

            if (data == null || !data.renderPostProcessing)
            {
                Debug.LogError("[Horizon] Post: the camera is not rendering post-processing. Every "
                             + "volume and every profile can be correct and the frame still comes out "
                             + "raw — this builds, validates and looks exactly like a build that works.");
            }

            // Both assets, not just the active one. The editor runs PC_RPAsset and the phone runs
            // Mobile_RPAsset, and a stray quality-default profile on the one nobody is looking at is
            // exactly the shape this fault already had: Unity's leftover SampleSceneProfile was wired
            // into both of them, carrying an active bloom and an active vignette, underneath every
            // scene volume, where no amount of correct authoring above could override it away.
            CheckNoQualityProfile(UniversalRenderPipeline.asset);
            CheckNoQualityProfile(
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(MobilePipelinePath));

            Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            var tonemapping = default(Tonemapping);
            var grade = default(ColorAdjustments);
            var bloom = default(Bloom);

            for (int i = 0; i < volumes.Length; i++)
            {
                VolumeProfile profile = volumes[i].sharedProfile;
                if (profile == null)
                {
                    continue;
                }

                if (tonemapping == null)
                {
                    profile.TryGet(out tonemapping);
                }

                if (grade == null)
                {
                    profile.TryGet(out grade);
                }

                if (bloom == null)
                {
                    profile.TryGet(out bloom);
                }
            }

            // Same rule the snow line follows: a world with no tone map in it builds and validates
            // exactly like one that has it, so the absence has to be the loud case.
            if (tonemapping == null || tonemapping.mode.value == TonemappingMode.None)
            {
                Debug.LogError("[Horizon] Post: no tone map in the scene. Everything above 1 clips flat "
                             + "to white, which is every lamp lens, forecourt sign and tower beacon in "
                             + "the world.");
            }

            if (!UniversalRenderPipeline.asset.supportsHDR)
            {
                Debug.LogWarning("[Horizon] Post: HDR is off, so nothing ever exceeds 1 in the colour "
                               + "buffer and the tone map has nothing to compress.");
            }

            string grading = grade != null
                ? $"exposure {grade.postExposure.value:+0.00;-0.00}, contrast {grade.contrast.value:0}, "
                  + $"saturation {grade.saturation.value:0}"
                : "no colour grade";

            string blooming = bloom != null
                ? $"intensity {bloom.intensity.value:0.00} above {bloom.threshold.value:0.00}, "
                  + $"{bloom.downscale.value}, {bloom.maxIterations.value} iterations"
                : "no bloom";

            Debug.Log($"[Horizon] Post: {volumes.Length} volumes — "
                    + $"{(tonemapping != null ? tonemapping.mode.value.ToString() : "no")} tone map, "
                    + $"{grading}; {blooming}.");

            // Named, and the mobile asset read by path, because the first version of this line reported
            // "render scale 1.00" against a mobile asset that says 0.80. UniversalRenderPipeline.asset
            // is whichever quality level the *editor* is on, which is PC_RPAsset — so the log was
            // faithfully describing a pipeline the phone never runs. That is this file's own recurring
            // fault: a build reporting a number from the wrong copy of it and going on looking right.
            ReportPipeline(UniversalRenderPipeline.asset, "active in the editor");
            ReportPipeline(
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(MobilePipelinePath),
                "shipping on Android");
        }

        /// <summary>Where the Android quality level's pipeline asset lives.</summary>
        private const string MobilePipelinePath = "Assets/Settings/Mobile_RPAsset.asset";

        private static void CheckNoQualityProfile(UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline != null && pipeline.volumeProfile != null)
            {
                Debug.LogError($"[Horizon] Post: '{pipeline.name}' carries a quality-default volume "
                             + $"profile ('{pipeline.volumeProfile.name}'). That layer sits under every "
                             + "scene volume, so whatever it overrides cannot be switched off from "
                             + "above — a bloom there would go on blooming with the quality director's "
                             + "bloom volume at zero weight. Clear m_VolumeProfile on the RP asset.");
            }
        }

        private static void ReportPipeline(UniversalRenderPipelineAsset pipeline, string role)
        {
            if (pipeline == null)
            {
                Debug.LogWarning($"[Horizon] Pipeline ({role}): no asset found.");
                return;
            }

            Debug.Log($"[Horizon] Pipeline '{pipeline.name}' ({role}): "
                    + $"HDR {(pipeline.supportsHDR ? "on" : "off")}, "
                    + $"MSAA {pipeline.msaaSampleCount}x, "
                    + $"render scale {pipeline.renderScale:0.00}, "
                    + $"quality volume profile "
                    + $"{(pipeline.volumeProfile != null ? pipeline.volumeProfile.name : "none")}.");
        }

        /// <summary>
        /// The baked map, re-loaded by path.
        ///
        /// <para><b>Not carried across from the world build, and the build said so.</b> An asset
        /// reference does not survive <c>EditorSceneManager.NewScene(..., Single)</c> — the same trap
        /// <see cref="LoadVehicleConfig"/> is written up against, and it fails in the same silent way:
        /// <c>objectReferenceValue</c> takes the dead reference, writes null, and reports nothing. The
        /// first version of this handed the <c>WorldMap</c> straight from <c>BuildWorldScene</c> to
        /// <c>BuildBootstrapScene</c>, and both minimap and map page came out wired to nothing. Only
        /// <c>AssertReferenceAssigned</c> caught it.</para>
        /// </summary>
        private static WorldMap LoadWorldMap()
        {
            return AssetDatabase.LoadAssetAtPath<WorldMap>(WorldMapPath);
        }

        /// <summary>
        /// Bakes the world in plan into <c>WorldMap.asset</c>, for the minimap and the map page.
        ///
        /// <para><b>The list of roads is the whole point of this method.</b> The scene ends up holding
        /// 199 <c>RoadPath</c> components and only nine of them are paved: <c>MotorwayPath</c> is the
        /// median the two carriageways are offset from, and <c>SeeburgAxis</c> and <c>ArterialPath</c>
        /// are the frames <c>TownShape.ToWorld</c> maps a town against. Nothing about a path says which
        /// it is, so a builder that went looking would draw a road down the middle of two towns and a
        /// third carriageway down the motorway — and a picture is the only place that would show it.
        /// Every road below is one that <c>RoadMeshBuilder.BuildRoad</c> or <c>BuildCarriageway</c> was
        /// called on a few hundred lines above.</para>
        ///
        /// <para>The motorway's <i>features</i> come off its median course rather than off a
        /// carriageway, because that is what their distances were measured along. Ten metres of offset
        /// is nothing at map scale; a third carriageway is not.</para>
        /// </summary>
        /// <summary>
        /// Everything the circuit builders need to know about <i>which</i> circuit they are building.
        ///
        /// <para><b>This exists because a second one arrived and five builders were wired to the
        /// first.</b> <c>BuildPaddock</c>, <c>BuildLapTiming</c>, <c>BuildSectorGates</c>,
        /// <c>BuildStartingGrid</c> and <c>AddPaddockSamples</c> all read <c>WeissjochringCourse</c>
        /// directly and all wrote mesh assets under fixed names, so calling any of them twice would
        /// have built the Bahçe Ring's furniture over the Weissjochring's and left one circuit with no
        /// paddock, no kerbs and no timing — silently, with a correct triangle count in the log each
        /// time. It is the same trap <c>BuildMotorwayMerge</c> and <c>TrunkForkBuilder</c> have each
        /// already been through, which is why both of those take a name.</para>
        /// </summary>
        private readonly struct CircuitBuild
        {
            /// <summary>What the circuit is called, on the timing board and in the log.</summary>
            public readonly string Name;

            /// <summary>ASCII stem for generated assets and scene objects. Must be unique per circuit.</summary>
            public readonly string Label;

            /// <summary>Where the start/finish line is, along the path.</summary>
            public readonly float LineDistance;

            /// <summary>Which hand the infield is on. See <c>CircuitMeshes.Append</c>.</summary>
            public readonly float PaddockSide;

            /// <summary>Centre of the level apron.</summary>
            public readonly Vector3 PaddockCentre;

            /// <summary>Radius of the level apron.</summary>
            public readonly float PaddockRadius;

            public CircuitBuild(string name, string label, float lineDistance, float paddockSide,
                Vector3 paddockCentre, float paddockRadius)
            {
                Name = name;
                Label = label;
                LineDistance = lineDistance;
                PaddockSide = paddockSide;
                PaddockCentre = paddockCentre;
                PaddockRadius = paddockRadius;
            }
        }

        /// <summary>The Weissjochring, as the builders below want it.</summary>
        private static CircuitBuild Weissjochring => new CircuitBuild(
            WeissjochringCourse.CircuitName,
            "Weissjochring",
            WeissjochringCourse.LineDistance,
            WeissjochringCourse.PaddockSide,
            WeissjochringCourse.PaddockCentre,
            WeissjochringCourse.PaddockRadius);

        /// <summary>The Bahçe Ring, likewise.</summary>
        private static CircuitBuild BahceRing => new CircuitBuild(
            BahceRingCourse.CircuitName,
            "BahceRing",
            BahceRingCourse.LineDistance,
            BahceRingCourse.PaddockSide,
            BahceRingCourse.PaddockCentre,
            BahceRingCourse.PaddockRadius);

        /// <summary>
        /// How many gates a lap has to pass. Six, so they fall roughly every two kilometres — close
        /// enough that no useful short cut exists between two of them, far enough apart that a lap is
        /// not a slalom between painted lines.
        /// </summary>
        private const int GateCount = 6;

        /// <summary>Paints the sector gates. See <see cref="CircuitMeshes.AppendGates"/> for why.</summary>
        private static void BuildSectorGates(
            Transform parent, RoadPath ring, in RoadShape shape, in CircuitBuild circuit,
            float[] distances, PrototypeMaterials materials)
        {
            var buffer = new VegetationMeshBuffer(CircuitMeshes.CircuitSubmeshCount);

            CircuitMeshes.AppendGates(ring, shape, distances, buffer);
            buffer.MergeTinted(CircuitMeshes.SurfaceTints());

            var used = new List<int>(CircuitMeshes.CircuitSubmeshCount);
            Mesh mesh = buffer.ToMesh($"SectorGate{circuit.Label}Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {circuit.Name}'s sector gates came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, GeneratedFolder + $"/SectorGate{circuit.Label}Mesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = materials.RoadTint;
            }

            GameObject gates = CreateMeshObject(
                parent, $"SectorGates{circuit.Label}", mesh, meshMaterials);

            WorldChunk chunk = gates.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);
        }

        /// <summary>
        /// Bakes the twelve starting slots into a <see cref="StartingGrid"/>.
        ///
        /// <para>Read out of <c>CircuitMeshes.GridSlot</c> rather than counted here, because the boxes
        /// painted on the road come from the same call. See <see cref="StartingGrid"/> for why this is
        /// worth having before there is anything to race.</para>
        /// </summary>
        private static void BuildStartingGrid(
            Transform parent, RoadPath ring, in RoadShape shape, in CircuitBuild circuit,
            float rideHeight)
        {
            var gridObject = new GameObject($"StartingGrid{circuit.Label}");
            gridObject.transform.SetParent(parent, false);

            var positions = new Vector3[CircuitMeshes.GridSlots];
            var headings = new float[CircuitMeshes.GridSlots];

            for (int slot = 0; slot < CircuitMeshes.GridSlots; slot++)
            {
                CircuitMeshes.GridSlot(slot, circuit.LineDistance, shape,
                    out float along, out float across);

                // Wrapped, not clamped: every slot is behind the line and therefore at a negative
                // distance, which on a closed course is the far end of the main straight.
                float at = ring.NormalizeDistance(along);

                Vector3 forward = ring.GetDirectionAtDistance(at);

                positions[slot] = ring.GetPositionAtDistance(at)
                                  + ring.GetRightAtDistance(at) * across
                                  + Vector3.up * rideHeight;

                headings[slot] = Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y;
            }

            StartingGrid grid = gridObject.AddComponent<StartingGrid>();
            grid.SetGrid(circuit.Name, positions, headings);

            EditorUtility.SetDirty(grid);

            // Measured across the road rather than along a world axis: the straight runs whichever way
            // it runs, and a stagger reported in world x said 0.4 m for two boxes 6.5 m apart.
            CircuitMeshes.GridSlot(0, circuit.LineDistance, shape,
                out float poleAlong, out float poleAcross);
            CircuitMeshes.GridSlot(1, circuit.LineDistance, shape,
                out float secondAlong, out float secondAcross);

            Debug.Log($"[Horizon] Starting grid: {grid.SlotCount} slots on the "
                      + $"{circuit.Name}, in {CircuitMeshes.GridSlots / 2} rows. "
                      + $"Pole sits {circuit.LineDistance - poleAlong:0} m behind the line, "
                      + $"{Mathf.Abs(secondAcross - poleAcross):0.0} m across from second and "
                      + $"{Mathf.Abs(secondAlong - poleAlong):0} m ahead of it.");
        }

        /// <summary>
        /// Bakes the start/finish line and a coarse walk of the circuit into a <see cref="LapTiming"/>.
        ///
        /// <para>The line is taken from the path at distance zero rather than from the course's own
        /// start pose, for the reason <c>BuildTrunkFork</c> records about the fork: a course's distance
        /// is the sum of its straights and arcs while a path's is arc length along the Catmull-Rom
        /// curve through the same points, and the two disagree. What the car actually crosses is the
        /// path.</para>
        ///
        /// <para>The walk is deliberately coarse. It answers one question — is the car on the circuit at
        /// all — four times a second, and eighty metres between samples against a seventy-metre reach
        /// leaves the pair overlapping everywhere.</para>
        /// </summary>
        private static void BuildLapTiming(
            Transform parent, RoadPath ring, in RoadShape shape, in CircuitBuild circuit,
            PrototypeMaterials materials)
        {
            const float SampleSpacing = 80f;

            var timingObject = new GameObject($"LapTiming{circuit.Label}");
            timingObject.transform.SetParent(parent, false);

            int count = Mathf.Max(2, Mathf.CeilToInt(ring.Length / SampleSpacing));
            var samples = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                samples[i] = ring.GetPositionAtDistance(ring.Length * i / count);
            }

            LapTiming timing = timingObject.AddComponent<LapTiming>();

            float lineAt = circuit.LineDistance;

            timing.SetCircuit(
                circuit.Name,
                ring.GetPositionAtDistance(lineAt),
                ring.GetDirectionAtDistance(lineAt),
                shape.OuterHalfWidth,
                samples);

            // Spread evenly round the lap and deliberately not at the line: the first gate sits a
            // seventh of the way round, the last a seventh short of home, so neither can be tripped by
            // the same crossing that starts or ends the lap.
            var gatePoints = new Vector3[GateCount];
            var gateForwards = new Vector3[GateCount];
            var gateDistances = new float[GateCount];

            for (int i = 0; i < GateCount; i++)
            {
                float at = ring.NormalizeDistance(lineAt + ring.Length * (i + 1) / (GateCount + 1f));

                gateDistances[i] = at;
                gatePoints[i] = ring.GetPositionAtDistance(at);
                gateForwards[i] = ring.GetDirectionAtDistance(at);
            }

            timing.SetGates(gatePoints, gateForwards);
            EditorUtility.SetDirty(timing);

            BuildSectorGates(parent, ring, shape, circuit, gateDistances, materials);

            ValidateLapGates(ring, lineAt, gatePoints, gateForwards, shape.OuterHalfWidth, circuit.Name);

            Vector3 line = ring.GetPositionAtDistance(lineAt);

            Debug.Log($"[Horizon] Lap timing on the {circuit.Name}: the start/finish line "
                      + $"{lineAt:0} m along, at "
                      + $"({line.x:0}, {line.z:0}), {shape.OuterHalfWidth:0.0} m either side of the "
                      + $"centreline, over {timing.SampleCount} samples of circuit. {GateCount} gates "
                      + $"every {ring.Length / (GateCount + 1f) / 1000f:0.0} km, all of which have to be "
                      + "passed in order before the line will take a time.");
        }

        /// <summary>
        /// Whether a lap driven on the centreline actually passes every gate.
        ///
        /// <para><b>Nothing else in the build asks, and the answer is not obvious.</b> A gate is a plane
        /// with a window in it: <c>LapTiming</c> waits for the car to go from the negative side of the
        /// one it is expecting to the positive side, within half a road's width of the point on the
        /// centreline. Every part of that can be true of the geometry and false of the drive — a circuit
        /// that doubles back can leave the car already on a gate's positive side when the lap starts,
        /// and a gate laid across a corner tight enough can be crossed outside its own window. The
        /// consequence is a lap that never counts, with the readout sitting on <c>0/6</c> and the road
        /// saying nothing about why.</para>
        ///
        /// <para>So this walks the path from the line, once round, running exactly the test the runtime
        /// runs. A driver does not follow the centreline, but a centreline that cannot pass the gates is
        /// a circuit no line can.</para>
        /// </summary>
        private static void ValidateLapGates(
            RoadPath ring,
            float lineAt,
            Vector3[] gatePoints,
            Vector3[] gateForwards,
            float halfWidth,
            string what)
        {
            if (ring == null || gatePoints == null || gatePoints.Length == 0)
            {
                return;
            }

            const float Step = 1f;

            int passed = 0;
            float previous = 0f;
            bool hasPrevious = false;
            float worstAcross = 0f;

            // A little over a lap: the last gate sits close to the line, and a walk that stopped exactly
            // on it would be reporting a rounding error rather than a gate.
            for (float walked = 0f; walked <= ring.Length + Step && passed < gatePoints.Length;
                 walked += Step)
            {
                Vector3 at = ring.GetPositionAtDistance(ring.NormalizeDistance(lineAt + walked));

                Vector3 forward = gateForwards[passed];
                forward.y = 0f;
                forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

                Vector3 offset = at - gatePoints[passed];
                offset.y = 0f;

                float side = Vector3.Dot(offset, forward);

                if (hasPrevious && previous < 0f && side >= 0f)
                {
                    float across = (offset - forward * side).magnitude;
                    worstAcross = Mathf.Max(worstAcross, across);

                    if (across <= halfWidth)
                    {
                        passed++;
                        hasPrevious = false;
                        continue;
                    }
                }

                previous = side;
                hasPrevious = true;
            }

            if (passed < gatePoints.Length)
            {
                Debug.LogError($"[Horizon] A lap of the {what} driven down the middle of the road passes "
                               + $"{passed} of its {gatePoints.Length} gates. It stalls on gate "
                               + $"{passed + 1}, so no lap on this circuit can ever be timed and the "
                               + "readout will sit at 0/" + gatePoints.Length + " for as long as anybody "
                               + "drives it. Either that gate falls where the lap does not cross its "
                               + "plane cleanly, or it is crossed further than "
                               + $"{halfWidth:0.0} m off the centreline — the worst crossing measured "
                               + $"{worstAcross:0.0} m out.");
                return;
            }

            Debug.Log($"[Horizon] Lap gates on the {what}: all {gatePoints.Length} pass on a centreline "
                      + $"lap, the worst crossing {worstAcross:0.0} m off centre against a "
                      + $"{halfWidth:0.0} m window.");
        }

        /// <summary>
        /// The paddock at the start/finish line. See <see cref="CircuitMeshes"/> for what stands there
        /// and why the board is on a slot of its own.
        /// </summary>
        private static void BuildPaddock(
            Transform parent,
            IRoadPath path,
            in RoadShape shape,
            in CircuitBuild circuit,
            PrototypeMaterials materials)
        {
            var buffer = new VegetationMeshBuffer(CircuitMeshes.CircuitSubmeshCount);

            CircuitMeshes.Append(path, shape, circuit.LineDistance, circuit.PaddockSide, buffer);
            buffer.MergeTinted(CircuitMeshes.SurfaceTints());

            var used = new List<int>(CircuitMeshes.CircuitSubmeshCount);
            Mesh mesh = buffer.ToMesh($"Paddock{circuit.Label}Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {circuit.Name}'s paddock came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, GeneratedFolder + $"/Paddock{circuit.Label}Mesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                // The board takes the filling stations' plain bright face and is deliberately not
                // registered with TownLights, for the reason CircuitMeshes records. The paint goes on
                // the road's own tinted material so it sits at asphalt smoothness rather than at a
                // building's; everything else is structure.
                meshMaterials[i] = used[i] == CircuitMeshes.BoardSubmesh
                    ? materials.SignFace
                    : used[i] == CircuitMeshes.PaintSubmesh
                        ? materials.RoadTint
                        : materials.BuildingTint;
            }

            GameObject paddock = CreateMeshObject(
                parent, $"Paddock{circuit.Label}", mesh, meshMaterials);

            WorldChunk chunk = paddock.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            Debug.Log($"[Horizon] Paddock on the {circuit.Name}: {mesh.triangles.Length / 3} "
                      + $"triangles in {used.Count} draw "
                      + "call(s) at the start/finish line — gantry, pit block, grandstand, the line and "
                      + "the grid. Three is the expected count: structure, board, paint.");
        }

        /// <summary>
        /// Whether the circuit actually closes, and whether it closes <i>smoothly</i>.
        ///
        /// <para>Nothing else in the build asks. A closure that misses by a metre still paves, still
        /// carries rails and kerbs, still passes the clearance sweep and still draws on the map — it is
        /// simply a step or a kink across the one piece of road every lap crosses, at the fastest point
        /// on the circuit. The three questions here are the three ways <c>RoadCourseBuilder.Close</c>
        /// can come out wrong while reporting nothing: the flag never got set, the solve landed
        /// somewhere else, or it landed in the right place facing the wrong way.</para>
        /// </summary>
        private static void ValidateCircuitClosure(RoadCourse course, RoadPath path, string what)
        {
            if (course == null || path == null)
            {
                return;
            }

            if (!course.IsClosed)
            {
                Debug.LogError($"[Horizon] {what} is not a closed course. It will be paved as a road "
                               + "with two ends a few metres apart, which looks like a circuit from "
                               + "everywhere except the start/finish line. Call Close() on the builder.");
                return;
            }

            IReadOnlyList<Vector3> points = course.ControlPoints;
            float gap = Plan(points[points.Count - 1] - points[0]).magnitude;

            // Close() trims the point the solve landed on, because a looping RoadPath draws the segment
            // from the last control point back to the first itself. So what should be left is one
            // ordinary point spacing, not zero — and much more than that means the solve did not
            // actually reach the start pose.
            if (gap > 18f)
            {
                Debug.LogError($"[Horizon] {what} closes {gap:0.0} m short of its own start. The Dubins "
                               + "solve is exact, so this is not drift: either the closure emitted "
                               + "nothing, or the walk above it was retuned and the target pose no "
                               + "longer belongs to it.");
            }

            // Read off the finished path rather than the control points, because the Catmull-Rom curve
            // through them is what the car drives and what the ribbon is extruded along.
            float length = path.Length;
            Vector3 before = path.GetDirectionAtDistance(length - 6f);
            Vector3 after = path.GetDirectionAtDistance(6f);
            float turn = Vector3.Angle(Plan(before), Plan(after));

            if (turn > 4f)
            {
                Debug.LogError($"[Horizon] {what} has a {turn:0.0}° kink at the start/finish line. The "
                               + "two ends meet but they do not agree about which way the road is "
                               + "going, and this is the fastest point on the lap. The line has to sit "
                               + "on straight track with straight track behind it.");
            }

            float step = Mathf.Abs(
                path.GetPositionAtDistance(length - 3f).y - path.GetPositionAtDistance(3f).y);

            if (step > 0.6f)
            {
                Debug.LogWarning($"[Horizon] {what} steps {step:0.00} m in height across the line. "
                                 + "Close() derives one uniform grade over the whole solve, so a step "
                                 + "this size means the approach is still climbing where the straight "
                                 + "has gone level.");
            }

            Debug.Log($"[Horizon] {what}: closed loop, {length / 1000f:0.00} km a lap, meeting itself "
                      + $"within {gap:0.0} m and {turn:0.0}° at the line.");
        }

        /// <summary>
        /// Whether the ground inside a closed circuit exists.
        ///
        /// <para><b>This is the check the shape of the Weissjochring was designed around, and nothing
        /// else in the build can stand in for it.</b> Terrain tiles are chosen by distance to a road —
        /// <c>TerrainShape.CorridorWidth</c>, 200 m — so a loop that encloses more than about four
        /// hundred metres of open ground has a hole in the middle of it. Every other check here looks
        /// along a road or across it; a hole in an infield is not near any road at all, which is
        /// precisely why it is invisible to all of them. From the car it reads as the world ending in
        /// mid-air on the inside of a corner.</para>
        ///
        /// <para><b>It asks the tile builder rather than guessing, and it did not always.</b> The first
        /// version measured distance-to-road against the corridor width and errored past it — a proxy,
        /// and a proxy strict enough to condemn a circuit that has no hole in it at all. A tile is kept
        /// if its <i>centre</i> is within the corridor plus most of a tile, so ground reaches a good way
        /// further out than 200 m, and a fold like Istanbul Park's doubles back often enough that every
        /// tile it encloses is one the corridor already pulls in. The check now builds the same list
        /// <c>BuildTerrainTiles</c> is about to build and asks whether the point falls on one of them,
        /// which is the rule this file states elsewhere: a checker with an opinion of its own agrees
        /// with the builder right up until one of them is wrong.</para>
        ///
        /// <para>The corridor distance is still measured and still reported, because it says something
        /// worth knowing — how much of the infield is ground the roads shaped and how much is ground the
        /// tile grid merely reached. It is a line in the log rather than an error.</para>
        /// </summary>
        private static void ValidateInfieldCoverage(
            MountainField field, in TerrainShape terrainShape, RoadPath path, string what)
        {
            if (field == null || path == null)
            {
                return;
            }

            const float Sample = 12f;
            const float Grid = 25f;

            int steps = Mathf.Max(8, Mathf.CeilToInt(path.Length / Sample));
            var outline = new Vector2[steps];

            for (int i = 0; i < steps; i++)
            {
                Vector3 on = path.GetPositionAtDistance(path.Length * i / steps);
                outline[i] = new Vector2(on.x, on.z);
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < steps; i++)
            {
                minX = Mathf.Min(minX, outline[i].x);
                maxX = Mathf.Max(maxX, outline[i].x);
                minZ = Mathf.Min(minZ, outline[i].y);
                maxZ = Mathf.Max(maxZ, outline[i].y);
            }

            // The list the tile builder is about to fill, rather than this check's own opinion of where
            // ground will be. Asked without the extra bounds the towns and the water bands contribute,
            // so it is a subset of what actually gets built: a point this finds covered is covered.
            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            List<TerrainTileKey> tiles = TerrainTileBuilder.ListTiles(
                field, terrainShape, terrainShape.CorridorWidth);

            var built = new HashSet<long>();
            for (int i = 0; i < tiles.Count; i++)
            {
                built.Add(((long)tiles[i].Column << 32) ^ (uint)tiles[i].Row);
            }

            float worst = 0f;
            Vector2 worstAt = Vector2.zero;
            int inside = 0;
            int beyondCorridor = 0;
            int missing = 0;
            Vector2 missingAt = Vector2.zero;

            for (float z = minZ; z <= maxZ; z += Grid)
            {
                for (float x = minX; x <= maxX; x += Grid)
                {
                    if (!Encloses(outline, x, z))
                    {
                        continue;
                    }

                    inside++;

                    float away = field.DistanceToRoad(x, z);
                    if (away > terrainShape.CorridorWidth)
                    {
                        beyondCorridor++;
                    }

                    if (away > worst)
                    {
                        worst = away;
                        worstAt = new Vector2(x, z);
                    }

                    long key = ((long)Mathf.FloorToInt(x / tileSize) << 32)
                               ^ (uint)Mathf.FloorToInt(z / tileSize);

                    if (!built.Contains(key))
                    {
                        missing++;
                        missingAt = new Vector2(x, z);
                    }
                }
            }

            if (missing > 0)
            {
                Debug.LogError($"[Horizon] {missing} sampled points inside {what} fall on tiles the "
                               + "terrain builder will not produce — one of them at "
                               + $"({missingAt.x:0}, {missingAt.y:0}). That is a hole in the world in "
                               + "the middle of the circuit. Either fold the lap tighter, or ask for "
                               + "the tiles with a bounds the way the paddock and the water bands do.");
                return;
            }

            if (beyondCorridor > 0)
            {
                Debug.Log($"[Horizon] {what} infield: {inside} sampled points inside the loop, ground "
                          + $"under all of them. {beyondCorridor} of them sit outside the "
                          + $"{terrainShape.CorridorWidth:0} m corridor — the furthest {worst:0} m from "
                          + $"tarmac, at ({worstAt.x:0}, {worstAt.y:0}) — so the terrain there is "
                          + "carried by tiles the corridor pulled in rather than authored around a "
                          + "road. Flat infield, which is what an infield is.");
            }
            else
            {
                Debug.Log($"[Horizon] {what} infield: {inside} sampled points inside the loop, the "
                          + $"furthest {worst:0} m from tarmac against a {terrainShape.CorridorWidth:0} m "
                          + "corridor.");
            }
        }

        /// <summary>Whether a point is inside a closed polyline, by crossing count.</summary>
        private static bool Encloses(Vector2[] outline, float x, float z)
        {
            bool inside = false;

            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[j];

                if (a.y > z != b.y > z
                    && x < (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// Lays the circuit's kerbs. See <see cref="KerbBuilder"/> for why they are worth more than
        /// anything else built on this road.
        /// </summary>
        private static void BuildKerbs(
            Transform parent,
            IRoadPath path,
            in RoadShape shape,
            RoadCourse course,
            in CircuitBuild circuit,
            PrototypeMaterials materials)
        {
            var buffer = new VegetationMeshBuffer(KerbBuilder.KerbSubmeshCount);

            KerbBuilder.Append(path, shape, course, buffer);
            buffer.MergeTinted(KerbBuilder.KerbTints());

            var used = new List<int>(KerbBuilder.KerbSubmeshCount);
            Mesh mesh = buffer.ToMesh($"Kerb{circuit.Label}Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning("[Horizon] The circuit's kerbs came out empty — either every corner on "
                                 + "it is gentler than KerbBuilder.CornerRadius, or the path handed in "
                                 + "is not the one that was paved.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(
                mesh, GeneratedFolder + $"/Kerb{circuit.Label}Mesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = materials.RoadTint;
            }

            GameObject kerbObject = CreateMeshObject(
                parent, $"Kerbs{circuit.Label}", mesh, meshMaterials);

            WorldChunk chunk = kerbObject.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            // One submesh is the right answer, not a missing colour: MergeTinted folds both tinted
            // slots into the first and bakes the red and the white into the vertices, which is the whole
            // reason the kerbs cost one draw call and no material of their own.
            Debug.Log($"[Horizon] Kerbs on the {circuit.Name}: {mesh.triangles.Length / 3} "
                      + $"triangles in {used.Count} draw "
                      + $"call(s), red and white, on the corners of {path.Length / 1000f:0.0} km of "
                      + "circuit.");
        }

        /// <summary>
        /// Fills the circuit's paddock with level samples, so the apron the pit buildings and the grid
        /// stand on is a floor rather than a hillside.
        ///
        /// <para>The grid pitch has to stay under twice <c>MountainField.Verge</c> or the shelves the
        /// samples raise do not merge into one and the apron comes out corrugated. Half the verge is
        /// comfortably inside that and costs a few hundred samples.</para>
        /// </summary>
        private static void AddPaddockSamples(
            List<Vector3> levelSamples, in TerrainShape terrainShape, in CircuitBuild circuit)
        {
            Vector3 centre = circuit.PaddockCentre;
            float radius = circuit.PaddockRadius;
            float pitch = Mathf.Max(terrainShape.VergeWidth, terrainShape.CellSize * 2f) * 0.5f;

            for (float z = -radius; z <= radius; z += pitch)
            {
                for (float x = -radius; x <= radius; x += pitch)
                {
                    if (x * x + z * z > radius * radius)
                    {
                        continue;
                    }

                    levelSamples.Add(new Vector3(centre.x + x, centre.y, centre.z + z));
                }
            }
        }

        private static void BuildWorldMap(
            RoadPath pass,
            in RoadShape roadShape,
            RoadPath ebental,
            RoadPath stadtfeld,
            RoadPath kalkgrat,
            RoadPath meerenge,
            RoadPath yalikoy,
            RoadPath coast,
            RoadPath weissjoch,
            IRoadPath westbound,
            IRoadPath eastbound,
            in RoadShape motorwayShape,
            RoadPath link,
            RoadPath motorway,
            RoadCourse passCourse,
            RoadCourse ebentalCourse,
            RoadCourse stadtfeldCourse,
            RoadCourse kalkgratCourse,
            RoadCourse meerengeCourse,
            RoadCourse yalikoyCourse,
            RoadCourse coastCourse,
            RoadCourse weissjochCourse,
            RoadCourse motorwayCourse,
            RoadCourse linkCourse,
            RoadPath ring,
            RoadCourse ringCourse,
            RoadPath ringAccess,
            RoadPath bahce,
            RoadCourse bahceCourse,
            RoadPath bahceAccess,
            in RoadShape circuitShape,
            IReadOnlyList<TownBuild> towns,
            IReadOnlyList<WaterBody> waters,
            IReadOnlyList<SpawnPoint> spawns)
        {
            float half = roadShape.HalfWidth;

            var roads = new List<WorldMapBuilder.Road>
            {
                new WorldMapBuilder.Road(pass, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(ebental, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(stadtfeld, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(kalkgrat, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(meerenge, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(yalikoy, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(coast, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(weissjoch, MapLineKind.Trunk, half),
                new WorldMapBuilder.Road(westbound, MapLineKind.Motorway, motorwayShape.HalfWidth),
                new WorldMapBuilder.Road(eastbound, MapLineKind.Motorway, motorwayShape.HalfWidth),
                new WorldMapBuilder.Road(link, MapLineKind.Motorway, half),

                // Its own kind, so the one closed loop in the world does not read as another country
                // road that happens to bend a lot. The key beside the full-screen map reads its swatch
                // off MapGraphic.ColourOf, so adding the kind is the whole of the change there.
                new WorldMapBuilder.Road(ring, MapLineKind.Circuit, circuitShape.HalfWidth),
                new WorldMapBuilder.Road(ringAccess, MapLineKind.Trunk, half),

                // The second one takes the same kind rather than a colour of its own. What the kind
                // says is "closed loop, driven for its own sake"; that is true of both, and a map with
                // one red circuit and one of something else would be claiming a difference there is
                // not. They are two thousand kilometres apart on the map and cannot be confused.
                new WorldMapBuilder.Road(bahce, MapLineKind.Circuit, circuitShape.HalfWidth),
                new WorldMapBuilder.Road(bahceAccess, MapLineKind.Trunk, half),
            };

            var featured = new List<WorldMapBuilder.Featured>
            {
                new WorldMapBuilder.Featured(pass, passCourse),
                new WorldMapBuilder.Featured(ebental, ebentalCourse),
                new WorldMapBuilder.Featured(stadtfeld, stadtfeldCourse),
                new WorldMapBuilder.Featured(weissjoch, weissjochCourse),
                new WorldMapBuilder.Featured(kalkgrat, kalkgratCourse),
                new WorldMapBuilder.Featured(meerenge, meerengeCourse),
                new WorldMapBuilder.Featured(yalikoy, yalikoyCourse),
                new WorldMapBuilder.Featured(coast, coastCourse),
                new WorldMapBuilder.Featured(motorway, motorwayCourse),
                new WorldMapBuilder.Featured(link, linkCourse),
                new WorldMapBuilder.Featured(ring, ringCourse),
                new WorldMapBuilder.Featured(bahce, bahceCourse),
            };

            var settlements = new List<WorldMapBuilder.Town>(towns.Count);
            for (int i = 0; i < towns.Count; i++)
            {
                settlements.Add(new WorldMapBuilder.Town(towns[i].Name, towns[i].Network));
            }

            // SpawnPoint lives in Horizon.Game, which is above Horizon.World. The builder takes its own
            // record and the conversion happens here, where both are in scope.
            var places = new List<WorldMapBuilder.Place>(spawns.Count);
            for (int i = 0; i < spawns.Count; i++)
            {
                places.Add(new WorldMapBuilder.Place(spawns[i].Name, spawns[i].Position));
            }

            WorldMap map = WorldMapBuilder.Build(
                roads, featured, settlements, waters, places, out string report);

            map.name = "WorldMap";
            map = HorizonAssetUtility.ReplaceAsset(map, WorldMapPath);

            Debug.Log($"[Horizon] Map:{report}");

            ValidateWorldMap(map);
        }

        /// <summary>
        /// What the map has to be true about before anyone looks at a picture of it.
        ///
        /// <para>Every one of these fails silently otherwise: a line with a point missing draws nothing,
        /// a marker on the wrong path lands in a field with no complaint, and a mesh over the vertex
        /// limit is not a partial map but no map at all — uGUI drops the whole thing.</para>
        /// </summary>
        private static void ValidateWorldMap(WorldMap map)
        {
            if (map == null || map.LineCount == 0)
            {
                Debug.LogWarning("[Horizon] The world map came out empty.");
                return;
            }

            int segments = 0;

            for (int line = 0; line < map.LineCount; line++)
            {
                int span = map.LineEndAt(line) - map.LineStartAt(line);

                if (span < 2)
                {
                    Debug.LogWarning(
                        $"[Horizon] Map line {line} ({map.KindOf(line)}) has {span} points and cannot "
                        + "be drawn.");
                    continue;
                }

                segments += span - 1;
            }

            // One canvas mesh holds 65 535 vertices, which is 16 383 quads. Town streets are dropped
            // past a zoom threshold in MapGraphic, so the figure that matters is everything else.
            int wide = 0;
            for (int line = 0; line < map.LineCount; line++)
            {
                if (map.KindOf(line) != MapLineKind.Street)
                {
                    wide += Mathf.Max(0, map.LineEndAt(line) - map.LineStartAt(line) - 1);
                }
            }

            if (wide > 15000)
            {
                Debug.LogWarning(
                    $"[Horizon] The map has {wide} segments outside the towns, which is {wide * 4} "
                    + "vertices against the 65 535 one canvas mesh holds. Zoomed out it will draw "
                    + "nothing at all rather than draw partially. Coarsen WorldMapBuilder's sampling.");
            }

            // A marker is placed by feeding a course's own distance back through a path. Handing it the
            // wrong path is the one mistake here that produces a clean build and a wrong picture.
            int adrift = 0;
            string worst = string.Empty;
            float furthest = 0f;

            for (int i = 0; i < map.MarkerCount; i++)
            {
                float distance = NearestMapLine(map, map.MarkerAt(i));

                if (distance <= 40f)
                {
                    continue;
                }

                adrift++;

                if (distance > furthest)
                {
                    furthest = distance;
                    worst = map.MarkerNameOf(i);
                }
            }

            if (adrift > 0)
            {
                Debug.LogWarning(
                    $"[Horizon] {adrift} map markers stand more than 40 m from any road — worst is "
                    + $"'{worst}' at {furthest:0} m. A feature is positioned along a path, so this is a "
                    + "course measured against a road it is not on.");
            }

            Debug.Log(
                $"[Horizon] Map check: {segments} segments ({wide} outside the towns), "
                + $"{map.MarkerCount} markers, {adrift} adrift.");
        }

        /// <summary>Distance from a point to the nearest map line, through the map's own grid.</summary>
        private static float NearestMapLine(WorldMap map, Vector2 at)
        {
            float best = float.MaxValue;

            // Two cells each way: a marker further off than that is already well past the threshold.
            int column = map.ColumnOf(at.x);
            int row = map.RowOf(at.y);

            for (int r = row - 2; r <= row + 2; r++)
            {
                for (int c = column - 2; c <= column + 2; c++)
                {
                    map.CellRange(c, r, out int from, out int to);

                    for (int i = from; i < to; i++)
                    {
                        int point = map.ItemAt(i);

                        if (map.KindOf(map.LineOfPoint(point)) == MapLineKind.River)
                        {
                            continue;
                        }

                        best = Mathf.Min(best, DistanceToSegment(
                            at, map.PointAt(point), map.PointAt(point + 1)));
                    }
                }
            }

            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 span = b - a;
            float length = span.sqrMagnitude;

            if (length < 0.0001f)
            {
                return Vector2.Distance(point, a);
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, span) / length);
            return Vector2.Distance(point, a + span * t);
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
            RoadPath stadtfeld,
            RoadPath kalkgrat,
            RoadPath meerenge,
            RoadCourse meerengeCourse,
            RoadPath yalikoy,
            RoadPath weissjoch,
            RoadCourse weissjochCourse,
            RoadPath ring,
            RoadCourse ringCourse,
            RoadPath bahce,
            float rideHeight)
        {
            var spawns = new List<SpawnPoint>(14);

            void Add(string name, IRoadPath path, float distance, float across, float lift)
            {
                // NormalizeDistance, not a clamp: on a closed course a distance behind the start line is
                // negative, and clamping it to zero puts every grid slot on the line itself. It clamps
                // exactly as before on every road that has two ends.
                float at = path.NormalizeDistance(distance);

                Vector3 forward = path.GetDirectionAtDistance(at);
                Vector3 position = path.GetPositionAtDistance(at)
                                   + path.GetRightAtDistance(at) * across
                                   + Vector3.up * lift;

                spawns.Add(new SpawnPoint(name, position, Quaternion.LookRotation(forward, Vector3.up)));
            }

            // In the right-hand lane in each case, not astride the centre line.
            //
            // Both of these are read from the shape rather than typed. The city's was the literal 4,
            // which is the boulevard's half-width halved and was correct for exactly as long as that
            // half-width was 8 — widen the boulevard for the cars and the player is spawned astride a
            // lane line with nothing anywhere saying so.
            float passLane = passShape.HalfWidth * 0.5f;
            float boulevardLane = TownStreetShape.For(TownStreetKind.Boulevard).HalfWidth * 0.5f;

            Add("Talheim", pass, MountainPassCourse.TownStartDistance + 45f, passLane, rideHeight);

            // The summit, found by walking the course for its highest point rather than by a distance
            // somebody counted — the switchback stack is retuned often enough that a literal would rot.
            Add("Passhöhe", pass, HighestDistance(pass), passLane, rideHeight);

            // On the eastbound carriageway at the interchange, pointing at the city.
            Add("Autobahn", motorway, AutobahnCourse.JunctionDistance,
                AutobahnCourse.CarriagewayOffset + motorwayShape.HalfWidth * 0.5f, rideHeight);

            // On the boulevard, a little inside the city gate so the skyline is ahead rather than
            // overhead.
            Add("Hochstadt", arterial, 120f, boulevardLane, rideHeight);

            // On Seeburg's waterfront, a little past the harbour so the quay and the moles are in the
            // mirror rather than behind the camera. The right-hand lane here is the inland one, so the
            // water is out of the driver's window from the moment the scene loads.
            Add("Seeburg", seeburgAxis, SeeburgCourse.BasinAlong + 60f, boulevardLane, rideHeight);

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

            // On the Stadtfeld road's last crest, facing the fork three hundred metres below it — so
            // the first thing on screen is the junction that makes the world a ring rather than a line.
            //
            // From HighestDistance rather than from a counted distance, and unlike the Ebental's spawn
            // that is the right tool here: this road's high point is its last crest by two metres, it
            // is the only place on the leg that sees anything, and the profile is the whole design, so
            // a summit walk lands where the road was aimed even after it is retuned.
            Add("Stadtfeld", stadtfeld, HighestDistance(stadtfeld), passLane, rideHeight);

            // On the col at 906 m, facing the way the road falls away from it. The highest place a car
            // can stand in this world by four and a half times, and the only start point where the whole
            // of what follows is downhill.
            //
            // Taken from the viewpoint the course marks there rather than from HighestDistance, for the
            // reason the Ebental's spawn is: the summit is a two-hundred-metre level plateau, so a
            // highest-point walk lands wherever the noise happens to peak on it.
            Add(WeissjochCourse.ColName, weissjoch,
                ViewpointDistance(weissjochCourse, WeissjochCourse.ColName), passLane, rideHeight);

            // On pole, in the painted box, facing the line. This one is not a convenience: the circuit
            // is twenty-five kilometres and nine hundred metres of climb from anywhere else in the
            // world, and a race track nobody can get to in under a quarter of an hour is a race track
            // nobody drives.
            //
            // On the grid rather than on the line, and that is what makes it read as a start. Standing
            // astride the timing line there is nothing to say which way round the lap goes or where it
            // begins; sixteen metres back, in the first box, with eleven more behind, the answer is
            // painted on the road. The slot comes from CircuitMeshes' own table so the car lands on the
            // box rather than beside it.
            CircuitMeshes.GridSlot(0, WeissjochringCourse.LineDistance, RoadShape.Circuit,
                out float poleAlong, out float poleAcross);
            Add(WeissjochringCourse.CircuitName, ring, poleAlong, poleAcross, rideHeight);

            // And pole on the other one, for the same reasons. The Bahçe Ring is at the far end of the
            // eastern branch, which is further from the pass than the Weissjochring is.
            CircuitMeshes.GridSlot(0, BahceRingCourse.LineDistance, RoadShape.Circuit,
                out float bahcePoleAlong, out float bahcePoleAcross);
            Add(BahceRingCourse.CircuitName, bahce, bahcePoleAlong, bahcePoleAcross, rideHeight);

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

            // On Yalıköy's seafront by the harbour, facing along the front. The right-hand lane here is
            // the inland one — the same choice Seeburg's spawn makes, and for the same reason: the water
            // should be out of the driver's own window from the moment the scene loads.
            Add(YalikoyCourse.TownName, yalikoy, YalikoyCourse.BasinAlong + 60f, passLane, rideHeight);

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
        /// <param name="shape">
        /// The town this harbour belongs to, read for its cross-fall alone.
        ///
        /// <para>Handed in rather than looked up because there are two of these now — Seeburg's, on an
        /// axis of its own, and Yalıköy's, on the seafront road itself. Everything else that differs
        /// between them is a distance, which is why they are the parameters below.</para>
        /// </param>
        private static void BuildHarbour(
            Transform parent,
            string name,
            string assetName,
            RoadPath axis,
            in TownShape shape,
            MountainField field,
            in TerrainShape terrainShape,
            PrototypeMaterials materials,
            StreetNetwork streets,
            Vector3 basinAt,
            Vector3 seaward,
            float seaLevel,
            float basinAlong,
            float basinAcross,
            float basinRadius,
            float basinDepth,
            float shoreOffset,
            float promenadeFrom,
            float promenadeTo,
            float railAcross,
            List<MeshRenderer> litRenderers,
            List<int> litSlotStart,
            List<int> litSlots,
            List<int> litSlotGroups)
        {
            // Sized so its landward rim stands this far out from the waterfront. The quay geometry is
            // laid against the same figure, because a quay wall that is not on the edge of the basin is a
            // wall in the water or a wall in a field.
            float basinRimAcross = -basinAcross - basinRadius;

            // The quay's paving is the town's own floor at the basin's rim, less the shelf drop the field
            // applies to every levelled sample. Derived rather than sampled, because sampling the ground
            // there reads the bank that has just been dug into it.
            //
            // Through TownShape.CrossFall rather than a course's own copy of the formula: two towns want
            // this now, and both of their courses declare the same three numbers for the same reason —
            // so the one function that already takes them is the place to ask.
            float quayY = axis.GetPositionAtDistance(basinAlong).y
                          + TownShape.CrossFall(shape, basinRimAcross)
                          - terrainShape.RoadShelfDrop;

            var site = new HarbourMeshes.HarbourSite(
                basinAt,
                basinRadius,
                -seaward,
                seaLevel,
                seaLevel - basinDepth,
                quayY,
                // Where the open sea's waterline crosses, measured from the basin's centre. The moles
                // start there, because an arm that starts anywhere else starts in the water.
                -basinAcross - shoreOffset);

            var buffer = new VegetationMeshBuffer(HarbourMeshes.SubmeshCount);
            HarbourMeshes.AddHarbour(buffer, site);

            // The same figure twice: it is how far outside the kerb the rail nominally stands, and it is
            // the margin the clearance test holds it to. Two numbers here means every post on a dead
            // straight stretch counts as blocked and swings out for nothing — which is what happened.
            const float railClearance = 1.2f;

            HarbourMeshes.AddPromenade(buffer, axis, field, streets,
                promenadeFrom, promenadeTo, railAcross, railClearance,
                // How far the rail may lean out to get round a pad before it gives up and leaves a gap.
                // Six metres, because the beach begins about twenty out from the waterfront's centreline
                // and the rail nominally stands fourteen.
                6f,
                out float worstSwing, out int railGaps);

            buffer.MergeTinted(HarbourMeshes.Tints());

            var used = new List<int>(HarbourMeshes.SubmeshCount);
            // Named separately from the town, because an asset path is not the place for a dotless ı.
            string asset = assetName + "Harbour";
            Mesh mesh = buffer.ToMesh(asset + "Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {name} harbour: nothing was built.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{asset}Mesh.asset");

            var harbourMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                harbourMaterials[i] = used[i] == HarbourMeshes.LanternSubmesh
                    ? materials.WindowDay
                    : materials.BuildingTint;
            }

            GameObject harbour = CreateMeshObject(parent, asset, mesh, harbourMaterials);

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

            Debug.Log($"[Horizon] {name} harbour: {mesh.triangles.Length / 3} triangles in "
                      + $"{used.Count} draw call(s) — a {basinRadius:0} m basin with its rim "
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

        /// <summary>
        /// The sky material, loaded by path after a scene switch.
        ///
        /// <para>By path and not through a <c>PrototypeMaterials</c> carried across the switch, which is
        /// the rule <c>LoadWorldMap</c> and <c>LoadTimeOfDayProfile</c> already state for themselves:
        /// opening a new scene invalidates object references held from the old one.</para>
        /// </summary>
        private static Material LoadSkyMaterial()
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);

            if (sky == null)
            {
                Debug.LogError($"[Horizon] No sky material at {SkyMaterialPath}. Bootstrap is the "
                               + "active scene at run time, so without this the game renders Unity's "
                               + "built-in dome — and every preview frame still looks correct.");
            }

            return sky;
        }

        private static void BuildBootstrapScene(IReadOnlyList<SpawnPoint> spawns)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // <b>This scene's RenderSettings are the ones the game actually renders, and nothing here
            // had ever written them.</b> GameBootstrap loads the world additively and never calls
            // SetActiveScene, and RenderSettings is per-scene — so the world scene's copy is loaded and
            // then ignored, and every frame the player sees comes out of Bootstrap's. Both scenes carried
            // `m_SkyboxMaterial: {fileID: 10304}`, Unity's built-in default, and the game looked right
            // only because TimeOfDayController rewrote the skybox into whatever was active every frame.
            //
            // It is also the fault a picture cannot catch. Rebuild leaves the editor with Bootstrap
            // active, so every preview frame this project takes already uses these settings: bake the
            // sky into the world scene alone and the pictures come back perfect while the build ships a
            // stock blue dome.
            RenderSettings.skybox = LoadSkyMaterial();

            // Quarter of the resolution it was. There are no reflection probes here, so this cubemap is
            // every wet road, every pane of glass and every wheel rim in the world — and it is now
            // rebuilt as the clock moves rather than once at load, so its cost is per second instead of
            // once.
            //
            // The binding surface is M_CarGlass at smoothness 0.92, which samples the top mip, and 64
            // is enough for it only because of what is being reflected: this sky is a gradient with
            // soft-edged cloud on it and carries no detail a higher resolution could resolve. That is
            // the argument, and it is worth stating rather than the arithmetic about the wet road at
            // 0.46 being three mips down — the road is not what sets this number.
            RenderSettings.defaultReflectionResolution = 64;

            // After the scene switch, never before it. See LoadWorldMap.
            WorldMap map = LoadWorldMap();

            var root = new GameObject("GameBootstrap");
            GameBootstrap bootstrap = root.AddComponent<GameBootstrap>();
            DriveInputRouter router = root.AddComponent<DriveInputRouter>();
            DriveDebugOverlay overlay = root.AddComponent<DriveDebugOverlay>();
            QualityDirector quality = root.AddComponent<QualityDirector>();

            // On the Bootstrap object rather than in the world, for the same reason the menus are: a
            // room outlives a zone change, and the multiplayer page has to exist before there is a car
            // to report. The lap board is beside it because it reads LapTiming the way LapTimer does —
            // from Bootstrap, retrying while the world scene is still loading.
            NetSession session = root.AddComponent<NetSession>();
            NetLapBoard lapBoard = root.AddComponent<NetLapBoard>();

            HorizonAssetUtility.Configure(lapBoard, serialized =>
                serialized.FindProperty("session").objectReferenceValue = session);

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

            TouchUiSetup.UiParts ui = TouchUiSetup.Build(root, router, spawnNames, map);

            // Now that the start screen and the quality director exist, GameBootstrap can be told about
            // them. Wired explicitly rather than left to the FindFirstObjectByType fallbacks in Awake —
            // those are there for a scene somebody assembled by hand, not for generated output.
            HorizonAssetUtility.Configure(bootstrap, serialized =>
            {
                serialized.FindProperty("worldSceneName").stringValue = WorldSceneName;
                serialized.FindProperty("inputRouter").objectReferenceValue = router;
                serialized.FindProperty("qualityDirector").objectReferenceValue = quality;
                serialized.FindProperty("startScreen").objectReferenceValue = ui.StartScreen;
                serialized.FindProperty("netSession").objectReferenceValue = session;
            });

            // The one thing a guest takes from the host that this class does not own: the weather goes
            // through PauseMenu.SetWeather, which is the single place a preset becomes an Overcast
            // value. A second writer ramping that field is the fault recorded at length against the
            // rain, and it shows as a sky that snaps and then slides back.
            HorizonAssetUtility.Configure(session, serialized =>
                serialized.FindProperty("pauseMenu").objectReferenceValue = ui.Menu);

            // And back the other way, for the one question the menu asks the session: is the sky on
            // this device somebody else's to set.
            HorizonAssetUtility.Configure(ui.Menu, serialized =>
                serialized.FindProperty("session").objectReferenceValue = session);

            HorizonAssetUtility.AssertReferenceAssigned(session, "pauseMenu");

            // The menu builder wires every part of its own two pages and leaves this one hole, because
            // the session lives on the Bootstrap object rather than on the canvas. Without it the page
            // draws perfectly and no button on it does anything.
            HorizonAssetUtility.Configure(ui.Multiplayer, serialized =>
                serialized.FindProperty("session").objectReferenceValue = session);

            HorizonAssetUtility.AssertReferenceAssigned(ui.Multiplayer, "session");

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
                    bool shadows, bool exhaust, bool tyreSmoke, bool airRush, float rainDrops,
                    float bloom, int frameRate)
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
                    level.FindPropertyRelative("RainDrops").floatValue = rainDrops;
                    level.FindPropertyRelative("Bloom").floatValue = bloom;
                    level.FindPropertyRelative("TargetFrameRate").intValue = frameRate;
                }

                // Low keeps the tyre smoke while losing the exhaust, which looks like the wrong way
                // round until you ask what each one is for. The tailpipe plume is atmosphere and runs
                // constantly; tyre smoke is feedback — it is how the player sees that the car has let
                // go — and taking that away on a weak phone would remove information rather than
                // decoration. It also costs nothing at all until something actually slides.
                // Rain is the one effect here that is thinned rather than switched off, and Low still
                // draws a third of it. The other three are decoration; rain is a state the world is in,
                // and a phone that showed none of it while the road was slippery and the roof was
                // rattling would be lying about the weather rather than saving a frame.
                // Bloom is off on Low and full above it, which is what the performance budget has said
                // all along. It is the one part of the post stack that is a separate blur chain over the
                // colour buffer rather than a curve applied to a pixel already being written, so it is
                // the only part with a cost worth a setting. The tone map and the grade run on all three
                // — those are not polish, they are what every colour in this world is.
                Set((int)QualityPreset.Low, "Low",
                    380f, 500f, 140f, 24, 320f, 460f, false, false, true, false, 0.33f, 0f, 30);

                Set((int)QualityPreset.Balanced, "Balanced",
                    650f, 820f, 220f, 56, 650f, 900f, true, true, true, true, 0.7f, 1f, 60);

                Set((int)QualityPreset.High, "High",
                    820f, 1000f, 260f, TrafficPoolSize, 800f, 1050f, true, true, true, true, 1f, 1f, 60);
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
            string name,
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
            Mesh mesh = buffer.ToMesh(name + "Mesh", used);
            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {name} came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/" + name + "Mesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = materials.RoadTint;
            }

            GameObject merge = CreateMeshObject(parent, name, mesh, meshMaterials);

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

            Debug.Log($"[Horizon] {name}: {MotorwayMergeBuilder.TotalLength:0} m of acceleration "
                      + $"lane on the {(side < 0f ? "left" : "right")} of the carriageway, opening "
                      + $"{mouthWidth:0.0} m wide at the link's cap and closing to nothing. The ramp's "
                      + $"paving and the {lateral - motorwayShape.HalfWidth - linkShape.HalfWidth:0.0} m "
                      + "of gravel between the two roads are both under it now.");
        }

        /// <summary>
        /// Bakes one end of the motorway: the paving that brings two carriageways together into the one
        /// road that carries on. See <c>MotorwayTerminusBuilder</c> for the geometry and for what was
        /// there before, which was nothing at all.
        /// </summary>
        private static void BuildMotorwayTerminus(
            Transform parent,
            string name,
            IRoadPath median,
            in RoadShape motorwayShape,
            in RoadShape onwardShape,
            float atDistance,
            float travelSign,
            PrototypeMaterials materials,
            string onward)
        {
            var buffer = new VegetationMeshBuffer(MotorwayTerminusBuilder.TerminusSubmeshCount);

            MotorwayTerminusBuilder.Append(
                median, motorwayShape, AutobahnCourse.CarriagewayOffset, onwardShape,
                atDistance, travelSign, buffer);

            buffer.MergeTinted(MotorwayTerminusBuilder.SurfaceTints());

            var used = new List<int>(MotorwayTerminusBuilder.TerminusSubmeshCount);
            Mesh mesh = buffer.ToMesh(name + "Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {name} came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/" + name + "Mesh.asset");

            // Two materials, and which is which comes from the buffer's own list rather than from the
            // order they happened to be filled in. The shoulder slot carries no tint precisely so it can
            // keep the gravel material; everything tinted has already been folded into one.
            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = used[i] == MotorwayTerminusBuilder.ShoulderSubmesh
                    ? materials.RoadShoulder
                    : materials.RoadTint;
            }

            GameObject terminus = CreateMeshObject(parent, name, mesh, meshMaterials);

            WorldChunk chunk = terminus.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            float wide = AutobahnCourse.CarriagewayOffset + motorwayShape.HalfWidth;

            Debug.Log($"[Horizon] {name}: {MotorwayTerminusBuilder.TerminusLength:0} m bringing "
                      + $"{wide * 2f:0.0} m of dual carriageway down to the {onwardShape.HalfWidth * 2f:0.0} m "
                      + $"of {onward}, at {atDistance:0} m along the median.");
        }

        /// <summary>
        /// Which end of a branch road meets the fork, and therefore which way everything at the mouth
        /// walks.
        ///
        /// <para>Asked rather than assumed, because both answers occur in this world: a branch grafted
        /// onto a published pose starts at the fork, and one solved into it with <c>ConnectTo</c>
        /// finishes there. <c>AppendTrunkMouth</c> records what assuming the equivalent cost the town's
        /// mouths.</para>
        ///
        /// <para>It is here rather than inside <see cref="BuildTrunkFork"/> because the branch's own
        /// ribbon is trimmed against the same answer, one pass earlier — and two copies of this would
        /// agree until the day a course was walked the other way round, then trim one end of a road and
        /// pave the other.</para>
        /// </summary>
        private static void ResolveBranchMouth(
            IRoadPath branch, Vector3 fork, out float branchAt, out float branchSign, out float miss)
        {
            // Squared distance in plan, because a branch that arrives from below is still the branch
            // that arrives.
            float toZero = Plan(branch.GetPositionAtDistance(0f) - fork).sqrMagnitude;
            float toEnd = Plan(branch.GetPositionAtDistance(branch.Length) - fork).sqrMagnitude;

            bool mouthAtStart = toZero <= toEnd;

            branchAt = mouthAtStart ? 0f : branch.Length;
            branchSign = mouthAtStart ? 1f : -1f;
            miss = Mathf.Sqrt(Mathf.Min(toZero, toEnd));
        }

        /// <summary>
        /// A branch road's ribbon, stopped short of the carriageway it joins.
        ///
        /// <para>See <c>TrunkForkBuilder.RibbonTrim</c> for the arithmetic and
        /// <c>RoadMeshBuilder.BuildRoad</c>'s trim for what keeps the markings in phase across it. What
        /// this adds is the bookkeeping: which end to cut, and a line in the log, because a trim is
        /// invisible in a triangle count and the one thing every laid-on surface in this project has
        /// gone wrong by is being counted correctly and built in the wrong place.</para>
        /// </summary>
        private static Mesh BuildBranchRoad(
            IRoadPath branch,
            in RoadShape branchShape,
            string meshName,
            IRoadPath trunk,
            in RoadShape trunkShape,
            Vector3 fork,
            string what)
        {
            ResolveBranchMouth(branch, fork, out float branchAt, out float branchSign, out _);

            float atDistance = NearestDistanceOn(trunk, fork);

            float trim = TrunkForkBuilder.RibbonTrim(
                trunk, trunkShape, atDistance, branch, branchShape, branchAt, branchSign);

            float from = branchSign > 0f ? trim : 0f;
            float to = branchSign > 0f ? branch.Length : branch.Length - trim;

            Debug.Log($"[Horizon] {what}: its ribbon stops {trim:0.0} m short of the junction, at the "
                      + $"{(branchSign > 0f ? "start" : "end")} of its own course. Past that the fork's "
                      + "throat is the road surface, and the carriageway it joins keeps its own.");

            return RoadMeshBuilder.BuildRoad(branch, branchShape, meshName, from, to);
        }

        /// <summary>
        /// The paved mouth where a branch road leaves a trunk road out in the country.
        ///
        /// <para>Mirrors <see cref="BuildMotorwayMerge"/> in every respect that is bookkeeping — fill a
        /// buffer, tint it, bake the mesh, hang it on a chunk that never unloads — and differs in the
        /// two things it measures rather than assumes. Which <i>end</i> of the branch the mouth is at
        /// is worked out from the geometry, because a branch grafted onto a fork starts there and a
        /// branch solved into one finishes there, and the two run opposite ways. Which way the throat
        /// then walks follows from that. <c>AppendTrunkMouth</c> records what assuming the equivalent
        /// cost it: five town streets with their mouths built on the wrong side of the road.</para>
        /// </summary>
        private static void BuildTrunkFork(
            Transform parent,
            string name,
            IRoadPath trunk,
            in RoadShape trunkShape,
            Vector3 fork,
            IRoadPath branch,
            in RoadShape branchShape,
            Mesh branchMesh,
            PrototypeMaterials materials)
        {
            if (trunk == null || branch == null)
            {
                return;
            }

            // The mouth is found on the trunk by <b>position</b>, not by the course distance the fork was
            // marked at, and the difference is not academic. A RoadCourse's distance is the sum of its
            // straights and arcs; a RoadPath's is arc length along the Catmull-Rom curve through the same
            // control points, and the two disagree — the Ebental is 5073 m as a course and 5074 m as a
            // path. Sampled at 4873 m that put the throat 0.8 m up the road from the fork it is the mouth
            // of, and the agreement check below reported it as two courses that had drifted apart when
            // nothing had. It is the same trap BuildMotorwayMerge records for the median line and its
            // carriageways, which is why it uses the same helper.
            float atDistance = NearestDistanceOn(trunk, fork);

            ResolveBranchMouth(branch, fork, out float branchAt, out float branchSign, out float miss);

            // A fork is the one feature two courses have to agree about, and this is where that
            // agreement is checked rather than trusted. Half a metre is generous for two poses that
            // should be identical; anything above it means the mark and the graft have come apart, and
            // the throat would be laid between two roads that do not meet.
            if (miss > 0.5f)
            {
                Debug.LogError(
                    $"[Horizon] The branch's end is {miss:0.0} m from the junction mark on the road it "
                    + "leaves. One of the two has been retuned without the other — the fork's pose is "
                    + "published by the trunk's course and read by the branch's, so neither should be a "
                    + "literal.");
            }

            var buffer = new VegetationMeshBuffer(TrunkForkBuilder.ForkSubmeshCount);

            TrunkForkBuilder.Append(
                trunk, trunkShape, atDistance, branch, branchShape, branchAt, branchSign, buffer);

            buffer.MergeTinted(TrunkForkBuilder.SurfaceTints());

            var used = new List<int>(TrunkForkBuilder.ForkSubmeshCount);
            Mesh mesh = buffer.ToMesh(name + "Mesh", used);

            if (mesh == null)
            {
                Debug.LogWarning($"[Horizon] {name} came out empty.");
                return;
            }

            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/" + name + "Mesh.asset");

            var meshMaterials = new Material[used.Count];
            for (int i = 0; i < used.Count; i++)
            {
                meshMaterials[i] = materials.RoadTint;
            }

            GameObject forkObject = CreateMeshObject(parent, name, mesh, meshMaterials);

            WorldChunk chunk = forkObject.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            Debug.Log($"[Horizon] {name} at {atDistance:0} m, opening from the branch's "
                      + $"{branchShape.HalfWidth:0.0} m half-width to "
                      + $"{TrunkForkBuilder.MouthHalfWidth(branchShape, trunkShape):0.0} m at the "
                      + "mouth. The branch meets the mark "
                      + $"within {miss:0.00} m.");

            ValidateForkSeam(trunk, trunkShape, atDistance, branch, branchAt, branchSign,
                mesh, branchMesh, name);
        }

        /// <summary>The same vector with its height thrown away. Junctions are a question in plan.</summary>
        private static Vector3 Plan(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

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
            float worstReach = 0f;
            float worstLink = 0f;
            float worstWedge = 0f;

            // How far the carriageway's banked frame is tilted here. The wedge is built in that frame
            // and the ramp is not, so every centimetre of this is multiplied by how far out the ramp
            // stands — which is what a wider motorway and a ramp pushed out to clear it both increase.
            float bankDegrees = Mathf.Asin(Mathf.Clamp(wayRight.y, -1f, 1f)) * Mathf.Rad2Deg;

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
                    worstReach = motorwayShape.HalfWidth + width;
                    worstLink = onLink.y;
                    worstWedge = onWedge.y;
                }
            }

            // A tenth of the tyre's radius. Below that a raycast wheel rides over it as a bump; above it
            // the suspension takes the whole step in one physics tick.
            //
            // <b>Of the *smallest* tyre in the garage, and read rather than written down.</b> It was the
            // literal 0.04, which is a tenth of the 0.40 the hatchback's wheel used to be — a rule about
            // the car, spelt as a number, in a file the car does not pass through. 5bd7396 grew every
            // wheel by 15 % and the constant stayed where it was, so the check went on holding the
            // interchange to a tyre nobody drives any more. The smallest wheel is the one that has to
            // ride the step, which is why this is a minimum rather than the default car's.
            float tolerable = SmallestWheelRadius() * 0.1f;

            if (worst > tolerable)
            {
                Debug.LogWarning(
                    $"[Horizon] The ramp meets the merge with a {worst * 100f:0.0} cm step, worst "
                    + $"{worstAcross:0.0} m across its width — the ramp at {worstLink:0.00} m against the "
                    + $"wedge at {worstWedge:0.00} m, {worstReach:0.0} m out from the carriageway's "
                    + $"centreline in a frame banked {bankDegrees:0.00}°. A wheel crosses that at ramp "
                    + "speed. The two roads are graded by different courses — see "
                    + "AutobahnCourse.MotorwayGradeAtJunction, which is what keeps them level with each "
                    + "other; the bank and the reach are what multiply any disagreement.");
                return;
            }

            Debug.Log($"[Horizon] Merge seam: the ramp meets the acceleration lane within "
                      + $"{worst * 1000f:0} mm across its whole width, {worstReach:0.0} m out from the "
                      + $"carriageway's centreline in a frame banked {bankDegrees:0.00}°.");
        }

        /// <summary>
        /// Radius of the smallest wheel any car in the garage rolls on, metres.
        ///
        /// <para>Whatever a step or a lip in the world has to be ridden over, it is this wheel that has
        /// to ride it. Read off the profiles the meshes are lofted from, which is where
        /// <c>VehicleConfigPresets</c> already reads its own copy of the same number.</para>
        /// </summary>
        private static float SmallestWheelRadius()
        {
            float smallest = float.MaxValue;
            for (int i = 0; i < CarMeshBuilder.PlayerProfiles.Length; i++)
            {
                smallest = Mathf.Min(smallest, CarMeshBuilder.PlayerProfiles[i].WheelRadius);
            }

            return smallest == float.MaxValue ? 0.46f : smallest;
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
            PrototypeMaterials materials,
            float fromDistance = 0f,
            float toDistance = -1f)
        {
            Mesh mesh = RoadMeshBuilder.BuildRoad(path, shape, name + "Mesh", fromDistance, toDistance);
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}Mesh.asset");

            GameObject carriageway = CreateMeshObject(parent, name, mesh,
                new[] { materials.MotorwaySurface, materials.RoadShoulder },
                surfaces: CarriagewaySurfaces);

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

            // Where it runs out, which is the one number the next leg is authored against: every course
            // in this world starts at the previous one's EndPoint and EndHeading, and reading them out
            // of the build beats deriving them by hand from a chain of turns and grades.
            Vector3 end = path.GetPositionAtDistance(length);
            Vector3 heading = path.GetDirectionAtDistance(length);

            Debug.Log($"[Horizon] {what} ends at ({end.x:0}, {end.y:0}, {end.z:0}), heading "
                      + $"{Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg:0.0}°.");

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

        /// <summary>
        /// The post-processing stack: a tone map and a colour grade that always run, and a bloom that
        /// the quality director can turn off.
        ///
        /// <para><b>This is not a new feature so much as an unpaid bill.</b> The performance budget has
        /// specified "tonemapping + colour grading always; bloom on Mid/High only" from the beginning
        /// and none of it existed — the project's volume profile was Unity's stock file with every
        /// override neutral, neither scene held a <see cref="Volume"/>, and the chase camera had no
        /// <see cref="UniversalAdditionalCameraData"/> at all, so <c>renderPostProcessing</c> sat at its
        /// default of false. Meanwhile <c>VehicleLights</c> drives its lens colours to 2.4 and 3.2 with a
        /// comment saying that "reads as a lit lamp <i>and blooms</i>", the fuel station signs, the
        /// bridge beacons and the circuit board are all deliberately bright unlit materials, and every
        /// one of those values was being clipped flat to white.</para>
        ///
        /// <para><b>Two volumes rather than one, because a profile is an asset.</b> Switching bloom by
        /// writing <c>active</c> on a component inside a shared profile would edit that asset, and Unity
        /// does not roll asset edits back when Play mode ends — the hazard <c>TownLights</c> and
        /// <c>WetSurfaces</c> both document. So the bloom gets a volume of its own and
        /// <see cref="PostProcessing"/> turns its <see cref="Volume.weight"/> down instead, which is a
        /// runtime value on a component and nothing else.</para>
        ///
        /// <para>Profiles go through <c>LoadOrCreate</c>, so a grade tuned by hand survives a rebuild the
        /// way a <c>VehicleConfig</c> does.</para>
        /// </summary>
        private static PostProcessing BuildPostProcessing(Transform parent)
        {
            VolumeProfile baseProfile = HorizonAssetUtility.LoadOrCreate(
                "Assets/_Project/Settings/PostProfile_Base.asset",
                () => ScriptableObject.CreateInstance<VolumeProfile>());

            if (baseProfile.components.Count == 0)
            {
                // Neutral rather than ACES, and that is the art direction deciding. ACES crushes the
                // shadows, desaturates them and pulls the highlights towards film — which is a look, and
                // it is not this one. The reference points written down for this project are Alto's
                // Odyssey and Monument Valley: flat, saturated, warm. Neutral keeps those flats intact
                // and does the one job actually wanted here, which is to stop values above 1 clipping.
                Tonemapping tonemapping = baseProfile.Add<Tonemapping>(true);
                tonemapping.mode.value = TonemappingMode.Neutral;

                // Deliberately close to nothing. A grade that leans hard against the tone map makes the
                // tone map invisible and keeps its cost, and the honest place to fix a colour is the
                // colour. These two give back the small amount of bite Neutral takes out, and the asset
                // exists mainly so there is somewhere to tune without a rebuild.
                // +0.5 stops, and leaving it at zero would have made this change a regression.
                // Neutral maps linear 1.0 to 0.63 and 2.4 to 0.89, so a tone map with no exposure lift
                // takes every value in the world *down* — and the lamp lenses at 2.4 and the brake
                // lights at 3.2, which clip to flat white today, would come back dimmer than they are
                // now. That is the exact opposite of what VehicleLights' comment has been promising.
                // Full compensation of the highlights wants about +1.1 stops, which lifts mid grey from
                // 0.18 to 0.35 and washes the world out; +0.5 opens the shadows and mid tones, brings
                // the highlights down far enough to have shape instead of clipping, and leaves the
                // brake lights the brightest thing in the frame.
                ColorAdjustments grade = baseProfile.Add<ColorAdjustments>(true);
                grade.postExposure.value = 0.5f;

                // Both applied after the curve — postExposure is the only grading control that acts
                // before it. These give back the separation and the bite the shoulder takes out.
                grade.contrast.value = 10f;
                grade.saturation.value = 8f;

                // Four ALU inside the pass that is already running, no extra pass, and the HUD is a
                // ScreenSpaceOverlay canvas so the dials are composited after it and stay unvignetted.
                // Deliberately half of the 0.2 that Unity's sample profile had been about to impose.
                Vignette vignette = baseProfile.Add<Vignette>(true);
                vignette.intensity.value = 0.12f;
                vignette.smoothness.value = 0.4f;

                AttachProfileComponents(baseProfile);
            }

            VolumeProfile bloomProfile = HorizonAssetUtility.LoadOrCreate(
                "Assets/_Project/Settings/PostProfile_Bloom.asset",
                () => ScriptableObject.CreateInstance<VolumeProfile>());

            if (bloomProfile.components.Count == 0)
            {
                Bloom bloom = bloomProfile.Add<Bloom>(true);

                // Above the brightest thing the sun can make of an ordinary surface, and that is the
                // whole design of this number. Snow at 0.9 albedo under a 1.15 sun comes out around
                // 1.03, so a threshold at 1 would put a halo on every snowfield on the Weissjoch and
                // every white line on the motorway. At 1.1 nothing lit blooms and only things *driven*
                // past 1 do — which is exactly the set of objects this project has been building for
                // a bloom that was not there: lamp lenses at 2.4 and 3.2, forecourt signs, tower
                // beacons, the start/finish board.
                bloom.threshold.value = 1.1f;
                bloom.intensity.value = 0.55f;
                bloom.scatter.value = 0.62f;

                // Both off the cheap path on purpose. High-quality filtering costs extra taps per
                // iteration for a difference nobody sees on a phone, and half-resolution downscale is
                // the standard mobile setting.
                // Quarter and four iterations rather than half and six, and the reason is passes rather
                // than pixels. Every mip in the chain is its own render pass with a load and a store, and
                // on a tile GPU that fixed cost dominates once the textures are small — the last two
                // iterations of a six-deep chain are a 15x7 and an 8x4 texture bought at full pass price.
                // Quarter-res on an 0.8-scale render is a fifth of native, which for a glow around a lamp
                // is not a loss but a free widening.
                bloom.highQualityFiltering.value = false;
                bloom.downscale.value = BloomDownscaleMode.Quarter;
                bloom.maxIterations.value = 4;

                // A ceiling, so a future emissive or a specular hit cannot turn into a white disc.
                bloom.clamp.value = 20f;

                AttachProfileComponents(bloomProfile);
            }

            var baseObject = new GameObject("Post_Base");
            baseObject.transform.SetParent(parent, false);
            Volume baseVolume = baseObject.AddComponent<Volume>();
            baseVolume.isGlobal = true;
            baseVolume.priority = 0f;
            baseVolume.sharedProfile = baseProfile;

            var bloomObject = new GameObject("Post_Bloom");
            bloomObject.transform.SetParent(parent, false);
            Volume bloomVolume = bloomObject.AddComponent<Volume>();
            bloomVolume.isGlobal = true;
            bloomVolume.priority = 1f;
            bloomVolume.sharedProfile = bloomProfile;

            PostProcessing post = parent.gameObject.AddComponent<PostProcessing>();
            HorizonAssetUtility.Configure(post, serialized =>
                serialized.FindProperty("bloomVolume").objectReferenceValue = bloomVolume);

            HorizonAssetUtility.AssertReferenceAssigned(post, "bloomVolume");

            Debug.Log("[Horizon] Post-processing: Neutral tone map and colour grade always, "
                    + $"bloom above {1.1f:0.00} on a volume of its own.");

            return post;
        }

        /// <summary>
        /// Writes a profile's components into the profile's own asset file.
        ///
        /// <para><see cref="VolumeProfile.Add{T}"/> creates the component and puts it in the profile's
        /// list, but a <c>VolumeComponent</c> is a <c>ScriptableObject</c> in its own right — left
        /// unattached it is never serialised, and the profile comes back from a domain reload with a
        /// list of nulls. Nothing complains; the stack simply stops doing anything, which is
        /// indistinguishable from the stack not being there.</para>
        /// </summary>
        private static void AttachProfileComponents(VolumeProfile profile)
        {
            for (int i = 0; i < profile.components.Count; i++)
            {
                VolumeComponent component = profile.components[i];
                if (component != null && !AssetDatabase.IsSubAsset(component))
                {
                    component.name = component.GetType().Name;
                    AssetDatabase.AddObjectToAsset(component, profile);
                }
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
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
        /// The rain, as a box of drops hanging over the camera.
        ///
        /// <para><b>Parented to the rig but simulated in world space</b>, which is the whole of why this
        /// works. The emitter box travels with the camera so there is always rain in frame wherever the
        /// player looks; the drops, once emitted, belong to the world and fall straight down. Simulated
        /// in local space instead they would be dragged sideways with the car and lean into every
        /// corner, which reads as the rain being attached to the windscreen.</para>
        ///
        /// <para>Emitted by rate rather than by hand, unlike <see cref="BuildSpeedAtmosphere"/>'s grit.
        /// That one has to be placed relative to the direction of travel so the car meets it head on;
        /// rain only has to be everywhere, and a rate on a box shape is what a particle system is for.
        /// The rate itself is the one thing <c>WeatherDirector</c> writes.</para>
        ///
        /// <para>Stretched billboards, and the stretch comes from velocity rather than from a fixed
        /// length: a drop is a streak because it is moving, and a fixed length makes standing rain look
        /// like falling rain in a still frame — which is exactly the frame the preview tools take.</para>
        /// </summary>
        private static ParticleSystem BuildRain(Transform cameraTransform, PrototypeMaterials materials)
        {
            var rainObject = new GameObject("Rain");
            rainObject.transform.SetParent(cameraTransform, false);

            // Above and slightly ahead. Ahead because the camera looks forward and rain behind the lens
            // is rain nobody sees; the drops fall through the frame from the top, which is where they
            // are wanted.
            rainObject.transform.localPosition = new Vector3(0f, 14f, 8f);

            ParticleSystem particles = rainObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = particles.main;
            main.duration = 1f;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = true;

            // A drop from 14 m up at 22 m/s and accelerating is out of frame in well under a second.
            // Longer lifetimes only pay for drops that have already fallen past the road.
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.75f, 1.05f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(20f, 26f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.11f);
            main.gravityModifier = 1.1f;
            main.startColor = new Color(0.88f, 0.92f, 0.98f, 0.85f);

            // Rate over time only, and set to nothing here. The system plays from the first frame so
            // that the first drop of a shower is not a burst of the entire box arriving at once, and
            // WeatherDirector opens the tap.
            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            // Sized to the far plane rather than to the box it falls from: 90 m across is wider than the
            // frame at any sane field of view, and drops outside it are drops drawn behind the camera.
            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(90f, 1f, 60f);
            shape.rotation = new Vector3(90f, 0f, 0f);

            main.maxParticles = 2400;

            var renderer = rainObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.07f;
            renderer.lengthScale = 1f;
            renderer.cameraVelocityScale = 0f;
            renderer.sharedMaterial = materials.Rain;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return particles;
        }

        /// <summary>
        /// Sweeps the finished world for everything painted like a road, and records the wet twin of
        /// each slot.
        ///
        /// <para><b>Found by material identity rather than told by the builders.</b> Roads are painted
        /// by a dozen builders — the ribbons, the town streets, the forecourt aprons, the fork throats,
        /// the motorway merges and termini, the bridge decks — and threading a flag through all of them
        /// is a dozen places to forget one, with no symptom but a stretch of tarmac that stays dry. The
        /// test here is the exact asset the builder assigned, so a surface is a road if and only if a
        /// builder painted it as one. That is not the checker forming its own opinion; it is reading the
        /// builder's.</para>
        ///
        /// <para><b>Town streets are deliberately not in the list, and cannot be.</b> They are painted
        /// <c>M_TerrainTint</c> — the one vertex-tinted material that also carries grass, rock, sand and
        /// snow — so wetting them would wet every hillside in the world. Giving the streets a material
        /// of their own is the honest fix and it is a change to make on purpose rather than in passing,
        /// which is the same call the Weissjochring's missing snow got. Until then a shower darkens the
        /// carriageways and leaves the towns dry.</para>
        /// </summary>
        private static WetSurfaces BuildWetSurfaces(Transform worldRoot, PrototypeMaterials materials)
        {
            Material[] dry = materials.WetRoadMaterials;
            Material[] wet = materials.RoadWet;

            var groups = new List<WetSurfaces.Group>();
            var slots = new List<int>();
            var dryFound = new List<Material>();
            var wetFound = new List<Material>();

            MeshRenderer[] renderers = worldRoot.GetComponentsInChildren<MeshRenderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] assigned = renderers[i].sharedMaterials;

                slots.Clear();
                dryFound.Clear();
                wetFound.Clear();

                for (int slot = 0; slot < assigned.Length; slot++)
                {
                    int match = System.Array.IndexOf(dry, assigned[slot]);
                    if (match < 0 || match >= wet.Length || wet[match] == null)
                    {
                        continue;
                    }

                    slots.Add(slot);
                    dryFound.Add(assigned[slot]);
                    wetFound.Add(wet[match]);
                }

                if (slots.Count == 0)
                {
                    continue;
                }

                groups.Add(new WetSurfaces.Group
                {
                    Renderer = renderers[i],
                    Slots = slots.ToArray(),
                    Dry = dryFound.ToArray(),
                    Wet = wetFound.ToArray(),
                });
            }

            var holder = new GameObject("WetSurfaces");
            holder.transform.SetParent(worldRoot, false);

            WetSurfaces surfaces = holder.AddComponent<WetSurfaces>();
            surfaces.SetGroups(groups.ToArray());
            EditorUtility.SetDirty(surfaces);

            int slotCount = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                slotCount += groups[i].Slots.Length;
            }

            // Read back off the component rather than printed from the local list, which is the same
            // number twice until the day SetGroups refuses one of them. TrunkForkBuilder.MouthHalfWidth
            // is here because the build reported a fork's width from its own second copy of the formula
            // and went on looking right after the formula had been fixed.
            Debug.Log($"[Horizon] Rain: {surfaces.GroupCount} renderers carrying {slotCount} road material "
                      + "slots will darken when it rains, found by the material the builder assigned — "
                      + "the town streets among them now that they have an asset of their own rather "
                      + "than sharing one with every hillside in the world.");

            if (groups.Count == 0)
            {
                Debug.LogWarning(
                    "[Horizon] Nothing in the world is painted with a road material, so rain will fall "
                    + "on tarmac that never darkens. Either the sweep ran before the roads were built, "
                    + "or PrototypeMaterials.WetRoadMaterials has drifted from what the builders "
                    + "actually assign.");
            }

            return surfaces;
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
                result[i] = materials.TownStreet;
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
        /// <summary>
        /// The slope from a sample to one neighbour, or zero where that neighbour is not ground.
        ///
        /// <para>See the call site: a harbour bank is a bank, and a report about buildable area has
        /// nothing to say about how steeply the land falls into water.</para>
        /// </summary>
        private static float GradeTo(
            MountainField field, Vector3 from, float dx, float dz, float here, float step)
        {
            float x = from.x + dx;
            float z = from.z + dz;
            float there = field.HeightAt(x, z);

            if (field.IsUnderWater(x, z, there, 0.5f)
                || field.IsShore(x, z, there, ShoreFreeboard, ShoreReach))
            {
                return 0f;
            }

            return Mathf.Abs(there - here) / step;
        }

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

                    // Measured only against neighbours that are themselves buildable ground.
                    //
                    // <b>A step down into water is not a slope a house cannot stand on, it is a bank.</b>
                    // Skipping a sample and then using it as the neighbour of the next one measures
                    // exactly the thing the skip exists to ignore: at Yalıköy the quay apron came out at
                    // 33 % because the cell beside it is the inside of a dredged harbour, and Seeburg's
                    // equivalent sat just under the threshold by two-tenths of a metre of freeboard
                    // rather than by being any different.
                    float grade = Mathf.Max(
                        GradeTo(field, point, step, 0f, here, step),
                        GradeTo(field, point, 0f, step, here, step));

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
            IReadOnlyList<LandRegion> regions,
            LandRegion avenueRegion,
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
                avenueRegion != null ? avenueRoad : null, forecourts);
            var vegetationTotal = new VegetationStats();
            int heaviestTile = 0;
            string heaviestTileName = "none";
            int snowTriangles = 0;
            int aboveSnowLine = 0;
            int petalTriangles = 0;

            // Shore, snow and blossom are counted by the tile builder now, not read back off the
            // finished mesh by colour.
            //
            // <b>That used to be a fair trade and stopped being one.</b> Matching exact rgb is correct
            // for exactly as long as a kind is exactly one colour, and TerrainTileBuilder.Variation
            // ended that: every tint in the world now wanders, so all three counters would have come
            // back zero and two of them would have warned, on a world with nothing wrong with it. A
            // tolerance would not have rescued it either — the blossom drift and the snow sit about
            // forty levels apart in a world where either may wander thirty, so the distance that told
            // them apart would not have been reliably smaller than their separation.
            //
            // It is also three fewer whole-array allocations per tile across fifteen hundred tiles:
            // Mesh.colors32 copies the lot on every read.

            var townTotals = new TownStats[towns.Count];
            for (int i = 0; i < towns.Count; i++)
            {
                townTotals[i] = new TownStats();
            }

            int waterTiles = 0;
            int waterTriangleTotal = 0;
            int shoreTriangles = 0;
            int drownedTiles = 0;

            // One region per tile, picked by which of them reaches it.
            //
            // <b>They cannot overlap, and that is what makes one enough.</b> A region is a corridor
            // about its own carriageway, 260 m wide at the outside, and the two in this world are five
            // kilometres apart. Picking the first that reaches is therefore exact rather than a
            // tie-break. A tile that a region reaches but whose weight is nought — everything west of
            // the Meerenge's crossing, which shares its road with the far shore — comes out unchanged,
            // which is the answer wanted there anyway.
            // Read out here rather than inside: a local function may not touch an `in` parameter.
            float regionTileSize = TerrainTileBuilder.TileSize(terrainShape);

            LandRegion RegionFor(TerrainTileKey key)
            {
                if (regions == null)
                {
                    return null;
                }

                float half = regionTileSize * 0.5f;
                float centreX = key.Column * regionTileSize + half;
                float centreZ = key.Row * regionTileSize + half;

                for (int r = 0; r < regions.Count; r++)
                {
                    if (regions[r] != null && regions[r].Reaches(centreX, centreZ, half * 1.5f))
                    {
                        return regions[r];
                    }
                }

                return null;
            }

            for (int i = 0; i < tiles.Count; i++)
            {
                TerrainTileKey key = tiles[i];
                string name = $"Terrain_{key.Column}_{key.Row}";
                LandRegion region = RegionFor(key);

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
                    Mesh mesh = TerrainTileBuilder.BuildTile(
                        key, field, terrainShape, name,
                        out TerrainTileBuilder.TerrainTintCounts tints, region);

                    totalTriangles += tints.Triangles;

                    // The shore. The snow, which is decided by an elevation against a region's own line
                    // and is therefore exactly the kind of thing that comes out as nothing at all — or
                    // as the whole mountain — without the build saying a word. And the Bahçe's blossom
                    // drift, which is one parcel value in four inside one region: the smallest thing in
                    // this build anybody would notice missing and the least likely to announce itself.
                    shoreTriangles += tints.Sand;
                    snowTriangles += tints.Snow;
                    aboveSnowLine += tints.AboveSnowLine;
                    petalTriangles += tints.Petal;

                    mesh = HorizonAssetUtility.ReplaceAsset(mesh, $"{GeneratedFolder}/{name}.asset");

                    tileObject = CreateMeshObject(
                        terrainRoot.transform, name, mesh, new[] { materials.TerrainTint },
                        surfaces: TerrainSurfaces);
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

            // Snow, reported outside the water block because it has nothing to do with water — and
            // reported at all for a better reason than symmetry with the shoreline: it is decided by an
            // elevation against a region's own line, so it is exactly the kind of thing that comes out
            // as nothing, or as the whole mountain, without the build saying a word.
            if (snowTriangles > 0)
            {
                // The share is the number that means something. The absolute count says a winter region
                // exists; this says whether the slope test left it a snowfield or a quarry, and it is
                // one threshold away from doing the second. CLAUDE.md carried a claim that it had for
                // some time, on no measurement at all.
                float held = snowTriangles * 100f / Mathf.Max(1, aboveSnowLine);

                Debug.Log($"[Horizon] Snow line: {snowTriangles} terrain triangles tinted snow, "
                          + $"{snowTriangles * 100f / Mathf.Max(1, totalTriangles):0.0} % of the terrain "
                          + $"— {held:0} % of the {aboveSnowLine} above a line, the rest too steep to "
                          + "hold any. Same material, same draw call as the rock under it.");

                if (held < 40f)
                {
                    Debug.LogWarning($"[Horizon] Snow line: only {held:0} % of the ground above a snow "
                                     + "line came out snow, so the winter regions read as rock with "
                                     + "drifts rather than as snowfields with rock through them. The "
                                     + "slope test is doing that, not the line — see "
                                     + "TerrainShape.RockSlopeThreshold against DetailAmplitude.");
                }
            }
            else
            {
                Debug.LogWarning(
                    "[Horizon] Nothing came out above a snow line. Either no region carries one, or the "
                    + "mountain never reaches it, or every face up there is steep enough to count as "
                    + "rock — see TerrainTileBuilder.SnowTint. A winter region with no snow in it builds "
                    + "and validates exactly like one that works.");
            }

            if (petalTriangles > 0)
            {
                Debug.Log($"[Horizon] Blossom drift: {petalTriangles} terrain triangles tinted petal in "
                          + "the Bahçe — same material, same draw call as the meadow beside it.");
            }
            else
            {
                Debug.LogWarning(
                    "[Horizon] No ground came out tinted blossom. Either LandRegion.Bahce is not in the "
                    + "regions array, or it is shadowed there by one listed before it, or its parcel "
                    + "palette lost its fourth entry — see LandRegion.BahcePetal.");
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
            RoadShape countryShape,
            IReadOnlyList<TrafficNetworkBuilder.OnwardRoad> onward)
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
                country, countryShape, onward);
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

            // Which slot the tail lamps ended up in on each body, -1 where a body folded them away.
            // Looked up rather than assumed for the reason the headlight index is, one loop down.
            var taillightSlots = new int[TrafficPoolSize];

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
                taillightSlots[i] = taillight;

                if (headlight >= 0 || taillight >= 0)
                {
                    litRenderers.Add(renderer);

                    if (headlight >= 0)
                    {
                        litSlots.Add(headlight);
                        litSlotGroups.Add((int)LitGroup.Headlights);
                    }

                    // The tail lamps are deliberately *not* registered any more. TownLights swaps a
                    // whole group at once, which is exactly right for a thing that only knows about
                    // dusk and exactly wrong for one that also has to know about the brake pedal — and
                    // two writers on one material slot is the failure this project keeps naming. The
                    // director owns them now, day, night and braking, because it is the only thing that
                    // knows all three.
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

                // The tail lamps, which the director owns rather than TownLights — see UpdateTailLamps
                // for why a group swap cannot also answer a brake pedal.
                SerializedProperty slotArray = serialized.FindProperty("taillightSlots");
                slotArray.arraySize = taillightSlots.Length;
                for (int slot = 0; slot < taillightSlots.Length; slot++)
                {
                    slotArray.GetArrayElementAtIndex(slot).intValue = taillightSlots[slot];
                }

                serialized.FindProperty("taillightDay").objectReferenceValue = materials.WindowDay;
                serialized.FindProperty("taillightNight").objectReferenceValue = materials.TailNight;
                serialized.FindProperty("taillightBraking").objectReferenceValue = materials.TailBrake;

                // From the mesh builder, so reshaping the body cannot leave the traffic riding at a
                // height nothing else believes in.
                serialized.FindProperty("rideHeight").floatValue = CarMeshBuilder.TrafficRideHeight;
            });

            HorizonAssetUtility.AssertReferenceAssigned(director, "network");
            HorizonAssetUtility.AssertReferenceAssigned(director, "taillightBraking");

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
            RoadPath ebental, RoadPath stadtfeld, RoadPath kalkgrat, RoadPath meerenge,
            RoadPath yalikoy, RoadPath weissjoch, RoadPath ring, RoadPath bahce)
        {
            var stations = new List<Vector3>(24);

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

            if (weissjoch != null)
            {
                // The densest road geometry in the world: twenty-eight hairpins inside 1.7 km, with the
                // verge furniture of all of them. Sampled low, at the tree line and at the col, because
                // the three carry completely different loads — forest, bare rock and snow — and a budget
                // taken at one of them says nothing about the other two.
                stations.Add(weissjoch.GetPositionAtDistance(weissjoch.Length * 0.15f));
                stations.Add(weissjoch.GetPositionAtDistance(weissjoch.Length * 0.55f));
                stations.Add(weissjoch.GetPositionAtDistance(weissjoch.Length));
            }

            if (ring != null)
            {
                // The circuit, at the paddock and at the bottom of it. The paddock is the one place on
                // the mountain with buildings on it; the Kesselgrund is the only stretch of the lap
                // under the tree line, and therefore the only one carrying a forest as well as two
                // carriageways' worth of rails and kerbs. A budget taken at one says nothing about the
                // other.
                stations.Add(ring.GetPositionAtDistance(0f));
                stations.Add(ring.GetPositionAtDistance(ring.Length * 0.5f));
            }

            if (bahce != null)
            {
                // The other circuit, at its paddock and out on the lap. Same argument, different loads:
                // the paddock carries the buildings, and the far side of the lap carries an orchard
                // valley at a density no other flat country here is built at.
                stations.Add(bahce.GetPositionAtDistance(0f));
                stations.Add(bahce.GetPositionAtDistance(bahce.Length * 0.5f));
            }

            if (stadtfeld != null)
            {
                // The fork, and the city edge the other end of the road. The fork is the one place in
                // the world where two courses' verges, two sets of vegetation and a laid-on throat are
                // all in one frame; the city edge is where open country meets Hochstadt's perimeter
                // blocks, and a budget measured on either side of that line misses it.
                stations.Add(stadtfeld.GetPositionAtDistance(stadtfeld.Length));
                stations.Add(stadtfeld.GetPositionAtDistance(stadtfeld.Length * 0.05f));
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

            if (yalikoy != null)
            {
                // On the seafront at the harbour, and up on the hairpins behind the village where the
                // whole of it is in frame at once — which is the frame this leg is built for and
                // therefore the one that has to be counted.
                stations.Add(yalikoy.GetPositionAtDistance(
                    Mathf.Min(YalikoyCourse.BasinAlong, yalikoy.Length)));
                stations.Add(yalikoy.GetPositionAtDistance(
                    Mathf.Min(YalikoyCourse.CityEnd + 500f, yalikoy.Length)));
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
                    float apart = NearestApproach(a.Path, b.Path);
                    if (apart < clearance)
                    {
                        crossings++;

                        // The numbers, not only the pair. "Streets 9 and 27" sends you counting
                        // AddStreet calls down the layout table; "17.0 m apart against 17.2 m of
                        // paving" says what to change and by how much — which is the same argument
                        // ValidateRoadClearance makes for printing a world position beside a distance.
                        worstCrossing ??= $"{i} ({a.Kind}) and {j} ({b.Kind}), {apart:0.0} m apart "
                                          + $"against {clearance:0.0} m of paving between their "
                                          + "centrelines";
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
            // road's DriverBoxHalfWidth: that box is over a fifth of a 7.8 m alley, and a check that
            // fires on every kerb is a check nobody reads.
            int blockedStreets = 0;
            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                float halfWidth = Mathf.Min(DriverBoxHalfWidth, edge.HalfWidth - 0.6f);

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

            // The wind, counted, because it is the one thing here a picture genuinely cannot check: a
            // still frame of a swaying wood and a still frame of a dead one are the same photograph.
            // Same rule as the snow line — a world whose mask never got written builds and validates
            // exactly like one that works.
            if (stats.SwayingVertices == 0)
            {
                Debug.LogWarning("[Horizon] Wind: not one vertex carries a sway mask, so nothing in this "
                               + "world will move. Either ApplySway is not being called or MergeTinted is "
                               + "flattening the alpha channel again.");
            }
            else
            {
                Debug.Log($"[Horizon] Wind: {stats.SwayingVertices} vertices carry a sway mask.");
            }

            // The region's own, counted apart. An avenue that failed to plant is invisible in a total —
            // five hundred trees against a hundred thousand is a rounding error — and it is the one
            // thing in the world whose absence nothing else would report.
            Debug.Log($"[Horizon] Ebental: {stats.Poplars} avenue poplars, {stats.FruitTrees} fruit trees, "
                      + $"{stats.WallRuns} field boundaries, {stats.HayBales} bales.");

            // Anadolu's, on their own line for the reason the Ebental has one: a region's own planting is
            // a rounding error against the world's total, so its absence would not show there.
            Debug.Log($"[Horizon] Anadolu: {stats.Cypresses} cypresses.");

            // And the Bahçe's, for the same reason again — this one more sharply than either, because
            // a region whose whole character is that it is in flower reads as any other valley if the
            // number is nought, and nothing else in the build would say so.
            if (stats.CherryTrees == 0)
            {
                Debug.LogWarning("[Horizon] Bahçe: no cherry trees anywhere. The region is bound to a "
                                 + "path, carries a BlossomChance and is listed in the regions array, "
                                 + "or it is not — see LandRegion.Bahce. A valley meant to be in "
                                 + "blossom builds and validates exactly like one that works.");
            }
            else
            {
                Debug.Log($"[Horizon] Bahçe: {stats.CherryTrees} cherry trees.");
            }

            // Flowers, warned at nought for the reason the cherries are. They ride on the grass scatter
            // rather than on one of their own, so a share that never fires takes nothing away and moves
            // no other number in this log — the tufts simply stay tufts, and the verge looks like every
            // other verge in the world.
            if (stats.Flowers == 0)
            {
                Debug.LogWarning("[Horizon] Verges: no flowers anywhere. Some region has to carry a "
                                 + "FlowerChance and be listed in the regions array — see "
                                 + "LandRegion.FlowerChance. Nothing else in this build reports it, "
                                 + "because the grass they replace is counted on its own line.");
            }
            else
            {
                Debug.Log($"[Horizon] Verges: {stats.Flowers} wildflowers among "
                          + $"{stats.Tufts} grass tufts.");
            }

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
                                 + $"over the {vegetationShape.MaxTrianglesPerTile} budget. The knob is "
                                 + "LandRegion.TreeDensity on whichever region owns that tile, not "
                                 + "FarDensity: a switchback stack has no ground far from a "
                                 + "carriageway, so there is nothing there for the falloff to thin. See "
                                 + "VegetationShape.MeasuredWorldTriangles.");
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
        /// <see cref="DriverBoxHalfWidth"/> box that is right for a 13.2 m trunk carriageway is over a
        /// fifth of a 7.8 m alley, and a check that fires on every kerb is a check that gets
        /// ignored.</para>
        /// </summary>
        /// <summary>
        /// Half-width of the box the corridor sweeps with, metres.
        ///
        /// <para><b>It is the car, not the road.</b> 1.3 was right while the widest collider was 2.26 m
        /// across; the cars grew a quarter in plan in 5bd7396 and the widest is 2.92 m now, so a 1.3 m
        /// box sweeps a corridor narrower than the thing meant to drive down it — and a check that
        /// cannot reach its subject finds nothing wrong and is indistinguishable from a clean pass.
        /// 1.5 covers the widest body and the 3.00 m across the offroader's tyres.</para>
        /// </summary>
        private const float DriverBoxHalfWidth = 1.5f;

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
        /// Whether anything a fork built stands on the carriageway it joins, and whether the two
        /// surfaces meet flush where they touch.
        ///
        /// <para><b>This is the check the world did not have.</b> <c>ValidateMergeSeam</c> asks it of the
        /// one motorway on-ramp; nothing asked it of the three forks, and all three were wrong the same
        /// way. A branch's course ends on the centreline of the road it joins, so its ribbon laid its
        /// last cross-section across that road and the throat was laid from the centreline outward —
        /// 5.2 m of pit road onto a 6.5 m racing surface at the Weissjochring, ending in a square edge on
        /// the fastest part of the lap. It built, it validated, and it was reported from the car.</para>
        ///
        /// <para><b>Measured off the meshes rather than re-derived.</b> Every other way of asking this
        /// re-runs the builder's own arithmetic and therefore agrees with the builder right up until one
        /// of them is wrong — the rule this project already states about supports and infields. The
        /// vertices are what got built, so they are what is asked.</para>
        /// </summary>
        private static void ValidateForkSeam(
            IRoadPath trunk,
            in RoadShape trunkShape,
            float atDistance,
            IRoadPath branch,
            float branchAt,
            float branchSign,
            Mesh forkMesh,
            Mesh branchMesh,
            string what)
        {
            if (trunk == null || branch == null)
            {
                return;
            }

            float trunkAt = Mathf.Clamp(atDistance, 0f, trunk.Length);

            Vector3 trunkCentre = trunk.GetPositionAtDistance(trunkAt);
            Vector3 trunkRight = trunk.GetBankedRightAtDistance(
                trunkAt, trunkShape.MaxBankDegrees, trunkShape.FullBankRadius);

            Vector3 trunkUp = Vector3.Cross(trunk.GetDirectionAtDistance(trunkAt), trunkRight).normalized;
            if (trunkUp.y < 0f)
            {
                trunkUp = -trunkUp;
            }

            Vector3 trunkSurface = trunkCentre + trunkUp * trunkShape.SurfaceLift;
            Vector3 trunkForward = trunk.GetDirectionAtDistance(trunkAt);

            // Which hand the branch leaves on, measured over its whole throat for the reason
            // TrunkForkBuilder records: at the mouth the two centrelines are the same line.
            Vector3 mouth = branch.GetPositionAtDistance(Mathf.Clamp(branchAt, 0f, branch.Length));
            Vector3 away = branch.GetPositionAtDistance(
                Mathf.Clamp(branchAt + branchSign * 70f, 0f, branch.Length));

            float side = Vector3.Dot(away - mouth, trunkRight) >= 0f ? 1f : -1f;

            // Only vertices near the fork. Both meshes are kilometres long and the question is local.
            const float reach = 140f;

            float worstAcross = float.MaxValue;
            Vector3 worstAt = Vector3.zero;
            string worstIn = null;

            float worstStep = 0f;
            Vector3 worstStepAt = Vector3.zero;

            for (int m = 0; m < 2; m++)
            {
                Mesh mesh = m == 0 ? forkMesh : branchMesh;
                if (mesh == null)
                {
                    continue;
                }

                string which = m == 0 ? "the throat" : "the branch's own ribbon";
                Vector3[] vertices = mesh.vertices;

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 offset = vertices[i] - trunkSurface;

                    if (Plan(offset).sqrMagnitude > reach * reach)
                    {
                        continue;
                    }

                    float across = Vector3.Dot(offset, trunkRight) * side;

                    if (across < worstAcross)
                    {
                        worstAcross = across;
                        worstAt = vertices[i];
                        worstIn = which;
                    }

                    // The seam itself, and only the throat's half of it: the branch's ribbon is trimmed
                    // to clear the carriageway by MouthOverlap, so its shoulder vertices land in this
                    // band too and they are a quarter of a metre lower by design. Measuring those was
                    // the first version of this check reporting 62 cm of step on a fork that had none.
                    if (m != 0 || Mathf.Abs(across - trunkShape.HalfWidth) > 0.35f)
                    {
                        continue;
                    }

                    // Against the trunk's <b>ribbon</b>, sampled where this vertex actually stands on it,
                    // rather than against the flat plane the throat was built from. Comparing a surface
                    // with the thing it was derived from is a check that agrees with the builder until
                    // one of them is wrong; the road as laid is the independent answer. The along is a
                    // projection because a fork stands on straight track — AddJunction requires it, and
                    // AppendFillets already leans on the same fact.
                    float along = trunkAt + Vector3.Dot(offset, trunkForward);
                    float clamped = Mathf.Clamp(along, 0f, trunk.Length);

                    Vector3 onTrunk = trunk.GetPositionAtDistance(clamped);
                    Vector3 edgeRight = trunk.GetBankedRightAtDistance(
                        clamped, trunkShape.MaxBankDegrees, trunkShape.FullBankRadius);

                    Vector3 edgeUp = Vector3.Cross(
                        trunk.GetDirectionAtDistance(clamped), edgeRight).normalized;

                    if (edgeUp.y < 0f)
                    {
                        edgeUp = -edgeUp;
                    }

                    // The camber is exactly zero at the paved edge — RoadMeshBuilder.AppendRing puts it
                    // there — which is the whole reason the throat is clipped to this line and not to
                    // any other.
                    Vector3 paved = onTrunk
                                    + edgeRight * (side * trunkShape.HalfWidth)
                                    + edgeUp * trunkShape.SurfaceLift;

                    float step = Mathf.Abs(vertices[i].y - paved.y);

                    if (step > worstStep)
                    {
                        worstStep = step;
                        worstStepAt = vertices[i];
                    }
                }
            }

            if (worstIn == null)
            {
                Debug.LogWarning($"[Horizon] Fork seam ({what}): no geometry within {reach:0} m of the "
                                 + "junction. That is not a clean fork, it is no answer.");
                return;
            }

            // A tenth of the tyre's radius, the same figure ValidateMergeSeam uses and for the same
            // reason: below it a raycast wheel rides over the step, above it the suspension takes the
            // whole thing in one physics tick.
            const float tolerable = 0.04f;

            bool onTheRoad = worstAcross < trunkShape.HalfWidth - 0.05f;

            if (onTheRoad)
            {
                Debug.LogWarning(
                    $"[Horizon] Fork seam ({what}): {worstIn} reaches {trunkShape.HalfWidth - worstAcross:0.00} m "
                    + $"inside the carriageway it joins — {worstAcross:0.00} m from its centreline against a "
                    + $"{trunkShape.HalfWidth:0.00} m half-width, at ({worstAt.x:0}, {worstAt.y:0}, {worstAt.z:0}). "
                    + "A branch's ribbon is trimmed and the throat is clipped so that neither of them does.");
            }

            if (worstStep > tolerable)
            {
                Debug.LogWarning(
                    $"[Horizon] Fork seam ({what}): the fork meets the carriageway with a "
                    + $"{worstStep * 100f:0} cm step at ({worstStepAt.x:0}, {worstStepAt.y:0}, "
                    + $"{worstStepAt.z:0}). A wheel crosses that at the speed of the road it is leaving.");
            }

            // Always printed, warning or not. Two numbers that only ever appear when something is wrong
            // are two numbers nobody has a feel for, and the first thing anybody asks of a warning here
            // is what the clean value used to be.
            Debug.Log($"[Horizon] Fork seam ({what}): nothing the fork built comes nearer than "
                      + $"{worstAcross:0.00} m to the centreline of a {trunkShape.HalfWidth:0.00} m "
                      + $"half-width, and the throat meets the paved edge within {worstStep * 1000f:0} mm.");
        }

        /// <summary>
        /// Walks the carriageway and reports anywhere the ground has fallen away from under it.
        ///
        /// <para><b>The missing sign.</b> <see cref="ValidateRoadClearance"/> measures terrain standing
        /// <i>above</i> the asphalt and nothing measured the other direction, which is a gap rather than
        /// an oversight: <c>MountainField</c> averages road samples, so wherever two roads at different
        /// heights come within reach of each other the lower one gets ground on its carriageway — which
        /// is reported — and the higher one loses the ground under it, which was not. Every breach that
        /// check has ever printed had a silent twin.</para>
        ///
        /// <para>Measured at the <b>shoulder's outer edge</b>, because that is where a road first shows
        /// daylight: the section falls <c>ShoulderDrop</c> from the centreline to there and the shelf is
        /// only <c>RoadShelfDrop</c> below the centreline, so on correct ground the shelf stands slightly
        /// <i>above</i> that edge and the number reported here is negative.</para>
        /// </summary>
        private static void ValidateRoadSupport(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string what = "Road")
        {
            const float step = 2f;

            // The section itself can only explain ShoulderDrop, and the deepest in this world is the
            // motorway's 0.7. A metre is more than twice anything a correctly seated road can produce,
            // and well short of GuardRailBuilder's 3 m, which is the drop at which a rail goes up —
            // a road with a rail beside it is on a shelf, not in the air.
            const float tolerable = 1f;

            float length = path.Length;

            int breaches = 0;
            float worst = 0f;
            float worstAt = 0f;
            float worstAcross = 0f;

            // Where along the road the breaches are, collapsed into stretches. One number of sampled
            // points cannot tell a single hole beside one hairpin from a road that is in the air for
            // half a kilometre, and those two are not the same fault. A stretch is a run of breaches
            // with no more than fifty metres of sound road inside it.
            var stretches = new List<Vector2>(8);
            float lastBreachAt = float.NegativeInfinity;

            for (float distance = 0f; distance <= length; distance += step)
            {
                // Under a bore the ground is meant to be overhead; over a span it is meant to be a long
                // way down, and the deck is held up by piers that ValidateBridgeSupport counts.
                //
                // Both with a margin, and the margin is MountainField's own rather than a taste. A deck
                // does not find a gap, it carves one, and that carve eases back out to nothing over
                // BridgeCorridor — so for forty-six metres past an abutment the ground is legitimately
                // still on its way up to meet the road. Measured without it, this check reported 128
                // points on each motorway carriageway and 68 on the Kalkgrat, every one of them an
                // abutment doing exactly what it is drawn to do. A tunnel mouth is the same argument:
                // the ground there has been cut away for the slot.
                if (course != null
                    && (course.IsCoveredOrNear(distance, MountainField.BridgeCorridor)
                        || course.IsBridged(distance, MountainField.BridgeCorridor)))
                {
                    continue;
                }

                Vector3 centre = path.GetPositionAtDistance(distance);
                Vector3 right = path.GetBankedRightAtDistance(
                    distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 point = centre + right * (roadShape.OuterHalfWidth * sign);

                    float edge = point.y + roadShape.SurfaceLift - roadShape.ShoulderDrop;
                    float ground = field.HeightAt(point.x, point.z);

                    float gap = edge - ground;
                    if (gap <= tolerable)
                    {
                        continue;
                    }

                    breaches++;

                    if (distance - lastBreachAt > 50f)
                    {
                        stretches.Add(new Vector2(distance, distance));
                    }
                    else
                    {
                        Vector2 open = stretches[stretches.Count - 1];
                        stretches[stretches.Count - 1] = new Vector2(open.x, distance);
                    }

                    lastBreachAt = distance;

                    if (gap > worst)
                    {
                        worst = gap;
                        worstAt = distance;
                        worstAcross = roadShape.OuterHalfWidth * sign;
                    }
                }
            }

            if (breaches == 0)
            {
                Debug.Log($"[Horizon] {what} support: the ground reaches the shoulder everywhere.");
                return;
            }

            Vector3 worstPoint = path.GetPositionAtDistance(worstAt);

            var where = new System.Text.StringBuilder();
            for (int i = 0; i < stretches.Count && i < 6; i++)
            {
                where.Append(i == 0 ? "" : ", ");
                where.Append(Mathf.Approximately(stretches[i].x, stretches[i].y)
                    ? $"{stretches[i].x:0}"
                    : $"{stretches[i].x:0}–{stretches[i].y:0}");
            }

            if (stretches.Count > 6)
            {
                where.Append($" and {stretches.Count - 6} more");
            }

            Debug.LogWarning(
                $"[Horizon] {what} support: the ground stands more than {tolerable:0.0} m below the "
                + $"shoulder at {breaches} sampled points in {stretches.Count} stretch(es) — at "
                + $"{where} m along. Worst is {worst:0.00} m at {worstAt:0} m along "
                + $"the course, {worstAcross:0.0} m across — at ({worstPoint.x:0}, {worstPoint.y:0}, "
                + $"{worstPoint.z:0}). That is a road standing on a plinth, or in the air. The cause is "
                + "very often a second road near it and higher, which MountainField has averaged against.");
        }

        /// <summary>
        /// Whether a town can actually be driven into from the road that arrives at it.
        ///
        /// <para><b>What <c>CountUnreachable</c> cannot answer.</b> That walk is seeded with the town's
        /// gateway node as a given — its own remarks say so — so it reports a city as reachable because
        /// it assumed it. Hochstadt's boulevard begins on the motorway's <i>median line</i> with a
        /// carriageway ten and a half metres to each side of it and the median barrier, solid and
        /// unbroken, standing between them. The graph was perfect and there was no way in.</para>
        ///
        /// <para>Two numbers and a sweep: how far the town's nearest paving is from the arriving road's
        /// nearest paving, how far apart they are in height, and whether anything solid stands on the
        /// line between them. The sweep borrows <see cref="ValidateDriveableCorridor"/>'s canary for the
        /// same reason it has one — an overlap query that finds nothing may mean a clear road or a
        /// missing collider, and those look identical in a log.</para>
        /// </summary>
        private static void ValidateTownEntry(
            StreetNetwork network,
            IRoadPath arriving,
            in RoadShape arrivingShape,
            string what)
        {
            if (network == null || arriving == null || network.Nodes.Count == 0)
            {
                return;
            }

            StreetNode nearest = null;
            float planGap = float.MaxValue;
            Vector3 onRoad = Vector3.zero;

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                StreetNode node = network.Nodes[i];

                // Each node against its own nearest point on the arriving road, not against one chosen
                // distance along it: a town whose gate is two hundred metres past the end of the
                // carriageway is at the same distance from it as one that is beside it, if the
                // measurement is taken from a fixed station.
                float along = NearestDistanceOn(arriving, node.Position);

                Vector3 centre = arriving.GetPositionAtDistance(along);
                Vector3 right = arriving.GetRightAtDistance(along);

                float across = Vector3.Dot(node.Position - centre, right);

                Vector3 edge = centre + right * Mathf.Clamp(
                    across, -arrivingShape.HalfWidth, arrivingShape.HalfWidth);

                float gap = Plan(node.Position - edge).magnitude;

                if (gap < planGap)
                {
                    planGap = gap;
                    onRoad = edge;
                    nearest = node;
                }
            }

            if (nearest == null || planGap > 400f)
            {
                Debug.LogWarning($"[Horizon] Town entry ({what}): no street node within 400 m of the "
                                 + "paving of the road that serves this town. Nothing here connects to "
                                 + "anything.");
                return;
            }

            float rise = Mathf.Abs(nearest.Position.y - onRoad.y);

            int blocked = CountBlockedBetween(onRoad, nearest.Position, out string blocker);

            // A car is 1.8 m wide and the paving either side of a mouth is a metre or two of verge, so a
            // gap of a few metres is a kerb line and a gap of ten is a median.
            const float reachable = 4f;

            if (planGap <= reachable && blocked == 0 && rise < 1f)
            {
                Debug.Log($"[Horizon] Town entry ({what}): '{nearest.Name ?? "unnamed"}' stands "
                          + $"{planGap:0.0} m from the paving of the road that arrives, {rise:0.00} m "
                          + "apart in height, with nothing solid between them.");
                return;
            }

            Debug.LogWarning(
                $"[Horizon] Town entry ({what}): the nearest street node "
                + $"('{nearest.Name ?? "unnamed"}') is {planGap:0.0} m from the paving of the road that "
                + $"arrives and {rise:0.00} m apart from it in height"
                + (blocked > 0
                    ? $", and something solid stands between them at {blocked} of the sampled points, "
                      + $"first against '{blocker}'."
                    : ".")
                + " A graph that says the town is reachable is not the same question as whether a car can "
                + "get there.");
        }

        /// <summary>
        /// Sweeps a car-sized box along a line and counts the samples something solid stands in. Shares
        /// <see cref="ValidateDriveableCorridor"/>'s exemption for parked ambient traffic and its canary.
        /// </summary>
        private static int CountBlockedBetween(Vector3 from, Vector3 to, out string blocker)
        {
            blocker = null;

            Vector3 span = to - from;
            span.y = 0f;

            float length = span.magnitude;
            if (length < 0.5f)
            {
                return 0;
            }

            Physics.SyncTransforms();

            var hits = new Collider[8];
            var halfExtents = new Vector3(0.9f, 1f, 1f);
            var rotation = Quaternion.LookRotation(span / length, Vector3.up);

            int blocked = 0;
            const float step = 1f;

            for (float travelled = 0f; travelled <= length; travelled += step)
            {
                Vector3 at = Vector3.Lerp(from, to, travelled / length) + Vector3.up * 1.35f;

                int count = Physics.OverlapBoxNonAlloc(
                    at, halfExtents, hits, rotation, ~0, QueryTriggerInteraction.Ignore);

                for (int i = 0; i < count; i++)
                {
                    if (hits[i] == null || IsTraffic(hits[i]))
                    {
                        continue;
                    }

                    blocked++;
                    blocker ??= hits[i].gameObject.name;
                    break;
                }
            }

            return blocked;
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

        /// <summary>
        /// Measures the longest stretch of deck with nothing under it, and says so.
        ///
        /// <para><b>The question <see cref="ValidateBridges"/> cannot ask.</b> That one measures the air
        /// between the deck and the ground, and a deck standing on nothing at all over a nine-metre hole
        /// is the best score it can give. Both halves of a bridge are needed and they are built by
        /// different code: <c>IsBridged</c> takes the ground away for either kind of span, and only
        /// <c>BridgeBuilder</c> and <c>SuspensionBridgeBuilder</c> put anything back. The suspension
        /// crossing's two side spans were a hundred and fifty metres of carriageway each, over carved-out
        /// air, held up by nothing whatever — and the build reported a clean world.</para>
        ///
        /// <para>Walks the list the builders filled rather than working out for itself where a pier
        /// belongs. A checker with its own opinion about pier spacing agrees with the builder right up
        /// until one of them is wrong, and then reports the wrong one as correct.</para>
        /// </summary>
        private static void ValidateBridgeSupport(
            RoadCourse course,
            List<float> supports,
            string what)
        {
            if (course == null)
            {
                return;
            }

            // A bay longer than this reads as a deck floating: it is a pier spacing and a half, which is
            // the most any span here is ever meant to cross without something under or over it.
            const float LongestBay = 60f;

            supports.Sort();

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Bridge
                    && feature.Kind != RoadFeatureKind.Suspension)
                {
                    continue;
                }

                float previous = feature.StartDistance;
                float worst = 0f;
                float worstAt = feature.StartDistance;
                int count = 0;

                for (int j = 0; j < supports.Count; j++)
                {
                    float at = supports[j];
                    if (at < feature.StartDistance - 0.5f || at > feature.EndDistance + 0.5f)
                    {
                        continue;
                    }

                    count++;
                    float bay = at - previous;
                    if (bay > worst)
                    {
                        worst = bay;
                        worstAt = previous;
                    }

                    previous = at;
                }

                float last = feature.EndDistance - previous;
                if (last > worst)
                {
                    worst = last;
                    worstAt = previous;
                }

                Debug.Log($"[Horizon] Bridge support '{feature.Name}': {count} support(s) over "
                          + $"{feature.Length:0} m, longest unsupported bay {worst:0} m.");

                if (worst > LongestBay)
                {
                    Debug.LogWarning(
                        $"[Horizon] Bridge '{feature.Name}' has {worst:0} m of deck with nothing under "
                        + $"or over it, starting {worstAt:0} m along {what}. MountainField has already "
                        + "taken the ground away — a span is only a span because of that — so this is "
                        + "carriageway hanging in mid-air. Either the builder skipped its piers (too "
                        + "short a span, or feet in deep water) or nothing is hanging the deck there.");
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

            // The world position as well as the distance along, because the two answer different
            // questions. A distance says where on this road to look; a position is what lets the cause
            // be found, and the cause is very often another road. The Weissjochring's own worst
            // breach — 183 m of terrain standing on the carriageway — was a second road passing eighty
            // metres away and a hundred and eighty metres higher, and the distance-along alone said
            // nothing about that at all.
            Vector3 worstPoint = path.GetPositionAtDistance(worstAt);

            Debug.LogWarning(
                $"[Horizon] {what} clearance: terrain stands above the asphalt at {breaches} sampled points. "
                + $"Worst is {worst:0.00} m at {worstAt:0} m along the course, {worstAcross:0.0} m across "
                + $"from the centreline — at ({worstPoint.x:0}, {worstPoint.y:0}, {worstPoint.z:0}).");
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

            // Solid, but not against the mesh above. A collider taken from the rail as drawn is a row of
            // re-entrant corners every four metres and the car catches on each of them — which is what
            // the old "the rails are visual" decision was really objecting to. GuardRailBuilder walks the
            // same plan a second time and returns a smooth wall along it, so the car is held and slides
            // off. Until this, nothing at the edge of any road in the world was solid.
            Mesh collision = GuardRailBuilder.BuildCollision(
                path, roadShape, field, course, $"GuardRail{label}CollisionMesh");

            if (collision != null)
            {
                collision = HorizonAssetUtility.ReplaceAsset(
                    collision, $"{GeneratedFolder}/GuardRail{label}CollisionMesh.asset");
            }

            CreateMeshObject(parent, "GuardRails" + label, mesh, new[] { materials.GuardRail },
                addCollider: collision != null, markStatic: true, collisionMesh: collision);

            int collisionTriangles = collision == null ? 0 : collision.triangles.Length / 3;
            Debug.Log($"[Horizon] Guard rails on {Where(label)}: {triangles} triangles, "
                      + $"{collisionTriangles} of collision.");
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
        /// <param name="roads">
        /// One entry per carriageway. <c>Closed</c> says the road is a circuit, and it changes the gap
        /// arithmetic rather than switching the check off: on an open road the two ends count, because
        /// the start of a road is somewhere a driver can be, while on a loop there are no ends and the
        /// gap between the last pump and the first one wraps past the line.
        /// </param>
        private static void ValidateFuelStations(
            params (IRoadPath Path, RoadCourse Course, RoadShape Shape, string Where, float Side,
                bool Closed)[] roads)
        {
            int counted = 0;
            float worstGap = 0f;
            string worstGapOn = string.Empty;

            for (int r = 0; r < roads.Length; r++)
            {
                (IRoadPath road, RoadCourse course, RoadShape shape, string where, float side,
                    bool closed) = roads[r];

                if (road == null || course == null)
                {
                    continue;
                }

                // Walked in course order so the gaps below are gaps along the road, and the two ends
                // count: what matters is the longest a driver can go without passing a pump, and the
                // start of a road is somewhere a driver can be.
                float previous = 0f;
                bool any = false;
                float firstAt = 0f;

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

                    float at = Mathf.Clamp(feature.StartDistance, 0f, road.Length);

                    if (!any)
                    {
                        firstAt = at;

                        // On a loop the run-up to the first pump is not a gap: it is the far side of
                        // the wrap, and it is counted once at the bottom of the walk instead.
                        if (closed)
                        {
                            previous = at;
                        }
                    }

                    any = true;

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

                // Past the last pump: on an open road, to the end of it; on a loop, round to the first
                // pump again.
                float tail = road.Length - previous + (closed ? firstAt : 0f);
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

            // No collider, and now the only roadside furniture without one: a delineator is a marker
            // rather than a barrier, it stands where there is nothing to fall off, and a car that
            // clipped one should carry on rather than be stopped by a plastic post.
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
            PrototypeMaterials materials,
            float endClearance)
        {
            Mesh mesh = GuardRailBuilder.BuildMedian(centre, roadShape, course, endClearance);
            if (mesh == null)
            {
                return;
            }

            int triangles = mesh.triangles.Length / 3;
            mesh = HorizonAssetUtility.ReplaceAsset(mesh, GeneratedFolder + "/MedianBarrierMesh.asset");

            // The longest continuous barrier in the world, so the one whose collision budget matters:
            // the wall takes a cross-section every 24 m against the posts' 12, which is why this is a
            // few thousand triangles of physics rather than a few tens of thousands.
            Mesh collision = GuardRailBuilder.BuildMedianCollision(centre, roadShape, course, endClearance);

            if (collision != null)
            {
                collision = HorizonAssetUtility.ReplaceAsset(
                    collision, GeneratedFolder + "/MedianBarrierCollisionMesh.asset");
            }

            CreateMeshObject(parent, "MedianBarrier", mesh, new[] { materials.GuardRail },
                addCollider: collision != null, markStatic: true, collisionMesh: collision);

            int collisionTriangles = collision == null ? 0 : collision.triangles.Length / 3;
            Debug.Log($"[Horizon] Median barrier: {triangles} triangles, "
                      + $"{collisionTriangles} of collision, stopping {endClearance:0} m short of each "
                      + "end of the motorway so the terminus paving is not fenced down the middle.");
        }

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
            var supports = new List<float>();
            Mesh mesh = SuspensionBridgeBuilder.Build(
                path, roadShape, field, course, shape, used, "Suspension" + label, supports);

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

            Mesh collision = SuspensionBridgeBuilder.BuildParapetCollision(
                path, roadShape, course, $"SuspensionParapetCollision{label}Mesh");

            if (collision != null)
            {
                collision = HorizonAssetUtility.ReplaceAsset(
                    collision, $"{GeneratedFolder}/Suspension{label}CollisionMesh.asset");
            }

            GameObject bridge = CreateMeshObject(parent, "SuspensionBridges" + label, mesh, slots,
                addCollider: collision != null, markStatic: true, collisionMesh: collision);

            WorldChunk chunk = bridge.AddComponent<WorldChunk>();
            chunk.RecalculateBounds();
            chunk.SetBounds(chunk.Center, 100000f);

            int collisionTriangles = collision == null ? 0 : collision.triangles.Length / 3;
            Debug.Log($"[Horizon] Suspension bridges on {Where(label)}: {triangles} triangles, "
                      + $"{used.Count} material slot(s), {collisionTriangles} of parapet collision, "
                      + "never streamed out.");

            ValidateBridgeSupport(course, supports, $"the crossing on {Where(label)}");
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
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            in SuspensionShape shape)
        {
            ValidateStructureClearsTheRoad(roadShape);

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

        /// <summary>
        /// The fifth question: does any of this structure stand in the road it is carrying?
        ///
        /// <para><b>The check that would have caught the entrance.</b> Every structural offset on the
        /// crossing used to be one number sized for a cable, and handed to bodies metres across: the
        /// tower foundations' inner faces landed exactly on the edge of the asphalt and the anchor
        /// blocks' landed two metres inside the lane, leaving 6.3 m of clear width on a 13.5 m road, as
        /// seven-metre concrete walls at the two ends. The build said nothing — every other check here
        /// looks up, down or along, and none of them looks across.</para>
        ///
        /// <para>Asked of the shape rather than of the mesh, because the shape is where the fault was:
        /// the answer is the same at every station of a structure whose cross-section does not
        /// change.</para>
        /// </summary>
        private static void ValidateStructureClearsTheRoad(in RoadShape roadShape)
        {
            // Half a metre outside the asphalt. Not the shoulder's edge: a shoulder is where a car ends
            // up when something has gone wrong, and that is exactly when it must not find a tower.
            float mustClear = roadShape.HalfWidth + 0.5f;

            SuspensionBridgeBuilder.InnerFaces(roadShape,
                out float foundation, out float shaft, out float anchor);

            float narrowest = Mathf.Min(foundation, Mathf.Min(shaft, anchor));

            Debug.Log($"[Horizon] Crossing clear width: {narrowest * 2f:0.0} m between the structure's "
                      + $"inner faces, against {roadShape.HalfWidth * 2f:0.0} m of carriageway and "
                      + $"{roadShape.OuterHalfWidth * 2f:0.0} m paved. Tower foundation {foundation:0.0} m, "
                      + $"shaft {shaft:0.0} m, anchor block {anchor:0.0} m from the centreline.");

            WarnIfInTheRoad("tower foundation", foundation, mustClear, roadShape);
            WarnIfInTheRoad("tower shaft", shaft, mustClear, roadShape);
            WarnIfInTheRoad("anchor block", anchor, mustClear, roadShape);
        }

        private static void WarnIfInTheRoad(
            string what,
            float innerFace,
            float mustClear,
            in RoadShape roadShape)
        {
            if (innerFace >= mustClear)
            {
                return;
            }

            Debug.LogWarning(
                $"[Horizon] The crossing's {what} reaches to {innerFace:0.00} m from the centreline, "
                + $"inside the {roadShape.HalfWidth:0.00} m edge of the carriageway. That is a driver "
                + "arriving at the bridge and finding the road narrower than the one they are on. The "
                + "structure's lateral axes come from SuspensionBridgeBuilder.DeckOverhang and "
                + "AnchorClearance; widening the deck is the fix, not moving the block.");
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

        /// <summary>
        /// Every viaduct on a course, as one mesh per carriageway.
        ///
        /// <para>The parapet gets a collider and the rest does not. A car that leaves the deck should
        /// hit something rather than fall through the world, but a concave collider wrapped round piers
        /// forty metres below is a large amount of geometry nothing can ever reach.</para>
        ///
        /// <para>This paragraph stood for some time over the wrong method, saying what the build ought
        /// to do while every bridge in the world was created with <c>addCollider: false</c>. Worth
        /// keeping the note: a doc comment is not a test, and this one read as a decision that had been
        /// taken.</para>
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
            var supports = new List<float>();
            Mesh mesh = BridgeBuilder.Build(
                path, roadShape, field, course, used, "Bridge" + label, supports);

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

            // The parapet is solid and the piers are not. A car that leaves the deck should hit
            // something rather than fall through the world; a concave collider wrapped round legs forty
            // metres below is a large amount of geometry nothing can ever reach — so the collision mesh
            // is the parapet line alone, as a smooth wall. Same split the tunnels use.
            Mesh collision = BridgeBuilder.BuildParapetCollision(
                path, roadShape, course, $"BridgeParapetCollision{label}Mesh");

            if (collision != null)
            {
                collision = HorizonAssetUtility.ReplaceAsset(
                    collision, $"{GeneratedFolder}/Bridge{label}CollisionMesh.asset");
            }

            CreateMeshObject(parent, "Bridges" + label, mesh, slots,
                addCollider: collision != null, markStatic: true, collisionMesh: collision);

            int collisionTriangles = collision == null ? 0 : collision.triangles.Length / 3;
            Debug.Log($"[Horizon] Bridges on {Where(label)}: {triangles} triangles, "
                      + $"{collisionTriangles} of parapet collision.");

            ValidateBridgeSupport(course, supports, $"the bridges on {Where(label)}");
        }

        private static string Where(string label)
        {
            return string.IsNullOrEmpty(label) ? "the pass" : label;
        }

        /// <summary>Asphalt, then gravel. The submesh order <c>RoadMeshBuilder</c> builds every ribbon in.</summary>
        private static readonly SurfaceKind[] CarriagewaySurfaces =
        {
            SurfaceKind.Asphalt,
            SurfaceKind.Shoulder,
        };

        /// <summary>Terrain. One kind, because a tile's grass, rock and snow are tints on one mesh.</summary>
        private static readonly SurfaceKind[] TerrainSurfaces = { SurfaceKind.Ground };

        /// <summary>How many objects came out of this build carrying a surface tag, and of which kind.</summary>
        private static int taggedCarriageways;

        private static int taggedGround;

        /// <summary>How many surface probes <see cref="ValidateSurfaces"/> cast, and how many missed.</summary>
        private static int surfaceProbes;

        private static int surfaceProbeMisses;

        /// <summary>Verge probes cast, and how many found terrain standing over the gravel.</summary>
        private static int surfaceVergeSamples;

        private static int surfaceVergeBuried;

        /// <summary>
        /// Marks a collider with what it drives like, so <c>VehicleController</c> can read it off its own
        /// wheel raycast rather than searching the road network every physics step.
        ///
        /// <para><b>Only where it is asked for, and that is deliberate.</b> Untagged geometry reads as
        /// asphalt (see <see cref="GroundSurface"/> for why that is the safe default), so a building, a
        /// guard rail or a tunnel wall needs no component — and this world has some thousands of those.
        /// Two things actually have to be said: that a terrain tile is not a road, and that the outer
        /// strips of a carriageway are gravel.</para>
        ///
        /// <para><b>The runs are measured off the collision mesh, never the rendered one.</b> The tunnels
        /// are built with the two deliberately different, and <see cref="RaycastHit.triangleIndex"/>
        /// indexes whatever the ray actually hit. Taking the counts from the visible mesh would put the
        /// asphalt/gravel boundary at a triangle number that means nothing in the mesh being asked.</para>
        /// </summary>
        /// <summary>
        /// Says what the world came out tagged as.
        ///
        /// <para><b>And warns when either count is nought, which is the whole reason it exists.</b>
        /// Untagged geometry drives like asphalt, so a build that tagged nothing at all is a build in
        /// which the car has full grip in a ploughed field and the verges are as fast as the road —
        /// which looks exactly like a build with no surfaces in it, and there is nothing on screen and
        /// nothing in the physics that would say so. It is the same argument the snow line makes one
        /// system over.</para>
        /// </summary>
        /// <summary>
        /// Casts a ray at the built world and asks what the car will be told it is standing on.
        ///
        /// <para><b>This is a picture's job done by measurement, and that is deliberate.</b> Every other
        /// feature in this project is checked by photographing it, because what goes wrong is visible
        /// and silent. A surface is the opposite: it is invisible and silent. A carriageway whose gravel
        /// run starts in the middle of the road looks exactly like one that does not, in every frame
        /// this project can take, day or night — and the only symptom is a car that is mysteriously
        /// slippery down the crown of the road. So it is asked rather than looked at.</para>
        ///
        /// <para><b>It asks the scene, not the data.</b> Three separate things have to line up before a
        /// wheel gets the right answer — the submesh order the mesh was built in, the triangle counts the
        /// tag was given, and the collider actually being the mesh that was measured — and only a real
        /// raycast tests all three at once. A check that recomputed the boundaries from the course would
        /// agree with the builder right up until one of them was wrong, which is the rule this project
        /// keeps having to relearn.</para>
        ///
        /// <para><b>The crown is an error and the verge is a measurement, and that asymmetry is the
        /// finding this check was written to make.</b> A carriageway that does not read asphalt is
        /// always wrong. A verge that does not read gravel very often is not: <c>ShoulderDrop</c> is
        /// 0.5 m and <c>TerrainShape.RoadShelfDrop</c> is 0.45, so the shoulder already hangs below the
        /// shelf on level ground, and the camber on the inside of a corner takes it a further
        /// <c>sin(bank)</c> down. The hillside stands over the outer half of the verge there — so a
        /// wheel running wide genuinely touches terrain, and reporting the terrain it touches is right.
        /// <c>RoadShape.ShoulderDrop</c> states this rule for the *asphalt* edge and stops there;
        /// nothing had ever measured what happens to the gravel behind it, because
        /// <c>ValidateRoadClearance</c> asks about the carriageway and <c>ValidateRoadSupport</c> asks
        /// whether the ground is too low rather than too high.</para>
        ///
        /// <para>So the verge is counted and reported, and only a <i>majority</i> failing is an error —
        /// that is the shape of a submesh boundary in the wrong place, where a scattering is the shape
        /// of banked corners.</para>
        ///
        /// <para>Off the road entirely is deliberately not probed: at 25 m from a carriageway the honest
        /// answer is sometimes a bridge deck, a forecourt, a town street or the next road over, and a
        /// check that called any of those a fault would be a check nobody reads.</para>
        /// </summary>
        private static void ValidateSurfaces(
            IRoadPath path, in RoadShape shape, RoadCourse course, string what)
        {
            // Coarse on purpose: this is asking whether the tagging is right in principle, and a
            // boundary that is wrong is wrong for kilometres. 37 m rather than a round number so the
            // samples do not land on the same phase of every ribbon's control points.
            const float step = 37f;

            // Half a sample's spacing past whatever the junction itself claims, so the one probe that
            // lands just outside a terminus — where the paving has already begun converging away from
            // this carriageway's centreline — is not read as a hole in the road.
            const float JunctionProbeMargin = step * 0.5f;

            float shoulderAcross = shape.HalfWidth + shape.ShoulderWidth * 0.5f;

            int samples = 0;
            int missed = 0;
            int crownWrong = 0;
            int vergeSamples = 0;
            int vergeWrong = 0;

            SurfaceKind crownFound = SurfaceKind.Asphalt;
            Vector3 crownAt = Vector3.zero;
            float crownDistance = -1f;

            for (float distance = step; distance < path.Length; distance += step)
            {
                // A junction is where a ribbon deliberately stops: MotorwayTerminusBuilder replaces
                // 200 m of both carriageways and BuildBranchRoad trims a branch back by twenty to forty
                // metres, so the probe drops through where the road used to be and lands on the shelf.
                // Skipped by the same predicate the guard rails and the kerbs read — a checker with an
                // opinion of its own about where a road ends agrees with the builder right up until one
                // of them is wrong.
                if (course != null && course.IsJunction(distance, JunctionProbeMargin))
                {
                    continue;
                }

                Vector3 centre = path.GetPositionAtDistance(distance);
                Vector3 across = path.GetRightAtDistance(distance);

                samples++;

                if (!ProbeSurface(centre, out SurfaceKind crown))
                {
                    missed++;
                }
                else if (crown != SurfaceKind.Asphalt)
                {
                    crownWrong++;

                    if (crownDistance < 0f)
                    {
                        crownFound = crown;
                        crownAt = centre;
                        crownDistance = distance;
                    }
                }

                for (int side = -1; side <= 1; side += 2)
                {
                    if (!ProbeSurface(centre + across * (shoulderAcross * side), out SurfaceKind verge))
                    {
                        continue;
                    }

                    vergeSamples++;

                    if (verge != SurfaceKind.Shoulder)
                    {
                        vergeWrong++;
                    }
                }
            }

            surfaceProbes += samples;
            surfaceProbeMisses += missed;
            surfaceVergeSamples += vergeSamples;
            surfaceVergeBuried += vergeWrong;

            // Reported before either verdict, and it is the more important of the three. A check whose
            // rays all miss finds nothing wrong and says nothing at all — which is indistinguishable
            // from a clean pass, and is what this would be if the colliders were not registered in the
            // editor or if the ribbon were floating over its own shelf.
            if (missed > samples / 4)
            {
                Debug.LogWarning(
                    $"[Horizon] {what}: {missed} of {samples} surface probes found no tagged collider "
                    + "under the road at all. Nothing below can be trusted — a check that cannot reach "
                    + "its subject reports a clean pass.");
            }

            if (crownWrong > 0)
            {
                Debug.LogWarning(
                    $"[Horizon] {what}: {crownWrong} of {samples} crown samples do not read asphalt. "
                    + $"The first is {crownFound} at {crownDistance:0} m along, world "
                    + $"({crownAt.x:0}, {crownAt.y:0}, {crownAt.z:0}). A wheel reads this off "
                    + "RaycastHit.triangleIndex, so the causes are a submesh order that no longer "
                    + "matches RoadMeshBuilder, or terrain standing over the carriageway — and the "
                    + "world position is what tells those two apart.");
            }

            // More than half is the shape of a boundary in the wrong place. A scattering is the shape of
            // banked corners, and is reported as one number for the whole world rather than as eleven
            // warnings nobody would read past the second of.
            if (vergeSamples > 0 && vergeWrong * 2 > vergeSamples)
            {
                Debug.LogWarning(
                    $"[Horizon] {what}: {vergeWrong} of {vergeSamples} verge samples do not read gravel. "
                    + "That is most of the shoulder, which is too many to be the terrain shelf standing "
                    + "over the outside of banked corners — look at the shoulder submesh boundary.");
            }
        }

        /// <summary>
        /// Drops a ray onto the world and returns what the collider it lands on says it is.
        ///
        /// <para>From three metres up and no further, which is what keeps this honest inside a tunnel:
        /// the bore is over four metres high, so the ray starts in the air under the rock and finds the
        /// carriageway rather than the massif above it.</para>
        /// </summary>
        private static bool ProbeSurface(Vector3 point, out SurfaceKind kind)
        {
            kind = SurfaceKind.Asphalt;

            if (!Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 8f))
            {
                return false;
            }

            var tag = hit.collider.GetComponent<GroundSurface>();
            if (tag == null)
            {
                return false;
            }

            kind = tag.KindAt(hit.triangleIndex);
            return true;
        }

        private static void ReportSurfaces()
        {
            Debug.Log($"[Horizon] Surfaces: {taggedCarriageways} carriageways tagged asphalt over gravel, "
                      + $"{taggedGround} terrain tiles tagged ground. Everything else drives like a road, "
                      + "which is the default and is deliberate. "
                      + $"{surfaceProbes - surfaceProbeMisses} of {surfaceProbes} probes reached one.");

            // Not a fault, and worth a line of its own so nobody re-derives it. The shoulder hangs 0.5 m
            // below the asphalt against a terrain shelf 0.45 m below the crown, so on the inside of a
            // banked corner the hillside stands over the outer half of the verge — and a wheel that runs
            // wide there really is on terrain rather than on gravel. What is drawn as gravel and what is
            // stood on are different questions, and this is the one number that says how far apart.
            Debug.Log($"[Horizon] Verge: {surfaceVergeBuried} of {surfaceVergeSamples} probes at the "
                      + $"middle of a shoulder land on terrain rather than gravel "
                      + $"({surfaceVergeBuried * 100f / Mathf.Max(1, surfaceVergeSamples):0.0} %), "
                      + "which is the terrain shelf standing over the verge on banked corners. See "
                      + "RoadShape.ShoulderDrop against TerrainShape.RoadShelfDrop.");

            if (taggedCarriageways == 0 || taggedGround == 0)
            {
                Debug.LogWarning(
                    "[Horizon] One of the two surface kinds came out empty. Untagged geometry reads as "
                    + "asphalt, so this builds, validates and drives — with a verge that is as quick as "
                    + "the carriageway, or a hillside that is. Check the surfaces argument on "
                    + "CreateMeshObject.");
            }
        }

        private static void TagSurface(GameObject meshObject, Mesh colliding, SurfaceKind[] surfaces)
        {
            if (surfaces == null || surfaces.Length == 0 || colliding == null)
            {
                return;
            }

            var tag = meshObject.AddComponent<GroundSurface>();

            if (surfaces.Length == 1 || colliding.subMeshCount != surfaces.Length)
            {
                // A mesh whose submesh count has drifted from the table falls back to its first kind
                // rather than mapping runs onto boundaries that are no longer where they were. Wrong by
                // a shoulder is recoverable; a shoulder starting in the middle of the carriageway is not.
                tag.SetUniform(surfaces[0]);
            }
            else
            {
                var starts = new int[surfaces.Length];
                int running = 0;

                for (int i = 0; i < surfaces.Length; i++)
                {
                    starts[i] = running;
                    running += (int)(colliding.GetIndexCount(i) / 3);
                }

                tag.SetRuns(starts, surfaces);
            }

            if (surfaces[0] == SurfaceKind.Ground)
            {
                taggedGround++;
            }
            else
            {
                taggedCarriageways++;
            }
        }

        private static GameObject CreateMeshObject(
            Transform parent,
            string name,
            Mesh mesh,
            Material[] materials,
            bool addCollider = true,
            bool markStatic = true,
            StaticEditorFlags? staticFlags = null,
            Mesh collisionMesh = null,
            SurfaceKind[] surfaces = null)
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
                Mesh colliding = collisionMesh != null ? collisionMesh : mesh;
                meshObject.AddComponent<MeshCollider>().sharedMesh = colliding;

                TagSurface(meshObject, colliding, surfaces);
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
