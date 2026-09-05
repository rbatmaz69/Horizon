using System;
using UnityEngine;

namespace Horizon.Input
{
    /// <summary>
    /// Owns the control methods, samples the active pair, and smooths the result. Deadzone and
    /// smoothing live here — once — so every scheme drives the same car. Speed-dependent steering
    /// reduction deliberately does *not* live here: that is a property of the vehicle and belongs
    /// to <c>VehicleConfig.SteeringBySpeed</c>.
    ///
    /// <para><b>Steering and pedals are chosen separately.</b> There used to be four whole schemes,
    /// which offered four of the twelve combinations that actually exist — you could have tilt with an
    /// automatic throttle or buttons with pedals, but not the wheel with a slider, for no reason other
    /// than that nobody had written that class. Two halves compose into all of them.</para>
    /// </summary>
    public sealed class DriveInputRouter : MonoBehaviour, IDriveInput
    {
        [Header("Smoothing")]
        [Tooltip("Seconds for steering to catch up to the raw input. Higher feels heavier.\n\n"
               + "Down from 0.09, which was too much of a good thing once the on-screen controls got "
               + "proportional. The wheel already hands over a smooth, self-centring angle and the arrows "
               + "already ramp, so this was smoothing something that was not rough — 90 ms of pure delay "
               + "on top of a rack that has its own rate limit, all of it read as the car answering late.")]
        [SerializeField] private float steerSmoothTime = 0.05f;

        [Tooltip("Units per second the throttle and brake ramp toward their raw values.")]
        [SerializeField] private float pedalRate = 6f;

        [Tooltip("Raw steering below this is treated as centred.")]
        [SerializeField] private float steerDeadzone = 0.02f;

        [Header("Defaults")]
        [Tooltip("Used when nothing has been saved yet. On a phone this is overridden — see LoadPreferences.")]
        [SerializeField] private SteeringMethod defaultSteering = SteeringMethod.KeyboardGamepad;

        [SerializeField] private PedalMethod defaultPedals = PedalMethod.KeyboardGamepad;

        private const string SteeringKey = "Horizon.Steering";
        private const string PedalKey = "Horizon.Pedals";

        /// <summary>
        /// The key the four old whole-schemes were saved under.
        ///
        /// Read once and translated — a player who had already chosen tilt should not silently be put
        /// back on the keyboard because the settings were restructured underneath them.
        /// </summary>
        private const string LegacySchemeKey = "Horizon.InputScheme";

        private readonly TiltInput tilt = new TiltInput();

        private ISteerInput steer;
        private IPedalInput pedals;
        private float steerVelocity;

        public float Steer { get; private set; }
        public float Throttle { get; private set; }
        public float Brake { get; private set; }
        public bool Handbrake { get; private set; }

        public SteeringMethod Steering { get; private set; }

        public PedalMethod Pedals { get; private set; }

        /// <summary>The tilt source, so the pause menu can re-zero it where the player is sitting.</summary>
        public TiltInput Tilt => tilt;

        /// <summary>Display names of the active pair, for the settings screen and the debug overlay.</summary>
        public string ActiveSchemeName =>
            steer != null && pedals != null ? $"{steer.DisplayName} + {pedals.DisplayName}" : "None";

        /// <summary>Raised after either half changes, so the on-screen controls can rebuild.</summary>
        public event Action SchemeChanged;

        private void Awake()
        {
            LoadPreferences();
        }

        private void OnEnable()
        {
            Apply(Steering, Pedals, save: false);
            DriveInput.Current = this;
        }

        private void OnDisable()
        {
            steer?.Disable();
            pedals?.Disable();
            steer = null;
            pedals = null;

            if (ReferenceEquals(DriveInput.Current, this))
            {
                DriveInput.Current = NullDriveInput.Instance;
            }
        }

        private void Update()
        {
            if (steer == null || pedals == null)
            {
                return;
            }

            // Unscaled, because the pause menu sets the time scale to zero and input still has to be
            // sampled — otherwise the smoothing below freezes mid-corner and the car resumes with
            // whatever was held a moment before it paused.
            float deltaTime = Time.unscaledDeltaTime;

            steer.Sample(deltaTime);
            pedals.Sample(deltaTime);

            float rawSteer = Mathf.Abs(steer.Steer) <= steerDeadzone ? 0f : steer.Steer;
            Steer = Mathf.SmoothDamp(Steer, rawSteer, ref steerVelocity, steerSmoothTime, Mathf.Infinity, deltaTime);
            Throttle = Mathf.MoveTowards(Throttle, pedals.Throttle, pedalRate * deltaTime);
            Brake = Mathf.MoveTowards(Brake, pedals.Brake, pedalRate * deltaTime);
            Handbrake = pedals.Handbrake;
        }

        /// <summary>Switches the steering half, keeping the pedals.</summary>
        public void SetSteering(SteeringMethod method)
        {
            Apply(method, Pedals, save: true);
        }

