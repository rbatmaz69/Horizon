using System.Collections.Generic;
using System.IO;
using Horizon.Game;
using Horizon.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs the driving HUD as the player sees it: the canvas, at the canvas's own reference
    /// resolution, with the instruments up.
    ///
    /// <para><b>Nothing had ever taken this picture.</b> <c>TouchUiSetup</c> places every control by
    /// arithmetic against a screen corner, and the arithmetic has been argued about in comments — the
    /// two-column grid exists because a handbrake and a slider overlapped by 120 × 100 units and took
    /// each other's taps. That was found by driving. Everything about a corner is decided at build time
    /// and can be looked at at build time, so this looks at it.</para>
    ///
    /// <para>It runs on whatever scenes are open, which is why <c>Rebuild</c> calls it at the very end —
    /// after it has reopened Bootstrap. Run on its own with the world scene open too, it gives the same
    /// frame.</para>
    /// </summary>
    public static class HudPreviewRenderer
    {
        /// <summary>The canvas's own reference resolution, so a unit in the builders is a pixel here.</summary>
        private const int Width = 1920;

        private const int Height = 1080;

        /// <summary>
        /// Samples for these two frames.
        ///
        /// <para>Under test. The minimap is clipped by a stencil <c>Mask</c>, and a stencil that is not
        /// honoured shows up as a map drawn past its own rim — which is exactly what this tool has been
        /// reporting. Multisampled offscreen targets are the remaining suspect.</para>
        /// </summary>
        private const int Msaa = 1;

        [MenuItem("Tools/Horizon/Render HUD Preview", priority = 48)]
        public static void Render()
        {
            Canvas canvas = FindCanvas();

            if (canvas == null)
            {
                // Additively, never Single: run from the menu with work open, this must not be the tool
                // that closed somebody's scene to take a photograph.
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    "Assets/_Project/Scenes/Bootstrap.unity",
                    UnityEditor.SceneManagement.OpenSceneMode.Additive);

                canvas = FindCanvas();
            }

            if (canvas == null)
            {
                Debug.LogWarning("[Horizon] No canvas to photograph. Run Rebuild Prototype Scene first.");
                return;
            }

            string directory = Directory.GetParent(Application.dataPath).FullName;

            // The canvas is saved as a Screen Space - Overlay, which no camera can photograph: an
            // overlay is composited after every camera has finished. Borrowed for the shot and handed
            // back in the finally, and the scene is never saved from here.
            RenderMode wasMode = canvas.renderMode;
            Camera wasCamera = canvas.worldCamera;

            var cameraObject = new GameObject("HudPreviewCamera");

            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;

                // A mid grey rather than black: half of this HUD is white at 30 % alpha, and on black
                // that reads far heavier than it does over a road.
                camera.backgroundColor = new Color(0.32f, 0.34f, 0.30f, 1f);
                camera.enabled = false;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 200f;

                // Well above the world. A Screen Space - Camera canvas hangs at planeDistance in front
                // of its camera, so anything standing between the two is drawn over the HUD: left at the
                // origin, this photographed a hillside through the middle of the minimap and the rev
                // counter, which looks exactly like a clipping fault in the UI and is not one.
                cameraObject.transform.position = new Vector3(0f, 20000f, 0f);

                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 20f;

                // --- Driving.
                AimTheMinimap(canvas);
                LayOutTheDials(canvas);
                ShowOneScheme(canvas);

                Canvas.ForceUpdateCanvases();
                MapPreviewRenderer.Shoot(camera, Width, Height, Path.Combine(directory, DrivingShot), Msaa);

                Restore();

                // --- The full-screen map, which is the only way to see the key.
                ShowTheMap(canvas);

                Canvas.ForceUpdateCanvases();
                MapPreviewRenderer.Shoot(camera, Width, Height, Path.Combine(directory, MapShot), Msaa);

                Debug.Log($"[Horizon] HUD preview written to {directory}/{DrivingShot} and {MapShot}");
            }
            finally
            {
                canvas.renderMode = wasMode;
                canvas.worldCamera = wasCamera;

                Restore();
                Object.DestroyImmediate(cameraObject);
            }
        }

        private const string DrivingShot = "HudPreview_Driving.png";

        private const string MapShot = "HudPreview_Map.png";

        /// <summary>What a shot switched off or on, and puts back.</summary>
        private static readonly List<GameObject> Hidden = new List<GameObject>();

        private static readonly List<GameObject> Shown = new List<GameObject>();

        private static void Restore()
        {
            for (int i = 0; i < Hidden.Count; i++)
            {
                if (Hidden[i] != null)
                {
                    Hidden[i].SetActive(true);
                }
            }

            for (int i = 0; i < Shown.Count; i++)
            {
                if (Shown[i] != null)
                {
                    Shown[i].SetActive(false);
                }
            }

            Hidden.Clear();
            Shown.Clear();
        }

        /// <summary>
        /// Puts the map page up, with the driving controls out of the way.
        ///
        /// <para><c>MapScreen.Open</c> is called by hand because <c>OnEnable</c> does not run in the
        /// editor — see the note there.</para>
        /// </summary>
        private static void ShowTheMap(Canvas canvas)
        {
            Hide(canvas, "Wheel");
            Hide(canvas, "Arrows");
            Hide(canvas, "Pedals");
            Hide(canvas, "AutoPedals");
            Hide(canvas, "Slider");
            Hide(canvas, "Handbrake");
            Hide(canvas, "PauseButton");
            Hide(canvas, "Instruments");
            Hide(canvas, "MinimapRim");
            Hide(canvas, "Backdrop");

            Transform[] all = canvas.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name != "MapPanel")
                {
                    continue;
                }

                all[i].gameObject.SetActive(true);
                Shown.Add(all[i].gameObject);

                MapScreen screen = all[i].GetComponent<MapScreen>();
                if (screen != null)
                {
                    screen.Open();
                }
            }
        }

        /// <summary>
        /// Leaves one set of controls up.
        ///
        /// <para><c>TouchControlsHud</c> shows the wheel or the arrows, and the pedals or the slider,
        /// according to the scheme the player chose — but it decides that in <c>Update</c>, and there is
        /// no <c>Update</c> here. The saved scene has every alternative active at once, which the first
        /// frame of this tool duly photographed: a slider through a brake pedal, and arrows across the
        /// steering wheel. A preview that shows a layout nobody will ever see is worse than none, since
        /// it invents overlaps to go and fix.</para>
        ///
        /// <para>Wheel and pedals, because that is the pairing the other two are alternatives to. The
        /// backdrop goes too — it belongs to the start screen and is opaque, and this is the driving
        /// screen.</para>
        /// </summary>
        private static void ShowOneScheme(Canvas canvas)
        {
            Hide(canvas, "Arrows");
            Hide(canvas, "AutoPedals");
            Hide(canvas, "Slider");
            Hide(canvas, "Backdrop");
        }

        private static void Hide(Canvas canvas, string name)
        {
            Transform[] all = canvas.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name != name || !all[i].gameObject.activeSelf)
                {
                    continue;
                }

                all[i].gameObject.SetActive(false);
                Hidden.Add(all[i].gameObject);
            }
        }

        private static Canvas FindCanvas()
        {
            Canvas[] canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);

            for (int i = 0; i < canvases.Length; i++)
            {
                // The root one. A Mask puts a nested Canvas on things at run time, and a child canvas
                // has no render mode of its own worth borrowing.
                if (canvases[i].isRootCanvas)
                {
                    return canvases[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Puts the marks on the two small dials.
        ///
        /// <para><b>The same argument <see cref="AimTheMinimap"/> makes.</b> Both gauges place their
        /// own marks on the first frame they get, and there is no frame here — so every mark
        /// photographed stacked at the centre of its dial, under the needle's hub, and both faces came
        /// out as a bare ring with a needle across it. That went unnoticed for several builds because
        /// the dials are correct in the running game, which is exactly the kind of thing this tool
        /// exists to stop being taken on trust.</para>
        ///
        /// <para><b>Not the rev counter, and that is not an oversight.</b> Its face is built from the
        /// car — full scale off the redline, the numbers written from that, the red zone at the real
        /// redline — and there is no car in this scene. A tacho with no marks in a frame with no engine
        /// is honest; laying one out here would mean choosing a redline, and a picture that invents its
        /// subject is worse than one that admits it has none.</para>
        /// </summary>
        private static void LayOutTheDials(Canvas canvas)
        {
            FuelGauge[] fuel = canvas.GetComponentsInChildren<FuelGauge>(true);
            for (int i = 0; i < fuel.Length; i++)
            {
                fuel[i].LayOutFace();
            }

            BoostGauge[] boost = canvas.GetComponentsInChildren<BoostGauge>(true);
            for (int i = 0; i < boost.Length; i++)
            {
                boost[i].LayOutFace();
            }
        }

        /// <summary>
        /// Gives the minimap something to draw.
        ///
        /// <para><c>Minimap</c> reads the car every frame, and there is no car and no frame here — the
        /// widget would otherwise photograph as an empty disc, which is exactly the fault this picture
        /// exists to rule out. The pass is the view worth checking: it is the tightest geometry in the
        /// world and the one the driver reads at speed.</para>
        /// </summary>
        private static void AimTheMinimap(Canvas canvas)
        {
            MapGraphic[] graphics = canvas.GetComponentsInChildren<MapGraphic>(true);

            for (int i = 0; i < graphics.Length; i++)
            {
                MapGraphic graphic = graphics[i];

                if (graphic.Map == null)
                {
                    Debug.LogWarning($"[Horizon] '{graphic.name}' has no world map. "
                                     + "Run Rebuild Prototype Scene.");
                    continue;
                }

                // The minimap's own rect is 300 units; the map page's fills the screen. Only the small
                // one is on screen while driving.
                var rect = (RectTransform)graphic.transform;
                if (rect.rect.width > 400f)
                {
                    continue;
                }

                // Span and forward bias both taken from the component that drives this widget in the
                // game rather than typed again here — a preview with its own copy of either is a
                // preview of a HUD nobody ships. The car sprite is fixed geometry below the middle, so
                // the view has to run ahead by the same amount or the marker sits beside the road.
                var minimap = rect.GetComponentInParent<Minimap>();
                float across = minimap != null ? minimap.MetresAcross : 440f;
                float metres = across / Mathf.Max(1f, rect.rect.width);

                Vector2 centre = Hairpins(graphic);
                float radians = 35f * Mathf.Deg2Rad;
                var ahead = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));

                graphic.SetView(
                    centre + ahead * (rect.rect.height * 0.5f * Minimap.ForwardBias * metres),
                    metres,
                    35f);
            }
        }

        /// <summary>Somewhere on the pass, taken from the map rather than typed.</summary>
        private static Vector2 Hairpins(MapGraphic graphic)
        {
            WorldMap map = graphic.Map;

            for (int line = 0; line < map.LineCount; line++)
            {
                if (map.KindOf(line) != MapLineKind.Trunk)
                {
                    continue;
                }

                int from = map.LineStartAt(line);
                int to = map.LineEndAt(line);

                return map.PointAt(Mathf.Clamp(from + (to - from) * 42 / 100, from, to - 1));
            }

            return map.PlanCentre;
        }
    }
}
