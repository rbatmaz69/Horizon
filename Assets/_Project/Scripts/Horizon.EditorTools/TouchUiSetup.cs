using System.Collections.Generic;
using Horizon.Game;
using Horizon.Input;
using Horizon.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Builds the canvas and the on-screen driving controls into the Bootstrap scene.
    ///
    /// <para>From code, like everything else here, and for the stated reason: a canvas laid out by hand
    /// is a canvas that lives only in a scene file nobody can review. It is also the only way this can
    /// be rebuilt from scratch — the Bootstrap scene is generated output, so a hand-placed button would
    /// vanish the next time anybody ran <c>Rebuild Prototype Scene</c>.</para>
    ///
    /// <para>Everything is anchored to a screen corner rather than positioned absolutely, and the
    /// controls sit inside a safe-area panel — a notch or a gesture bar otherwise lands on top of the
    /// handbrake, and on the phone it was tested on there is one at each end.</para>
    ///
    /// <para><b>The menu now lives in <see cref="MenuUiSetup"/>.</b> This file used to build both, and
    /// the seam between them was always there — one half is about what your thumbs rest on while
    /// driving, the other about pages of buttons you read while stopped. Three panels was the most that
    /// could share a file; the start screen brings eight, and the parts record alone had reached fifteen
    /// fields. What stays here is the canvas, the driving controls, and the small builders both halves
    /// draw with.</para>
    /// </summary>
    public static class TouchUiSetup
    {
        private const string SpriteFolder = "Assets/_Project/Art/UI";

        /// <summary>Big enough to hit with a thumb without looking. Roughly 15 mm on a typical phone.</summary>
        private const float ButtonSize = 150f;

        // --- The right-hand grid.
        //
        // Two columns and two rows, and every driving control on that side comes from these four
        // numbers rather than carrying coordinates of its own. That is not tidiness: the handbrake and
        // the throttle slider were both authored by hand into the same corner and overlapped by
        // 120x100 units, right across the slider's full-brake end. Both are visible at once in slider
        // mode and the handbrake is built later, so it took the raycast — pulling the slider hard down
        // pressed the handbrake and did not brake.
        //
        // The rule the grid encodes: the outer column is whatever the right thumb rests on all the
        // time (throttle, or the slider), and the inner column is everything it reaches across for.
        // Nothing may share a column with the slider, because the slider is 440 units tall and owns
        // its whole column top to bottom.

        /// <summary>Outer column centre, from the right screen edge: throttle or slider.</summary>
        private const float OuterColumnX = -150f;

        /// <summary>Inner column centre: brake and handbrake, reached across for.</summary>
        private const float InnerColumnX = -365f;

        /// <summary>Height of the primary control in each column.</summary>
        private const float PrimaryRowY = 300f;

        /// <summary>Height of the handbrake, below the inner column's primary control.</summary>
        private const float HandbrakeRowY = 110f;

        /// <summary>Driving buttons, larger than <see cref="ButtonSize"/> — these are held, not tapped.</summary>
        private static readonly Vector2 PedalSize = new Vector2(200f, 200f);

        /// <summary>The drawn symbols. Built once per run and handed to the widgets that need them.</summary>
        private struct Glyphset
        {
            public Sprite Left;
            public Sprite Right;
            public Sprite Throttle;
            public Sprite Brake;
            public Sprite Handbrake;
            public Sprite Pause;
            public Sprite Fuel;
        }

        private static Glyphset Glyphs;

        internal static readonly Color ControlTint = new Color(1f, 1f, 1f, 0.30f);
        internal static readonly Color PanelTint = new Color(0.05f, 0.06f, 0.08f, 0.88f);
        internal static readonly Color GlyphTint = new Color(1f, 1f, 1f, 0.92f);

        internal static readonly Color AccentTint = new Color(0.86f, 0.36f, 0.17f, 0.92f);

        /// <summary>Everything the canvas build produces that the caller has to wire up.</summary>
        public sealed class UiParts
        {
            public TouchControlsHud Hud;
            public PauseMenu Menu;
            public MenuPanels Panels;
            public StartScreen StartScreen;
        }

        /// <summary>
        /// Creates the canvas, the driving controls and every menu page, and wires them to the router.
        /// </summary>
        public static UiParts Build(
            GameObject root, DriveInputRouter router, IReadOnlyList<string> spawnNames, WorldMap map)
        {
            EnsureEventSystem(root);

            Sprite box = HorizonAssetUtility.LoadOrCreateUiSprite($"{SpriteFolder}/UI_Box.png");
            Sprite wheelSprite = HorizonAssetUtility.LoadOrCreateWheelSprite($"{SpriteFolder}/UI_Wheel.png");

            Glyphs = new Glyphset
            {
                Left = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Left.png", "left"),
                Right = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Right.png", "right"),
                Throttle = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Throttle.png", "throttle"),
                Brake = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Brake.png", "brake"),
                Handbrake = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Handbrake.png", "handbrake"),
                Fuel = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Fuel.png", "fuel"),
                Pause = HorizonAssetUtility.LoadOrCreateGlyphSprite($"{SpriteFolder}/UI_Pause.png", "pause"),
            };

            Canvas canvas = CreateCanvas(root);
            RectTransform safe = CreateSafeArea(canvas);

            // --- Driving controls.
            GameObject wheel = BuildWheel(safe, wheelSprite);
            GameObject arrows = BuildArrows(safe, box);
            GameObject pedals = BuildPedals(safe, box);
            GameObject slider = BuildSlider(safe, box);
            GameObject autoPedals = BuildAutoPedals(safe, box);
            GameObject handbrake = BuildHandbrake(safe, box);

            GameObject pauseButton = BuildPauseButton(safe, box);
            GameObject instruments = BuildInstruments(safe, map, out Button minimapButton);

            TouchControlsHud hud = canvas.gameObject.AddComponent<TouchControlsHud>();

            HorizonAssetUtility.Configure(hud, serialized =>
            {
                serialized.FindProperty("router").objectReferenceValue = router;
                serialized.FindProperty("wheel").objectReferenceValue = wheel;
                serialized.FindProperty("arrows").objectReferenceValue = arrows;
                serialized.FindProperty("pedals").objectReferenceValue = pedals;
                serialized.FindProperty("slider").objectReferenceValue = slider;
                serialized.FindProperty("autoPedals").objectReferenceValue = autoPedals;
                serialized.FindProperty("handbrake").objectReferenceValue = handbrake;
                serialized.FindProperty("instruments").objectReferenceValue = instruments;
            });

            // --- The menu, in its own file.
            return MenuUiSetup.Build(
                canvas, safe, box, router, hud, pauseButton, minimapButton, spawnNames, map);
        }

        /// <summary>
        /// uGUI does nothing at all without an EventSystem — no clicks, no drags, no touches. It is the
        /// single most common reason a canvas that looks right does not respond.
        /// </summary>
        private static void EnsureEventSystem(GameObject root)
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var events = new GameObject("EventSystem");
            events.transform.SetParent(root.transform, false);
            events.AddComponent<EventSystem>();

            // The Input System package's module, not the legacy StandaloneInputModule — this project
            // has Active Input Handling set to the package, and the old module reads nothing under it.
            events.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        private static Canvas CreateCanvas(GameObject root)
        {
            var canvasObject = new GameObject("TouchControls");
            canvasObject.transform.SetParent(root.transform, false);

            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Well above anything else that might appear, and IMGUI draws over it regardless, so the
            // debug overlay stays readable on top.
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Match the height rather than the width: phones vary hugely in aspect, and matching width
            // makes controls creep off the bottom of a tall screen.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;

            canvasObject.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>
        /// A child rect inset to the device's safe area, so nothing lands under a notch or a gesture
        /// bar. Driven at run time by <see cref="SafeAreaPanel"/>, because the safe area is not known
        /// until the app is on the hardware.
        /// </summary>
        private static RectTransform CreateSafeArea(Canvas canvas)
        {
            var area = new GameObject("SafeArea", typeof(RectTransform));
            area.transform.SetParent(canvas.transform, false);

            RectTransform rect = (RectTransform)area.transform;
            Stretch(rect);

            area.AddComponent<SafeAreaPanel>();
            return rect;
        }

        private static GameObject BuildWheel(RectTransform parent, Sprite ring)
        {
            GameObject group = Group(parent, "Wheel");

            // 320 rather than 280: the angle a thumb can sweep is fixed by the thumb, so a bigger rim
            // is more travel for the same swing and a finer angle per pixel. The dead zone at the hub
            // is taken from the rim's own width, so it grows with it and stays a hub rather than
            // becoming a target.
            RectTransform rect = Panel(group.transform, "Rim", ring, ControlTint,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(320f, 320f),
                new Vector2(230f, 210f));

            // On the rim, not the group: the drag angle is measured about this rect's own centre, and
            // the group is stretched over the whole screen.
            TouchSteeringWheel steering = rect.gameObject.AddComponent<TouchSteeringWheel>();

            HorizonAssetUtility.Configure(steering, serialized =>
                serialized.FindProperty("wheel").objectReferenceValue = rect);

            return group;
        }

        private static GameObject BuildArrows(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Arrows");

            // Wider apart and larger than the first attempt: two thumbs-width buttons crammed together
            // is how you press both at once in a corner.
            HoldButton(group.transform, "Left", box, TouchHoldButton.Action.SteerLeft, Glyphs.Left,
                new Vector2(0f, 0f), new Vector2(145f, PrimaryRowY - 130f), PedalSize);
            HoldButton(group.transform, "Right", box, TouchHoldButton.Action.SteerRight, Glyphs.Right,
                new Vector2(0f, 0f), new Vector2(375f, PrimaryRowY - 130f), PedalSize);

            return group;
        }

        private static GameObject BuildPedals(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Pedals");

            HoldButton(group.transform, "Throttle", box, TouchHoldButton.Action.Throttle, Glyphs.Throttle,
                new Vector2(1f, 0f), new Vector2(OuterColumnX, PrimaryRowY), PedalSize);
            HoldButton(group.transform, "Brake", box, TouchHoldButton.Action.Brake, Glyphs.Brake,
                new Vector2(1f, 0f), new Vector2(InnerColumnX, PrimaryRowY), PedalSize);

            return group;
        }

        private static GameObject BuildAutoPedals(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "AutoPedals");

            HoldButton(group.transform, "Brake", box, TouchHoldButton.Action.Brake, Glyphs.Brake,
                new Vector2(1f, 0f), new Vector2(OuterColumnX, PrimaryRowY), PedalSize);

            return group;
        }

        /// <summary>
        /// The bipolar throttle/brake slider: push up for throttle, pull down for brake, let go and it
        /// centres.
        ///
        /// <para>It is drawn as two halves either side of a centre line rather than as one plain box.
        /// A single rectangle with a knob in it says nothing about which way is which, and the control
        /// it most resembles on a phone is a volume slider, which has its zero at the bottom. The tinted
        /// upper half and the line at rest are the only cues that the middle means coasting.</para>
        /// </summary>
        private static GameObject BuildSlider(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Slider");

            RectTransform track = Panel(group.transform, "Track", box, ControlTint,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(130f, 440f),
                new Vector2(OuterColumnX, PrimaryRowY));

            // Upper half tinted and lower half left plain: throttle is the half a thumb lives in.
            RectTransform throttleHalf = Panel(track, "ThrottleHalf", box,
                new Color(AccentTint.r, AccentTint.g, AccentTint.b, 0.16f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(130f, 220f), new Vector2(0f, 110f));
            throttleHalf.GetComponent<Image>().raycastTarget = false;

            RectTransform centreLine = Panel(track, "CentreLine", box, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(130f, 4f), Vector2.zero);
            centreLine.GetComponent<Image>().raycastTarget = false;

            RectTransform knob = Panel(track, "Knob", box, AccentTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(118f, 90f), Vector2.zero);
            knob.GetComponent<Image>().raycastTarget = false;

            TouchThrottleSlider throttle = track.gameObject.AddComponent<TouchThrottleSlider>();
            HorizonAssetUtility.Configure(throttle, serialized =>
                serialized.FindProperty("knob").objectReferenceValue = knob);

            return group;
        }

        /// <summary>
        /// Inner column, below the brake — never the outer one. The slider lives in the outer column
        /// and is 440 units tall, so anything placed there is placed on top of it. See the grid
        /// constants for what that cost.
        /// </summary>
        private static GameObject BuildHandbrake(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Handbrake");

            HoldButton(group.transform, "Handbrake", box, TouchHoldButton.Action.Handbrake, Glyphs.Handbrake,
                new Vector2(1f, 0f), new Vector2(InnerColumnX, HandbrakeRowY), new Vector2(200f, 130f));

            return group;
        }

        /// <summary>
        /// Top-left, diagonally opposite the pedals and well away from the wheel.
        ///
        /// Deliberately small and deliberately not near a driving control: a pause button under a thumb
        /// that is steering is a pause button pressed halfway round a hairpin.
        /// </summary>
        private static GameObject BuildPauseButton(RectTransform parent, Sprite box)
        {
            // Beside the minimap rather than in the corner it used to have. The map wants the corner
            // for the same reason the rev counter wants the other one — a square readout reads best
            // against two screen edges — and 400 clears the map's own 40..340 span with room for a
            // thumb. It is still well left of the fuel notice, which starts at 580.
            RectTransform rect = Panel(parent, "PauseButton", box, ControlTint,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, 110f),
                new Vector2(400f, -80f));

            RectTransform icon = Panel(rect, "Icon", Glyphs.Pause, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(52f, 52f), Vector2.zero);
            icon.GetComponent<Image>().type = Image.Type.Simple;
            icon.GetComponent<Image>().raycastTarget = false;

            rect.gameObject.AddComponent<Button>().targetGraphic = rect.GetComponent<Image>();

            return rect.gameObject;
        }

        /// <summary>
        /// A panel whose height is whatever its rows add up to.
        ///
        /// <para>The offsets it replaced were wrong, and not by accident — they could not be kept
        /// right. Every row carried an absolute y, so the panel's height, the gap between rows and the
        /// position of the last row were three independent numbers that had to agree, and they did
        /// not: "Back" sat on top of the sensitivity slider and its label on top of that. Worse, a row
        /// that hides leaves its gap behind. A vertical layout group and a content-size fitter make the
        /// height a consequence of the rows instead of a fourth number to keep in step.</para>
        /// </summary>
        internal static RectTransform StackPanel(RectTransform parent, string name, Sprite box, float width)
        {
            RectTransform panel = Panel(parent, name, box, PanelTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(width, 0f), Vector2.zero);

            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(60, 60, 46, 46);
            layout.spacing = MenuRowSpacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return panel;
        }

        /// <summary>
        /// A slider as a menu row: track, fill and handle, with no opinion about what it measures.
        ///
        /// <para>Everything inside is stretched rather than sized, because the layout group decides the
        /// row's width — a fixed width here is right at one panel width and wrong at every other.</para>
        /// </summary>
        internal static Slider BuildTrackedSlider(RectTransform parent, Sprite box, string name)
        {
            RectTransform rect = Panel(parent, name, box, ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(520f, 50f), Vector2.zero);
            Row(rect.gameObject, 50f);

            RectTransform fillArea = StretchChild(rect, "Fill Area", 25f, 10f);
            RectTransform fill = Panel(fillArea, "Fill", box, AccentTint,
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;

            RectTransform handleArea = StretchChild(rect, "Handle Slide Area", 25f, 0f);
            RectTransform handle = Panel(handleArea, "Handle", box, Color.white,
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(50f, 0f), Vector2.zero);

            Slider slider = rect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();

            return slider;
        }

        /// <summary>
        /// The instruments, in the top-right corner: the rev counter, and the fuel gauge beside it.
        ///
        /// <para><b>The one corner that was free.</b> The wheel owns the bottom left, the throttle
        /// slider owns the whole right-hand column from y=300 up, and the pause button sits top left.
        /// Anywhere else and a gauge would either be under a thumb or on top of a control — which is
        /// why the fuel dial, when it arrived, went beside the tacho rather than into a corner of its
        /// own. See <see cref="BuildFuelDial"/> for the arithmetic that says it fits.</para>
        ///
        /// <para>Nothing here is tappable — <c>raycastTarget</c> is off on every graphic. A 300-unit
        /// square of raycast target parked in a corner swallows taps without any sign that it did, and
        /// this is a readout.</para>
        /// </summary>
        private static GameObject BuildInstruments(
            RectTransform parent, WorldMap map, out Button minimapButton)
        {
            const float dialSize = 300f;

            Sprite ring = HorizonAssetUtility.LoadOrCreateUiSprite(
                $"{SpriteFolder}/UI_Dial.png", 256, 1f, 0.80f);
            Sprite needleSprite = HorizonAssetUtility.LoadOrCreateNeedleSprite($"{SpriteFolder}/UI_Needle.png");
            Sprite tickSprite = HorizonAssetUtility.LoadOrCreateTickSprite($"{SpriteFolder}/UI_Tick.png");

            GameObject group = Group(parent, "Instruments");

            RectTransform dial = Panel(group.transform, "Dial", ring, ControlTint,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(dialSize, dialSize), new Vector2(-190f, -180f));

            // Simple, not Sliced. Panel assumes a nine-sliced box; a ring has no border, and stretching
            // one as though it did turns the circle into a lozenge.
            Untargeted(dial, Image.Type.Simple);

            // The red zone, under the marks. A radial fill on the same ring, rotated at run time so it
            // begins at whatever this car's redline is.
            RectTransform redline = Panel(dial, "Redline", ring, RedlineTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(dialSize, dialSize), Vector2.zero);

            Image redlineImage = Untargeted(redline, Image.Type.Filled);
            redlineImage.fillMethod = Image.FillMethod.Radial360;
            redlineImage.fillOrigin = (int)Image.Origin360.Top;
            redlineImage.fillClockwise = true;
            redlineImage.fillAmount = 0f;

            // Nine of each: eight thousand rpm is the fastest engine in the fleet, so 0..8 is the
            // longest face any car asks for. InstrumentCluster switches off the ones it does not need.
            var tickMarks = new RectTransform[MaxDialMarks];
            var tickLabels = new Text[MaxDialMarks];

            for (int i = 0; i < MaxDialMarks; i++)
            {
                // 17x23 for a mark five units wide and twenty tall: the bar inside UI_Tick.png fills
                // 30% of the sprite's width and 86% of its height, so the rect has to be that much
                // bigger than the mark you want. Sizing the rect to the mark draws a hairline.
                tickMarks[i] = Panel(dial, $"Tick{i}", tickSprite, GlyphTint,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(17f, 23f), Vector2.zero);

                Untargeted(tickMarks[i], Image.Type.Simple);

                // Its own rect rather than a stretched Label: these are positioned around a circle, and
                // Label stretches to its parent.
                var labelObject = new GameObject($"TickLabel{i}", typeof(RectTransform));
                labelObject.transform.SetParent(dial, false);

                var labelRect = (RectTransform)labelObject.transform;
                labelRect.anchorMin = new Vector2(0.5f, 0.5f);
                labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.sizeDelta = new Vector2(40f, 30f);

                tickLabels[i] = LabelOn(labelRect, string.Empty, 22, new Color(1f, 1f, 1f, 0.75f));

                tickMarks[i].gameObject.SetActive(false);
                labelObject.SetActive(false);
            }

            // The readouts, stacked down the middle and parted around the needle's hub — a 39-unit
            // disc over the centre of the dial, which a number centred on the middle would sit under.
            // Speed above it, unit and gear below. The unit is a fixed caption, wired to nothing.
            Text speed = CentreLabel(dial, "Speed", "0", 48, new Vector2(0f, 48f), new Vector2(170f, 60f),
                Color.white);
            CentreLabel(dial, "Unit", "km/h", 18, new Vector2(0f, -30f), new Vector2(120f, 26f),
                new Color(1f, 1f, 1f, 0.65f));
            Text gear = CentreLabel(dial, "Gear", "1", 32, new Vector2(0f, -68f), new Vector2(90f, 40f),
                AccentTint);

            // Last, so it draws over the face — uGUI draws canvas children in hierarchy order. Full
            // dial size, because the needle sprite is drawn from the centre of its own square: the
            // rect then turns about the middle of the dial with the default centre pivot, and there is
            // no pivot to get subtly wrong.
            RectTransform needle = Panel(dial, "Needle", needleSprite, NeedleTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(dialSize, dialSize), Vector2.zero);

            Untargeted(needle, Image.Type.Simple);

            InstrumentCluster cluster = dial.gameObject.AddComponent<InstrumentCluster>();

            HorizonAssetUtility.Configure(cluster, serialized =>
            {
                serialized.FindProperty("needle").objectReferenceValue = needle;
                serialized.FindProperty("redlineArc").objectReferenceValue = redlineImage;
                serialized.FindProperty("speedLabel").objectReferenceValue = speed;
                serialized.FindProperty("gearLabel").objectReferenceValue = gear;

                HorizonAssetUtility.SetObjectArray(serialized, "tickMarks", tickMarks);
                HorizonAssetUtility.SetObjectArray(serialized, "tickLabels", tickLabels);
            });

            BuildFuelDial(group.transform, ring, needleSprite, tickSprite);
            BuildFuelNotice(group.transform);
            BuildLapTimer(group.transform);

            // In this group so it hides with the rest of the HUD: TouchControlsHud switches the whole
            // group off whenever the player is not driving, and a map floating over the start screen
            // would be a second map beside the one the menu can already open.
            minimapButton = BuildMinimap(group.transform, ring, tickSprite, map);

            return group;
        }

        /// <summary>
        /// The line that appears when the tank is low, along the top edge.
        ///
        /// <para><b>The last free strip on the screen.</b> The pause button owns the top left corner and
        /// the instruments the top right; between them, across the middle of the top edge, nothing has
        /// ever been. It is also where a notification conventionally goes and nowhere near a thumb.</para>
        ///
        /// <para>Inside the instruments group rather than in one of its own, which is what keeps it off
        /// the start screen and hides it with the rest of the HUD on pause — see
        /// <c>TouchControlsHud</c>. It is a readout, and this is where the readouts live.</para>
        ///
        /// <para>760 wide centred: on the narrowest canvas Android produces, 4:3 at 1440 units, that
        /// spans x 340…1100 against a pause button ending at 145.</para>
        /// </summary>
        private static void BuildFuelNotice(Transform parent)
        {
            Sprite box = HorizonAssetUtility.LoadOrCreateUiSprite($"{SpriteFolder}/UI_Box.png");

            RectTransform notice = Panel(parent, "FuelNotice", box, PanelTint,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(760f, 68f), new Vector2(0f, -56f));

            Untargeted(notice, Image.Type.Sliced);

            Text line = LabelOn(notice, string.Empty, 28, GlyphTint);

            // The component goes on the group, not on the panel it hides. A MonoBehaviour that switched
            // off its own GameObject would stop being updated at the same moment, and would then have no
            // way to switch it back on — the notice would appear once and never leave. The group stays
            // up for as long as the HUD does; the panel under it is what comes and goes.
            FuelNotice component = parent.gameObject.AddComponent<FuelNotice>();

            HorizonAssetUtility.Configure(component, serialized =>
            {
                serialized.FindProperty("panel").objectReferenceValue = notice.gameObject;
                serialized.FindProperty("label").objectReferenceValue = line;
            });

            HorizonAssetUtility.AssertReferenceAssigned(component, "panel");
            HorizonAssetUtility.AssertReferenceAssigned(component, "label");

            // Nothing to say yet.
            notice.gameObject.SetActive(false);
        }

        /// <summary>
        /// The lap readout, under the minimap.
        ///
        /// <para><b>Under the map rather than beside the rev counter, and that is not just what fits.</b>
        /// The two things a driver on a circuit looks at are which way the next corner goes and how the
        /// lap is going, and those are the same glance. The tacho corner is the other side of the
        /// screen.</para>
        ///
        /// <para>Three rows of caption and value rather than three composed lines, because
        /// <c>Label</c> stretches to its parent and a composed string is a string built every frame —
        /// see <see cref="LapTimer"/>, which is the component this exists to give somewhere to write.
        /// The panel starts switched off; a world with no circuit in it never turns it on.</para>
        /// </summary>
        private static void BuildLapTimer(Transform parent)
        {
            Sprite box = HorizonAssetUtility.LoadOrCreateUiSprite($"{SpriteFolder}/UI_Box.png");

            const float Width = 300f;
            const float RowHeight = 34f;

            RectTransform frame = Panel(parent, "LapTimer", box, PanelTint,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(Width, RowHeight * 4f + 16f), new Vector2(190f, -389f));

            Untargeted(frame, Image.Type.Sliced);

            Text Row(string caption, float y)
            {
                RectTransform left = Cell(frame, Width * 0.5f - 12f, RowHeight, -Width * 0.25f + 6f, y);
                Text name = LabelOn(left, caption, 20, GlyphTint);
                name.alignment = TextAnchor.MiddleLeft;

                RectTransform right = Cell(frame, Width * 0.5f - 12f, RowHeight, Width * 0.25f - 6f, y);
                Text value = LabelOn(right, "--:--.-", 22, GlyphTint);
                value.alignment = TextAnchor.MiddleRight;

                return value;
            }

            Text current = Row("LAP", RowHeight * 1.5f);
            Text last = Row("LAST", RowHeight * 0.5f);
            Text best = Row("BEST", -RowHeight * 0.5f);
            Text gates = Row("GATES", -RowHeight * 1.5f);

            // On the group, not on the panel it hides, for the reason BuildFuelNotice records: a
            // component that switched off its own GameObject would stop being updated at the same
            // moment and could never switch it back on.
            LapTimer component = parent.gameObject.AddComponent<LapTimer>();

            HorizonAssetUtility.Configure(component, serialized =>
            {
                serialized.FindProperty("panel").objectReferenceValue = frame.gameObject;
                serialized.FindProperty("currentLabel").objectReferenceValue = current;
                serialized.FindProperty("lastLabel").objectReferenceValue = last;
                serialized.FindProperty("bestLabel").objectReferenceValue = best;
                serialized.FindProperty("gateLabel").objectReferenceValue = gates;
            });

            HorizonAssetUtility.AssertReferenceAssigned(component, "panel");
            HorizonAssetUtility.AssertReferenceAssigned(component, "currentLabel");
            HorizonAssetUtility.AssertReferenceAssigned(component, "lastLabel");
            HorizonAssetUtility.AssertReferenceAssigned(component, "bestLabel");
            HorizonAssetUtility.AssertReferenceAssigned(component, "gateLabel");

            // Nothing to time until the car reaches a circuit.
            frame.gameObject.SetActive(false);
        }

        /// <summary>A bare rect to hang one stretched <see cref="Label"/> in.</summary>
        private static RectTransform Cell(RectTransform parent, float width, float height, float x, float y)
        {
            var go = new GameObject("Cell", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);

            return rect;
        }

        /// <summary>
        /// The minimap, in the corner the pause button used to have.
        ///
        /// <para><b>The mirror of the rev counter, and for the same reason.</b> A square readout wants
        /// two screen edges, and there are exactly two corners nothing is held in — the tacho has one
        /// and this has the other. The pause button moved 310 units right to make room; the steering
        /// wheel below tops out at about y = −710 from here, so nothing on the left is under a thumb.
        /// </para>
        ///
        /// <para><b>Round, and it clips itself.</b> There is no <c>Mask</c> here and no
        /// <c>RectMask2D</c>: <c>MapGraphic.circular</c> clips the geometry as it is generated. The
        /// long version of why is on <c>MapGraphic.AddConvex</c>; the short version is that a stencil
        /// mask would not clip in any frame this project can take, so its behaviour in a running game
        /// was going to be something taken on trust, and this is a project that photographs things
        /// instead.</para>
        ///
        /// <para>The disc behind it is still a real sprite, because it is the widget's own background —
        /// the map is drawn over it. The rim is a sibling rather than a child for the reason it always
        /// was: it edges the circle and must not be clipped to it.</para>
        ///
        /// <para><b>It is the one thing in this group that takes a tap.</b> Everything else here has
        /// <c>raycastTarget</c> off, on the argument that a readout swallowing taps is worse than one
        /// that cannot be touched. This is a readout you press.</para>
        /// </summary>
        private static Button BuildMinimap(Transform parent, Sprite ring, Sprite tick, WorldMap map)
        {
            const float MapSize = 300f;

            // cornerRadius 1 rounds a square all the way to a circle, and no hole: the dial's own ring
            // with its middle filled in. Nothing new had to be generated for this.
            Sprite disc = HorizonAssetUtility.LoadOrCreateUiSprite($"{SpriteFolder}/UI_Disc.png", 256, 1f);

            RectTransform face = Panel(parent, "Minimap", disc, PanelTint,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(MapSize, MapSize), new Vector2(190f, -180f));

            Image faceImage = face.GetComponent<Image>();

            // Simple, not Sliced: a disc has no border, and stretching one as though it had turns the
            // circle into a lozenge. The same note stands over the rev counter's ring.
            faceImage.type = Image.Type.Simple;
            faceImage.raycastTarget = true;

            RectTransform mapRect = StretchChild(face, "Map", 0f, 0f);
            GameObject mapObject = mapRect.gameObject;

            MapGraphic graphic = mapObject.AddComponent<MapGraphic>();
            graphic.raycastTarget = false;

            HorizonAssetUtility.Configure(graphic, serialized =>
            {
                serialized.FindProperty("map").objectReferenceValue = map;
                serialized.FindProperty("circular").boolValue = true;

                // 0.80 is UI_Dial.png's own hole, the ring drawn over this. One number, two places it
                // has to match; the sprite's is the one that decides.
                serialized.FindProperty("clipFraction").floatValue = 0.80f;
            });

            // North. A full-size pivot rect with a mark near its rim: turning the rect walks the mark
            // round the dial, which is the same trick the tacho's ticks use and needs no second sprite.
            var northObject = new GameObject("North", typeof(RectTransform));
            northObject.transform.SetParent(face, false);

            var north = (RectTransform)northObject.transform;
            north.anchorMin = new Vector2(0.5f, 0.5f);
            north.anchorMax = new Vector2(0.5f, 0.5f);
            north.pivot = new Vector2(0.5f, 0.5f);
            north.sizeDelta = new Vector2(MapSize, MapSize);

            RectTransform mark = Panel(north, "Mark", tick, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(17f, 30f), new Vector2(0f, MapSize * 0.42f));

            Untargeted(mark, Image.Type.Simple);

            // The car. It never moves and never turns — the world turns under it — so it is geometry
            // rather than something a component has to drive.
            //
            // Its own sprite rather than the arrows' glyph rotated ninety degrees, and taller than it is
            // wide. The glyph is a near equilateral triangle: at 34 units across, which way it points is
            // a guess, and on a heading-up minimap that is the one thing the widget is for. See
            // HorizonAssetUtility.LoadOrCreateCarMarkerSprite.
            Sprite carSprite = HorizonAssetUtility.LoadOrCreateCarMarkerSprite(
                $"{SpriteFolder}/UI_CarMarker.png");

            // Below the middle rather than on it, and Minimap.ForwardBias is the one number that says
            // how far — this places the sprite and the component pushes the view forward by the same
            // distance, so the marker still sits on the road it is drawn over. See there for why a map
            // read at a glance should not spend half of itself on the road already driven.
            RectTransform car = Panel(face, "Car", carSprite, AccentTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(30f, 36f),
                new Vector2(0f, -MapSize * 0.5f * Minimap.ForwardBias));

            Untargeted(car, Image.Type.Simple);

            // Outside the mask, so its own outer edge survives being drawn.
            RectTransform rim = Panel(parent, "MinimapRim", ring, ControlTint,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(MapSize, MapSize), new Vector2(190f, -180f));

            Untargeted(rim, Image.Type.Simple);

            Minimap minimap = face.gameObject.AddComponent<Minimap>();

            HorizonAssetUtility.Configure(minimap, serialized =>
            {
                serialized.FindProperty("graphic").objectReferenceValue = graphic;
                serialized.FindProperty("northNeedle").objectReferenceValue = north;
            });

            HorizonAssetUtility.AssertReferenceAssigned(minimap, "graphic");
            HorizonAssetUtility.AssertReferenceAssigned(graphic, "map");

            Button button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = faceImage;
            return button;
        }

        /// <summary>
        /// The fuel gauge, tucked in beside the rev counter.
        ///
        /// <para><b>Not in a corner of its own, because there is not one left.</b> The wheel owns the
        /// bottom left, the slider owns the whole right-hand column from y=300 up, the pause button sits
        /// top left, and the rev counter has the top right. So this shares the top right and reads as
        /// part of the same cluster, which is what it is.</para>
        ///
        /// <para><b>It does share a horizontal band with the brake column, and that is fine.</b> The
        /// brake sits at <see cref="InnerColumnX"/> −365 spanning x −465…−265; this dial spans −540…−370
        /// and overlaps it. They are 415 units apart vertically — this ends 265 down from the top, the
        /// brake begins 680 down. The right-hand grid's rule is about what a thumb rests on, and nothing
        /// in the top strip is under a thumb. Left here in writing so that the overlap in x is not
        /// "fixed" by somebody reading the grid's note without the heights.</para>
        ///
        /// <para>170 across against the tacho's 300: it is the secondary instrument and should look
        /// like one, and the 30-unit gap between the two rims is what stops them reading as one lozenge.
        /// On the narrowest canvas Android produces — 4:3, so 1440 units wide — its left edge sits at
        /// x=900 against a pause button ending at 145, which is 755 units of clearance.</para>
        /// </summary>
        private static void BuildFuelDial(
            Transform parent, Sprite ring, Sprite needleSprite, Sprite tickSprite)
        {
            const float fuelDialSize = 170f;

            RectTransform dial = Panel(parent, "FuelDial", ring, ControlTint,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(fuelDialSize, fuelDialSize), new Vector2(-455f, -180f));

            Untargeted(dial, Image.Type.Simple);

            RectTransform reserve = Panel(dial, "Reserve", ring, RedlineTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(fuelDialSize, fuelDialSize), Vector2.zero);

            Image reserveImage = Untargeted(reserve, Image.Type.Filled);
            reserveImage.fillMethod = Image.FillMethod.Radial360;
            reserveImage.fillOrigin = (int)Image.Origin360.Top;
            reserveImage.fillClockwise = false;
            reserveImage.fillAmount = 0f;

            // Five: E, a quarter, a half, three quarters, F. FuelGauge places them and never moves
            // them again — unlike the tacho's, they do not depend on which car is being driven.
            var tickMarks = new RectTransform[FuelDialMarks];

            for (int i = 0; i < FuelDialMarks; i++)
            {
                tickMarks[i] = Panel(dial, $"FuelTick{i}", tickSprite, GlyphTint,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(13f, 18f), Vector2.zero);

                Untargeted(tickMarks[i], Image.Type.Simple);
            }

            // Only the two ends are captioned, and they are captions rather than readouts — written
            // here, wired to nothing, never touched again. A gauge marked 0 to 60 would be a gauge that
            // had to know how big this car's tank was, which is the whole thing this dial avoids.
            CentreLabel(dial, "Empty", "E", 18, new Vector2(-52f, -34f), new Vector2(24f, 24f),
                new Color(1f, 1f, 1f, 0.75f));
            CentreLabel(dial, "Full", "F", 18, new Vector2(52f, -34f), new Vector2(24f, 24f),
                new Color(1f, 1f, 1f, 0.75f));

            RectTransform pump = Panel(dial, "Pump", Glyphs.Fuel, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(40f, 40f), new Vector2(0f, 36f));

            Image pumpImage = Untargeted(pump, Image.Type.Simple);

            RectTransform needle = Panel(dial, "FuelNeedle", needleSprite, NeedleTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(fuelDialSize, fuelDialSize), Vector2.zero);

            Untargeted(needle, Image.Type.Simple);

            FuelGauge gauge = dial.gameObject.AddComponent<FuelGauge>();

            HorizonAssetUtility.Configure(gauge, serialized =>
            {
                serialized.FindProperty("needle").objectReferenceValue = needle;
                serialized.FindProperty("reserveArc").objectReferenceValue = reserveImage;
                serialized.FindProperty("pumpGlyph").objectReferenceValue = pumpImage;

                HorizonAssetUtility.SetObjectArray(serialized, "tickMarks", tickMarks);
            });

            HorizonAssetUtility.AssertReferenceAssigned(gauge, "needle");
            HorizonAssetUtility.AssertReferenceAssigned(gauge, "reserveArc");
            HorizonAssetUtility.AssertReferenceAssigned(gauge, "pumpGlyph");
        }

        /// <summary>Marks on the fuel dial: E, a quarter, a half, three quarters, F.</summary>
        private const int FuelDialMarks = 5;

        /// <summary>How many marks the dial pool holds. The Coupe revs to 8000, so 0..8.</summary>
        private const int MaxDialMarks = 9;

        /// <summary>The red zone's colour. Warmer than a pure red, to sit with the rest of the palette.</summary>
        private static readonly Color RedlineTint = new Color(0.86f, 0.22f, 0.16f, 0.85f);

        /// <summary>
        /// The needle, in the menu's accent orange rather than white.
        ///
        /// <para>It has to be findable in a glance taken away from the road, and white on a dial whose
        /// marks are also white is the one colour that will not do that.</para>
        /// </summary>
        private static readonly Color NeedleTint = new Color(0.96f, 0.55f, 0.28f, 0.98f);

        /// <summary>Sets an Image's draw type and takes it out of the raycast, which is what a readout wants.</summary>
        private static Image Untargeted(RectTransform rect, Image.Type type)
        {
            Image image = rect.GetComponent<Image>();
            image.type = type;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A label stretched over a rect of its own, tinted.</summary>
        private static Text LabelOn(RectTransform parent, string caption, int fontSize, Color colour)
        {
            Text text = Label(parent, caption, fontSize);
            text.color = colour;
            return text;
        }

        /// <summary>One of the readouts down the middle of the dial.</summary>
        private static Text CentreLabel(
            RectTransform parent, string name, string caption, int fontSize,
            Vector2 position, Vector2 size, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            return LabelOn(rect, caption, fontSize, colour);
        }

        // --- Small builders.

        private static GameObject Group(RectTransform parent, string name)
        {
            var group = new GameObject(name, typeof(RectTransform));
            group.transform.SetParent(parent, false);
            Stretch((RectTransform)group.transform);
            return group;
        }

        private static void HoldButton(
            Transform parent,
            string name,
            Sprite box,
            TouchHoldButton.Action action,
            Sprite glyph,
            Vector2 anchor,
            Vector2 position,
            Vector2? size = null)
        {
            RectTransform rect = Panel(parent, name, box, ControlTint, anchor, anchor,
                size ?? new Vector2(ButtonSize, ButtonSize), position);

            // The symbol sits inside the button as a child, so the button's own tint can flash on press
            // without the symbol flashing with it.
            RectTransform icon = Panel(rect, "Icon", glyph, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                (size ?? new Vector2(ButtonSize, ButtonSize)) * 0.55f, Vector2.zero);

            icon.GetComponent<Image>().type = Image.Type.Simple;
            icon.GetComponent<Image>().raycastTarget = false;

            TouchHoldButton hold = rect.gameObject.AddComponent<TouchHoldButton>();
            Image image = rect.GetComponent<Image>();

            HorizonAssetUtility.Configure(hold, serialized =>
            {
                serialized.FindProperty("action").enumValueIndex = (int)action;
                serialized.FindProperty("highlight").objectReferenceValue = image;
            });
        }

        /// <summary>Height of a tappable menu row. Comfortably over the 9 mm a fingertip needs.</summary>
        internal const float MenuRowHeight = 96f;

        /// <summary>Gap between menu rows. Shared, so a scrolling list is pitched like the page it is in.</summary>
        internal const float MenuRowSpacing = 22f;

        internal static Button MenuButton(RectTransform parent, string name, Sprite box, string caption)
        {
            RectTransform rect = Panel(parent, name, box, ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(560f, MenuRowHeight), Vector2.zero);

            Label(rect, caption, 32);
            Row(rect.gameObject, MenuRowHeight);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            return button;
        }

        /// <summary>
        /// How many rows a scrolling list shows before it starts scrolling.
        ///
        /// <para>Five, and the constraint is the canvas rather than taste. A page is a title, a list and
        /// a Back button inside 46 of padding at each end, on a 1080-unit canvas that a phone's safe
        /// area then eats into — call it a thousand usable. Five rows put the place page at 860. Six
        /// put it at 978, which does fit, and which leaves it the same dozen units of margin the front
        /// page has — on the one page whose length grows every time a road is built.</para>
        /// </summary>
        internal const int VisibleRows = 5;

        /// <summary>
        /// A list inside a menu page that scrolls when it is longer than the page.
        ///
        /// <para><b>Because a page whose length is decided by the world will outgrow the screen.</b>
        /// <see cref="StackPanel"/> makes a panel exactly as tall as its rows, which is right while the
        /// rows are a fixed set of settings and wrong for a list that grows every time a leg of road is
        /// added: at ten start points the place page stood 1450 units tall on a 1080 canvas, so its
        /// first and last rows were simply off the screen with no way to reach them.</para>
        ///
        /// <para><b>The title and the Back button stay outside it.</b> They belong to the page rather
        /// than to the list, and a Back button that scrolls away is the same bug in a politer form.</para>
        ///
        /// <para>Returns the content transform — rows go into that, not into the viewport. The viewport
        /// is what gets the mask and the fixed height; the content is what moves under it.</para>
        /// </summary>
        /// <param name="rows">How many rows the list will hold, so the viewport can be short if it is.</param>
        internal static RectTransform ScrollList(RectTransform parent, string name, int rows)
        {
            var viewportObject = new GameObject(name, typeof(RectTransform));
            viewportObject.transform.SetParent(parent, false);

            var viewport = (RectTransform)viewportObject.transform;
            viewport.anchorMin = new Vector2(0.5f, 0.5f);
            viewport.anchorMax = new Vector2(0.5f, 0.5f);
            viewport.pivot = new Vector2(0.5f, 0.5f);

            int shown = Mathf.Clamp(rows, 1, VisibleRows);
            float height = shown * MenuRowHeight + (shown - 1) * MenuRowSpacing;

            viewport.sizeDelta = new Vector2(0f, height);
            Row(viewportObject, height);

            // RectMask2D rather than Mask: it needs no stencil buffer and no material of its own, which
            // on a tile GPU is the difference between a clipped list and an extra full-screen pass.
            viewportObject.AddComponent<RectMask2D>();

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);

            var content = (RectTransform)contentObject.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            var layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = MenuRowSpacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = viewportObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;

            // Clamped, not elastic: this is a list of seven things, and a rubber-band overshoot on a
            // list that short reads as the panel coming loose.
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.135f;

            // A row is 96 units tall, so a wheel notch moving one row is the readable step. Touch drags
            // do not use this — they move the content one-to-one with the finger.
            scroll.scrollSensitivity = MenuRowHeight;

            return content;
        }

        internal static Text MenuLabel(RectTransform parent, string caption, int fontSize, float height)
        {
            Text text = Label(parent, caption, fontSize);
            Row(text.gameObject, height);
            return text;
        }

        /// <summary>
        /// Fixes a row's height for the layout group. Without one the group asks the row how tall it
        /// wants to be, and an Image has no opinion — every row collapses to nothing.
        /// </summary>
        internal static void Row(GameObject go, float height)
        {
            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            element.flexibleHeight = 0f;
        }

        /// <summary>A child filling its parent, inset by <paramref name="x"/> and <paramref name="y"/>.</summary>
        internal static RectTransform StretchChild(RectTransform parent, string name, float x, float y)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(x, y);
            rect.offsetMax = new Vector2(-x, -y);

            return rect;
        }

        internal static RectTransform Panel(
            Transform parent,
            string name,
            Sprite sprite,
            Color tint,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            Image image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = tint;
            image.type = Image.Type.Sliced;

            return rect;
        }

        /// <summary>
        /// Always stretched to its parent. It used to take an offset and force a 760-unit width with it,
        /// which is wider than the 720-wide panel the captions were being put in — so the text was
        /// centred on something larger than the box around it.
        /// </summary>
        internal static Text Label(RectTransform parent, string caption, int fontSize)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            Stretch((RectTransform)go.transform);

            Text text = go.AddComponent<Text>();
            text.text = caption;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = fontSize;
            text.color = Color.white;

            // The built-in font, so no font asset has to be imported or kept.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.raycastTarget = false;

            return text;
        }

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
