using System.Collections.Generic;
using Horizon.Game;
using Horizon.Input;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Builds every menu page: the start screen, the garage, the paint shop, the places, the
    /// conditions, the controls, the quality setting and the pause menu.
    ///
    /// <para><b>Pages are registered in order and the order is checked.</b> Which page a button opens is
    /// baked into its saved UnityEvent as a bare integer — see <c>MenuPage</c> — so the array
    /// <c>MenuPanels</c> holds and the enum the buttons carry have to agree exactly. Rather than trust
    /// that, <see cref="Register"/> asserts it as each page goes in, so inserting a page in the middle
    /// fails here at build time instead of quietly sending the player to the wrong screen.</para>
    ///
    /// <para>Every page is a <c>StackPanel</c> — a vertical layout group with a content-size fitter — so
    /// a page is a list of rows and its height is whatever those rows come to. Nothing on any of them
    /// carries a coordinate.</para>
    /// </summary>
    public static class MenuUiSetup
    {
        private const string SpriteFolder = "Assets/_Project/Art/UI";

        /// <summary>Panel width for the pages that are lists of buttons.</summary>
        private const float PanelWidth = 860f;

        /// <summary>Tall enough that a car is legible rather than a smear. The rows are pictures.</summary>
        /// <summary>
        /// Height of one car row.
        ///
        /// <para>Was 150 while there were five cars. Ten of those would stand the garage page 1990 px
        /// tall against a reference height of 1080, so the page went to two columns and the rows came
        /// down to fit: <c>92 padding + 60 title + 5×120 + 96 back + 6×22 spacing = 980</c>.</para>
        ///
        /// <para>It costs something and it is worth naming. The thumbnails are 512×256 drawn with
        /// <c>preserveAspect</c>, so a 300×120 slot renders them at 240×120 rather than 300×150 — a
        /// fifth smaller. 120 is still well clear of the 96 this file calls a dark smudge, and there is
        /// no arrangement of ten of these that fits on a phone without either this or a scroll view.</para>
        /// </summary>
        private const float CarRowHeight = 120f;

        /// <summary>
        /// The garage is the one page wider than <see cref="PanelWidth"/>, because it is the one page
        /// with two columns. 1320 leaves 1200 inside the padding — two 590 cells, which is enough for a
        /// full-width thumbnail beside its name — and still sits well inside a 1920 reference width.
        /// </summary>
        private const float GaragePanelWidth = 1320f;

        /// <summary>
        /// Builds the whole menu and wires it. Returns the components the scene builder still has to
        /// finish off — the spawn table, the quality levels and the bootstrap references.
        /// </summary>
        public static TouchUiSetup.UiParts Build(
            Canvas canvas,
            RectTransform safe,
            Sprite box,
            DriveInputRouter router,
            TouchControlsHud hud,
            GameObject pauseButton,
            IReadOnlyList<string> spawnNames)
        {
            var panelList = new List<GameObject>();

            GameObject backdrop = BuildBackdrop(canvas);

            PauseMenu menu = canvas.gameObject.AddComponent<PauseMenu>();
            MenuPanels panels = canvas.gameObject.AddComponent<MenuPanels>();
            StartScreen start = canvas.gameObject.AddComponent<StartScreen>();
            UpdateScreen updates = canvas.gameObject.AddComponent<UpdateScreen>();

            // All three on the canvas object, which is what makes the Awake/Start ordering in
            // StartScreen safe to rely on — see the note there.
            StartPage startPage = BuildStartPage(safe, box);
            Register(panelList, MenuPage.Start, startPage.Panel);

            GaragePage garage = BuildGaragePage(safe, box);
            Register(panelList, MenuPage.Garage, garage.Panel);

            PaintPage paint = BuildPaintPage(safe, box);
            Register(panelList, MenuPage.Paint, paint.Panel);

            PlacePage place = BuildPlacePage(safe, box, spawnNames);
            Register(panelList, MenuPage.Place, place.Panel);

            ConditionsPage conditions = BuildConditionsPage(safe, box);
            Register(panelList, MenuPage.Conditions, conditions.Panel);

            ControlsPage controls = BuildControlsPage(safe, box);
            Register(panelList, MenuPage.Controls, controls.Panel);

            QualityPage quality = BuildQualityPage(safe, box);
            Register(panelList, MenuPage.Quality, quality.Panel);

            PausedPage paused = BuildPausedPage(safe, box);
            Register(panelList, MenuPage.Paused, paused.Panel);

            UpdatePage update = BuildUpdatePage(safe, box);
            Register(panelList, MenuPage.Update, update.Panel);

            HorizonAssetUtility.Configure(panels, serialized =>
                HorizonAssetUtility.SetObjectArray(serialized, "panels", panelList.ToArray()));

            HorizonAssetUtility.Configure(menu, serialized =>
            {
                serialized.FindProperty("router").objectReferenceValue = router;
                serialized.FindProperty("hud").objectReferenceValue = hud;
                serialized.FindProperty("panels").objectReferenceValue = panels;
                serialized.FindProperty("pauseButton").objectReferenceValue = pauseButton;
                serialized.FindProperty("startScreen").objectReferenceValue = start;
                serialized.FindProperty("schemeLabel").objectReferenceValue = controls.SchemeLabel;
                serialized.FindProperty("sensitivitySlider").objectReferenceValue = controls.Sensitivity;
                serialized.FindProperty("recalibrateButton").objectReferenceValue = controls.Recalibrate;
                serialized.FindProperty("timeSlider").objectReferenceValue = conditions.TimeSlider;
                serialized.FindProperty("timeLabel").objectReferenceValue = conditions.TimeLabel;
            });

            HorizonAssetUtility.Configure(start, serialized =>
            {
                serialized.FindProperty("pauseMenu").objectReferenceValue = menu;
                serialized.FindProperty("panels").objectReferenceValue = panels;
                serialized.FindProperty("backdrop").objectReferenceValue = backdrop;

                HorizonAssetUtility.SetObjectArray(serialized, "carRows", garage.Backgrounds);
                // Backgrounds, not Swatches. StartScreen.paintSwatches is an Image[], and handing a
                // SerializedProperty a Button where it wants an Image is not an error — objectReferenceValue
                // simply refuses the type and writes null, so every swatch loses its selection tick and
                // nothing anywhere says so. The other four arrays below are Image[] for the same reason.
                HorizonAssetUtility.SetObjectArray(serialized, "paintSwatches", paint.Backgrounds);
                HorizonAssetUtility.SetObjectArray(serialized, "placeRows", place.Backgrounds);
                HorizonAssetUtility.SetObjectArray(serialized, "weatherRows", conditions.WeatherBackgrounds);
                HorizonAssetUtility.SetObjectArray(serialized, "qualityRows", quality.Backgrounds);

                serialized.FindProperty("carLabel").objectReferenceValue = startPage.CarLabel;
                serialized.FindProperty("paintLabel").objectReferenceValue = startPage.PaintLabel;
                serialized.FindProperty("placeLabel").objectReferenceValue = startPage.PlaceLabel;
                serialized.FindProperty("weatherLabel").objectReferenceValue = startPage.WeatherLabel;
                serialized.FindProperty("qualityLabel").objectReferenceValue = startPage.QualityLabel;
            });

            HorizonAssetUtility.Configure(updates, serialized =>
            {
                serialized.FindProperty("summaryLabel").objectReferenceValue = startPage.UpdateLabel;
                serialized.FindProperty("statusLabel").objectReferenceValue = update.Status;
                serialized.FindProperty("notesLabel").objectReferenceValue = update.Notes;
                serialized.FindProperty("downloadButton").objectReferenceValue = update.Download;
                serialized.FindProperty("downloadLabel").objectReferenceValue = update.DownloadLabel;
            });

            WireStartPage(startPage, start, panels);
            WireGarage(garage, start, panels);
            WirePaint(paint, start, panels);
            WirePlace(place, start, menu, panels);
            WireConditions(conditions, start, menu, panels);
            WireControls(controls, menu, panels);
            WireQuality(quality, start, panels);
            WirePaused(paused, menu, panels, pauseButton);
            WireUpdate(update, updates, panels);

            // Everything starts hidden. StartScreen shows its own first page in Start().
            for (int i = 0; i < panelList.Count; i++)
            {
                panelList[i].SetActive(false);
            }

            return new TouchUiSetup.UiParts
            {
                Hud = hud,
                Menu = menu,
                Panels = panels,
                StartScreen = start,
            };
        }

        /// <summary>
        /// Adds a page and checks it landed at the index its enum value says it should.
        ///
        /// <para>The one thing that cannot be allowed to drift silently. Every navigation button in this
        /// file bakes an integer into a saved event; if the array and the enum disagree by one, every
        /// button in the menu opens the page next to the one it names, and nothing anywhere reports an
        /// error.</para>
        /// </summary>
        private static void Register(List<GameObject> panels, MenuPage page, RectTransform panel)
        {
            if (panels.Count != (int)page)
            {
                Debug.LogError($"[Horizon] Menu page '{page}' is being registered at index "
                               + $"{panels.Count} but its enum value is {(int)page}. The pages must be "
                               + "built in MenuPage order, or every navigation button opens the wrong "
                               + "screen.");
            }

            panels.Add(panel.gameObject);
        }

        /// <summary>
        /// The opaque sheet the start screen sits on, so the menu is its own screen rather than a panel
        /// floating over a frozen road.
        ///
        /// <para><b>First sibling, deliberately.</b> uGUI draws canvas children in hierarchy order, and
        /// this is created after the safe area that holds every page — so left where it lands it would
        /// cover the entire menu. <c>SetAsFirstSibling</c> puts it behind everything on the canvas while
        /// still being in front of the world, which is exactly the layer it wants.</para>
        ///
        /// <para>Stretched to the canvas rather than to the safe area: a notch is still part of the
        /// screen, and a backdrop that respected the safe inset would leave a strip of road showing
        /// along the top of the phone.</para>
        ///
        /// <para>It also swallows taps. An opaque sheet with <c>raycastTarget</c> left on means a finger
        /// that misses a button cannot reach a driving control underneath it — which, with the world
        /// paused and the HUD hidden, would otherwise be a press queued up against the moment the game
        /// starts.</para>
        /// </summary>
        private static GameObject BuildBackdrop(Canvas canvas)
        {
            var go = new GameObject("Backdrop", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);

            TouchUiSetup.Stretch((RectTransform)go.transform);

            Image image = go.AddComponent<Image>();

            // The panel colour at full opacity, so the menu reads as one surface rather than as a dark
            // box on a slightly different dark box.
            image.color = new Color(0.05f, 0.06f, 0.08f, 1f);
            image.raycastTarget = true;

            go.transform.SetAsFirstSibling();
            return go;
        }

        // --- The pages.

        private sealed class StartPage
        {
            public RectTransform Panel;
            public Button Drive;
            public Button Garage;
            public Button Paint;
            public Button Place;
            public Button Conditions;
            public Button Controls;
            public Button Quality;
            public Button Update;

            public Text CarLabel;
            public Text PaintLabel;
            public Text PlaceLabel;
            public Text WeatherLabel;
            public Text QualityLabel;
            public Text UpdateLabel;
        }

        /// <summary>
        /// The front page: what is currently chosen, a way into each choice, and the button that starts
        /// the game.
        ///
        /// <para>Drive sits at the top rather than the bottom. It is the only thing on this screen that
        /// somebody opening the game for the twentieth time wants, and making them read past six rows of
        /// settings to reach it is making them pay for a menu they are not using.</para>
        /// </summary>
        private static StartPage BuildStartPage(RectTransform parent, Sprite box)
        {
            var page = new StartPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "StartPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "HORIZON", 52, 70f);

            page.Drive = TouchUiSetup.MenuButton(page.Panel, "Drive", box, "Drive");
            Accent(page.Drive);

            page.Garage = SummaryRow(page.Panel, box, "Car", out page.CarLabel);
            page.Paint = SummaryRow(page.Panel, box, "Paint", out page.PaintLabel);
            page.Place = SummaryRow(page.Panel, box, "Start", out page.PlaceLabel);
            page.Conditions = SummaryRow(page.Panel, box, "Weather", out page.WeatherLabel);
            page.Quality = SummaryRow(page.Panel, box, "Quality", out page.QualityLabel);

            // Side by side rather than one under the other, and that is arithmetic rather than taste.
            // The page already stands 988 units tall against a reference height of 1080 — title, Drive,
            // five summary rows and this — so a ninth full-height row would hang off the bottom of the
            // screen, and further off it on a phone with a notch eating into the safe area.
            Button[] bottom = ButtonPair(page.Panel, box, "Controls", "Controls", "Update", "Version");
            page.Controls = bottom[0];
            page.Update = bottom[1];
            page.UpdateLabel = page.Update.GetComponentInChildren<Text>();

            return page;
        }

        private sealed class GaragePage
        {
            public RectTransform Panel;
            public Button[] Rows;
            public Image[] Backgrounds;
            public Button Back;
        }

        /// <summary>
        /// One row per body, each with a rendered side view of the car it selects, two to a line.
        ///
        /// <para>The thumbnails come from <c>CarPreviewRenderer.RenderUiThumbnails</c>, which runs
        /// earlier in the same rebuild. A missing sprite leaves the row as its name alone, which is
        /// ugly but usable — worth saying, because the first rebuild after a clone renders them and the
        /// import is what makes them appear.</para>
        ///
        /// <para><b>Two columns, in the shape <see cref="BuildPaintPage"/> already uses.</b> The page is
        /// a vertical layout with a content-size fitter and no scroll view, so its height is simply the
        /// sum of its rows — ten full-width rows would be nearly twice the screen and the ends of the
        /// list would be unreachable. Pairing them halves that, and a scroll view would mean a drag
        /// gesture competing with a tap on every row, on a page whose rows are the only thing on it.
        /// If the garage ever outgrows two columns of five, that is the moment for the scroll view, not
        /// a third column: a car at a third of 1200 px is a smudge again.</para>
        /// </summary>
        private static GaragePage BuildGaragePage(RectTransform parent, Sprite box)
        {
            var page = new GaragePage();
            page.Panel = TouchUiSetup.StackPanel(parent, "GaragePanel", box, GaragePanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "CAR", 44, 60f);

            CarMeshBuilder.CarProfile[] profiles = CarMeshBuilder.PlayerProfiles;
            page.Rows = new Button[profiles.Length];
            page.Backgrounds = new Image[profiles.Length];

            const int perRow = 2;
            for (int start = 0; start < profiles.Length; start += perRow)
            {
                int count = Mathf.Min(perRow, profiles.Length - start);

                var lineObject = new GameObject($"Cars{start}", typeof(RectTransform));
                lineObject.transform.SetParent(page.Panel, false);
                TouchUiSetup.Row(lineObject, CarRowHeight);

                var line = lineObject.AddComponent<HorizontalLayoutGroup>();
                line.spacing = 20f;
                line.childAlignment = TextAnchor.MiddleCenter;
                line.childControlWidth = true;
                line.childControlHeight = true;
                line.childForceExpandWidth = true;
                line.childForceExpandHeight = true;

                for (int i = 0; i < count; i++)
                {
                    int index = start + i;
                    Sprite thumb = AssetDatabase.LoadAssetAtPath<Sprite>(
                        $"{SpriteFolder}/CarThumb_{profiles[index].Name}.png");

                    page.Rows[index] = CarRow(
                        (RectTransform)lineObject.transform, box, thumb,
                        profiles[index].Name, $"Car{index}");

                    page.Backgrounds[index] = page.Rows[index].GetComponent<Image>();
                }
            }

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class PaintPage
        {
            public RectTransform Panel;
            public Button[] Swatches;
            public Image[] Backgrounds;
            public Button Back;
        }

        /// <summary>
        /// Eight colours in two rows of four.
        ///
        /// <para>The colours come from <see cref="CarPaintPalette"/> — the same table the material assets
        /// are created from, so a swatch cannot show a colour the car will not be.</para>
        /// </summary>
        private static PaintPage BuildPaintPage(RectTransform parent, Sprite box)
        {
            var page = new PaintPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "PaintPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "PAINT", 44, 60f);

            Color[] colours = CarPaintPalette.Colours;
            page.Swatches = new Button[colours.Length];
            page.Backgrounds = new Image[colours.Length];

            const int perRow = 4;
            for (int start = 0; start < colours.Length; start += perRow)
            {
                int count = Mathf.Min(perRow, colours.Length - start);
                Button[] row = SwatchRow(page.Panel, box, colours, start, count);

                for (int i = 0; i < count; i++)
                {
                    page.Swatches[start + i] = row[i];
                    page.Backgrounds[start + i] = row[i].GetComponent<Image>();
                }
            }

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class PlacePage
        {
            public RectTransform Panel;
            public Button[] Rows;
            public Image[] Backgrounds;
            public Button Back;
        }

        private static PlacePage BuildPlacePage(
            RectTransform parent, Sprite box, IReadOnlyList<string> spawnNames)
        {
            var page = new PlacePage();
            page.Panel = TouchUiSetup.StackPanel(parent, "PlacePanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "START", 44, 60f);

            page.Rows = new Button[spawnNames.Count];
            page.Backgrounds = new Image[spawnNames.Count];

            for (int i = 0; i < spawnNames.Count; i++)
            {
                page.Rows[i] = TouchUiSetup.MenuButton(page.Panel, $"Place{i}", box, spawnNames[i]);
                page.Backgrounds[i] = page.Rows[i].GetComponent<Image>();
            }

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class ConditionsPage
        {
            public RectTransform Panel;
            public Slider TimeSlider;
            public Text TimeLabel;
            public Button[] Weather;
            public Image[] WeatherBackgrounds;
            public Button Back;
        }

        /// <summary>
        /// The hour, and how thick the air is.
        ///
        /// <para>The weather buttons say Clear, Hazy and Overcast because that is all they do — see
        /// <c>WeatherPreset</c>. There is no rain in this game and a button claiming otherwise would be
        /// the menu lying about the world, which is the one thing a menu must not do.</para>
        /// </summary>
        private static ConditionsPage BuildConditionsPage(RectTransform parent, Sprite box)
        {
            var page = new ConditionsPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "ConditionsPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "CONDITIONS", 44, 60f);

            TouchUiSetup.MenuLabel(page.Panel, "Time of day", 26, 38f);
            page.TimeLabel = TouchUiSetup.MenuLabel(page.Panel, "--:--", 34, 44f);
            page.TimeSlider = BuildTimeSlider(page.Panel, box);

            TouchUiSetup.MenuLabel(page.Panel, "Weather", 26, 38f);

            string[] names = PlayerChoices.WeatherNames;
            page.Weather = new Button[names.Length];
            page.WeatherBackgrounds = new Image[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                page.Weather[i] = TouchUiSetup.MenuButton(page.Panel, $"Weather{i}", box, names[i]);
                page.WeatherBackgrounds[i] = page.Weather[i].GetComponent<Image>();
            }

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class ControlsPage
        {
            public RectTransform Panel;
            public Text SchemeLabel;
            public Slider Sensitivity;
            public GameObject Recalibrate;
            public Button CycleSteering;
            public Button CyclePedals;
            public Button Back;
        }

        private static ControlsPage BuildControlsPage(RectTransform parent, Sprite box)
        {
            var page = new ControlsPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "ControlsPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "CONTROLS", 44, 60f);
            page.SchemeLabel = TouchUiSetup.MenuLabel(page.Panel, "—", 30, 42f);

            page.CycleSteering = TouchUiSetup.MenuButton(page.Panel, "Steering", box, "Steering: change");
            page.CyclePedals = TouchUiSetup.MenuButton(page.Panel, "Throttle", box, "Throttle: change");

            // Hidden outside the tilt scheme, and the only row that is — everything below applies to
            // whatever the player is steering with. The layout group closes the gap when it goes.
            Button recalibrate = TouchUiSetup.MenuButton(page.Panel, "Recalibrate", box, "Recalibrate tilt");
            page.Recalibrate = recalibrate.gameObject;

            TouchUiSetup.MenuLabel(page.Panel, "Steering sensitivity", 26, 38f);
            page.Sensitivity = BuildSensitivitySlider(page.Panel, box);

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class QualityPage
        {
            public RectTransform Panel;
            public Button[] Rows;
            public Image[] Backgrounds;
            public Button Back;
        }

        private static QualityPage BuildQualityPage(RectTransform parent, Sprite box)
        {
            var page = new QualityPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "QualityPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "QUALITY", 44, 60f);
            TouchUiSetup.MenuLabel(page.Panel, "How much world to draw", 26, 38f);

            string[] names = PlayerChoices.QualityNames;
            page.Rows = new Button[names.Length];
            page.Backgrounds = new Image[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                page.Rows[i] = TouchUiSetup.MenuButton(page.Panel, $"Quality{i}", box, names[i]);
                page.Backgrounds[i] = page.Rows[i].GetComponent<Image>();
            }

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class UpdatePage
        {
            public RectTransform Panel;
            public Text Status;
            public Text Notes;
            public Button Download;
            public Text DownloadLabel;
            public Button Back;
        }

        /// <summary>
        /// Which version is running, and the newer one if there is one.
        ///
        /// <para>The page is built for both outcomes and <c>UpdateScreen</c> decides which one it is
        /// showing: the notes and the download button are switched off unless GitHub actually answered
        /// with something newer. Building it the other way — a page that only exists when an update
        /// does — would mean generating scene content at run time, which is the one thing the rest of
        /// this file exists to avoid.</para>
        /// </summary>
        private static UpdatePage BuildUpdatePage(RectTransform parent, Sprite box)
        {
            var page = new UpdatePage();
            page.Panel = TouchUiSetup.StackPanel(parent, "UpdatePanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "UPDATE", 44, 60f);

            // Two lines' worth of height: every state but the first says something about the running
            // version and something about GitHub, and a row sized for one line would clip the second.
            page.Status = TouchUiSetup.MenuLabel(page.Panel, "Checking for updates...", 28, 84f);

            page.Notes = TouchUiSetup.MenuLabel(page.Panel, string.Empty, 22, 160f);
            page.Notes.alignment = TextAnchor.UpperLeft;
            page.Notes.color = new Color(1f, 1f, 1f, 0.72f);
            page.Notes.horizontalOverflow = HorizontalWrapMode.Wrap;
            page.Notes.verticalOverflow = VerticalWrapMode.Truncate;

            page.Download = TouchUiSetup.MenuButton(page.Panel, "Download", box, "Download");
            Accent(page.Download);
            page.DownloadLabel = page.Download.GetComponentInChildren<Text>();

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        private sealed class PausedPage
        {
            public RectTransform Panel;
            public Button Resume;
            public Button Place;
            public Button Conditions;
            public Button Garage;
            public Button Controls;
            public Button Respawn;
        }

        /// <summary>
        /// The in-drive pause menu. The same pages behind it, reached from here instead of from the
        /// start screen — which is the whole reason page routing became a component.
        /// </summary>
        private static PausedPage BuildPausedPage(RectTransform parent, Sprite box)
        {
            var page = new PausedPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "PausedPanel", box, 720f);

            TouchUiSetup.MenuLabel(page.Panel, "PAUSED", 48, 66f);

            page.Resume = TouchUiSetup.MenuButton(page.Panel, "Resume", box, "Resume");
            page.Place = TouchUiSetup.MenuButton(page.Panel, "Start", box, "Start somewhere else");
            page.Garage = TouchUiSetup.MenuButton(page.Panel, "Car", box, "Car and paint");
            page.Conditions = TouchUiSetup.MenuButton(page.Panel, "Conditions", box, "Time and weather");
            page.Controls = TouchUiSetup.MenuButton(page.Panel, "Controls", box, "Controls");
            page.Respawn = TouchUiSetup.MenuButton(page.Panel, "Respawn", box, "Put the car back");

            return page;
        }

        // --- Wiring. Persistent listeners throughout, so it is saved into the scene rather than
        //     rebuilt at run time — the same reason everything else here goes through SerializedObject.

        private static void WireStartPage(StartPage page, StartScreen start, MenuPanels panels)
        {
            Bind(page.Drive, start, nameof(StartScreen.Drive));

            BindPage(page.Garage, panels, MenuPage.Garage);
            BindPage(page.Paint, panels, MenuPage.Paint);
            BindPage(page.Place, panels, MenuPage.Place);
            BindPage(page.Conditions, panels, MenuPage.Conditions);
            BindPage(page.Controls, panels, MenuPage.Controls);
            BindPage(page.Quality, panels, MenuPage.Quality);
            BindPage(page.Update, panels, MenuPage.Update);
        }

        private static void WireGarage(GaragePage page, StartScreen start, MenuPanels panels)
        {
            for (int i = 0; i < page.Rows.Length; i++)
            {
                BindInt(page.Rows[i], start.SelectCar, i);
            }

            BindBack(page.Back, panels);
        }

        private static void WirePaint(PaintPage page, StartScreen start, MenuPanels panels)
        {
            for (int i = 0; i < page.Swatches.Length; i++)
            {
                BindInt(page.Swatches[i], start.SelectPaint, i);
            }

            BindBack(page.Back, panels);
        }

        private static void WirePlace(
            PlacePage page, StartScreen start, PauseMenu menu, MenuPanels panels)
        {
            for (int i = 0; i < page.Rows.Length; i++)
            {
                // Through StartScreen rather than PauseMenu.StartAt: tapping a place should move the car
                // and let the player look at it, not send them off. StartAt still exists and is still
                // what a place means once the game is running — see PauseMenu.MoveTo.
                BindInt(page.Rows[i], start.SelectPlace, i);
            }

            BindBack(page.Back, panels);
        }

        private static void WireConditions(
            ConditionsPage page, StartScreen start, PauseMenu menu, MenuPanels panels)
        {
            UnityEditor.Events.UnityEventTools.AddPersistentListener(
                page.TimeSlider.onValueChanged,
                new UnityEngine.Events.UnityAction<float>(menu.OnTimeOfDayChanged));

            for (int i = 0; i < page.Weather.Length; i++)
            {
                BindInt(page.Weather[i], start.SelectWeather, i);
            }

            BindBack(page.Back, panels);
        }

        private static void WireControls(ControlsPage page, PauseMenu menu, MenuPanels panels)
        {
            Bind(page.CycleSteering, menu, nameof(PauseMenu.CycleSteering));
            Bind(page.CyclePedals, menu, nameof(PauseMenu.CyclePedals));
            Bind(page.Recalibrate.GetComponent<Button>(), menu, nameof(PauseMenu.RecalibrateTilt));

            UnityEditor.Events.UnityEventTools.AddPersistentListener(
                page.Sensitivity.onValueChanged,
                new UnityEngine.Events.UnityAction<float>(menu.OnSteerSensitivityChanged));

            BindBack(page.Back, panels);
        }

        private static void WireQuality(QualityPage page, StartScreen start, MenuPanels panels)
        {
            for (int i = 0; i < page.Rows.Length; i++)
            {
                BindInt(page.Rows[i], start.SelectQuality, i);
            }

            BindBack(page.Back, panels);
        }

        private static void WireUpdate(UpdatePage page, UpdateScreen updates, MenuPanels panels)
        {
            Bind(page.Download, updates, nameof(UpdateScreen.Download));
            BindBack(page.Back, panels);
        }

        private static void WirePaused(
            PausedPage page, PauseMenu menu, MenuPanels panels, GameObject pauseButton)
        {
            Bind(pauseButton.GetComponent<Button>(), menu, nameof(PauseMenu.Toggle));

            Bind(page.Resume, menu, nameof(PauseMenu.Resume));
            Bind(page.Respawn, menu, nameof(PauseMenu.Respawn));

            BindPage(page.Place, panels, MenuPage.Place);
            BindPage(page.Garage, panels, MenuPage.Garage);
            BindPage(page.Conditions, panels, MenuPage.Conditions);
            BindPage(page.Controls, panels, MenuPage.Controls);
        }

        private static void Bind(Button button, MonoBehaviour target, string method)
        {
            var call = System.Delegate.CreateDelegate(
                typeof(UnityEngine.Events.UnityAction), target, method) as UnityEngine.Events.UnityAction;

            UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick, call);
        }

        /// <summary>
        /// Wires a button to an int-taking method with the index baked into the saved event.
        ///
        /// <para>The pattern the spawn buttons have always used, now doing most of the work in this
        /// file: car, paint, place, weather, quality and page navigation are all "one method, an index
        /// per button". The alternative is a method per row and a rebuild every time a row is added.</para>
        /// </summary>
        private static void BindInt(
            Button button, UnityEngine.Events.UnityAction<int> method, int index)
        {
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(button.onClick, method, index);
        }

        private static void BindPage(Button button, MenuPanels panels, MenuPage page)
        {
            // Show(int), explicitly. MenuPanels also has a Show(MenuPage) overload for callers holding
            // the enum, but a persistent listener can only serialize an int argument.
            BindInt(button, panels.Show, (int)page);
        }

        private static void BindBack(Button button, MenuPanels panels)
        {
            Bind(button, panels, nameof(MenuPanels.Back));
        }

        // --- Row builders.

        /// <summary>
        /// A summary row on the front page: what the setting is called on the left, what it is currently
        /// set to on the right, and the whole row a button into that page.
        /// </summary>
        private static Button SummaryRow(
            RectTransform parent, Sprite box, string caption, out Text value)
        {
            RectTransform rect = TouchUiSetup.Panel(parent, caption, box, TouchUiSetup.ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(560f, TouchUiSetup.MenuRowHeight), Vector2.zero);

            TouchUiSetup.Row(rect.gameObject, TouchUiSetup.MenuRowHeight);

            Text name = TouchUiSetup.Label(rect, caption, 30);
            name.alignment = TextAnchor.MiddleLeft;
            ((RectTransform)name.transform).offsetMin = new Vector2(34f, 0f);

            value = TouchUiSetup.Label(rect, "—", 30);
            value.alignment = TextAnchor.MiddleRight;
            value.color = new Color(1f, 1f, 1f, 0.72f);
            ((RectTransform)value.transform).offsetMax = new Vector2(-34f, 0f);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            return button;
        }

        /// <summary>
        /// A car row: a rendered side view on the left, the body's name on the right.
        ///
        /// <para>Taller than an ordinary menu row, because the point of it is the picture. A car at 96
        /// units is a dark smudge; at <see cref="CarRowHeight"/> the difference between a fastback and a
        /// pickup is the thing you notice first, which is the only reason to show it at all.</para>
        ///
        /// <para>The 590 is a hint, not a width: these sit inside a <c>HorizontalLayoutGroup</c> that
        /// controls and expands its children, so the pair share the panel between them. The thumbnail's
        /// inset is what actually has to be right, and it is half the picture's width plus a margin.</para>
        /// </summary>
        private static Button CarRow(
            RectTransform parent, Sprite box, Sprite thumbnail, string caption, string name)
        {
            const float thumbWidth = 300f;

            RectTransform rect = TouchUiSetup.Panel(parent, name, box, TouchUiSetup.ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(590f, CarRowHeight), Vector2.zero);

            TouchUiSetup.Row(rect.gameObject, CarRowHeight);

            if (thumbnail != null)
            {
                // Height tied to the row rather than written out: preserveAspect fits the 2:1 sprite
                // inside whichever of the two is binding, and a slot taller than its row would have the
                // picture spilling over the panel edge instead of being letterboxed inside it.
                RectTransform art = TouchUiSetup.Panel(rect, "Thumb", thumbnail, Color.white,
                    new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(thumbWidth, CarRowHeight), new Vector2(thumbWidth * 0.5f + 20f, 0f));

                Image image = art.GetComponent<Image>();

                // Simple, not Sliced: a photograph of a car has no nine-slice borders, and stretching one
                // as though it did is how a preview stops resembling what it previews.
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
                image.raycastTarget = false;
            }

            Text label = TouchUiSetup.Label(rect, caption, 30);
            label.alignment = TextAnchor.MiddleRight;
            ((RectTransform)label.transform).offsetMax = new Vector2(-28f, 0f);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            return button;
        }

        /// <summary>
        /// One row of colour swatches.
        ///
        /// <para>Each swatch carries its own colour, so it cannot be tinted to show that it is selected —
        /// that would repaint it. It gets an outline child instead, inactive until chosen, which
        /// <c>StartScreen.RefreshAll</c> switches. That child being at index 0 is the contract between
        /// the two, and the reason nothing else is parented here.</para>
        /// </summary>
        private static Button[] SwatchRow(
            RectTransform parent, Sprite box, Color[] colours, int start, int count)
        {
            const float height = 110f;

            var rowObject = new GameObject($"Swatches{start}", typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);

            var row = (RectTransform)rowObject.transform;
            TouchUiSetup.Row(rowObject, height);

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            var buttons = new Button[count];

            for (int i = 0; i < count; i++)
            {
                Color colour = colours[start + i];

                RectTransform swatch = TouchUiSetup.Panel(row, $"Paint{start + i}", box,
                    new Color(colour.r, colour.g, colour.b, 1f),
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(150f, height), Vector2.zero);

                // Child 0, and nothing else may be parented here — see the note above.
                RectTransform tick = TouchUiSetup.Panel(swatch, "Selected", box, TouchUiSetup.GlyphTint,
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(70f, 8f), new Vector2(0f, 14f));

                tick.GetComponent<Image>().raycastTarget = false;
                tick.gameObject.SetActive(false);

                buttons[i] = swatch.gameObject.AddComponent<Button>();
                buttons[i].targetGraphic = swatch.GetComponent<Image>();
            }

            return buttons;
        }

        /// <summary>
        /// Two menu buttons sharing one row's height.
        ///
        /// <para>The same trick the garage uses for its ten cars, for the same reason: the page is out of
        /// vertical room and a row is the unit that costs height. Both halves are full-height tap targets
        /// — it is the width that is halved, not the thing a thumb has to hit.</para>
        /// </summary>
        private static Button[] ButtonPair(
            RectTransform parent, Sprite box, string leftName, string leftCaption,
            string rightName, string rightCaption)
        {
            var lineObject = new GameObject($"{leftName}{rightName}", typeof(RectTransform));
            lineObject.transform.SetParent(parent, false);
            TouchUiSetup.Row(lineObject, TouchUiSetup.MenuRowHeight);

            var line = lineObject.AddComponent<HorizontalLayoutGroup>();
            line.spacing = 20f;
            line.childAlignment = TextAnchor.MiddleCenter;
            line.childControlWidth = true;
            line.childControlHeight = true;
            line.childForceExpandWidth = true;
            line.childForceExpandHeight = true;

            var row = (RectTransform)lineObject.transform;

            return new[]
            {
                TouchUiSetup.MenuButton(row, leftName, box, leftCaption),
                TouchUiSetup.MenuButton(row, rightName, box, rightCaption),
            };
        }

        /// <summary>Paints a button in the accent colour. For the one button on a page that is the point.</summary>
        private static void Accent(Button button)
        {
            Image image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = TouchUiSetup.AccentTint;
            }
        }

        /// <summary>
        /// The hour of the day, 0 to 24.
        ///
        /// <para>Whole hours rather than continuous: the interesting values are dawn, noon, dusk and
        /// night, and a slider that lands exactly on 18:00 is easier to use with a thumb than one that
        /// lands on 17:47. <c>TimeOfDayController</c> runs on from wherever it is put, so this is where
        /// to look from rather than a clock to stop.</para>
        /// </summary>
        private static Slider BuildTimeSlider(RectTransform parent, Sprite box)
        {
            Slider slider = TouchUiSetup.BuildTrackedSlider(parent, box, "TimeOfDay");

            slider.minValue = 0f;
            slider.maxValue = 24f;
            slider.wholeNumbers = true;
            slider.value = 18f;

            return slider;
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
            Slider slider = TouchUiSetup.BuildTrackedSlider(parent, box, "Sensitivity");

            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = TouchControlState.DefaultSensitivity;

            return slider;
        }
    }
}
