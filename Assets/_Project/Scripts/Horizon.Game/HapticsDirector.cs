using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Decides when the phone shakes.
    ///
    /// <para>The join, in the shape <see cref="ImpactEffects"/> and <see cref="DriveFeel"/> already use:
    /// the car publishes, this reads, and <c>Horizon.Vehicle</c> never learns that a phone exists. One
    /// owner of the device, so nothing else can fire it and there is a single place that knows what a
    /// given event is worth.</para>
    ///
    /// <para><b>Events only, never a continuous rumble.</b> That is the distinction <c>ContactAudio</c>
    /// already draws and it is the right one twice over here. A scrape is a state and it belongs to the
    /// sound, which can hold a level; a motor asked to hold one drains the battery and, on the eccentric
    /// mass in a phone, arrives as a buzz that swamps the events worth feeling. So there are three
    /// things and they are all moments: hitting something, changing gear, and the tyres finding the
    /// verge.</para>
    ///
    /// <para><b>Wheelspin is deliberately not one of them</b>, and it is the interesting omission. It was
    /// on the list, and it is a state rather than an event — a wheel lit up for two seconds out of a
    /// hairpin would be either one tick that says nothing about the two seconds, or a stream of them,
    /// which is the buzz again. It already has a voice: the tyre squeal is on exactly this number.</para>
    /// </summary>
    public sealed class HapticsDirector : MonoBehaviour
    {
        [Tooltip("The car to listen to. Found at run time if left empty.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("Scales everything. Zero is off, and it is what a player who dislikes this will want.")]
        [Range(0f, 1f)]
        [SerializeField] private float amount = 1f;

        [Header("Impact")]
        [Tooltip("Milliseconds at the lightest impact worth feeling.")]
        [SerializeField] private int lightImpactMilliseconds = 18;

        [Tooltip("Milliseconds at a full-severity crash.\n\n"
               + "Short even at the top. A phone's motor takes a few tens of milliseconds to spin up and "
               + "as long again to stop, so anything past about a tenth of a second stops reading as a "
               + "blow and starts reading as an alert — which is the one thing this must never feel "
               + "like.")]
        [SerializeField] private int heavyImpactMilliseconds = 90;

        [Tooltip("Amplitude at the lightest impact, 0 to 1.")]
        [Range(0f, 1f)]
        [SerializeField] private float lightImpactAmplitude = 0.25f;

        [Header("Gearshift")]
        [Tooltip("A short knock as the next gear engages. Off at zero.")]
        [Range(0f, 1f)]
        [SerializeField] private float shiftAmplitude = 0.3f;

        [SerializeField] private int shiftMilliseconds = 14;

        [Header("Verge")]
        [Tooltip("Surface roughness above which the tyres count as having left the asphalt.")]
        [Range(0f, 1f)]
        [SerializeField] private float vergeRoughness = 0.35f;

        [Tooltip("Roughness the car has to come back under before the verge can be felt again — "
               + "hysteresis, for the reason TownLights gives about dusk. Without it a wheel riding the "
               + "asphalt-to-gravel edge chatters the motor at fifty hertz.")]
        [Range(0f, 1f)]
        [SerializeField] private float vergeRelease = 0.2f;

        [Range(0f, 1f)]
        [SerializeField] private float vergeAmplitude = 0.35f;

        [SerializeField] private int vergeMilliseconds = 22;

        /// <summary>How often the car is looked for while it is missing. See <see cref="ImpactEffects"/>.</summary>
        private const float SearchInterval = 0.5f;

        private IHaptics device = new NullHaptics();
        private VehicleController subscribed;
        private float nextSearch;
        private bool onVerge;
        private bool wasShifting;

        /// <summary>
        /// What the last pulse asked for, so the editor can see a feature it can never feel.
        ///
        /// <para>This is the whole testing strategy for haptics and not a debug leftover.
        /// <c>DriveDebugOverlay</c> prints it, so when and how hard the phone would shake is tunable at a
        /// desk, and the device is only needed to confirm the last step.</para>
        /// </summary>
        public string LastPulse { get; private set; } = "none";

        private void Awake()
        {
            var android = new AndroidHaptics();
            device = android.Available ? android : (IHaptics)new NullHaptics();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (subscribed == null || vehicle != subscribed)
            {
                if (Time.unscaledTime < nextSearch)
                {
                    return;
                }

                nextSearch = Time.unscaledTime + SearchInterval;

                if (vehicle == null)
                {
                    vehicle = FindFirstObjectByType<VehicleController>();
                }

                if (vehicle != subscribed)
                {
                    Unsubscribe();
                    subscribed = vehicle;

                    if (subscribed != null)
                    {
                        subscribed.Impacted += OnImpacted;
                    }
                }

                if (subscribed == null)
                {
                    return;
                }
            }

            UpdateShift();
            UpdateVerge();
        }

        private void Unsubscribe()
        {
            if (subscribed != null)
            {
                subscribed.Impacted -= OnImpacted;
                subscribed = null;
            }
        }

        private void OnImpacted(float severity, Vector3 point)
        {
            int milliseconds = Mathf.RoundToInt(
                Mathf.Lerp(lightImpactMilliseconds, heavyImpactMilliseconds, severity));

            Pulse(milliseconds, Mathf.Lerp(lightImpactAmplitude, 1f, severity), "impact");
        }

        /// <summary>A knock as the gearbox lets the clutch back in.</summary>
        private void UpdateShift()
        {
            bool shifting = subscribed.IsShifting;

            // The falling edge, not the rising one. A shift starts with the throttle being cut and ends
            // with the next gear taking the load, and it is the second of those the driver feels.
            if (wasShifting && !shifting)
            {
                Pulse(shiftMilliseconds, shiftAmplitude, "shift");
            }

            wasShifting = shifting;
        }

        /// <summary>One knock as the tyres leave the asphalt, not a rumble for as long as they are off it.</summary>
        private void UpdateVerge()
        {
            float roughness = subscribed.SurfaceRoughness;

            if (!onVerge && roughness > vergeRoughness)
            {
                onVerge = true;
                Pulse(vergeMilliseconds, vergeAmplitude, "verge");
            }
            else if (onVerge && roughness < vergeRelease)
            {
                onVerge = false;
            }
        }

        private void Pulse(int milliseconds, float amplitude, string what)
        {
            float scaled = amplitude * amount;
            LastPulse = $"{what} {milliseconds} ms at {scaled:0.00}";

            if (scaled > 0.001f)
            {
                device.Pulse(milliseconds, scaled);
            }
        }
    }
}
