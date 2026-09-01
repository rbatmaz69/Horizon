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

        [Header("Sky")]
        [Tooltip("The fair-weather sky. Whatever the scene was built with.")]
        [SerializeField] private Material clearSky;

        [Tooltip("The bad-weather sky: grey, thick, and dim.\n\n"
               + "Two finished materials swapped between, never one material edited — Unity does not "
               + "roll an asset change back when Play mode ends, so a player who tried the rain once "
               + "would leave a skybox modified in the working tree. Same rule TownLights follows.")]
        [SerializeField] private Material overcastSky;

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

        [Header("Speed layer")]
        [Tooltip("How hard the viewer is travelling, 0 to 1. Written every frame by "
               + "Horizon.Game's SpeedAtmosphere — nothing sets it by hand.\n\n"
               + "It lives here as a plain field for the same reason Overcast does: fog belongs to this "
               + "class and is rewritten by Apply() every frame, so anything writing RenderSettings.fog "
               + "from outside would survive exactly until the next frame. Atmosphere cannot see the "
               + "vehicle module either, and must not — so the value is pushed in rather than pulled.")]
        [Range(0f, 1f)] public float SpeedHaze;

        [Tooltip("How much thicker the fog gets at full speed, as a fraction added to the density.\n\n"
               + "This is the world closing in on a driver going too fast, and it is honest rather than "
               + "a trick: at 235 km/h it cuts the sight line from around 790 m to 330 m, which is five "
               + "seconds of road. Corners on the pass start arriving with less warning than the driver "
               + "would like, and that is the whole intent.")]
        public float SpeedFogGain = 1.4f;

        [Tooltip("How far the fog darkens at full speed, 0 to 1. Small on purpose — the art direction "
               + "is warm and inviting, and this is the one place allowed to argue with it.")]
        [Range(0f, 0.5f)] public float SpeedFogDarkening = 0.1f;

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
        private float lastAppliedSpeedHaze = float.NaN;

        /// <summary>Which sky is currently up, so it is only assigned when it actually changes.</summary>
        private bool skyIsOvercast;

        /// <summary>
        /// Overcast at which the grey sky comes in, and the one it goes out at.
        ///
        /// <para>Hysteresis for the reason <c>TownLights</c> gives about dusk: a single threshold sits
        /// exactly where a slider is dragged and the sky flickers between the two under the player's
        /// thumb. Hazy is 0.45 and deliberately below the band — haze is a thin day, not a grey one.</para>
        /// </summary>
        private const float OvercastSkyOn = 0.60f;

        private const float OvercastSkyOff = 0.52f;

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
                || !Mathf.Approximately(Overcast, lastAppliedOvercast)
                || !Mathf.Approximately(SpeedHaze, lastAppliedSpeedHaze))
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
            lastAppliedSpeedHaze = SpeedHaze;

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

            // Speed thickens the fog on top of the weather, the same way the weather thickens it on top
            // of the time of day. Only ever thicker: fog is also what hides the draw distance, and a
            // term that could thin it would let the far plane show through at exactly the speed the
            // player is covering ground fastest.
            float haze = Mathf.Clamp01(SpeedHaze);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;

            Color fogColor = profile.FogColor.Evaluate(t);

            // Bad weather takes the colour out of the air as well as the light out of the sun, which is
            // what this class's own remarks have always promised and what it had never actually done.
            // Left saturated, the fog stayed the hour's warm gold while the sky above it went grey, and
            // the two halves of the same frame disagreed about the weather. Desaturated rather than
            // darkened: fog is also what hides the draw distance, and taking light out of it is the one
            // change that would let the far plane show through.
            if (Overcast > 0f)
            {
                float grey = fogColor.grayscale;
                fogColor = Color.Lerp(fogColor, new Color(grey, grey, grey), Overcast * 0.7f);
            }

            RenderSettings.fogColor = Color.Lerp(
                fogColor, fogColor * (1f - SpeedFogDarkening), haze);

            RenderSettings.fogDensity = Mathf.Max(0f, profile.FogDensity.Evaluate(t))
                * (1f + Overcast * 1.6f)
                * (1f + haze * Mathf.Max(0f, SpeedFogGain));

            ApplySky();
        }

        /// <summary>
        /// Puts the grey sky up in bad weather, and the fair one back afterwards.
        ///
        /// <para><b>Nothing here had ever touched the sky, and it took a photograph of rain falling out
        /// of a clear blue afternoon to notice.</b> This class writes the sun, the ambient and the fog;
        /// the skybox is a material on <c>RenderSettings</c> that only ever read the sun's *direction*.
        /// So <see cref="Overcast"/> has been dimming the light and thickening the air under an
        /// unchanged blue dome since the day it was written — for Hazy and Overcast as much as for
        /// rain.</para>
        ///
        /// <para><b>It also decides what smooth surfaces reflect</b>, which is the half nobody would
        /// predict. With no reflection probes in this world — a budget decision — URP's environment
        /// reflection is the skybox itself, so a wet road under a blue dome reflects blue. Greying the
        /// sky greys the reflection in the same stroke.</para>
        ///
        /// <para>Assigned only on the frame it changes. <c>RenderSettings.skybox</c> is a scene value
        /// and writing it every frame at edit time would leave the scene permanently dirty, which is
        /// the same trap <see cref="Update"/> guards against for everything else here.</para>
        /// </summary>
        private void ApplySky()
        {
            if (clearSky == null || overcastSky == null)
            {
                return;
            }

            bool wantOvercast = skyIsOvercast
                ? Overcast > OvercastSkyOff
                : Overcast > OvercastSkyOn;

            Material wanted = wantOvercast ? overcastSky : clearSky;

            if (skyIsOvercast == wantOvercast && RenderSettings.skybox == wanted)
            {
                return;
            }

            skyIsOvercast = wantOvercast;
            RenderSettings.skybox = wanted;
        }
    }
}
