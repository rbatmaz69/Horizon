using System.Collections.Generic;
using Horizon.Game;
using Horizon.Input;
using Horizon.Net;
using Horizon.World;
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
            Button minimapButton,
            IReadOnlyList<string> spawnNames,
            WorldMap map)
        {
            var panelList = new List<GameObject>();

            GameObject backdrop = BuildBackdrop(canvas);

            PauseMenu menu = canvas.gameObject.AddComponent<PauseMenu>();
            MenuPanels panels = canvas.gameObject.AddComponent<MenuPanels>();
            StartScreen start = canvas.gameObject.AddComponent<StartScreen>();
            UpdateScreen updates = canvas.gameObject.AddComponent<UpdateScreen>();
            MultiplayerScreen together = canvas.gameObject.AddComponent<MultiplayerScreen>();

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

            MapPage mapPage = BuildMapPage(safe, box, map);
            Register(panelList, MenuPage.Map, mapPage.Panel);

            MultiplayerPage multiplayer = BuildMultiplayerPage(safe, box);
            Register(panelList, MenuPage.Multiplayer, multiplayer.Panel);

            RoomPage room = BuildRoomPage(safe, box);
            Register(panelList, MenuPage.Room, room.Panel);

            HorizonAssetUtility.Configure(panels, serialized =>
                HorizonAssetUtility.SetObjectArray(serialized, "panels", panelList.ToArray()));

            ValidatePageHeights(panelList);

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
                serialized.FindProperty("weatherHeading").objectReferenceValue = conditions.WeatherHeading;
                HorizonAssetUtility.SetObjectArray(serialized, "weatherButtons", conditions.Weather);
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
                serialized.FindProperty("placeList").objectReferenceValue = place.List;
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

            WireStartPage(startPage, start, panels, together);
            WireGarage(garage, start, panels);
            WirePaint(paint, start, panels);
            WirePlace(place, start, menu, panels);
            WireConditions(conditions, start, menu, panels);
            WireControls(controls, menu, panels);
            WireQuality(quality, start, panels);
            WirePaused(paused, start, menu, panels, pauseButton, together);
            WireUpdate(update, updates, panels);
            WireMap(mapPage, menu, panels, minimapButton);
            WireMultiplayer(multiplayer, room, together, panels);

            // Wired last of the parts, because it needs both pages built. NetSession itself is attached
            // to the Bootstrap object rather than to the canvas, so PrototypeSetup fills that one in.
            together.SetParts(
                null,
                panels,
                start,
                menu,
                multiplayer.Status,
                multiplayer.Name,
                multiplayer.Hosts,
                multiplayer.HostLabels,
                multiplayer.Address,
                room.Status,
                room.Players,
                room.PlayerLabels);

            EditorUtility.SetDirty(together);

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
                Multiplayer = together,
            };
        }


        private sealed class MultiplayerPage
        {
            public RectTransform Panel;
            public Text Status;
            public InputField Name;
            public Button Host;
            public Button[] Hosts;
            public Text[] HostLabels;
            public InputField Address;
            public Button Join;
            public Button Back;
        }

        private sealed class RoomPage
        {
            public RectTransform Panel;
            public Text Status;
            public Button[] Players;
            public Text[] PlayerLabels;
            public Button Leave;
            public Button Back;
            public Button Drive;
        }

        /// <summary>
        /// Hosting a game on this network, or joining one.
        ///
        /// <para><b>The discovered-host list is two rows tall and holds six.</b> That is the page-height
        /// budget rather than a guess about how many friends anybody has: a thousand units of usable
        /// canvas, ninety-two of padding and seven rows leaves 776 for the rows themselves, and a
        /// three-row list puts it 24 over. It scrolls, which is what <c>TouchUiSetup.ScrollList</c> is
        /// for and what the place page already does with ten.</para>
        ///
        /// <para><b>The typed address is not a fallback nobody finds.</b> Two things routinely stop a
        /// broadcast arriving — client isolation, which most routers and every public network have on,
        /// and Android's Wi-Fi power saving, which drops broadcast frames unless something holds a
        /// multicast lock — and in both cases the list stays empty with no error anywhere. A field
        /// beside the list is the only thing that turns "it does not work" into "type this in".</para>
        /// </summary>
        private static MultiplayerPage BuildMultiplayerPage(RectTransform parent, Sprite box)
        {
            var page = new MultiplayerPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "MultiplayerPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "PLAY TOGETHER", 44, 52f);

            // Two lines. Every state but the first says something about the network and something about
            // what to do next, and a row sized for one line would clip the second.
            page.Status = TouchUiSetup.MenuLabel(page.Panel, "Host a game, or join one.", 24, 56f);
            page.Status.color = new Color(1f, 1f, 1f, 0.72f);
            page.Status.horizontalOverflow = HorizontalWrapMode.Wrap;

            page.Name = TextField(page.Panel, box, "Name", "Your name");

            page.Host = TouchUiSetup.MenuButton(page.Panel, "Host", box, "Host a game");
            Accent(page.Host);

            RectTransform list = TouchUiSetup.ScrollList(page.Panel, "Hosts", 2);
            page.Hosts = new Button[FoundHostRows];
            page.HostLabels = new Text[FoundHostRows];

            for (int i = 0; i < FoundHostRows; i++)
            {
                page.Hosts[i] = TouchUiSetup.MenuButton(list, $"Host{i}", box, string.Empty);
                page.HostLabels[i] = page.Hosts[i].GetComponentInChildren<Text>();

                // Off until a host has actually been found, the way the full-screen map's own name
                // labels are. Left on, the preview came back showing two blank grey slabs where the
                // list is — which is what a player would see for the first second of every visit, and
                // reads as two broken buttons rather than as an empty list.
                page.Hosts[i].gameObject.SetActive(false);
            }

            page.Join = FieldWithButton(
                page.Panel, box, "Address", "or type an address", "Join", out page.Address);

            page.Back = TouchUiSetup.MenuButton(page.Panel, "Back", box, "Back");
            return page;
        }

        /// <summary>Six rows in the list, which scrolls. Seven guests can be in a room; six fit a list.</summary>
        private const int FoundHostRows = 6;

        /// <summary>
        /// Who is in the room.
        ///
        /// <para>Separate from the page above because the two together do not fit — see
        /// <c>MenuPage.Room</c>. The split is also the better menu: the way out of a room and the way
        /// into one are different questions, and a page that answered both would have half its rows
        /// greyed out at any moment.</para>
        /// </summary>
        private static RoomPage BuildRoomPage(RectTransform parent, Sprite box)
        {
            var page = new RoomPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "RoomPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "ROOM", 44, 52f);

            page.Status = TouchUiSetup.MenuLabel(page.Panel, "Not in a game.", 24, 56f);
            page.Status.color = new Color(1f, 1f, 1f, 0.72f);
            page.Status.horizontalOverflow = HorizontalWrapMode.Wrap;

            RectTransform list = TouchUiSetup.ScrollList(page.Panel, "Players", 4);
            page.Players = new Button[NetProtocol.MaxPeers];
            page.PlayerLabels = new Text[NetProtocol.MaxPeers];

            for (int i = 0; i < NetProtocol.MaxPeers; i++)
            {
                page.Players[i] = TouchUiSetup.MenuButton(list, $"Player{i}", box, string.Empty);
                page.PlayerLabels[i] = page.Players[i].GetComponentInChildren<Text>();

                // A row in this list names somebody; it does nothing when tapped. Left as a Button so
                // it is the same shape, the same height and the same tint as every other row in this
                // menu — a Text on a panel would be the one row on any page that looked different.
                page.Players[i].interactable = false;

                // And off until there is somebody to name. Same finding as the host list above.
                page.Players[i].gameObject.SetActive(false);
            }

            // The point of being in a room, and it is the accent button for that reason. It was not
            // here at first and the way out was Back and then Drive from the front page — two people
            // sat in a room they could both see and neither could start.
            page.Drive = TouchUiSetup.MenuButton(page.Panel, "Drive", box, "Drive");
            Accent(page.Drive);

            // Side by side, so the page keeps the height it had. The room page stands at 930 units
            // against the thousand ValidatePageHeights allows, and a sixth full row would put it over.
            Button[] pair = ButtonPair(page.Panel, box, "Leave", "Leave the game", "Back", "Back");
            page.Leave = pair[0];
            page.Back = pair[1];

            return page;
        }

        /// <summary>
        /// A one-row text field.
        ///
        /// <para>The first text entry anywhere in this project, and it is deliberately a plain uGUI
        /// <c>InputField</c> rather than anything cleverer: on Android that raises the system keyboard,
        /// which is the keyboard the player already knows how to use.</para>
        /// </summary>
        private static InputField TextField(
            RectTransform parent, Sprite box, string name, string placeholder)
        {
            RectTransform row = TouchUiSetup.Panel(
                parent, name, box, TouchUiSetup.ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(560f, TouchUiSetup.MenuRowHeight), Vector2.zero);

            TouchUiSetup.Row(row.gameObject, TouchUiSetup.MenuRowHeight);
            return FillField(row, placeholder);
        }

        /// <summary>
        /// A text field with a button beside it, both on one row.
        ///
        /// <para>One row rather than two, and that is the page-height budget again: the field and its
        /// Join button as separate rows put the multiplayer page over the thousand units
        /// <c>ValidatePageHeights</c> allows.</para>
        /// </summary>
        private static Button FieldWithButton(
            RectTransform parent, Sprite box, string name, string placeholder, string caption,
            out InputField field)
        {
            var lineObject = new GameObject($"{name}Row", typeof(RectTransform));
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

            RectTransform fieldRect = TouchUiSetup.Panel(
                row, name, box, TouchUiSetup.ControlTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(360f, TouchUiSetup.MenuRowHeight), Vector2.zero);

            // Two to one in favour of the field: an address is fourteen characters and "Join" is four.
            var fieldElement = fieldRect.gameObject.AddComponent<LayoutElement>();
            fieldElement.flexibleWidth = 2f;

            field = FillField(fieldRect, placeholder);

            Button button = TouchUiSetup.MenuButton(row, $"{name}Go", box, caption);
            var buttonElement = button.gameObject.AddComponent<LayoutElement>();
            buttonElement.flexibleWidth = 1f;

            return button;
        }

        /// <summary>
        /// Puts the text, the placeholder and the caret of an <c>InputField</c> into a panel that
        /// already exists.
        ///
        /// <para>An <c>InputField</c> with no <c>textComponent</c> silently accepts input and draws
        /// none of it, which looks exactly like a field that is not receiving taps — so both children
        /// are built here rather than left to a caller to remember.</para>
        /// </summary>
        private static InputField FillField(RectTransform panel, string placeholder)
        {
            RectTransform textRect = TouchUiSetup.StretchChild(panel, "Text", 26f, 12f);
            Text text = TouchUiSetup.Label(textRect, string.Empty, 30);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            RectTransform hintRect = TouchUiSetup.StretchChild(panel, "Placeholder", 26f, 12f);
            Text hint = TouchUiSetup.Label(hintRect, placeholder, 30);
            hint.alignment = TextAnchor.MiddleLeft;
            hint.color = new Color(1f, 1f, 1f, 0.40f);

            InputField field = panel.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.lineType = InputField.LineType.SingleLine;
            field.targetGraphic = panel.GetComponent<Image>();

            return field;
        }

        /// <summary>
        /// Adds a page and checks it landed at the index its enum value says it should.
        ///
        /// <para>The one thing that cannot be allowed to drift silently. Every navigation button in this
        /// file bakes an integer into a saved event; if the array and the enum disagree by one, every
        /// button in the menu opens the page next to the one it names, and nothing anywhere reports an
        /// error.</para>
        /// </summary>
        /// <summary>
        /// Measures every page against the canvas it has to fit on, and says so.
        ///
        /// <para><b>The one thing about a menu that nothing else can see.</b> A page is as tall as its
        /// rows and the canvas is 1080 units, so a page grows past the screen the moment somebody adds
        /// the eighth thing to it — and what that looks like is not a broken layout but a page whose
        /// first and last rows are missing, on a screen with no scroll bar to suggest otherwise. The
        /// place page reached 1450 units at ten start points and the build reported a clean world.</para>
        ///
        /// <para>Against a margin rather than against 1080 exactly: the canvas scales to the screen, but
        /// a phone's safe area is inset from it, and a page that fits with nothing to spare on a
        /// reference canvas is a page that does not fit on a device with a notch.</para>
        /// </summary>
        private static void ValidatePageHeights(List<GameObject> panels)
        {
            // What SafeAreaPanel can be expected to leave of the 1080-unit reference height. In
            // landscape a notch costs width rather than height and the home indicator costs a little of
            // the bottom, so a thousand is the honest figure.
            //
            // <b>Not tighter than that, however tempting.</b> The front page and the garage have always
            // stood at about 980 and have always worked; a threshold that flags them is a threshold
            // that gets read once and then ignored, which is precisely how a page grew to 1450 units
            // with nothing complaining.
            const float Available = 1000f;

            float tallest = 0f;
            var tallestPage = MenuPage.Start;

            for (int i = 0; i < panels.Count; i++)
            {
                // The map is the one page that is meant to be the whole screen, so measuring it against
                // what fits on the screen would report it as too tall on every build forever — and a
                // warning that is always there is a warning nobody reads when it means something.
                if ((MenuPage)i == MenuPage.Map)
                {
                    continue;
                }

                var rect = (RectTransform)panels[i].transform;

                // The pages are laid out by a VerticalLayoutGroup and a ContentSizeFitter, neither of
                // which has run yet — a page built this frame reports whatever height it was created
                // with until something rebuilds it.
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

                float height = rect.rect.height;
                if (height > tallest)
                {
                    tallest = height;
                    tallestPage = (MenuPage)i;
                }

                if (height <= Available)
                {
                    continue;
                }

                Debug.LogWarning(
                    $"[Horizon] Menu page '{(MenuPage)i}' is {height:0} units tall against about "
                    + $"{Available:0} of usable canvas. Its top and bottom rows will be off the screen "
                    + "and there is nothing on a menu page to scroll with. Either take rows out of it "
                    + "or put the list part of it inside TouchUiSetup.ScrollList, the way the place "
                    + "page does.");
            }

            // Reported and not only warned about, so a zero here says the measurement itself has stopped
            // working rather than that every page suddenly fits.
            Debug.Log($"[Horizon] Menu: {panels.Count} pages, tallest is '{tallestPage}' at "
                      + $"{tallest:0} units against about {Available:0} of usable canvas.");
        }

        private sealed class MapPage
        {
            public RectTransform Panel;
            public Button ZoomIn;
            public Button ZoomOut;
            public Button Car;
            public Button World;
            public Button Back;
            public MapScreen Screen;
        }

        /// <summary>How many names the full-screen map can show at once.</summary>
        private const int MapLabels = 48;

        /// <summary>
        /// The full-screen map.
        ///
        /// <para><b>The one page that is not a <c>StackPanel</c>.</b> Every other page is a list of rows
        /// whose height is whatever the rows come to; this one is a picture, and a picture wants the
        /// screen. So it stretches, its controls float over it in a corner, and
        /// <see cref="ValidatePageHeights"/> is told to leave it alone — a stretched page measures the
        /// whole canvas and would otherwise be reported as too tall forever, which is exactly the kind
        /// of check that gets read once and then ignored.</para>
        ///
        /// <para><b>Opaque, not translucent.</b> The pause menu shows its panel over the frozen world,
        /// which is right for six buttons and wrong for a map: pale roads over a mountainside are pale
        /// roads nobody can follow.</para>
        /// </summary>
        private static MapPage BuildMapPage(RectTransform parent, Sprite box, WorldMap map)
        {
            var page = new MapPage();

            var panelObject = new GameObject("MapPanel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);

            page.Panel = (RectTransform)panelObject.transform;
            TouchUiSetup.Stretch(page.Panel);

            Image ground = panelObject.AddComponent<Image>();
            ground.color = new Color(0.07f, 0.08f, 0.10f, 1f);

            // The drag surface. MapGraphic takes itself out of the raycast, so every finger that is not
            // on a button lands here, which is where MapScreen listens.
            ground.raycastTarget = true;

            var viewObject = new GameObject("View", typeof(RectTransform));
            viewObject.transform.SetParent(page.Panel, false);

            var view = (RectTransform)viewObject.transform;
            TouchUiSetup.Stretch(view);

            MapGraphic graphic = viewObject.AddComponent<MapGraphic>();
            graphic.raycastTarget = false;

            HorizonAssetUtility.Configure(graphic, serialized =>
                serialized.FindProperty("map").objectReferenceValue = map);

            // The labels and the car are children of the view, and centre-anchored, because that is
            // what makes MapGraphic.LocalPointOf an anchoredPosition with nothing to convert.
            var labels = new Text[MapLabels];

            for (int i = 0; i < MapLabels; i++)
            {
                var labelObject = new GameObject($"Label{i}", typeof(RectTransform));
                labelObject.transform.SetParent(view, false);

                var rect = (RectTransform)labelObject.transform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(300f, 28f);

                labels[i] = TouchUiSetup.Label(rect, string.Empty, 22);
                labels[i].color = new Color(1f, 1f, 1f, 0.88f);

                labelObject.SetActive(false);
            }

            // The arrows' own triangle, still used by the key below.
            Sprite arrow = HorizonAssetUtility.LoadOrCreateGlyphSprite(
                $"{SpriteFolder}/UI_Right.png", "right");

            // The car marker is the minimap's sprite, not that glyph — and the reason is a bug rather
            // than a preference. This rect used to be built with a 90° rest rotation, because the glyph
            // points right and the marker has to point up. But MapScreen assigns localRotation outright
            // every frame to carry the heading, so the rest rotation was overwritten on the first
            // update and the arrow spent the whole time pointing ninety degrees off the car's actual
            // bearing. On a north-up map that is not a cosmetic fault: the one thing the marker is for
            // is which way you are facing, and it was reliably wrong.
            //
            // The proper sprite points up already, so nothing has to be added afterwards for MapScreen
            // to overwrite. Bigger than the minimap's, too: this view is zoomed out far enough that the
            // car is a speck among the roads.
            Sprite carSprite = HorizonAssetUtility.LoadOrCreateCarMarkerSprite(
                $"{SpriteFolder}/UI_CarMarker.png");

            RectTransform car = TouchUiSetup.Panel(view, "Car", carSprite, TouchUiSetup.AccentTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(36f, 46f), Vector2.zero);

            Image carImage = car.GetComponent<Image>();
            carImage.type = Image.Type.Simple;
            carImage.raycastTarget = false;

            // No rest rotation on purpose. MapScreen turns this to the car's heading, which the
            // north-up view leaves as an honest bearing — and anything set here is overwritten by it.

            // The other players, on the same sprite in a cooler colour, and children of the view for
            // exactly the reason the labels above are: LocalPointOf hands back an anchoredPosition with
            // nothing to convert. No clip radius — this map is a rectangle.
            var remoteMarkers = new RectTransform[RemoteMapMarkers.MarkerCount];

            for (int i = 0; i < remoteMarkers.Length; i++)
            {
                remoteMarkers[i] = TouchUiSetup.Panel(
                    view, $"Remote{i}", carSprite, RemoteMapMarkers.MarkerTint,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(32f, 40f), Vector2.zero);

                Image markerImage = remoteMarkers[i].GetComponent<Image>();
                markerImage.type = Image.Type.Simple;
                markerImage.raycastTarget = false;

                // Off, the way the full-screen map's own name labels are, and for the same reason: a
                // marker exists because somebody is in the room, and in a saved scene nobody ever is.
                // Left on, all seven sit stacked at the centre of the map — which is what the HUD
                // preview came back showing, a friend on a road with nobody on it.
                remoteMarkers[i].gameObject.SetActive(false);
            }

            RemoteMapMarkers remotes = viewObject.AddComponent<RemoteMapMarkers>();
            remotes.SetParts(graphic, remoteMarkers, 0f);
            EditorUtility.SetDirty(remotes);

            // --- Controls, over the map rather than beside it.
            page.ZoomIn = MapButton(page.Panel, box, "ZoomIn", "+", new Vector2(-110f, 400f));
            page.ZoomOut = MapButton(page.Panel, box, "ZoomOut", "\u2212", new Vector2(-110f, 280f));
            page.Car = MapButton(page.Panel, box, "Car", "Car", new Vector2(-110f, 160f));
            page.World = MapButton(page.Panel, box, "World", "All", new Vector2(-110f, 40f));

            BuildLegend(page.Panel, box, graphic, arrow);

            RectTransform back = TouchUiSetup.Panel(page.Panel, "Back", box, TouchUiSetup.ControlTint,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(280f, 96f),
                new Vector2(180f, 80f));

            TouchUiSetup.Label(back, "Back", 32);
            page.Back = back.gameObject.AddComponent<Button>();
            page.Back.targetGraphic = back.GetComponent<Image>();

            MapScreen screen = panelObject.AddComponent<MapScreen>();

            HorizonAssetUtility.Configure(screen, serialized =>
            {
                serialized.FindProperty("graphic").objectReferenceValue = graphic;
                serialized.FindProperty("carMarker").objectReferenceValue = car;

                HorizonAssetUtility.SetObjectArray(serialized, "labels", labels);
            });

            HorizonAssetUtility.AssertReferenceAssigned(screen, "graphic");
            HorizonAssetUtility.AssertReferenceAssigned(screen, "carMarker");
            HorizonAssetUtility.AssertReferenceAssigned(graphic, "map");

            page.Screen = screen;
            return page;
        }

        /// <summary>Height of one row of the key.</summary>
        private const float LegendRowHeight = 34f;

        /// <summary>
        /// The key: what every colour and every mark on the map means.
        ///
        /// <para><b>A map with symbols on it and no key is a map you have to have been told about.</b>
        /// Nine of the things drawn here are colour-coded and none of them is labelled in the picture —
        /// an orange line and an orange diamond are a motorway and a filling station, and nothing on
        /// screen said so.</para>
        ///
        /// <para>Every swatch is read off the <see cref="MapGraphic"/> that will draw the map beside it,
        /// never typed. See <c>MapGraphic.ColourOf</c> for why that is the whole point of the method
        /// being public.</para>
        ///
        /// <para>Bottom left, stacked over the Back button: the zoom controls own the right-hand edge,
        /// and the middle of the screen is the map.</para>
        /// </summary>
        private static void BuildLegend(
            RectTransform parent, Sprite box, MapGraphic graphic, Sprite arrow)
        {
            RectTransform panel = TouchUiSetup.StackPanel(parent, "Legend", box, 380f);

            // StackPanel centres itself; this one is pinned to the corner above Back.
            panel.anchorMin = new Vector2(0f, 0f);
            panel.anchorMax = new Vector2(0f, 0f);
            panel.pivot = new Vector2(0f, 0f);
            panel.anchoredPosition = new Vector2(40f, 200f);

            Text title = TouchUiSetup.MenuLabel(panel, "MAP KEY", 24, 34f);
            title.color = TouchUiSetup.AccentTint;

            // A hard accent rule under the title. The one borrowed gesture: it is what makes a stack of
            // rows read as an instrument panel rather than as a list of settings.
            RectTransform rule = TouchUiSetup.Panel(panel, "Rule", box, TouchUiSetup.AccentTint,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 3f), Vector2.zero);

            rule.GetComponent<Image>().raycastTarget = false;
            TouchUiSetup.Row(rule.gameObject, 3f);

            LegendRow(panel, box, graphic.ColourOf(MapLineKind.Motorway), "MOTORWAY", LegendMark.Line);
            LegendRow(panel, box, graphic.ColourOf(MapLineKind.Trunk), "ROAD", LegendMark.Line);
            LegendRow(panel, box, graphic.ColourOf(MapLineKind.Circuit), "CIRCUIT", LegendMark.Line);
            LegendRow(panel, box, graphic.ColourOf(MapLineKind.Street), "TOWN STREET", LegendMark.Line);
            LegendRow(panel, box, graphic.ColourOf(MapLineKind.River), "WATER", LegendMark.Block);
            LegendRow(panel, box, graphic.TownColour, "BUILT UP", LegendMark.Block);

            LegendRow(panel, box, graphic.ColourOf(MapMarkerKind.Place), "START PLACE", LegendMark.Diamond);
            // The shapes match what MapGraphic.AddMarker actually draws, kind for kind. They are shapes
            // rather than four colours of the same diamond because a silhouette needs no legend to be
            // told apart and survives being four pixels across, which is the size a mark is read at on
            // the minimap — and because a key that shows a shape the map does not draw is worse than no
            // key at all.
            LegendRow(panel, box, graphic.ColourOf(MapMarkerKind.FuelStation), "FUEL", LegendMark.Square);
            LegendRow(panel, arrow, graphic.ColourOf(MapMarkerKind.Viewpoint), "VIEWPOINT",
                LegendMark.Triangle);
            LegendRow(panel, box, graphic.ColourOf(MapMarkerKind.Tunnel), "TUNNEL, BRIDGE", LegendMark.Diamond);

            LegendRow(panel, arrow, TouchUiSetup.AccentTint, "YOU", LegendMark.Arrow);

            // The one thing on this map that is not baked into the WorldMap asset, and therefore the
            // one row here that does not come off MapGraphic.ColourOf. It reads the same constant the
            // marker itself is tinted with, which is the rule this key already follows: a swatch with
            // its own copy of a colour agrees until the first retune and then quietly lies.
            LegendRow(panel, arrow, RemoteMapMarkers.MarkerTint, "FRIENDS", LegendMark.Arrow);
        }

        /// <summary>How one row of the key draws its sample.</summary>
        private enum LegendMark
        {
            /// <summary>A stroke, for the things drawn as lines.</summary>
            Line,

            /// <summary>A filled patch, for the things drawn as areas.</summary>
            Block,

            /// <summary>The marker shape, which is a square stood on its corner.</summary>
            Diamond,

            /// <summary>A square stood square, which is what a filling station is drawn as.</summary>
            Square,

            /// <summary>A triangle on its base, which is what a viewpoint is drawn as.</summary>
            Triangle,

            /// <summary>The car, which is the arrows' triangle stood on end.</summary>
            Arrow,
        }

        private static void LegendRow(
            RectTransform parent, Sprite sprite, Color colour, string caption, LegendMark mark)
        {
            var rowObject = new GameObject(caption, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);

            var row = (RectTransform)rowObject.transform;
            row.anchorMin = new Vector2(0f, 0.5f);
            row.anchorMax = new Vector2(1f, 0.5f);

            TouchUiSetup.Row(rowObject, LegendRowHeight);

            Vector2 size;
            switch (mark)
            {
                case LegendMark.Line:
                    size = new Vector2(46f, 6f);
                    break;
                case LegendMark.Block:
                    size = new Vector2(30f, 18f);
                    break;
                default:
                    size = new Vector2(18f, 18f);
                    break;
            }

            RectTransform swatch = TouchUiSetup.Panel(row, "Swatch", sprite, colour,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), size, new Vector2(30f, 0f));

            Image image = swatch.GetComponent<Image>();

            // Simple for the diamond and the arrow: both are drawn from the middle of their own square,
            // and a nine-slice would stretch a border that is not there.
            // Simple for every mark drawn from the middle of its own square: a nine-slice would
            // stretch a border that is not there.
            image.type = mark == LegendMark.Line || mark == LegendMark.Block
                ? Image.Type.Sliced
                : Image.Type.Simple;

            image.raycastTarget = false;

            if (mark == LegendMark.Diamond)
            {
                swatch.localRotation = Quaternion.Euler(0f, 0f, 45f);
            }
            else if (mark == LegendMark.Arrow || mark == LegendMark.Triangle)
            {
                swatch.localRotation = Quaternion.Euler(0f, 0f, 90f);
            }

            Text text = TouchUiSetup.Label(TouchUiSetup.StretchChild(row, "Caption", 68f, 0f), caption, 20);
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(1f, 1f, 1f, 0.82f);
        }

        /// <summary>One of the square controls down the right-hand edge of the map.</summary>
        private static Button MapButton(
            RectTransform parent, Sprite box, string name, string caption, Vector2 position)
        {
            RectTransform rect = TouchUiSetup.Panel(parent, name, box, TouchUiSetup.ControlTint,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(110f, 96f), position);

            TouchUiSetup.Label(rect, caption, 32);

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            return button;
        }

        private static void WireMap(MapPage page, PauseMenu menu, MenuPanels panels, Button minimap)
        {
            // The minimap does not merely open a page: it has to stop the world first, which is why it
            // is bound to the menu rather than to MenuPanels.Show like every other navigation button.
            Bind(minimap, menu, nameof(PauseMenu.OpenMap));

            Bind(page.ZoomIn, page.Screen, nameof(MapScreen.ZoomIn));
            Bind(page.ZoomOut, page.Screen, nameof(MapScreen.ZoomOut));
            Bind(page.Car, page.Screen, nameof(MapScreen.CentreOnCar));
            Bind(page.World, page.Screen, nameof(MapScreen.Fit));

            Bind(page.Back, menu, nameof(PauseMenu.CloseSettings));
        }

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
            public Button Together;
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
            //
            // Three of them now rather than two, and for the same arithmetic: playing with somebody
            // else is a front-page thing — you host before you drive off, not after — and there is no
            // ninth row available to put it on.
            Button[] bottom = ButtonRow(
                page.Panel, box,
                "Controls", "Controls",
                "Together", "Together",
                "Update", "Version");

            page.Controls = bottom[0];
            page.Together = bottom[1];
            page.Update = bottom[2];
            page.UpdateLabel = page.Update.GetComponentInChildren<Text>();

            return page;
        }

        private sealed class GaragePage
        {
            public RectTransform Panel;
            public Button[] Rows;
            public Image[] Backgrounds;
            public Button Paint;
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

            // The way to the paint page, and it is here rather than only on the start screen because
            // the pause menu's own button is labelled "Car and paint" and could only reach the car.
            // Once the game is running there was no route to the colours at all: every page is reached
            // from the start screen or the pause menu, and the paint page was on neither. The menu said
            // the paint was changeable and it was not.
            page.Paint = TouchUiSetup.MenuButton(page.Panel, "Paint", box, "Paint");

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
            public ScrollRect List;
            public Button[] Rows;
            public Image[] Backgrounds;
            public Button Back;
        }

        /// <summary>
        /// Where to begin, as a scrolling list.
        ///
        /// <para><b>The one page whose length the world decides.</b> Every other page holds a fixed set
        /// of settings, so <c>StackPanel</c>'s "as tall as its rows" is exactly right for them. This one
        /// gains a row every time a leg of road is built, and at ten it stood 1450 units tall on a
        /// 1080-unit canvas — its first and last places off the screen with no way to reach them, which
        /// is what a menu with no scroll view looks like the moment somebody adds the eighth thing to
        /// it. See <c>TouchUiSetup.ScrollList</c>; the title and Back stay outside it.</para>
        /// </summary>
        private static PlacePage BuildPlacePage(
            RectTransform parent, Sprite box, IReadOnlyList<string> spawnNames)
        {
            var page = new PlacePage();
            page.Panel = TouchUiSetup.StackPanel(parent, "PlacePanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "START", 44, 60f);

            RectTransform list = TouchUiSetup.ScrollList(page.Panel, "Places", spawnNames.Count);
            page.List = list.parent.GetComponent<ScrollRect>();

            page.Rows = new Button[spawnNames.Count];
            page.Backgrounds = new Image[spawnNames.Count];

            for (int i = 0; i < spawnNames.Count; i++)
            {
                page.Rows[i] = TouchUiSetup.MenuButton(list, $"Place{i}", box, spawnNames[i]);
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
            public Text WeatherHeading;
        }

        /// <summary>
        /// The hour, and how thick the air is.
        ///
        /// <para>The buttons are built from <c>PlayerChoices.WeatherNames</c> rather than typed out, so
        /// a preset added to that enum arrives here on its own. Rain did exactly that, and it was only
        /// allowed to once it meant something: this comment used to say there was no rain in the game
        /// and that a button claiming otherwise would be the menu lying about the world. That rule has
        /// not changed — the button arrived with the weather, not before it.</para>
        /// </summary>
        private static ConditionsPage BuildConditionsPage(RectTransform parent, Sprite box)
        {
            var page = new ConditionsPage();
            page.Panel = TouchUiSetup.StackPanel(parent, "ConditionsPanel", box, PanelWidth);

            TouchUiSetup.MenuLabel(page.Panel, "CONDITIONS", 44, 60f);

            TouchUiSetup.MenuLabel(page.Panel, "Time of day", 26, 38f);
            page.TimeLabel = TouchUiSetup.MenuLabel(page.Panel, "--:--", 34, 44f);
            page.TimeSlider = BuildTimeSlider(page.Panel, box);

            // Kept as a field so PauseMenu can rewrite it while a guest is taking the sky from the
            // host — the heading is the free place to say why the buttons under it are dead, and it
            // costs the page no height at all.
            page.WeatherHeading = TouchUiSetup.MenuLabel(page.Panel, "Weather", 26, 38f);

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
            public Button Map;
            public Button Respawn;
            public Button Together;
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
            // Side by side, so the room is reachable from a paused game without the page growing a
            // ninth row — it already stands at about 984 units against a thousand of usable canvas.
            Button[] pair = ButtonPair(page.Panel, box, "Map", "Map", "Together", "Together");
            page.Map = pair[0];
            page.Together = pair[1];

            page.Respawn = TouchUiSetup.MenuButton(page.Panel, "Respawn", box, "Put the car back");

            return page;
        }

        // --- Wiring. Persistent listeners throughout, so it is saved into the scene rather than
        //     rebuilt at run time — the same reason everything else here goes through SerializedObject.

        private static void WireStartPage(
            StartPage page, StartScreen start, MenuPanels panels, MultiplayerScreen together)
        {
            Bind(page.Drive, start, nameof(StartScreen.Drive));

            BindPage(page.Garage, panels, MenuPage.Garage);
            BindPage(page.Paint, panels, MenuPage.Paint);

            // Opening the place page also scrolls it to the place already chosen — see
            // StartScreen.ShowChosenPlace for why that cannot be done once at startup instead.
            BindPage(page.Place, panels, MenuPage.Place);
            Bind(page.Place, start, nameof(StartScreen.ShowChosenPlace));
            BindPage(page.Conditions, panels, MenuPage.Conditions);
            BindPage(page.Controls, panels, MenuPage.Controls);
            BindPage(page.Quality, panels, MenuPage.Quality);
            BindPage(page.Update, panels, MenuPage.Update);

            // Two listeners, like the place button above: one opens the page, the other tells the
            // screen it is open so it can start listening for hosts.
            BindPage(page.Together, panels, MenuPage.Multiplayer);
            Bind(page.Together, together, nameof(MultiplayerScreen.Open));
        }

        private static void WireGarage(GaragePage page, StartScreen start, MenuPanels panels)
        {
            for (int i = 0; i < page.Rows.Length; i++)
            {
                BindInt(page.Rows[i], start.SelectCar, i);
            }

            BindPage(page.Paint, panels, MenuPage.Paint);
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
            PausedPage page, StartScreen start, PauseMenu menu, MenuPanels panels,
            GameObject pauseButton, MultiplayerScreen together)
        {
            Bind(pauseButton.GetComponent<Button>(), menu, nameof(PauseMenu.Toggle));

            Bind(page.Resume, menu, nameof(PauseMenu.Resume));
            Bind(page.Respawn, menu, nameof(PauseMenu.Respawn));

            BindPage(page.Place, panels, MenuPage.Place);
            Bind(page.Place, start, nameof(StartScreen.ShowChosenPlace));
            BindPage(page.Garage, panels, MenuPage.Garage);
            BindPage(page.Conditions, panels, MenuPage.Conditions);
            BindPage(page.Controls, panels, MenuPage.Controls);
            BindPage(page.Map, panels, MenuPage.Map);

            // Straight to the room when there is one, and to the page that opens one when there is
            // not. MultiplayerScreen decides which — a button cannot, because the answer changes while
            // the menu is closed.
            BindPage(page.Together, panels, MenuPage.Multiplayer);
            Bind(page.Together, together, nameof(MultiplayerScreen.Open));
        }

        /// <summary>
        /// Wires the two rooms pages.
        ///
        /// <para><b>The six host rows get six differently named methods rather than one taking an
        /// index.</b> A persistent listener can carry an int, which is how every other list in this
        /// file works — but those lists are fixed and this one changes while the page is open, so the
        /// row a listener was bound to is not the host it is showing by the time it is tapped. The
        /// index is read at the moment of the tap instead, off what is on screen.</para>
        ///
        /// <para>Opening and closing are bound <i>beside</i> the page navigation rather than instead of
        /// it, which is the arrangement the place page already uses to scroll itself to the chosen
        /// row. Here it is what starts and stops listening for hosts — and therefore what takes and
        /// gives back Android's Wi-Fi multicast lock.</para>
        /// </summary>
        private static void WireMultiplayer(
            MultiplayerPage page, RoomPage room, MultiplayerScreen screen, MenuPanels panels)
        {
            Bind(page.Host, screen, nameof(MultiplayerScreen.HostGame));
            Bind(page.Join, screen, nameof(MultiplayerScreen.JoinTyped));

            string[] rowMethods =
            {
                nameof(MultiplayerScreen.JoinRow0),
                nameof(MultiplayerScreen.JoinRow1),
                nameof(MultiplayerScreen.JoinRow2),
                nameof(MultiplayerScreen.JoinRow3),
                nameof(MultiplayerScreen.JoinRow4),
                nameof(MultiplayerScreen.JoinRow5),
            };

            for (int i = 0; i < page.Hosts.Length && i < rowMethods.Length; i++)
            {
                Bind(page.Hosts[i], screen, rowMethods[i]);
            }

            BindBack(page.Back, panels);
            Bind(page.Back, screen, nameof(MultiplayerScreen.Close));

            Bind(room.Drive, screen, nameof(MultiplayerScreen.Drive));
            Bind(room.Leave, screen, nameof(MultiplayerScreen.LeaveRoom));
            BindBack(room.Back, panels);
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
            return ButtonRow(parent, box, leftName, leftCaption, rightName, rightCaption);
        }

        /// <summary>
        /// Two or more buttons side by side on one row, as name/caption pairs.
        ///
        /// <para>Grew out of <see cref="ButtonPair"/> when the front page needed a third: that page has
        /// stood at about 988 units against a thousand of usable canvas since the garage reached ten
        /// cars, so a new full-height row would hang off the bottom of the screen. Widening a row that
        /// already exists costs nothing at all.</para>
        /// </summary>
        private static Button[] ButtonRow(RectTransform parent, Sprite box, params string[] pairs)
        {
            var lineObject = new GameObject(
                $"{pairs[0]}Row", typeof(RectTransform));
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

            var buttons = new Button[pairs.Length / 2];

            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = TouchUiSetup.MenuButton(row, pairs[i * 2], box, pairs[i * 2 + 1]);
            }

            return buttons;
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
