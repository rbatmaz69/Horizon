using UnityEngine;

namespace Horizon.Atmosphere
{
    /// <summary>
    /// Advances the clock and drives sun, ambient and fog from a <see cref="TimeOfDayProfile"/>.
    ///
    /// Weather is applied as a multiplying layer on top of the profile rather than replacing it, so
    /// "overcast" or "rain" later dim and desaturate whatever the time of day already produced
    /// instead of needing their own full set of gradients.
    /// </summary>
    [ExecuteAlways]
    public sealed class TimeOfDayController : MonoBehaviour
    {
        [SerializeField] private TimeOfDayProfile profile;
        [SerializeField] private Light sun;

        [Header("Clock")]
        [Tooltip("Current time in hours, 0–24. Starts at golden hour.")]
        [Range(0f, 24f)] public float TimeOfDayHours = 17.6f;

        [Tooltip("Real minutes for a full in-game day. Zero freezes the clock.")]
        public float DayLengthMinutes = 24f;

        [Tooltip("Advance the clock automatically. Turn off to hold a specific light.")]
        public bool Running = true;

        [Header("Weather layer")]
        [Tooltip("0 clear, 1 fully overcast. Dims the sun and thickens the fog.")]
        [Range(0f, 1f)] public float Overcast;

        [Header("Quality")]
        [Tooltip("Let the sun cast shadows at all.\n\n"
               + "The single biggest GPU saving available without touching the render pipeline asset, "
               + "which is why the quality setting reaches for it. It has to live here rather than being "
               + "written onto the Light from outside: Apply() rewrites sun.shadows whenever the clock "
               + "moves, so anything set externally would survive until the next minute of game time and "
               + "then quietly come back.")]
        public bool Shadows = true;

        /// <summary>Normalized time of day, 0 at midnight and 0.5 at noon.</summary>
        public float NormalizedTime => Mathf.Repeat(TimeOfDayHours, 24f) / 24f;

        /// <summary>True between sunrise and sunset.</summary>
        public bool IsDaytime => TimeOfDayHours > 6f && TimeOfDayHours < 18f;

        private float lastAppliedHours = float.NaN;
        private float lastAppliedOvercast = float.NaN;

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            if (Application.isPlaying)
            {
                if (Running && DayLengthMinutes > 0f)
                {
                    float hoursPerSecond = 24f / (DayLengthMinutes * 60f);
                    TimeOfDayHours = Mathf.Repeat(TimeOfDayHours + hoursPerSecond * Time.deltaTime, 24f);
                }

                Apply();
                return;
            }

            // In the Editor, applying every tick would rewrite RenderSettings and leave the scene
            // permanently dirty. Only push changes when a value actually moved.
            if (!Mathf.Approximately(TimeOfDayHours, lastAppliedHours)
                || !Mathf.Approximately(Overcast, lastAppliedOvercast))
            {
                Apply();
            }
        }

        /// <summary>Pushes the current time onto the scene's lighting. Safe to call at edit time.</summary>
        public void Apply()
        {
            if (profile == null)
            {
                return;
            }

            float t = NormalizedTime;
            float sunDim = 1f - Overcast * 0.75f;
            lastAppliedHours = TimeOfDayHours;
            lastAppliedOvercast = Overcast;

            if (sun != null)
            {
                // The procedural skybox takes its sun position from here, not from the light itself.
                RenderSettings.sun = sun;

                sun.transform.rotation = Quaternion.Euler(
                    TimeOfDayProfile.SunElevation(TimeOfDayHours),
                    profile.SunAzimuth,
                    0f);

                sun.color = profile.SunColor.Evaluate(t);
                sun.intensity = Mathf.Max(0f, profile.SunIntensity.Evaluate(t)) * sunDim;

                // Shadows off once the sun is below the horizon: they cost a full extra pass for
                // nothing, and a light at negative elevation produces nonsense shadows anyway. The
                // quality setting can switch them off in daylight too — see Shadows.
                sun.shadows = Shadows && sun.intensity > 0.02f ? LightShadows.Soft : LightShadows.None;
                sun.enabled = sun.intensity > 0.001f;
            }

            Color ambient = profile.AmbientColor.Evaluate(t) * sunDim;
            ambient = Color.Lerp(ambient, profile.NightAmbientFloor, Mathf.Max(0f, 1f - ambient.grayscale * 4f));

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = profile.FogColor.Evaluate(t);
            RenderSettings.fogDensity = Mathf.Max(0f, profile.FogDensity.Evaluate(t)) * (1f + Overcast * 1.6f);
        }
    }
}
