using Horizon.Game;
using Horizon.Input;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Builds the on-screen controls and the pause menu into the Bootstrap scene.
    ///
    /// <para>From code, like everything else here, and for the stated reason: a canvas laid out by hand
    /// is a canvas that lives only in a scene file nobody can review. It is also the only way this can
    /// be rebuilt from scratch — the Bootstrap scene is generated output, so a hand-placed button would
    /// vanish the next time anybody ran <c>Rebuild Prototype Scene</c>.</para>
    ///
    /// <para>Everything is anchored to a screen corner rather than positioned absolutely, and the
    /// controls sit inside a safe-area panel — a notch or a gesture bar otherwise lands on top of the
    /// handbrake, and on the phone it was tested on there is one at each end.</para>
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
        }

        private static Glyphset Glyphs;

        private static readonly Color ControlTint = new Color(1f, 1f, 1f, 0.30f);
        private static readonly Color PanelTint = new Color(0.05f, 0.06f, 0.08f, 0.88f);
        private static readonly Color GlyphTint = new Color(1f, 1f, 1f, 0.92f);

        private static readonly Color AccentTint = new Color(0.86f, 0.36f, 0.17f, 0.92f);

        /// <summary>
        /// Creates the canvas, the driving controls and the menu, and wires them to the router.
        /// </summary>
        public static void Build(GameObject root, DriveInputRouter router)
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

            // --- Menu.
            GameObject pauseButton = BuildPauseButton(safe, box);
            GameObject menuPanel = BuildMenuPanel(safe, box, out GameObject settingsPanel,
                out Text schemeLabel, out Slider sensitivity, out GameObject recalibrate,
                out Button resume, out Button openSettings, out Button closeSettings,
                out Button cycleSteering, out Button cyclePedals, out Button respawn);

            TouchControlsHud hud = canvas.gameObject.AddComponent<TouchControlsHud>();
            PauseMenu menu = canvas.gameObject.AddComponent<PauseMenu>();

            HorizonAssetUtility.Configure(hud, serialized =>
            {
                serialized.FindProperty("router").objectReferenceValue = router;
                serialized.FindProperty("wheel").objectReferenceValue = wheel;
                serialized.FindProperty("arrows").objectReferenceValue = arrows;
                serialized.FindProperty("pedals").objectReferenceValue = pedals;
                serialized.FindProperty("slider").objectReferenceValue = slider;
                serialized.FindProperty("autoPedals").objectReferenceValue = autoPedals;
                serialized.FindProperty("handbrake").objectReferenceValue = handbrake;
            });

            HorizonAssetUtility.Configure(menu, serialized =>
            {
                serialized.FindProperty("router").objectReferenceValue = router;
                serialized.FindProperty("hud").objectReferenceValue = hud;
                serialized.FindProperty("menuPanel").objectReferenceValue = menuPanel;
                serialized.FindProperty("settingsPanel").objectReferenceValue = settingsPanel;
                serialized.FindProperty("pauseButton").objectReferenceValue = pauseButton;
                serialized.FindProperty("schemeLabel").objectReferenceValue = schemeLabel;
                serialized.FindProperty("sensitivitySlider").objectReferenceValue = sensitivity;
                serialized.FindProperty("recalibrateButton").objectReferenceValue = recalibrate;
            });

            // Persistent listeners, so the wiring is saved into the scene rather than being rebuilt at
            // run time — the same reason everything else here goes through SerializedObject.
            Bind(pauseButton.GetComponent<Button>(), menu, nameof(PauseMenu.Toggle));
            Bind(resume, menu, nameof(PauseMenu.Resume));
            Bind(openSettings, menu, nameof(PauseMenu.OpenSettings));
            Bind(closeSettings, menu, nameof(PauseMenu.CloseSettings));
            Bind(cycleSteering, menu, nameof(PauseMenu.CycleSteering));
            Bind(cyclePedals, menu, nameof(PauseMenu.CyclePedals));
            Bind(recalibrate.GetComponent<Button>(), menu, nameof(PauseMenu.RecalibrateTilt));
            Bind(respawn, menu, nameof(PauseMenu.Respawn));

            UnityEditor.Events.UnityEventTools.AddPersistentListener(
                sensitivity.onValueChanged,
                new UnityEngine.Events.UnityAction<float>(menu.OnSteerSensitivityChanged));

            menuPanel.SetActive(false);
            settingsPanel.SetActive(false);
        }

        private static void Bind(Button button, PauseMenu menu, string method)
        {
            var call = System.Delegate.CreateDelegate(
                typeof(UnityEngine.Events.UnityAction), menu, method) as UnityEngine.Events.UnityAction;

            UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick, call);
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
            RectTransform rect = Panel(parent, "PauseButton", box, ControlTint,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, 110f),
                new Vector2(90f, -80f));

            RectTransform icon = Panel(rect, "Icon", Glyphs.Pause, GlyphTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(52f, 52f), Vector2.zero);
            icon.GetComponent<Image>().type = Image.Type.Simple;
            icon.GetComponent<Image>().raycastTarget = false;

            rect.gameObject.AddComponent<Button>().targetGraphic = rect.GetComponent<Image>();

            return rect.gameObject;
        }

        /// <summary>
        /// Both menu panels, stacked by a layout group rather than by hand-placed offsets.
        ///
        /// <para>The offsets were wrong, and not by accident — they cannot be kept right. Every row
        /// carried an absolute y, so the panel's height, the gap between rows and the position of the
        /// last row were three independent numbers that had to agree, and they did not: "Back" sat on
        /// top of the sensitivity slider, and its label on top of that. Worse, a row that hides leaves
        /// its gap behind, which is what the orphaned sensitivity control looked like. A vertical layout
        /// group and a content-size fitter make the height a consequence of the rows instead of a fourth
        /// number to keep in step, so hiding a row closes its gap and the panel shrinks to fit.</para>
        /// </summary>
        private static GameObject BuildMenuPanel(
            RectTransform parent,
            Sprite box,
            out GameObject settingsPanel,
            out Text schemeLabel,
            out Slider sensitivity,
            out GameObject recalibrate,
            out Button resume,
            out Button openSettings,
            out Button closeSettings,
            out Button cycleSteering,
            out Button cyclePedals,
            out Button respawn)
        {
            RectTransform panel = StackPanel(parent, "PauseMenu", box, 720f);

            MenuLabel(panel, "PAUSED", 48, 66f);

            resume = MenuButton(panel, "Resume", box, "Resume");
            openSettings = MenuButton(panel, "Controls", box, "Controls");
            respawn = MenuButton(panel, "Respawn", box, "Put the car back");

            // --- Settings, a second panel over the first.
            RectTransform settings = StackPanel(parent, "SettingsPanel", box, 860f);

            MenuLabel(settings, "CONTROLS", 44, 60f);
            schemeLabel = MenuLabel(settings, "—", 30, 42f);

            cycleSteering = MenuButton(settings, "Steering", box, "Steering: change");
            cyclePedals = MenuButton(settings, "Throttle", box, "Throttle: change");

            // Hidden outside the tilt scheme, and the only row that is — everything below applies to
            // whatever the player is steering with. The layout group closes the gap when it goes.
            Button recalibrateButton = MenuButton(settings, "Recalibrate", box, "Recalibrate tilt");
            recalibrate = recalibrateButton.gameObject;

            MenuLabel(settings, "Steering sensitivity", 26, 38f);
            sensitivity = BuildSensitivitySlider(settings, box);

            closeSettings = MenuButton(settings, "Back", box, "Back");

            settingsPanel = settings.gameObject;
            return panel.gameObject;
        }

        /// <summary>
        /// A panel whose height is whatever its rows add up to. See <see cref="BuildMenuPanel"/> for
        /// why that is not the same panel with a nicer implementation.
        /// </summary>
        private static RectTransform StackPanel(RectTransform parent, string name, Sprite box, float width)
        {
            RectTransform panel = Panel(parent, name, box, PanelTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(width, 0f), Vector2.zero);

            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(60, 60, 46, 46);
            layout.spacing = 22f;
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
        /// The steering sensitivity, 0 to 1, for whichever scheme is active — see
        /// <c>TouchControlState.SteerSensitivity01</c> for what each one does with it.
        ///
        /// <para>Normalised rather than in the units of one scheme, which is what it used to be: it read
        /// 12 to 40 because it was degrees of phone roll, and it sat in the menu saying "tilt
        /// sensitivity" while the player was steering with a wheel. One question, asked once, in units
        /// that survive changing your mind about how to steer.</para>
        /// </summary>
        private static Slider BuildSensitivitySlider(RectTransform parent, Sprite box)
        {
            RectTransform rect = Panel(parent, "Sensitivity", box, ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(520f, 50f), Vector2.zero);
            Row(rect.gameObject, 50f);

            // Stretched rather than sized: the layout group decides the slider's width, so anything
            // inside it with a fixed width would be right at one panel width and wrong at every other.
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

            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = TouchControlState.DefaultSensitivity;

            return slider;
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
        private const float MenuRowHeight = 96f;

        private static Button MenuButton(RectTransform parent, string name, Sprite box, string caption)
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

        private static Text MenuLabel(RectTransform parent, string caption, int fontSize, float height)
        {
            Text text = Label(parent, caption, fontSize);
            Row(text.gameObject, height);
            return text;
        }

        /// <summary>
        /// Fixes a row's height for the layout group. Without one the group asks the row how tall it
        /// wants to be, and an Image has no opinion — every row collapses to nothing.
        /// </summary>
        private static void Row(GameObject go, float height)
        {
            var element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            element.flexibleHeight = 0f;
        }

        /// <summary>A child filling its parent, inset by <paramref name="x"/> and <paramref name="y"/>.</summary>
        private static RectTransform StretchChild(RectTransform parent, string name, float x, float y)
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

        private static RectTransform Panel(
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
        private static Text Label(RectTransform parent, string caption, int fontSize)
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

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
