using Horizon.Atmosphere;
using Horizon.Core;
using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The screen the game opens on: which car, what colour, where to begin, at what hour, in what
    /// weather, with which controls and how much world drawn.
    ///
    /// <para><b>It previews rather than describes.</b> Every choice is applied to the real world the
    /// moment it is tapped — the car changes shape behind the panel, the sun moves, tapping a place
    /// teleports there and brings the camera with it. The alternative is a menu of words followed by a
    /// surprise, and this game is about how a place looks; asking someone to pick dusk from a slider
    /// they cannot see the effect of is asking the wrong question.</para>
    ///
    /// <para><b>Why the world loads before the screen can do anything.</b> The car does not exist until
    /// <c>GameBootstrap</c> has finished its additive load, so this starts as a panel over an empty
    /// scene and is handed the vehicle by <see cref="OnWorldReady"/> a frame or two later. Everything
    /// that touches the world is behind that call.</para>
    ///
    /// <para><b>And why <c>Time.timeScale</c> is zero but not from the beginning.</b> Pausing during the
    /// load would stop <c>WorldStreamingDriver</c>, which counts down on <c>Time.deltaTime</c> — the
    /// world behind the panel would never stream in. So the pause is applied here in <c>Awake</c>, which
    /// stops the <i>vehicle</i> (all of it lives in <c>FixedUpdate</c>) while leaving the coroutine that
    /// is loading the world to run on unscaled frames. A pleasant side effect: with the clock stopped,
    /// the hour the player picks is exactly the hour they drive off into.</para>
    /// </summary>
    public sealed class StartScreen : MonoBehaviour
    {
        [SerializeField] private PauseMenu pauseMenu;
        [SerializeField] private MenuPanels panels;
        [SerializeField] private QualityDirector quality;

        [Tooltip("Opaque sheet behind the menu pages, covering the world while the start screen is up. "
               + "Switched off when the player drives away, and never shown again — the pause menu is "
               + "deliberately see-through so you keep your bearings.")]
        [SerializeField] private GameObject backdrop;

        [Header("Garage")]
        [Tooltip("Row backgrounds on the car page, tinted to show which one is chosen.")]
        [SerializeField] private Image[] carRows = new Image[0];

        [Tooltip("Swatches on the paint page.")]
        [SerializeField] private Image[] paintSwatches = new Image[0];

        [Tooltip("Row backgrounds on the place page.")]
        [SerializeField] private Image[] placeRows = new Image[0];

        [Tooltip("The place page's scrolling list, so the chosen place can be brought into view. Null "
               + "on a menu built before there were enough places to need one.")]
        [SerializeField] private ScrollRect placeList;

        [SerializeField] private Image[] weatherRows = new Image[0];
        [SerializeField] private Image[] qualityRows = new Image[0];

        [Header("Labels")]
        [SerializeField] private Text carLabel;
        [SerializeField] private Text paintLabel;
        [SerializeField] private Text placeLabel;
        [SerializeField] private Text weatherLabel;
        [SerializeField] private Text qualityLabel;

        [Header("Selection tint")]
        [SerializeField] private Color selectedTint = new Color(0.86f, 0.36f, 0.17f, 0.92f);
        [SerializeField] private Color unselectedTint = new Color(1f, 1f, 1f, 0.30f);

        /// <summary>
        /// True once the player has driven off. <see cref="PauseMenu.Start"/> reads it to decide whether
        /// it is allowed to unpause — see the note there.
        /// </summary>
        public bool Finished { get; private set; }

        private VehicleBodySet bodySet;
        private TimeOfDayController timeOfDay;
        private ChaseCamera chaseCamera;
        private bool worldReady;

        private void Awake()
        {
            // In Awake, not Start, and that is the whole ordering argument: every Awake runs before any
            // Start, so this is guaranteed to be in place before PauseMenu.Start asks whether it may
            // unpause. Nothing depends on script execution order.
            Finished = false;

            if (pauseMenu == null)
            {
                pauseMenu = GetComponent<PauseMenu>();
            }

            if (panels == null)
            {
                panels = GetComponent<MenuPanels>();
            }

            pauseMenu?.SetPaused(true);

            if (backdrop != null)
            {
                backdrop.SetActive(true);
            }
        }

        private void Start()
        {
            // Always the front page, whoever the player is. Opening a first-timer on the garage was a
            // nice idea that produced a dead end — see MenuPanels.home — and even with that fixed it is
            // the wrong first impression: the first thing a menu should show is the way out of it.
            panels?.SetHome(MenuPage.Start);
            panels?.Show(MenuPage.Start);

            RefreshAll();
        }

        /// <summary>
        /// Called by <c>GameBootstrap.WireUpWorld</c> once the world scene is loaded and the camera is
        /// bound. Applies everything the player chose last time, so the panel is sitting over the world
        /// as they left it rather than over a default one.
        /// </summary>
        public void OnWorldReady(VehicleController vehicle)
        {
            worldReady = true;

            if (vehicle != null)
            {
                bodySet = vehicle.GetComponent<VehicleBodySet>();
            }

            timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            chaseCamera = FindFirstObjectByType<ChaseCamera>();

            // Again here as well as from WorldStreamingDriver.Start: the two are not ordered against
            // each other, and both are idempotent. See QualityDirector.ApplyToWorld.
            quality?.ApplyToWorld();

            ApplyCar();
            ApplyConditions();
            ApplyPlace();

            RefreshAll();
        }

        // --- The choices. Each one applies immediately and remembers itself.

        public void SelectCar(int index)
        {
            PlayerChoices.Car = index;
            ApplyCar();

            // Saved here rather than left to Drive, for the reason PauseMenu.SetTime already gives:
            // there is no leaving event on a phone, where the way out of a game is to stop looking at
            // it. Drive runs once at the start of a session, so a car or a colour chosen from the pause
            // menu afterwards was applied, shown as chosen, and forgotten by the next launch.
            PlayerChoices.Save();

            RefreshAll();
        }

        public void SelectPaint(int index)
        {
            PlayerChoices.Paint = index;

            if (bodySet != null)
            {
                bodySet.SetPaint(PlayerChoices.PaintIn(bodySet.PaintCount));
            }

            // See SelectCar. This one is also the only choice on the pause menu that does not move the
            // car: a repaint mid-drive leaves you exactly where you were, which is why it is safe to
            // offer there at all.
            PlayerChoices.Save();

            RefreshAll();
        }

        public void SelectPlace(int index)
        {
            PlayerChoices.Spawn = index;
            ApplyPlace();
            RefreshAll();
        }

        public void SelectWeather(int index)
        {
            pauseMenu?.SetWeather(index);
            RefreshAll();
        }

        public void SelectQuality(int index)
        {
            PlayerChoices.Quality = (QualityPreset)Mathf.Clamp(
                index, (int)QualityPreset.Low, (int)QualityPreset.High);

            if (quality != null)
            {
                quality.Apply(PlayerChoices.Quality);
                quality.ApplyToWorld();
            }

            PlayerChoices.Save();
            RefreshAll();
        }

        /// <summary>
        /// Hands over. Everything has already been applied as it was chosen, so this only has to write
        /// the choices down, put the camera behind the car and let the world run.
        /// </summary>
        public void Drive()
        {
            PlayerChoices.Save();

            // Defensively, in case the world arrived after the last selection was made — which it can,
            // on a slow first load where the player is tapping before OnWorldReady has fired.
            ApplyCar();
            ApplyConditions();
            ApplyPlace();

            Finished = true;

            // The sheet comes down here and stays down for the session. Pausing later shows the same
            // pages over the living world, which is what a pause menu should do — you want to see where
            // you left the car.
            if (backdrop != null)
            {
                backdrop.SetActive(false);
            }

            // Hand the fallback over to the pause menu, or the first Back after pausing would take the
            // player to a start screen that is no longer the screen they are on.
            panels?.SetHome(MenuPage.Paused);

            pauseMenu?.Resume();
        }

        // --- Applying.

        private void ApplyCar()
        {
            if (bodySet == null)
            {
                return;
            }

            bodySet.Select(
                PlayerChoices.CarIn(bodySet.BodyCount),
                PlayerChoices.PaintIn(bodySet.PaintCount));

            // Put the car back down properly after a body swap: Select has just changed the collider,
            // the mass and the centre of mass, and a taller shell would otherwise start the next
            // physics step with its sills inside the road.
            ApplyPlace();
        }

        private void ApplyPlace()
        {
            if (pauseMenu == null || pauseMenu.SpawnPoints == null)
            {
                return;
            }

            pauseMenu.MoveTo(PlayerChoices.SpawnIn(pauseMenu.SpawnPoints.Count));
            chaseCamera?.SnapToTarget();
        }

        private void ApplyConditions()
        {
            if (timeOfDay == null)
            {
                return;
            }

            timeOfDay.TimeOfDayHours = Mathf.Repeat(PlayerChoices.Hours, 24f);
            timeOfDay.Overcast = PlayerChoices.OvercastFor(PlayerChoices.Weather);
            timeOfDay.Apply();
        }

        // --- Showing which one is chosen.

        private void RefreshAll()
        {
            int cars = bodySet != null ? bodySet.BodyCount : carRows.Length;
            int paints = bodySet != null ? bodySet.PaintCount : paintSwatches.Length;
            int places = pauseMenu?.SpawnPoints != null ? pauseMenu.SpawnPoints.Count : placeRows.Length;

            Highlight(carRows, PlayerChoices.CarIn(cars));
            Highlight(placeRows, PlayerChoices.SpawnIn(places));
            Highlight(weatherRows, (int)PlayerChoices.Weather);
            Highlight(qualityRows, (int)PlayerChoices.Quality);

            // The paint swatches carry their own colour, so tinting them to show selection would repaint
            // them. They get an outline instead — see MenuUiSetup — and that outline is the child.
            int chosenPaint = PlayerChoices.PaintIn(paints);
            for (int i = 0; i < paintSwatches.Length; i++)
            {
                if (paintSwatches[i] == null || paintSwatches[i].transform.childCount == 0)
                {
                    continue;
                }

                paintSwatches[i].transform.GetChild(0).gameObject.SetActive(i == chosenPaint);
            }

            SetText(carLabel, bodySet != null ? bodySet.NameOf(PlayerChoices.CarIn(cars)) : "—");
            SetText(paintLabel, chosenPaint < CarPaintNames.Length ? CarPaintNames[chosenPaint] : "—");
            SetText(placeLabel, NameOfPlace(PlayerChoices.SpawnIn(places)));
            SetText(weatherLabel, PlayerChoices.WeatherNames[(int)PlayerChoices.Weather]);
            SetText(qualityLabel, PlayerChoices.QualityNames[(int)PlayerChoices.Quality]);

            ShowChosenPlace();
        }

        /// <summary>
        /// Scrolls the place list so the chosen place is on the screen.
        ///
        /// <para><b>Because the list is longer than the window it is seen through.</b> A player whose
        /// last start was the ninth place opens the page, sees the first six, and none of them is
        /// highlighted — which reads as "nothing is selected" rather than as "scroll down". The
        /// highlight has to be where the eye lands.</para>
        ///
        /// <para>Public and wired to the two buttons that open the page, as well as being called from
        /// <see cref="RefreshAll"/>. The page is hidden when the screen first refreshes, and a
        /// <c>ScrollRect</c> whose content has never been laid out has nothing to scroll — so doing it
        /// only once, early, does it to a list of height zero.</para>
        ///
        /// <para>The layout is forced first for the same reason: the page is activated and this runs in
        /// the same frame, before the canvas update that the content size fitter lives in.</para>
        ///
        /// <para>Position is set in the list's own normalised terms rather than by moving the content,
        /// so it stays right whatever the row height and the row count turn out to be. Unity measures
        /// vertical position from the bottom, which is why the fraction is subtracted from one.</para>
        /// </summary>
        public void ShowChosenPlace()
        {
            if (placeList == null || placeList.content == null || placeRows.Length < 2)
            {
                return;
            }

            int places = pauseMenu?.SpawnPoints != null ? pauseMenu.SpawnPoints.Count : placeRows.Length;
            int chosen = PlayerChoices.SpawnIn(places);

            LayoutRebuilder.ForceRebuildLayoutImmediate(placeList.content);

            float span = Mathf.Max(1f, placeRows.Length - 1f);
            placeList.verticalNormalizedPosition = 1f - Mathf.Clamp01(chosen / span);
        }

        /// <summary>
        /// The paint names, for the summary line on the front page.
        ///
        /// <para>Duplicated from <c>CarPaintPalette</c> rather than referenced: that class is
        /// editor-only, because it also creates the material assets. Names, not colours — the colours
        /// are on the swatches, which the build tool fills in from the palette itself, so there is no
        /// second copy of anything that could be visibly wrong.</para>
        /// </summary>
        private static readonly string[] CarPaintNames =
        {
            "Ember", "Midnight", "Racing Green", "Chalk",
            "Sunflower", "Signal Red", "Ice Blue", "Graphite",
        };

        private string NameOfPlace(int index)
        {
            var places = pauseMenu?.SpawnPoints;
            return places != null && index >= 0 && index < places.Count ? places[index].Name : "—";
        }

        private void Highlight(Image[] rows, int selected)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null)
                {
                    rows[i].color = i == selected ? selectedTint : unselectedTint;
                }
            }
        }

        private static void SetText(Text label, string value)
        {
            if (label != null)
            {
                label.text = value;
            }
        }

        /// <summary>
        /// Whether the world has arrived. Exposed for the debug overlay, which is the only thing that
        /// has any business asking.
        /// </summary>
        public bool WorldReady => worldReady;
    }
}
