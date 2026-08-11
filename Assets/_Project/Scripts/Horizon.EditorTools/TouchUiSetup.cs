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
                out Text schemeLabel, out Slider tiltSlider, out GameObject recalibrate,
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
                serialized.FindProperty("tiltRangeSlider").objectReferenceValue = tiltSlider;
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
                tiltSlider.onValueChanged,
                new UnityEngine.Events.UnityAction<float>(menu.OnTiltRangeChanged));

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

            RectTransform rect = Panel(group.transform, "Rim", ring, ControlTint,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(280f, 280f),
                new Vector2(210f, 190f));

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
                new Vector2(0f, 0f), new Vector2(130f, 160f), new Vector2(190f, 190f));
            HoldButton(group.transform, "Right", box, TouchHoldButton.Action.SteerRight, Glyphs.Right,
                new Vector2(0f, 0f), new Vector2(340f, 160f), new Vector2(190f, 190f));

            return group;
        }

        private static GameObject BuildPedals(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Pedals");

            HoldButton(group.transform, "Throttle", box, TouchHoldButton.Action.Throttle, Glyphs.Throttle,
                new Vector2(1f, 0f), new Vector2(-140f, 250f), new Vector2(190f, 190f));
            HoldButton(group.transform, "Brake", box, TouchHoldButton.Action.Brake, Glyphs.Brake,
                new Vector2(1f, 0f), new Vector2(-340f, 160f), new Vector2(190f, 190f));

            return group;
        }

        private static GameObject BuildAutoPedals(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "AutoPedals");

            HoldButton(group.transform, "Brake", box, TouchHoldButton.Action.Brake, Glyphs.Brake,
                new Vector2(1f, 0f), new Vector2(-140f, 250f), new Vector2(190f, 190f));

            return group;
        }

        private static GameObject BuildSlider(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Slider");

            RectTransform track = Panel(group.transform, "Track", box, ControlTint,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(120f, 420f),
                new Vector2(-140f, 250f));

            RectTransform knob = Panel(track, "Knob", box, AccentTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(110f, 90f), Vector2.zero);

            TouchThrottleSlider throttle = track.gameObject.AddComponent<TouchThrottleSlider>();
            HorizonAssetUtility.Configure(throttle, serialized =>
                serialized.FindProperty("knob").objectReferenceValue = knob);

            return group;
        }

        private static GameObject BuildHandbrake(RectTransform parent, Sprite box)
        {
            GameObject group = Group(parent, "Handbrake");

            HoldButton(group.transform, "Handbrake", box, TouchHoldButton.Action.Handbrake, Glyphs.Handbrake,
                new Vector2(1f, 0f), new Vector2(-140f, 80f), new Vector2(190f, 120f));

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

        private static GameObject BuildMenuPanel(
            RectTransform parent,
            Sprite box,
            out GameObject settingsPanel,
            out Text schemeLabel,
            out Slider tiltSlider,
            out GameObject recalibrate,
            out Button resume,
            out Button openSettings,
            out Button closeSettings,
            out Button cycleSteering,
            out Button cyclePedals,
            out Button respawn)
        {
            RectTransform panel = Panel(parent, "PauseMenu", box, PanelTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(720f, 620f), Vector2.zero);

            Label(panel, "PAUSED", 48, new Vector2(0f, 240f));

            resume = MenuButton(panel, "Resume", box, "Resume", 130f);
            openSettings = MenuButton(panel, "Controls", box, "Controls", 10f);
            respawn = MenuButton(panel, "Respawn", box, "Put the car back", -110f);

            // --- Settings, a second panel over the first.
            RectTransform settings = Panel(parent, "SettingsPanel", box, PanelTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(860f, 700f), Vector2.zero);

            Label(settings, "CONTROLS", 44, new Vector2(0f, 280f));
            schemeLabel = Label(settings, "—", 30, new Vector2(0f, 215f));

            cycleSteering = MenuButton(settings, "Steering", box, "Steering: change", 110f);
            cyclePedals = MenuButton(settings, "Throttle", box, "Throttle: change", 0f);

            Button recalibrateButton = MenuButton(settings, "Recalibrate", box, "Recalibrate tilt", -110f);
            recalibrate = recalibrateButton.gameObject;

            Label(settings, "Tilt sensitivity", 26, new Vector2(0f, -190f));
            tiltSlider = BuildTiltSlider(settings, box);

            closeSettings = MenuButton(settings, "Back", box, "Back", -300f);

            settingsPanel = settings.gameObject;
            return panel.gameObject;
        }

        private static Slider BuildTiltSlider(RectTransform parent, Sprite box)
        {
            RectTransform rect = Panel(parent, "TiltRange", box, ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(520f, 50f),
                new Vector2(0f, -235f));

            RectTransform fillArea = Child(rect, "Fill Area", new Vector2(520f, 30f), Vector2.zero);
            RectTransform fill = Panel(fillArea, "Fill", box, AccentTint,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(260f, 30f), Vector2.zero);

            RectTransform handleArea = Child(rect, "Handle Slide Area", new Vector2(520f, 50f), Vector2.zero);
            RectTransform handle = Panel(handleArea, "Handle", box, Color.white,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(50f, 50f), Vector2.zero);

            Slider slider = rect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();

            // Degrees of roll for full lock, now that the tilt reads a real angle rather than a raw
            // sensor component. Below about 12° full lock is a twitch; above about 40° you cannot reach
            // it sitting down holding a phone.
            slider.minValue = 12f;
            slider.maxValue = 40f;
            slider.value = 22f;

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

        private static Button MenuButton(
            RectTransform parent, string name, Sprite box, string caption, float y)
        {
            RectTransform rect = Panel(parent, name, box, ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(560f, 96f),
                new Vector2(0f, y));

            Label(rect, caption, 32);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            return button;
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

        private static RectTransform Child(RectTransform parent, string name, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            return rect;
        }

        private static Text Label(RectTransform parent, string caption, int fontSize, Vector2? offset = null)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;

            if (offset.HasValue)
            {
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(760f, 60f);
                rect.anchoredPosition = offset.Value;
            }
            else
            {
                Stretch(rect);
            }

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
