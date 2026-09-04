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
        [Tooltip("The sky. One material for every hour and every weather.\n\n"
               + "There used to be two, swapped between at Overcast 0.60 with hysteresis, and that "
               + "arrangement had two faults it could not be talked out of: Hazy at 0.45 sat below the "
               + "threshold and therefore never changed the sky at all, and the grey one was a painted "
               + "texture and so read exactly the same at midnight as at noon.\n\n"
               + "Nothing is written to this material. Everything the clock decides goes through global "
               + "shader uniforms — see PushSky — because Unity does not roll an asset change back when "
               + "Play mode ends, and a skybox has no renderer to hang a MaterialPropertyBlock on.")]
        [SerializeField] private Material sky;

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

        // The eight uniforms PushSky writes. Cached ids, because this runs every frame.
        private static readonly int SkyHorizonId = Shader.PropertyToID("_HorizonSkyHorizon");
        private static readonly int SkyZenithId = Shader.PropertyToID("_HorizonSkyZenith");
        private static readonly int CloudLitId = Shader.PropertyToID("_HorizonSkyCloudLit");
        private static readonly int CloudShadeId = Shader.PropertyToID("_HorizonSkyCloudShade");
        private static readonly int SunId = Shader.PropertyToID("_HorizonSun");
        private static readonly int SunTintId = Shader.PropertyToID("_HorizonSunTint");
        private static readonly int OvercastId = Shader.PropertyToID("_HorizonOvercast");

        /// <summary>
        /// How bright the sun's disc is, as a multiple of the light's own intensity.
        ///
        /// <para><b>Sized against the bloom threshold rather than by eye.</b> Bloom's linear threshold
        /// is <c>GammaToLinear(1.1)</c>, about 1.26, with the soft knee opening at 0.63. Everything else
        /// this shader produces is a chain of lerps between colours pushed from here, and a lerp cannot
        /// exceed its inputs — the brightest of which is the noon fog at about 0.75 linear. So the sky
        /// cannot bloom at all, by construction, and the disc is the one thing that is meant to: at a
        /// noon intensity of 1.15 this puts it at 3.7, which is above the headlamp lens at 2.4 and the
        /// brake light at 3.2, and at dusk it falls with the sun and blooms orange because the sun's own
        /// colour is orange by then.</para>
        /// </summary>
        private const float SunDiscBrightness = 3.2f;

        /// <summary>How much of the sun reaches a cloud top, against how much of the ambient sky.</summary>
        private const float CloudSunShare = 0.7f;

        /// <summary>
        /// How bright a cloud is against the light falling on it. A cloud is not a mirror.
        /// </summary>
        private const float CloudAlbedo = 0.72f;

        /// <summary>
        /// Shortest gap between two rebuilds of the environment reflection, seconds.
        ///
        /// <para><b>Nothing in this project had ever called <c>DynamicGI.UpdateEnvironment</c>, and it
        /// has to now.</b> There are no reflection probes here by budget, so the skybox <i>is</i> URP's
        /// environment reflection — which is what made greying the sky the fix for a wet road reflecting
        /// blue in a rainstorm. Assigning a skybox material is a change Unity notices; changing what a
        /// global uniform makes that material <i>draw</i> is not. Left out, the dome would dim perfectly
        /// while every wet carriageway in the world went on reflecting whichever hour the cubemap was
        /// last built at — the recorded bug, moved from the sky into the road.</para>
        ///
        /// <para>Public so <c>QualityDirector</c> can lengthen it, which is that class's own rule about
        /// runtime values on a component. At <c>DayLengthMinutes</c> 24 the clock runs an hour a minute,
        /// so the hour test below fires about once a second.</para>
        /// </summary>
        public float EnvironmentInterval = DefaultEnvironmentInterval;

        /// <summary>
        /// The value <see cref="EnvironmentInterval"/> starts at. A constant as well as a field because
        /// <c>PrototypeSetup</c> never writes the field, so this is what the built scene actually
        /// carries — and the build reports it.
        /// </summary>
        public const float DefaultEnvironmentInterval = 0.25f;

        private float lastEnvironmentAt = float.NegativeInfinity;
        private float lastEnvironmentOvercast = float.NaN;
        private float lastEnvironmentHours = float.NaN;

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

            // The ambient is pushed at the end of this method rather than here, because the sky band
            // takes its colour from the fog and the fog is not final until a dozen lines below. It was
            // written here for as long as there was only one colour to write.

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

            // The fog colour before the speed term, deliberately. Speed haze is a thing that happens
            // to the air between the driver and the world; the ambient is not between anything, and a
            // world that dims a little every time the throttle goes down is a brightness change tied to
            // the accelerator.
            ApplyAmbient(ambient, fogColor);
            PushSky(t, fogColor, ambient);
            RefreshEnvironment();
        }

        /// <summary>
        /// Light from above, from the side and from below, instead of one number for all three.
        ///
        /// <para><b>This world is flat shaded under a single directional light, so ambient is most of
        /// what a surface facing away from the sun has.</b> With <c>AmbientMode.Flat</c> every face of
        /// every mesh got the same one, which is why a rock face reads as a single slab, why a plan view
        /// of the terrain is one colour, and why a low-poly canopy in shadow has no form in it at all.
        /// Trilight gives an up-facing facet the sky and a down-facing one the ground, and the facets
        /// here are hard-normalled, so the difference lands per facet — which is the whole shape of the
        /// art.</para>
        ///
        /// <para><b>It costs nothing, and that is checkable rather than hopeful.</b> Unity fills the same
        /// seven <c>unity_SH*</c> vectors whichever mode is set — Flat writes the constant term and
        /// leaves the linear one zero, Trilight writes both — and <c>SAMPLE_GI</c> evaluates the same
        /// instructions either way. No pass, no variant, no keyword, no memory. Only
        /// <c>AmbientMode.Skybox</c> would need a convolution and a <c>DynamicGI</c> call, and it is not
        /// this.</para>
        ///
        /// <para><b>The three sum to three, and that is what keeps this a change of shape rather than a
        /// re-tune of the world's light.</b> Every colour in this project was chosen against the flat
        /// ambient; if the average moved, every one of them would need looking at again. So the gains are
        /// written to average exactly 1, <see cref="Tinted"/> moves colour without moving level, and
        /// <c>ValidateAmbient</c> reads <c>RenderSettings.ambientProbe</c> back and prints what the
        /// engine actually built from them. The claim in this paragraph is the one most worth
        /// distrusting, so it is the one the build measures.</para>
        ///
        /// <para>The night floor is applied to <paramref name="ambient"/> before it arrives here, so it
        /// still floors — split three ways rather than defeated.</para>
        /// </summary>
        /// <summary>
        /// The single colour this world's ambient would be under <c>AmbientMode.Flat</c>.
        ///
        /// <para><b>Published because it cannot be read back once Trilight is set.</b>
        /// <c>RenderSettings.ambientLight</c> is not a separate field — it is <c>ambientSkyColor</c>
        /// under another name — so the moment the three bands are written, the flat value they were
        /// meant to average to is gone. <c>ValidateAmbient</c> compares the two modes and would
        /// otherwise be comparing Trilight against its own sky band, which is what it did on the build
        /// that found this: it reported the world 44 % darker on a change that moves the mean by
        /// nothing.</para>
        /// </summary>
        public Color FlatAmbient { get; private set; }

        private void ApplyAmbient(Color ambient, Color fog)
        {
            FlatAmbient = ambient;

            // <b>The equator is left alone and the two caps deviate about it.</b> Not "the three sum
            // to three", which was the first parametrisation and is the wrong one: the equator band
            // covers far more of the sphere than either cap, so a gain above one placed there moves the
            // world's brightness while a matching gain below one on a cap barely moves it back. Written
            // this way, the only thing that can change the average is the caps failing to cancel, and
            // they cancel because they are equal and opposite in the space that matters.
            //
            // <b>And that space is linear, which is the whole of why the first three attempts were
            // wrong.</b> RenderSettings' ambient colours are gamma and the probe is built from their
            // linear values, so scaling a gamma colour by g scales the light by g^2.2. Gains that
            // average one on paper therefore do not average one in the frame — measured 8 % bright at
            // 1.35 / 1.00 / 0.65 applied the obvious way. Applied in linear, the same shape reads
            // within one per cent of neutral.
            //
            // 1.45 and 0.50 are a measured pair, not a chosen one: ValidateAmbient is what says they
            // hold, and it reports about +25 % on a facet facing the sky against -25 % on one facing
            // the ground. Do not retune them by eye.
            const float SkyGain = 1.45f;
            const float GroundGain = 0.50f;

            // How far each end goes towards its own colour. The ground is pushed harder than the sky
            // because the sky's tint is already most of the way there — the fog *is* the colour of the
            // air overhead — while nothing else in the frame says what the earth under the car is made
            // of.
            const float SkyTint = 0.5f;
            const float GroundTint = 0.65f;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Tinted(Scaled(ambient, SkyGain), fog, SkyTint);
            RenderSettings.ambientEquatorColor = ambient;
            RenderSettings.ambientGroundColor =
                Tinted(Scaled(ambient, GroundGain), profile.GroundBounce, GroundTint);

            // <b>RenderSettings.ambientLight is NOT a fourth field — it is ambientSkyColor under another
            // name.</b> Writing it here to leave a tidy record of the flat value overwrote the sky band
            // with the equator's colour, and ValidateAmbient reported exactly that: sky and equator
            // printed as the same three numbers, a facet facing up gained nought per cent, and the
            // world came out 12.8 % darker overall because two of the three gains had collapsed into
            // one. Every part of that is invisible in a frame — a world uniformly an eighth darker
            // looks like a world.
            //
            // So there is deliberately nothing here. The check is what found it, and it found it on the
            // first build.
        }

        /// <summary>
        /// Scales a gamma colour by a gain that means what it says in the light.
        ///
        /// <para><c>RenderSettings</c>' ambient colours are gamma and the probe is built from their
        /// linear values, so multiplying the colour by <paramref name="gain"/> multiplies the light by
        /// <c>gain^2.2</c>. A pair of gains chosen to cancel therefore does not cancel — which is what
        /// made three attempts at <see cref="ApplyAmbient"/>'s constants wrong before this existed.</para>
        /// </summary>
        private static Color Scaled(Color colour, float gain)
        {
            return (colour.linear * gain).gamma;
        }

        /// <summary>
        /// Moves a colour towards another one without moving how bright it is.
        ///
        /// <para>A plain <c>Lerp</c> towards the fog at noon would drag the sky band's level up by half
        /// the difference between two colours that have no reason to agree about brightness, and the
        /// ambient average would go with it. Matching the target's grayscale to the source's first means
        /// the only thing this can change is hue — which is all it is for, and which is what lets
        /// <see cref="ApplyAmbient"/>'s gains be the whole story about level.</para>
        /// </summary>
        private static Color Tinted(Color from, Color toward, float share)
        {
            float level = toward.grayscale;

            if (level < 0.0001f)
            {
                return from;
            }

            Color matched = toward * (from.grayscale / level);

            return Color.Lerp(from, matched, share);
        }

        /// <summary>
        /// Hands the hour, the weather and the sun to the sky shader.
        ///
        /// <para><b>Everything here is a global uniform and nothing writes the material.</b> Unity does
        /// not roll an asset change back when Play mode ends, so a player who tried the rain once would
        /// otherwise leave the skybox modified in their working tree — the hazard <c>TownLights</c>,
        /// <c>WetSurfaces</c> and <c>QualityDirector</c> all document. A skybox has no renderer, so a
        /// <c>MaterialPropertyBlock</c> is not available and globals are the only mechanism left. The
        /// project already had the pattern in <c>WindDirector</c>.</para>
        ///
        /// <para><b>The horizon is the fog colour, and that is forced rather than chosen.</b> Fog is
        /// exponential-squared against a 600 m far plane, so every distant ridge in this world resolves
        /// to exactly <c>RenderSettings.fogColor</c> — and a skybox is not fogged. Any other colour at
        /// the skyline is a seam under every horizon in the game. The zenith cannot be derived from it
        /// the same way: at dusk the fog is a warm gold and the sky overhead is a deep violet, and no
        /// darken-and-shift produces violet from gold, so that one is a gradient of its own.</para>
        ///
        /// <para><b>Nothing here can fail to go dark at night, and that is the recorded bug closed by
        /// construction.</b> Every colour pushed is either the fog, the ambient or the sun's own — all
        /// three already keyed on the hour, all three already carrying <c>sunDim</c>. The sky that this
        /// replaced was a painted grey texture at a fixed exposure, which is why it read the same at
        /// midnight as at noon and why no amount of driving it could have fixed it.</para>
        ///
        /// <para>Called every frame in play mode, because <c>Apply</c> is, and because a domain reload,
        /// a scene load or a shader recompile all drop global vectors and none of them raises anything
        /// to hook. That is <c>WindDirector.Push</c>'s own written reason, unchanged.</para>
        /// </summary>
        private void PushSky(float t, Color horizon, Color ambient)
        {
            // One guarded assignment, kept rather than dropped. RenderSettings belongs to the *active*
            // scene, and GameBootstrap loads the world additively without ever calling SetActiveScene —
            // so at runtime the settings that render are Bootstrap's, not the world's. Both scenes are
            // built with this material in them now, and this line is what covers a scene that is not.
            // Assigned only when it differs, or edit time would leave the scene permanently dirty.
            if (sky != null && RenderSettings.skybox != sky)
            {
                RenderSettings.skybox = sky;
            }

            if (profile == null)
            {
                return;
            }

            Color zenith = profile.SkyZenith.Evaluate(t);

            // Weather flattens the dome towards its own horizon. At full overcast there is no gradient
            // left at all, which is what an overcast sky actually is — and what the grey texture this
            // replaced was painting by hand.
            zenith = Color.Lerp(zenith, horizon * 0.82f, Mathf.Clamp01(Overcast) * 0.85f);

            Shader.SetGlobalVector(SkyHorizonId, (Vector4)horizon.linear);
            Shader.SetGlobalVector(SkyZenithId, (Vector4)zenith.linear);

            // Cloud tops are lit by the sun and their undersides by the sky. Both colours exist already,
            // both are keyed on the hour already, and neither is a new thing to tune.
            //
            // .linear first and the intensity after. Color.linear applies pow(v, 2.4) to channels above
            // one, so scaling before converting over-brightens the sun by its own intensity to the 2.4 —
            // which at noon is most of a stop and lands the sky in the bloom knee.
            Vector4 sunLinear = sun != null ? (Vector4)sun.color.linear * sun.intensity : Vector4.zero;
            Vector4 ambientLinear = (Vector4)ambient.linear;

            Shader.SetGlobalVector(CloudLitId,
                Vector4.Lerp(ambientLinear, sunLinear, CloudSunShare) * CloudAlbedo);
            Shader.SetGlobalVector(CloudShadeId, ambientLinear * 0.9f);

            // Read off the transform, after the rotation above wrote it — never recomputed from
            // SunAzimuth and SunElevation. Two copies of one formula agree until one of them changes,
            // and the symptom here would be a painted sun sitting somewhere the shadows do not come
            // from. TrunkForkBuilder.MouthHalfWidth records the general case.
            Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
            float intensity = sun != null ? sun.intensity : 0f;

            Shader.SetGlobalVector(SunId,
                new Vector4(toSun.x, toSun.y, toSun.z, intensity * SunDiscBrightness));

            Color tint = sun != null ? sun.color.linear : Color.white;

            Shader.SetGlobalVector(SunTintId,
                new Vector4(tint.r, tint.g, tint.b, Mathf.Clamp01(intensity * 1.4f)));

            Shader.SetGlobalFloat(OvercastId, Mathf.Clamp01(Overcast));
        }

        /// <summary>
        /// Rebuilds the environment reflection when the sky has actually moved. See
        /// <see cref="EnvironmentInterval"/> for why this exists at all.
        /// </summary>
        private void RefreshEnvironment()
        {
            if (!Application.isPlaying)
            {
                // One rebuild per change at edit time. The preview tools take their frames without a
                // frame loop, so anything deferred here would be photographed stale.
                DynamicGI.UpdateEnvironment();
                return;
            }

            bool moved = float.IsNaN(lastEnvironmentHours)
                || Mathf.Abs(Overcast - lastEnvironmentOvercast) > 0.01f
                || Mathf.Abs(Mathf.DeltaAngle(TimeOfDayHours * 15f, lastEnvironmentHours * 15f)) > 0.3f;

            if (!moved || Time.unscaledTime - lastEnvironmentAt < EnvironmentInterval)
            {
                return;
            }

            DynamicGI.UpdateEnvironment();
            lastEnvironmentAt = Time.unscaledTime;
            lastEnvironmentOvercast = Overcast;
            lastEnvironmentHours = TimeOfDayHours;
        }
    }
}
