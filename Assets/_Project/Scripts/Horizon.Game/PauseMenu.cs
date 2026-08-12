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
        [SerializeField] private GameObject menuPanel;
        [SerializeField] private GameObject settingsPanel;
        [SerializeField] private GameObject startPanel;
        [SerializeField] private GameObject pauseButton;

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

        private const string SensitivityKey = "Horizon.SteerSensitivity";

        /// <summary>
        /// What the setting was saved under while it was degrees of phone roll rather than a normalised
        /// sharpness. Read once and converted — a player who had already found the tilt range that suits
        /// them should not be put back to the middle because the setting was generalised underneath
        /// them. Same reasoning as <c>DriveInputRouter</c>'s legacy scheme key.
        /// </summary>
        private const string LegacyTiltRangeKey = "Horizon.TiltRangeDegrees";

        private VehicleController vehicle;
        private TimeOfDayController timeOfDay;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool spawnCaptured;

        public bool IsPaused { get; private set; }

        private void Update()
        {
            // The world arrives a frame or two after Bootstrap, and the spawn has to be captured once
            // it has — Respawn is otherwise a teleport to the origin, five kilometres under the pass.
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }
            else if (!spawnCaptured)
            {
                spawnPosition = vehicle.transform.position;
                spawnRotation = vehicle.transform.rotation;
                spawnCaptured = true;
            }

            // Escape on desktop, the hardware back button on Android — a phone player reaches for back
            // before they look for a button.
            if (UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Toggle();
            }
        }

        public void Toggle()
        {
            SetPaused(!IsPaused);
        }

        public void Resume()
        {
            SetPaused(false);
        }

        public void SetPaused(bool value)
        {
            IsPaused = value;

            // Zero stops FixedUpdate, and the whole vehicle lives there. The input router and the
            // widgets deliberately run on unscaled time so they keep working while this is zero.
            Time.timeScale = value ? 0f : 1f;
            AudioListener.pause = value;

            if (menuPanel != null)
            {
                menuPanel.SetActive(value);
            }

            if (settingsPanel != null && !value)
            {
                settingsPanel.SetActive(false);
            }

            if (startPanel != null && !value)
            {
                startPanel.SetActive(false);
            }

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
        /// Shows the settings and <b>hides the pause menu behind it</b>.
        ///
        /// The second half is not a detail. Both panels are children of the same canvas at the same
        /// depth, so leaving the first one up draws two lots of translucent panel and two sets of
        /// buttons through each other, and neither can be read — which is exactly what it did.
        /// </summary>
        public void OpenSettings()
        {
            if (menuPanel != null)
            {
                menuPanel.SetActive(false);
            }

            if (settingsPanel != null)
            {
                settingsPanel.SetActive(true);
            }

            RefreshSettings();
        }

        /// <summary>Back to the pause menu, which means putting it back as well as taking this away.</summary>
        public void CloseSettings()
        {
            if (settingsPanel != null)
            {
                settingsPanel.SetActive(false);
            }

            if (menuPanel != null && IsPaused)
            {
                menuPanel.SetActive(true);
            }
        }

        /// <summary>Shows the start panel, hiding the pause menu behind it. See <see cref="OpenSettings"/>.</summary>
        public void OpenStart()
        {
            if (menuPanel != null)
            {
                menuPanel.SetActive(false);
            }

            if (startPanel != null)
            {
                startPanel.SetActive(true);
            }

            RefreshStart();
        }

        /// <summary>Back to the pause menu.</summary>
        public void CloseStart()
        {
            if (startPanel != null)
            {
                startPanel.SetActive(false);
            }

            if (menuPanel != null && IsPaused)
            {
                menuPanel.SetActive(true);
            }
        }

        /// <summary>
        /// Puts the car at one of the chosen starting places and resumes.
        ///
        /// <para>Wired to each button with the index baked into the event, so one method serves all of
        /// them and adding a fifth place is a row in the table rather than a method here.</para>
        /// </summary>
        public void StartAt(int index)
        {
            if (vehicle == null || spawnPoints == null
                || index < 0 || index >= spawnPoints.Length)
            {
                return;
            }

            SpawnPoint point = spawnPoints[index];

            // Through the vehicle rather than the transform: Teleport clears the momentum and resets the
            // suspension, and a car dropped somewhere else still carrying 200 km/h of it arrives
            // sideways. It is also what the respawn button has always used.
            vehicle.Teleport(point.Position, point.Rotation);

            // The new place becomes what the respawn button means, or "put the car back" would send you
            // to a village you left twenty kilometres ago.
            spawnPosition = point.Position;
            spawnRotation = point.Rotation;

            Resume();
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

            timeOfDay.TimeOfDayHours = Mathf.Repeat(hours, 24f);
            timeOfDay.Apply();

            ShowTime(timeOfDay.TimeOfDayHours);
        }

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
        public void Respawn()
        {
            if (vehicle == null || !spawnCaptured)
            {
                return;
            }

            var body = vehicle.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            vehicle.transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            Resume();
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

            SetPaused(false);
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
