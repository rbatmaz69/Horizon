using System.Collections.Generic;
using Horizon.Atmosphere;
using Horizon.Input;
using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// Pause, and the settings behind it: which steering and which pedals, how sharp the steering is,
    /// and a way to re-zero the tilt.
    ///
    /// <para>This exists because there was no way to change anything on a phone. The only scheme
    /// switcher lived in <c>DriveDebugOverlay</c>, which is compiled out of release builds — so a
    /// player who did not get on with tilt steering had no route to anything else, and no way to fix a
    /// tilt that had calibrated itself wrong.</para>
    /// </summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        [SerializeField] private DriveInputRouter router;
        [SerializeField] private TouchControlsHud hud;

        [Header("Panels")]
        [Tooltip("Owns which page is up. See MenuPanels for why that is no longer done here.")]
        [SerializeField] private MenuPanels panels;

        [SerializeField] private GameObject pauseButton;

        [Tooltip("The start screen, if there is one. Null in a scene built without it.")]
        [SerializeField] private StartScreen startScreen;

        [Header("Settings widgets")]
        [SerializeField] private Text schemeLabel;
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private GameObject recalibrateButton;

        [Header("Start widgets")]
        [SerializeField] private Slider timeSlider;
        [SerializeField] private Text timeLabel;

        /// <summary>
        /// Where the player may choose to begin, worked out at build time from the courses themselves.
        ///
        /// <para>Baked rather than found at run time because only the setup tool knows where anything
        /// is: a summit is a distance along a course, a city gateway is a node in a layout table, and
        /// neither is discoverable from a finished scene without re-deriving the thing that placed it.
        /// The same reason the spawn point itself has always been computed there.</para>
        /// </summary>
        [SerializeField] private SpawnPoint[] spawnPoints = new SpawnPoint[0];

        [Tooltip("The weather buttons, so they can be switched off while a guest is taking the sky "
               + "from somebody else.")]
        [SerializeField] private UnityEngine.UI.Button[] weatherButtons = new UnityEngine.UI.Button[0];

        [Tooltip("The 'Weather' section heading on the conditions page. Rewritten while a guest, so "
               + "the reason the buttons do nothing is on the screen rather than a mystery.")]
        [SerializeField] private Text weatherHeading;

        [Tooltip("Found in Bootstrap. Only asked one question: is this device taking its weather from "
               + "a host.")]
        [SerializeField] private NetSession session;

        private const string SensitivityKey = "Horizon.SteerSensitivity";

        /// <summary>
        /// What the setting was saved under while it was degrees of phone roll rather than a normalised
        /// sharpness. Read once and converted — a player who had already found the tilt range that suits
        /// them should not be put back to the middle because the setting was generalised underneath
        /// them. Same reasoning as <c>DriveInputRouter</c>'s legacy scheme key.
        /// </summary>
        private const string LegacyTiltRangeKey = "Horizon.TiltRangeDegrees";

        private VehicleController vehicle;
        private Horizon.Vehicle.FuelTank fuel;
        private TimeOfDayController timeOfDay;
        private Horizon.Core.ChaseCamera chaseCamera;
        private Horizon.World.TrafficNetwork routes;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool spawnCaptured;

        public bool IsPaused { get; private set; }

        /// <summary>
        /// Finds the player's car, if it has arrived.
        ///
        /// <para>Called from <see cref="Update"/> and from everything that moves the car, rather than
        /// only from Update. The start screen puts the car at the chosen place from
        /// <c>GameBootstrap.WireUpWorld</c>, which runs inside a coroutine that resumes the moment the
        /// additive load finishes — not necessarily after an Update in which this had already been
        /// found. Depending on that ordering would mean the very first placement silently doing nothing
        /// on a fast load, and working on a slow one.</para>
        /// </summary>
        private VehicleController ResolveVehicle()
        {
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            return vehicle;
        }

        /// <summary>
        /// Whether the hour and the weather belong to somebody else.
        ///
        /// <para>True for a guest in a room. The host owns the world's clock and its sky, because two
        /// friends in the same valley at different hours is the first thing anybody would photograph —
        /// see <c>NetSession.ApplyHostConditions</c>.</para>
        /// </summary>
        public bool ConditionsLocked => session != null && session.IsGuest;

        private void Update()
        {
            // A guest's conditions page is read-only, and it says so rather than simply not working. A
            // button that silently does nothing is the menu lying about the world, which is the
            // argument the weather presets themselves were held to when rain was added.
            ApplyConditionsLock();

            // The world arrives a frame or two after Bootstrap, and the spawn has to be captured once
            // it has — Respawn is otherwise a teleport to the origin, five kilometres under the pass.
            if (ResolveVehicle() != null && !spawnCaptured)
            {
                spawnPosition = vehicle.transform.position;
                spawnRotation = vehicle.transform.rotation;
                spawnCaptured = true;
            }

            // Escape on desktop, the hardware back button on Android — a phone player reaches for back
            // before they look for a button.
            if (UnityEngine.InputSystem.Keyboard.current == null
                || !UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            // On the start screen it means "back a page", never "resume". Resuming from there would
            // unfreeze the world behind the start screen's opaque backdrop, which is still up until the
            // player presses Drive — so the game would be running underneath a sheet nobody can see
            // past. Back always lands somewhere now, so this cannot dead-end either.
            if (startScreen != null && !startScreen.Finished)
            {
                panels?.Back();
                return;
            }

            Toggle();
        }

        public void Toggle()
        {
            if (IsPaused)
            {
                Resume();
                return;
            }

            SetPaused(true);

            // Back's fallback follows whoever is showing pages. Without this, Back from a settings page
            // reached through pause would try to return to the start screen, which is not on screen and
            // has no way to leave itself once the game has begun.
            panels?.SetHome(MenuPage.Paused);
            panels?.Show(MenuPage.Paused);
        }

        public void Resume()
        {
            SetPaused(false);
            panels?.Hide();
        }

        /// <summary>
        /// Stops or restarts the world.
        ///
        /// <para><b>It no longer decides which panel is up</b>, and that is what lets the start screen
        /// use it. This used to show the PAUSED panel as an inseparable part of pausing, so anything
        /// else that wanted the world stopped got the pause menu whether it wanted it or not. Choosing
        /// the page is the caller's business now — see <see cref="Toggle"/> and
        /// <see cref="StartScreen"/> — and <see cref="MenuPanels"/> owns making one page mean not the
        /// others.</para>
        /// </summary>
        public void SetPaused(bool value)
        {
            IsPaused = value;

            // Zero stops FixedUpdate, and the whole vehicle lives there. The input router and the
            // widgets deliberately run on unscaled time so they keep working while this is zero.
            Time.timeScale = value ? 0f : 1f;
            AudioListener.pause = value;

            if (pauseButton != null)
            {
                pauseButton.SetActive(!value);
            }

            if (hud != null)
            {
                hud.SetPaused(value);
            }

            // Whatever a finger was on when the menu opened is not held any more.
            TouchControlState.Clear();
        }

        /// <summary>
        /// Stops the world and puts the full-screen map up. Wired to the minimap.
        ///
        /// <para>Everything <see cref="Toggle"/> does except which page it lands on — including
        /// <c>SetHome</c>, without which Back from here would try to return to a start screen that is
        /// not on the screen and cannot leave itself once the game has begun. It does not toggle,
        /// because the minimap is inside the instrument group and <c>TouchControlsHud</c> has already
        /// hidden that by the time this returns: there is nothing left to press twice.</para>
        /// </summary>
        public void OpenMap()
        {
            SetPaused(true);

            panels?.SetHome(MenuPage.Paused);
            panels?.Show(MenuPage.Map);
        }

        /// <summary>Shows the controls page.</summary>
        public void OpenSettings()
        {
            panels?.Show(MenuPage.Controls);
            RefreshSettings();
        }

        /// <summary>Shows the page for choosing where to begin, and the hour to begin at.</summary>
        public void OpenStart()
        {
            panels?.Show(MenuPage.Place);
            RefreshStart();
        }

        /// <summary>Back one page. Wired to every Back button.</summary>
        public void CloseSettings()
        {
            panels?.Back();
        }

        /// <summary>The places the setup tool baked in, for anything that needs to list them.</summary>
        public IReadOnlyList<SpawnPoint> SpawnPoints => spawnPoints;

        /// <summary>
        /// Puts the car at one of the chosen starting places, without resuming.
        ///
        /// <para>Split from <see cref="StartAt"/> so the start screen can move the car as the player
        /// taps through the places and let them look at each one. Resuming there would mean choosing
        /// where to begin and being sent off before deciding anything else.</para>
        /// </summary>
        public void MoveTo(int index)
        {
            if (ResolveVehicle() == null || spawnPoints == null
                || index < 0 || index >= spawnPoints.Length)
            {
                return;
            }

            SpawnPoint point = spawnPoints[index];

            // Through the vehicle rather than the transform: Teleport clears the momentum and resets the
            // suspension, and a car dropped somewhere else still carrying 200 km/h of it arrives
            // sideways. It is also what the respawn button has always used.
            vehicle.Teleport(point.Position, point.Rotation);
            FillTank();

            // The new place becomes what the respawn button means, or "put the car back" would send you
            // to a village you left twenty kilometres ago.
            spawnPosition = point.Position;
            spawnRotation = point.Rotation;

            SnapCamera();
        }

        /// <summary>
        /// Puts the car at one of the chosen starting places and resumes.
        ///
        /// <para>Wired to each button with the index baked into the event, so one method serves all of
        /// them and adding a fifth place is a row in the table rather than a method here.</para>
        /// </summary>
        public void StartAt(int index)
        {
            MoveTo(index);
            PlayerChoices.Spawn = index;
            PlayerChoices.Save();
            Resume();
        }

        /// <summary>
        /// Brims the tank, wherever the car has just been put.
        ///
        /// <para><b>Because a respawn that leaves the tank empty is not a recovery.</b> Every path that
        /// calls this exists to get the player out of a situation they cannot drive out of, and an empty
        /// tank is exactly such a situation — put back on the road, pointing the right way, still unable
        /// to move.</para>
        ///
        /// <para><b>It is also why the fuel level is never saved.</b> A run always begins by placing the
        /// car: <c>StartScreen.Drive</c> calls <c>ApplyPlace</c>, which calls <see cref="MoveTo"/>. So
        /// the tank is full at the start of every session no matter what, and a PlayerPrefs key for it
        /// would be a value written on every quit and overwritten before it could ever be read. A
        /// returning player is never handed a car that will not move and no explanation of why.</para>
        /// </summary>
        private void FillTank()
        {
            if (vehicle == null)
            {
                return;
            }

            if (fuel == null)
            {
                fuel = vehicle.GetComponent<Horizon.Vehicle.FuelTank>();
            }

            if (fuel != null)
            {
                fuel.FillFully();
            }
        }

        /// <summary>
        /// Brings the chase camera with the car.
        ///
        /// <para>Necessary because the camera smooths towards its target in <c>LateUpdate</c>, and a
        /// teleport of several kilometres would otherwise be flown rather than cut — at
        /// <c>timeScale 0</c> it would not even do that, since the smoothing has no time to work in and
        /// the camera simply stays where the car used to be, looking at nothing.</para>
        /// </summary>
        private void SnapCamera()
        {
            if (chaseCamera == null)
            {
                chaseCamera = FindFirstObjectByType<Horizon.Core.ChaseCamera>();
            }

            chaseCamera?.SnapToTarget();
        }

        /// <summary>
        /// Sets the hour of the day.
        ///
        /// <para>The clock keeps running from wherever it is put — this is a way to see the place at
        /// dusk, not a pause button for the sun. <c>TimeOfDayController.DayLengthMinutes</c> is what
        /// decides how long it stays there.</para>
        /// </summary>
        public void OnTimeOfDayChanged(float hours)
        {
            if (timeOfDay == null)
            {
                timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            }

            if (timeOfDay == null)
            {
                return;
            }

            // A guest's slider is not interactable, so this can only be reached by the slider being
            // written from code — and the one thing that does that is RefreshStart, putting the host's
            // hour back on the dial. Taking it as a command would be the guest arguing with the room.
            if (ConditionsLocked)
            {
                return;
            }

            timeOfDay.TimeOfDayHours = Mathf.Repeat(hours, 24f);
            timeOfDay.Apply();

            // Remembered, so the world looks the same the next time the game is opened. Saved on every
            // change rather than on leaving the panel: there is no leaving event on a phone, where the
            // way out of a game is to stop looking at it.
            PlayerChoices.Hours = timeOfDay.TimeOfDayHours;
            PlayerChoices.Save();

            ShowTime(timeOfDay.TimeOfDayHours);
        }

        /// <summary>
        /// Sets how thick the air is. See <see cref="WeatherPreset"/> for what this does and, more to
        /// the point, what it does not.
        /// </summary>
        public void SetWeather(int preset)
        {
            if (timeOfDay == null)
            {
                timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            }

            if (timeOfDay == null)
            {
                return;
            }

            PlayerChoices.Weather = (WeatherPreset)Mathf.Clamp(
                preset, (int)WeatherPreset.Clear, (int)WeatherPreset.Rain);

            timeOfDay.Overcast = PlayerChoices.OvercastFor(PlayerChoices.Weather);
            timeOfDay.Apply();

            PlayerChoices.Save();
        }

        /// <summary>
        /// Switches the conditions page between "yours" and "the host's".
        ///
        /// <para>Done every frame rather than on a change, because there is no event to hang it on: a
        /// room can be joined or left from a different page, and the state that decides this lives in
        /// another component. It is four assignments guarded by a comparison.</para>
        /// </summary>
        private void ApplyConditionsLock()
        {
            if (session == null)
            {
                // Lives on the Bootstrap object beside this one, but a scene somebody assembled by
                // hand may not have wired it. Everything else here resolves the same way.
                session = FindFirstObjectByType<NetSession>();
            }

            bool locked = ConditionsLocked;

            if (locked == conditionsLockShown)
            {
                return;
            }

            conditionsLockShown = locked;

            if (timeSlider != null)
            {
                timeSlider.interactable = !locked;
            }

            for (int i = 0; i < weatherButtons.Length; i++)
            {
                if (weatherButtons[i] != null)
                {
                    weatherButtons[i].interactable = !locked;
                }
            }

            if (weatherHeading != null)
            {
                weatherHeading.text = locked ? "Weather — the host decides" : "Weather";
            }
        }

        private bool conditionsLockShown;

        private void RefreshStart()
        {
            if (timeOfDay == null)
            {
                timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            }

            if (timeOfDay == null)
            {
                return;
            }

            // Without notify: the clock has moved on since the panel was last open, and writing the
            // slider back would otherwise fire the listener and re-set the time to itself.
            timeSlider?.SetValueWithoutNotify(timeOfDay.TimeOfDayHours);
            ShowTime(timeOfDay.TimeOfDayHours);
        }

        private void ShowTime(float hours)
        {
            if (timeLabel == null)
            {
                return;
            }

            int hour = Mathf.FloorToInt(hours) % 24;
            int minute = Mathf.FloorToInt((hours - Mathf.Floor(hours)) * 60f);

            timeLabel.text = $"{hour:00}:{minute:00}";
        }

        /// <summary>Steps through the steering methods. Wired to one button rather than four.</summary>
        public void CycleSteering()
        {
            if (router == null)
            {
                return;
            }

            var next = (SteeringMethod)(((int)router.Steering + 1) % 4);
            router.SetSteering(next);
            RefreshSettings();
        }

        /// <summary>Steps through the throttle methods.</summary>
        public void CyclePedals()
        {
            if (router == null)
            {
                return;
            }

            var next = (PedalMethod)(((int)router.Pedals + 1) % 4);
            router.SetPedals(next);
            RefreshSettings();
        }

        /// <summary>
        /// Re-zeroes the tilt where the phone is being held now.
        ///
        /// The most important button on this screen: neutral is a property of how the player is
        /// sitting, and until this existed a bad calibration could only be escaped by killing the app.
        /// </summary>
        public void RecalibrateTilt()
        {
            router?.Tilt?.Calibrate();
        }

        /// <summary>Puts the car back where it started, upright. For when it is on its roof.</summary>
        /// <summary>
        /// Puts the car back on the road, upright, <b>where it left it</b> — not where it started.
        ///
        /// <para>Going back to the start was the wrong thing twice over. It undid a drive the player had
        /// no wish to undo, and after the world grew to twenty kilometres it could mean being sent from
        /// a city back to a village half an hour away for the crime of clipping a kerb.</para>
        ///
        /// <para>It also did not reliably work. It wrote the <i>transform</i> and left the Rigidbody
        /// where it was; the body is non-kinematic and interpolated, so PhysX kept its own pose and put
        /// the car back where it had been on the next physics step. Sometimes. <c>Teleport</c> moves the
        /// body, the transform and the suspension together, which is why the spawn menu already used
        /// it.</para>
        /// </summary>
        public void Respawn()
        {
            if (ResolveVehicle() == null)
            {
                return;
            }

            if (!TryNearestRoad(vehicle.transform.position, out Vector3 position, out Quaternion rotation))
            {
                // No routes baked — fall back to where the car started, which is what this always did.
                if (!spawnCaptured)
                {
                    return;
                }

                position = spawnPosition;
                rotation = spawnRotation;
            }

            vehicle.Teleport(position, rotation);
            FillTank();
            SnapCamera();
            Resume();
        }

        /// <summary>
        /// The nearest point on any driveable road, resolving the traffic network on first use.
        ///
        /// <para>The search itself lives in <see cref="RoadRespawn"/>: the water hazard needs the same
        /// answer, and it is not a thing to have two of.</para>
        /// </summary>
        private bool TryNearestRoad(Vector3 from, out Vector3 position, out Quaternion rotation)
        {
            if (routes == null)
            {
                TrafficDirector director = FindFirstObjectByType<TrafficDirector>();
                routes = director != null ? director.Network : null;
            }

            return RoadRespawn.TryNearest(
                routes, from, RoadRespawn.RideHeight(vehicle), out position, out rotation);
        }

        /// <summary>
        /// One sharpness for every steering scheme, 0 to 1. Each scheme reads
        /// <see cref="TouchControlState.SteerSensitivity01"/> and decides for itself what that means in
        /// its own units, so nothing here has to know which one is active.
        /// </summary>
        public void OnSteerSensitivityChanged(float value)
        {
            TouchControlState.SteerSensitivity01 = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(SensitivityKey, TouchControlState.SteerSensitivity01);
            PlayerPrefs.Save();
        }

        private void Start()
        {
            if (router == null)
            {
                router = FindFirstObjectByType<DriveInputRouter>();
            }

            LoadSensitivity();

            // Unpausing here is only right when nothing else is going to pause. With a start screen in
            // the scene, that screen has already called SetPaused(true) from its Awake — which always
            // runs before any Start — and unpausing over the top of it would drop the player straight
            // into the world with the menu still drawn on it.
            if (startScreen == null || startScreen.Finished)
            {
                SetPaused(false);
            }

            RefreshSettings();
        }

        private void LoadSensitivity()
        {
            float value = TouchControlState.DefaultSensitivity;

            if (PlayerPrefs.HasKey(SensitivityKey))
            {
                value = PlayerPrefs.GetFloat(SensitivityKey);
            }
            else if (PlayerPrefs.HasKey(LegacyTiltRangeKey))
            {
                // Degrees of roll ran the other way round: a narrow range is a sharp setting.
                value = Mathf.InverseLerp(
                    TiltInput.WidestRange, TiltInput.NarrowestRange,
                    PlayerPrefs.GetFloat(LegacyTiltRangeKey));

                PlayerPrefs.SetFloat(SensitivityKey, value);
                PlayerPrefs.DeleteKey(LegacyTiltRangeKey);
                PlayerPrefs.Save();
            }

            TouchControlState.SteerSensitivity01 = Mathf.Clamp01(value);

            if (sensitivitySlider != null)
            {
                sensitivitySlider.SetValueWithoutNotify(TouchControlState.SteerSensitivity01);
            }
        }

        private void RefreshSettings()
        {
            if (schemeLabel != null && router != null)
            {
                schemeLabel.text = router.ActiveSchemeName;
            }

            // Only offer to recalibrate the thing that can be calibrated.
            if (recalibrateButton != null && router != null)
            {
                recalibrateButton.SetActive(router.Steering == SteeringMethod.Tilt);
            }
        }
    }
}
