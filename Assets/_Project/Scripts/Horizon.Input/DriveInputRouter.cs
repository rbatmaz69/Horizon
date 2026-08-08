using System;
using System.Collections.Generic;
using UnityEngine;

namespace Horizon.Input
{
    /// <summary>
    /// Owns the control schemes, samples the active one, and smooths the result. Deadzone and
    /// smoothing live here — once — so every scheme drives the same car. Speed-dependent steering
    /// reduction deliberately does *not* live here: that is a property of the vehicle and belongs
    /// to <c>VehicleConfig.SteeringBySpeed</c>.
    /// </summary>
    public sealed class DriveInputRouter : MonoBehaviour, IDriveInput
    {
        [Header("Smoothing")]
        [Tooltip("Seconds for steering to catch up to the raw input. Higher feels heavier.")]
        [SerializeField] private float steerSmoothTime = 0.09f;

        [Tooltip("Units per second the throttle and brake ramp toward their raw values.")]
        [SerializeField] private float pedalRate = 6f;

        [Tooltip("Raw steering below this is treated as centred.")]
        [SerializeField] private float steerDeadzone = 0.02f;

        [Header("Scheme")]
        [Tooltip("Scheme used when no preference has been saved yet.")]
        [SerializeField] private DriveInputScheme defaultScheme = DriveInputScheme.KeyboardGamepad;

        private const string SchemePreferenceKey = "Horizon.InputScheme";

        private readonly Dictionary<DriveInputScheme, DriveInputSource> sources =
            new Dictionary<DriveInputScheme, DriveInputSource>(4);

        private DriveInputSource active;
        private float steerVelocity;

        public float Steer { get; private set; }
        public float Throttle { get; private set; }
        public float Brake { get; private set; }
        public bool Handbrake { get; private set; }

        /// <summary>The active scheme. Setting it swaps sources and saves the preference.</summary>
        public DriveInputScheme Scheme { get; private set; }

        /// <summary>Display name of the active scheme, for the HUD.</summary>
        public string ActiveSchemeName => active != null ? active.DisplayName : "None";

        /// <summary>Touch regions the active scheme reads. Never null.</summary>
        public IReadOnlyList<TouchZone> ActiveZones =>
            active != null ? active.Zones : Array.Empty<TouchZone>();

        /// <summary>Raised after a scheme change, so HUDs can rebuild.</summary>
        public event Action<DriveInputScheme> SchemeChanged;

        private void Awake()
        {
            sources[DriveInputScheme.KeyboardGamepad] = new KeyboardGamepadInput();
            sources[DriveInputScheme.Tilt] = new TiltInput();
            sources[DriveInputScheme.TouchSteer] = new TouchSteerInput();
            sources[DriveInputScheme.TouchButtons] = new TouchButtonInput();

            Scheme = LoadPreferredScheme();
        }

        private void OnEnable()
        {
            SetScheme(Scheme, save: false);
            DriveInput.Current = this;
        }

        private void OnDisable()
        {
            if (active != null)
            {
                active.Disable();
                active = null;
            }

            if (ReferenceEquals(DriveInput.Current, this))
            {
                DriveInput.Current = NullDriveInput.Instance;
            }
        }

        private void Update()
        {
            if (active == null)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            active.Sample(deltaTime);

            float rawSteer = Mathf.Abs(active.Steer) <= steerDeadzone ? 0f : active.Steer;
            Steer = Mathf.SmoothDamp(Steer, rawSteer, ref steerVelocity, steerSmoothTime);
            Throttle = Mathf.MoveTowards(Throttle, active.Throttle, pedalRate * deltaTime);
            Brake = Mathf.MoveTowards(Brake, active.Brake, pedalRate * deltaTime);
            Handbrake = active.Handbrake;
        }

        /// <summary>Switches control scheme, optionally persisting the choice.</summary>
        public void SetScheme(DriveInputScheme scheme, bool save = true)
        {
            if (!sources.TryGetValue(scheme, out DriveInputSource next))
            {
                Debug.LogWarning($"[Horizon] No input source registered for {scheme}.", this);
                return;
            }

            if (active != null)
            {
                active.Disable();
            }

            Scheme = scheme;
            active = next;
            active.Enable();

            // Drop any carried-over steering so the car does not jerk on the swap.
            Steer = 0f;
            steerVelocity = 0f;

            if (save)
            {
                PlayerPrefs.SetInt(SchemePreferenceKey, (int)scheme);
                PlayerPrefs.Save();
            }

            SchemeChanged?.Invoke(scheme);
        }

        /// <summary>Steps to the next scheme. Wired to the debug overlay.</summary>
        public void CycleScheme()
        {
            int next = ((int)Scheme + 1) % 4;
            SetScheme((DriveInputScheme)next);
        }

        /// <summary>Re-captures the neutral hold angle, if the active scheme is tilt.</summary>
        public void CalibrateTilt()
        {
            if (active is TiltInput tilt)
            {
                tilt.Calibrate();
            }
        }

        private DriveInputScheme LoadPreferredScheme()
        {
            if (!PlayerPrefs.HasKey(SchemePreferenceKey))
            {
                // On desktop the touch schemes are only testable via the mouse fallback, so start
                // with the scheme that actually lets us tune the car.
                return Application.isMobilePlatform ? DriveInputScheme.Tilt : defaultScheme;
            }

            int stored = PlayerPrefs.GetInt(SchemePreferenceKey);
            return Enum.IsDefined(typeof(DriveInputScheme), stored)
                ? (DriveInputScheme)stored
                : defaultScheme;
        }
    }
}