        /// <summary>
        /// Lets go of every on-screen control when the app stops being the thing in front of the
        /// player.
        ///
        /// <para><b>A pedal is held by a finger, and a finger that leaves with the app never sends its
        /// release.</b> <c>TouchHoldButton</c> writes on pointer-down and clears on pointer-up, and
        /// clears again in <c>OnDisable</c> — which covers the pause menu, because that hides the
        /// controls. It does not cover a notification pulling the shade down, a call arriving, the home
        /// gesture, or Android's edge-swipe cancelling a touch: the app keeps running, the widget stays
        /// enabled, and the pointer-up simply never arrives. What is left behind is a throttle latched
        /// at 1 — a car that accelerates on its own — or a brake latched at 1, which below 0.6 m/s is
        /// this car's reverse, so it drives itself backwards.</para>
        ///
        /// <para>Both callbacks, because Android does not use them interchangeably: losing focus to a
        /// notification shade raises one, going to the background raises the other, and which of the
        /// two arrives depends on the launcher and the gesture. Clearing twice costs six
        /// assignments.</para>
        /// </summary>
        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                TouchControlState.Clear();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                TouchControlState.Clear();
            }
        }

        /// <summary>Switches the pedal half, keeping the steering.</summary>
        public void SetPedals(PedalMethod method)
        {
            Apply(Steering, method, save: true);
        }

        private void Apply(SteeringMethod steering, PedalMethod pedal, bool save)
        {
            steer?.Disable();
            pedals?.Disable();

            // Anything a widget was holding belongs to the controls that are going away.
            TouchControlState.Clear();

            Steering = steering;
            Pedals = pedal;

            steer = CreateSteering(steering);
            pedals = CreatePedals(pedal);

            steer.Enable();
            pedals.Enable();

            Steer = 0f;
            Throttle = 0f;
            Brake = 0f;
            Handbrake = false;
            steerVelocity = 0f;

            if (save)
            {
                PlayerPrefs.SetInt(SteeringKey, (int)steering);
                PlayerPrefs.SetInt(PedalKey, (int)pedal);
                PlayerPrefs.Save();
            }

            SchemeChanged?.Invoke();
        }

        private ISteerInput CreateSteering(SteeringMethod method)
        {
            switch (method)
            {
                case SteeringMethod.Tilt:
                    return tilt;
                case SteeringMethod.Wheel:
                    return new TouchSteer(true);
                case SteeringMethod.Arrows:
                    return new TouchSteer(false);
                default:
                    return new KeyboardSteer();
            }
        }

        private IPedalInput CreatePedals(PedalMethod method)
        {
            switch (method)
            {
                case PedalMethod.Auto:
                    return new AutoPedals();
                case PedalMethod.Pedals:
                    return new TouchPedals(false);
                case PedalMethod.Slider:
                    return new TouchPedals(true);
                default:
                    return new KeyboardPedals();
            }
        }

        /// <summary>
        /// Restores the saved pair, translating the old single-scheme preference if that is all there is.
        ///
        /// On a phone with nothing saved the keyboard default would be a game with no controls at all,
        /// so mobile starts on tilt and pedals — visible, and the combination the on-screen controls
        /// were designed around.
        /// </summary>
        private void LoadPreferences()
        {
            if (PlayerPrefs.HasKey(SteeringKey) && PlayerPrefs.HasKey(PedalKey))
            {
                Steering = Enum.IsDefined(typeof(SteeringMethod), PlayerPrefs.GetInt(SteeringKey))
                    ? (SteeringMethod)PlayerPrefs.GetInt(SteeringKey)
                    : defaultSteering;

                Pedals = Enum.IsDefined(typeof(PedalMethod), PlayerPrefs.GetInt(PedalKey))
                    ? (PedalMethod)PlayerPrefs.GetInt(PedalKey)
                    : defaultPedals;

                return;
            }

            if (PlayerPrefs.HasKey(LegacySchemeKey))
            {
                // 0 keyboard, 1 tilt, 2 drag-to-steer, 3 on-screen buttons. Drag-to-steer no longer
                // exists; its players get the arrows, which is the nearest thing that does.
                switch (PlayerPrefs.GetInt(LegacySchemeKey))
                {
                    case 1:
                        Steering = SteeringMethod.Tilt;
                        Pedals = PedalMethod.Auto;
                        return;
                    case 2:
                    case 3:
                        Steering = SteeringMethod.Arrows;
                        Pedals = PedalMethod.Pedals;
                        return;
                    default:
                        Steering = SteeringMethod.KeyboardGamepad;
                        Pedals = PedalMethod.KeyboardGamepad;
                        return;
                }
            }

            if (Application.isMobilePlatform)
            {
                // The wheel rather than tilt, now that it tracks a thumb properly. It is the most
                // precise of the four and the only one that is obvious on sight; tilt asks the player to
                // discover that the phone itself is the control, and needs calibrating before it is
                // even usable. Both are still one menu apart.
                Steering = SteeringMethod.Wheel;
                Pedals = PedalMethod.Pedals;
                return;
            }

            Steering = defaultSteering;
            Pedals = defaultPedals;
        }
    }
}
