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
    /// The view the game is actually played from: the chase camera's own pose, behind the player's car,
    /// through the post stack the player gets.
    ///
    /// <para><b>This project photographs its world, its HUD and its cars, and had never once
    /// photographed the frame a driver looks at.</b> The world previews stand on the carriageway at eye
    /// height and look along it — a camera where a car would be, with no car in shot. The car previews
    /// are a studio: a turntable against a flat colour with post deliberately off, because the same
    /// image is the garage thumbnail. So the one object that is in every frame of the shipping game,
    /// nearest to the camera, at the highest pixel density and carrying the only above-one materials in
    /// the near field, appeared in no picture taken through the pipeline it is drawn by.</para>
    ///
    /// <para>Which meant a whole class of fault was invisible here: anything whose artefacts scale with
    /// how close a thing is to the camera. Shadow-map texel size, bloom resolution and edge treatment
    /// are all of that class, and all three were changed together when the frame was given a tone map,
    /// antialiasing and a shadow distance. <b>It was reported from the car and by nothing else.</b></para>
    ///
    /// <para><b>The pose is asked of <c>ChaseCamera.SnapToTarget</c> rather than worked out here.</b>
    /// That method already exists for the respawn, it is the code that decides where the rig rests, and
    /// a tool carrying its own copy of a distance, a height and a look-ahead would photograph a framing
    /// the game does not use — the argument <c>PhotoMode.ShowcaseAt</c>, <c>VehicleCover.RoofedAt</c>
    /// and the gauges' <c>LayOutFace</c> each already make.</para>
    ///
    /// <para><b>The frames come in sets that differ by one thing.</b> A single picture of a soft frame
    /// says the frame is soft and nothing about why; three that differ by one setting each say which
    /// setting it was. That is the only way to tell a shadow cascade from a bloom pyramid from an edge
    /// filter in a still, because all three read as "fragments near the car".</para>
    ///
    /// <para><b>And one of those settings was not reachable from here at all, which cost the project
    /// the fault this tool exists to catch.</b> The High preset's ambient occlusion lives on a
    /// <i>second</i> renderer, and only <c>Mobile_RPAsset</c> has one — so on the asset the editor
    /// runs, the whole column moves nothing, and a camera built with a bare
    /// <c>AddComponent&lt;Camera&gt;()</c> could not have reached it in any case. What shipped on High
    /// meanwhile was an SSAO composited after opaques at four taps over a 0.55 m radius, which stained
    /// the sunlit bodywork of the player's car in dark dithered blotches. It was reported from the car,
    /// and no log line, check or picture here said a word. See
    /// <see cref="CaptureAmbientOcclusion"/>.</para>
    ///
    /// <para>An earlier version of this comment reasoned the question away — "its radius is 0.3 m and it
    /// darkens creases; it cannot draw a band across a whole wing". That is true of <c>PC_Renderer</c>'s
    /// SSAO, which is the one the editor runs, and it is not the SSAO anybody was looking at.
    /// <b>Reasoning about the wrong asset reads exactly like reasoning about the right one</b>, and the
    /// only thing that told the two apart was a picture taken through each.</para>
    /// </summary>
    public static class DriverPreviewRenderer
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";

        /// <summary>
        /// 1920 × 1080, and it is the one number here that must not be the other tools' 1280 × 720.
        ///
        /// <para>Every fault this tool exists to catch is measured in <i>pixels against a fixed world
        /// size</i> — a shadow texel, a bloom mip, an FXAA span. Photographing them at two thirds of the
        /// resolution the game runs at makes every one of them look two thirds as bad, which is the
        /// direction that ships.</para>
        /// </summary>
        private const int Width = 1920;

        private const int Height = 1080;

        [MenuItem("Tools/Horizon/Render Driver Preview", priority = 40)]
        public static void Render()
        {
            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            // Inactive included, for the reason CaptureStart records: the car in a saved world scene is
            // switched off until GameBootstrap wakes it, and a search that misses it reports "no car"
            // against a scene that has one.
            var vehicle = Object.FindFirstObjectByType<VehicleController>(FindObjectsInactive.Include);
            var chase = Object.FindFirstObjectByType<ChaseCamera>(FindObjectsInactive.Include);
            var post = Object.FindFirstObjectByType<PostProcessing>(FindObjectsInactive.Include);

            string directory = Directory.GetParent(Application.dataPath).FullName;
            var cameraObject = new GameObject("DriverPreviewCamera");

            UniversalRenderPipelineAsset pipeline = UniversalRenderPipeline.asset;
            float shadowsWere = pipeline != null ? pipeline.shadowDistance : 0f;
            float bloomWas = post != null ? post.BloomAmount : 1f;

            try
            {
                if (vehicle == null || chase == null)
                {
                    Debug.LogError("[Horizon] No car or no ChaseCamera in the world scene, so the "
                                   + "driver's view was not photographed. Run Rebuild Prototype Scene "
                                   + "first.");
                    return;
                }

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = Mathf.Max(900f, Horizon.World.BackdropBuilder.MinimumFarPlane);

                PlaceLikeTheRig(chase, vehicle, camera);

                // 1. What the player gets. Everything below differs from this by exactly one setting.
                Capture(camera, Path.Combine(directory, "DriverPreview_1_AsShipped.png"));

                // 2. The shadow distance the world had before the frame was given one. 50 m over four
                // cascades at 2048 is 2.4 cm a texel against the 7.8 it is now, so if the fragments are
                // the sun's own shadow stepping across the bodywork, this is the frame they leave.
                if (pipeline != null)
                {
                    pipeline.shadowDistance = 50f;
                    Capture(camera, Path.Combine(directory, "DriverPreview_2_ShadowsNear.png"));
                    pipeline.shadowDistance = shadowsWere;
                }

                // 3. No bloom. It runs at Quarter resolution over four iterations, and the car carries
                // the only materials in the near field driven above one — VehicleLights takes its lens
                // colours to 2.4 and 3.2. A quarter-resolution pyramid around those is blocky by
                // construction; the question is whether it is blocky enough to be what was seen.
                if (post != null)
                {
                    post.BloomAmount = 0f;
                    Capture(camera, Path.Combine(directory, "DriverPreview_3_NoBloom.png"));
                    post.BloomAmount = bloomWas;
                }

                // 4. No sun shadow at all, which is the frame that names the cause rather than
                // narrowing it. A real shadow is the same shape at any distance and merely coarser; a
                // shape that changes when the cascade does is the map undersampling its own subject.
                // Only this frame separates "the car is in shade" from "the car is in an artefact".
                //
                // The shadow distance is a runtime value on the pipeline asset and is put back in the
                // finally, which is the one way this may be switched: the SSAO beside it is a renderer
                // <i>feature</i>, and SetActive on one writes m_Active into an asset that Unity will not
                // roll back — the working-tree hazard QualityDirector, TownLights and WetSurfaces all
                // document. Which is exactly why frames 5 and 6 below point the camera at the other
                // renderer rather than switching the feature: choosing a renderer is a scene-side
                // call, and it is discarded with the throwaway camera it was made on.
                if (pipeline != null)
                {
                    pipeline.shadowDistance = 0f;
                    Capture(camera, Path.Combine(directory, "DriverPreview_4_NoSunShadow.png"));
                    pipeline.shadowDistance = shadowsWere;
                }

                // 5 and 6. The High preset's ambient occlusion, which no picture in this project
                // could take until these two frames existed — and taking them needs the *mobile*
                // pipeline asset rather than whichever one the editor happens to be on.
                //
                // That is the whole trap. The editor runs PC_RPAsset (measured: build target Android,
                // quality level 'PC'), which carries one renderer, so QualityDirector's guard falls
                // through to index 0 and the AO column moves nothing here at all. What ships on High is
                // Mobile_RPAsset's second renderer, and a frame that reaches it only when somebody has
                // happened to flip a project setting by hand is not an instrument — so this switches
                // the level itself and puts it back.
                //
                // Two frames rather than one, for this tool's own stated reason: a single picture of a
                // stained frame says the frame is stained and nothing about what stained it.
                CaptureAmbientOcclusion(camera, directory, out string aoPipeline);

                // The pipeline asset is named because a comment cannot say which one this was, and the
                // question is not academic: what the editor selects and what the phone runs are
                // different assets with different renderers, and the ambient occlusion lives on the one
                // the editor does not use.
                string pipelineName = pipeline != null ? pipeline.name : "no pipeline asset";
                int renderers = pipeline != null ? pipeline.rendererDataList.Length : 0;

                Debug.Log($"[Horizon] Driver's view: {(aoPipeline != null ? "six" : "four")} frames "
                          + $"from the chase camera's own resting pose, {Width}x{Height}, differing by "
                          + "one setting each — as shipped, with the shadow distance back at 50 m, "
                          + "with the bloom volume at zero, and with no sun shadow at all"
                          + (aoPipeline != null
                              ? $", and with and without '{aoPipeline}'s ambient occlusion renderer, "
                                + "which is what the High preset points the camera at on the phone"
                              : "")
                          + $". The editor is on '{pipelineName}', {renderers} renderer"
                          + $"{(renderers == 1 ? "" : "s")}"
                          + (aoPipeline != null
                              ? "."
                              : " — and no quality level here has a second renderer, so there is no "
                                + "ambient occlusion frame."));
            }
            finally
            {
                if (pipeline != null)
                {
                    pipeline.shadowDistance = shadowsWere;
                }

                if (post != null)
                {
                    post.BloomAmount = bloomWas;
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
        /// <para><b>The rig is asked, not copied.</b> <c>SnapToTarget</c> is what places the camera after
        /// a respawn, so it is already the one expression of where the chase camera belongs; reading its
        /// answer off the transform costs nothing and cannot drift. The field of view comes off the
        /// camera on that same object, which the setup tool wrote from
        /// <c>ChaseCamera.baseFieldOfView</c> — so this frame is the game's framing rather than a
        /// plausible one.</para>
        ///
        /// <para>The rig's own transform is written to do it, which is why this tool closes the scene
        /// without saving. Nothing here may leave a modified working tree for having looked at the
        /// world — the hazard <c>Backdrop</c> refuses <c>[ExecuteAlways]</c> over.</para>
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

        /// <summary>
        /// Shoots the two frames that answer what the High preset's ambient occlusion does, through the
        /// quality level whose pipeline asset actually carries a second renderer.
        ///
        /// <para><b>The level is switched here rather than assumed, and that is the point of the
        /// method.</b> <c>UniversalRenderPipeline.asset</c> is whichever level the editor is on, which
        /// is <c>PC_RPAsset</c> — one renderer, so <see cref="QualityDirector"/>'s guard falls through
        /// to index 0 and High and Balanced are the same frame. The ambient occlusion that ships lives
        /// on <c>Mobile_RPAsset</c>'s second renderer, and for two commits the only instrument that had
        /// ever seen it was somebody driving with the level flipped by hand.</para>
        ///
        /// <para>A project setting is written to do it, which is why the level is put back in a
        /// <c>finally</c> — the same call this tool already makes for <c>shadowDistance</c>, which is
        /// an asset field and no safer. <b>Putting the value back is not quite the same as leaving the
        /// file alone:</b> writing it at all makes Unity re-serialise <c>QualitySettings.asset</c>, so
        /// the first run after an editor upgrade leaves a format-only diff in the working tree. That is
        /// a one-off — the second run writes the same bytes — and it is said here because this project
        /// otherwise holds that nothing may leave a modified tree for having looked at the world.</para>
        ///
        /// <para>The renderer is chosen through <see cref="QualityDirector.PointAtAmbientOcclusionRenderer"/>
        /// rather than through a copy of its guard, which is the argument
        /// <c>VehicleCover.RoofedAt</c> and the gauges' <c>LayOutFace</c> each already make: a frame
        /// claiming to show what a setting does has to be produced by the code that does it.</para>
        /// </summary>
        private static void CaptureAmbientOcclusion(
            Camera camera, string directory, out string pipelineName)
        {
            pipelineName = null;

            int levelWas = QualitySettings.GetQualityLevel();

            try
            {
                for (int level = 0; level < QualitySettings.names.Length; level++)
                {
                    QualitySettings.SetQualityLevel(level, true);

                    // Whether this level's asset has the renderer at all is asked of the game's own
                    // method rather than of rendererDataList here, because that is the question it
                    // already answers — a length test beside it is the second copy of a guard.
                    if (!QualityDirector.PointAtAmbientOcclusionRenderer(camera, true))
                    {
                        continue;
                    }

                    pipelineName = UniversalRenderPipeline.asset.name;

                    Capture(camera, Path.Combine(directory, "DriverPreview_6_AmbientOcclusion.png"));

                    QualityDirector.PointAtAmbientOcclusionRenderer(camera, false);
                    Capture(camera, Path.Combine(directory, "DriverPreview_5_NoAmbientOcclusion.png"));
                    break;
                }
            }
            finally
            {
                QualitySettings.SetQualityLevel(levelWas, true);
            }
        }

        private static void Capture(Camera camera, string filePath) =>
            PreviewCapture.Shoot(camera, Width, Height, filePath);
    }
}
