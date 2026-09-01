using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Everything the car makes by touching something. Three layers, synthesised, and one component
    /// because they answer one question.
    ///
    /// <list type="bullet">
    /// <item><b>The thud</b> — a one-shot on <see cref="VehicleController.Impacted"/>, pitched down and
    /// lengthened with severity.</item>
    /// <item><b>The scrape</b> — a loop on <see cref="VehicleController.ScrapeSpeed"/>, for a car
    /// leaning on a barrier rather than hitting one.</item>
    /// <item><b>The rumble</b> — two loops on <see cref="VehicleController.SurfaceRoughness"/> and
    /// speed, crossfaded by <see cref="VehicleController.SurfaceGrit"/>, for wheels off the tarmac.</item>
    /// </list>
    ///
    /// <para><b>Two rumble clips and not one, which is the fix for the plainest fault this component
    /// had.</b> Gravel and open ground were one loop played at two volumes, so a car on a verge and a
    /// car in a field made the same noise louder or softer — one surface at two distances rather than
    /// two surfaces. They are separated by character now: loose stone is a rattle of individual strikes
    /// with a hard edge to it, and grass and earth are a dull boom with the top absorbed. Crossfaded on
    /// one level, exactly the way the engine's two voices are — same clip length, same pitch, and what
    /// differs is what is in them.</para>
    ///
    /// <para><b>One component rather than three, and the reason is the same one <c>SpeedAtmosphere</c>
    /// gives.</b> All three read the same vehicle, all three are shaped by the same speed, and the thud
    /// and the scrape are two readings of a single contact — a hit that also slides. Split apart, they
    /// would be three lookups of the car and three curves with nothing keeping them in agreement, and
    /// the failure mode is a bang and a scrape that describe different collisions.</para>
    ///
    /// <para><b>Why not in <see cref="EngineAudio"/>.</b> That class rebuilds its clips whenever the
    /// player changes car, because a diesel and a turbocharged six are different notes. None of these
    /// three depend on the car at all: a wing hitting a barrier sounds like a wing hitting a barrier.
    /// Putting them there would mean re-synthesising three clips that cannot change, every time somebody
    /// opened the garage.</para>
    ///
    /// <para><b>The loop rule applies to two of the three.</b> A looping generated clip has to hold a
    /// whole number of cycles or it ticks once a second; broadband noise has no cycle to count, so the
    /// scrape and the rumble get their tails crossfaded into their heads instead, exactly as
    /// <c>EngineAudio.BuildSquealClip</c> does. The thud is a one-shot and needs none of it.</para>
    /// </summary>
    public sealed class ContactAudio : MonoBehaviour
    {
        [Tooltip("The car being listened to. Found on this object or its parents if left empty.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("One-shot source for impacts. Not looping.")]
        [SerializeField] private AudioSource impactSource;

        [Tooltip("Looping source for bodywork sliding along something.")]
        [SerializeField] private AudioSource scrapeSource;

        [Tooltip("Looping source for wheels on soft ground — grass, earth, sand.")]
        [SerializeField] private AudioSource rumbleSource;

        [Tooltip("Looping source for wheels on loose stone. Crossfaded against the other one on one "
               + "level, so the pair is a surface rather than two sounds.")]
        [SerializeField] private AudioSource gritSource;

        [Header("Impacts")]
        [Tooltip("Level of a full-severity impact.")]
        [Range(0f, 1f)]
        [SerializeField] private float impactVolume = 0.9f;

        [Tooltip("Level of the gentlest impact that is reported at all. Not zero: the quietest audible "
               + "knock is what tells a driver they clipped something rather than imagined it.")]
        [Range(0f, 1f)]
        [SerializeField] private float lightImpactVolume = 0.22f;

        [Header("Scrape")]
        [Tooltip("Level of a scrape at full speed.")]
        [Range(0f, 1f)]
        [SerializeField] private float scrapeVolume = 0.55f;

        [Tooltip("Sliding speed the scrape reaches full level at, m/s.")]
        [SerializeField] private float scrapeFullSpeed = 16f;

        [Header("Rumble")]
        [Tooltip("Level of the rumble with all four wheels on open ground at speed.")]
        [Range(0f, 1f)]
        [SerializeField] private float rumbleVolume = 0.5f;

        [Tooltip("Road speed the rumble reaches full level at, m/s.")]
        [SerializeField] private float rumbleFullSpeed = 22f;

        private const int SampleRate = 44100;

        /// <summary>
        /// How fast the scrape and the rumble follow what the car is doing, per second.
        ///
        /// <para>Fast, but not instant. A contact is reported per physics step and a car riding a
        /// barrier does not touch it on every one of them, so an unsmoothed level chatters on and off at
        /// 50 Hz — which is a buzz rather than a scrape. This is the same argument
        /// <c>VehicleController.ScrapeSpeed</c> makes for collecting per step, one layer further out.</para>
        /// </summary>
        private const float LevelResponse = 14f;

        private float scrapeLevel;
        private float rumbleLevel;
        private float gritBlend;

        private AudioClip thud;

        private void Awake()
        {
            if (vehicle == null)
            {
                vehicle = GetComponentInParent<VehicleController>();
            }

            thud = BuildThudClip();

            StartLoop(scrapeSource, BuildScrapeClip());
            StartLoop(rumbleSource, BuildRumbleClip());
            StartLoop(gritSource, BuildGritClip());
        }

        private void OnEnable()
        {
            if (vehicle != null)
            {
                vehicle.Impacted += OnImpacted;
            }
        }

        private void OnDisable()
        {
            if (vehicle != null)
            {
                vehicle.Impacted -= OnImpacted;
            }

            // Silenced rather than left where it was. This component is disabled with the car, and a
            // scrape frozen at its last level is a noise that outlives the thing making it.
            scrapeLevel = 0f;
            rumbleLevel = 0f;

            if (scrapeSource != null)
            {
                scrapeSource.volume = 0f;
            }

            if (rumbleSource != null)
            {
                rumbleSource.volume = 0f;
            }

            if (gritSource != null)
            {
                gritSource.volume = 0f;
            }
        }

        private void Update()
        {
            if (vehicle == null)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            float follow = 1f - Mathf.Exp(-LevelResponse * deltaTime);

            UpdateScrape(follow);
            UpdateRumble(follow);
        }

        /// <summary>
        /// A knock, pitched by how hard it was.
        ///
        /// <para><b>Down with severity, which is the opposite of the obvious.</b> Pitch usually rises
        /// with energy, but not here: a light tap is a panel, and a heavy one is the whole structure —
        /// bigger things ring lower. Pitched up with severity, a big crash sounds like a small one
        /// played loudly.</para>
        /// </summary>
        private void OnImpacted(float severity, Vector3 point)
        {
            if (impactSource == null || thud == null)
            {
                return;
            }

            impactSource.pitch = Mathf.Lerp(1.3f, 0.72f, severity);
            impactSource.PlayOneShot(thud, Mathf.Lerp(lightImpactVolume, impactVolume, severity));
        }

        private void UpdateScrape(float follow)
        {
            if (scrapeSource == null)
            {
                return;
            }

            float speed = Mathf.Clamp01(vehicle.ScrapeSpeed / Mathf.Max(0.1f, scrapeFullSpeed));

            scrapeLevel = Mathf.Lerp(scrapeLevel, speed, follow);
            scrapeSource.volume = scrapeVolume * scrapeLevel;
            scrapeSource.pitch = Mathf.Lerp(0.7f, 1.35f, scrapeLevel);
        }

        /// <summary>
        /// The rumble, on roughness times speed, split between two surfaces.
        ///
        /// <para>The product, not either alone. Roughness alone rumbles at a standstill on grass, which
        /// is a car that has broken; speed alone rumbles on the motorway. What makes the noise is tread
        /// passing over something that is not smooth, and that stops when either factor does.</para>
        ///
        /// <para><b>One level and one pitch across both clips, and only the blend between them moves.</b>
        /// Two levels would be two sounds that happen to be playing, and the moment they disagreed —
        /// which is every moment the car has wheels on both, meaning every verge exit — the driver would
        /// hear a car in two places. What is being described here is one contact patch.</para>
        ///
        /// <para><b>The blend is smoothed like everything else, and it has to be.</b>
        /// <c>SurfaceGrit</c> is resolved from four raycasts a physics step, so a wheel skipping along
        /// the join between asphalt and gravel flips it at 50 Hz — and an unsmoothed crossfade on that
        /// is not a surface changing, it is a tremolo.</para>
        /// </summary>
        private void UpdateRumble(float follow)
        {
            if (rumbleSource == null && gritSource == null)
            {
                return;
            }

            float speed = Mathf.Clamp01(Mathf.Abs(vehicle.ForwardSpeed) / Mathf.Max(0.1f, rumbleFullSpeed));
            float target = vehicle.SurfaceRoughness * speed;

            rumbleLevel = Mathf.Lerp(rumbleLevel, target, follow);
            gritBlend = Mathf.Lerp(gritBlend, vehicle.SurfaceGrit, follow);

            float level = rumbleVolume * rumbleLevel;
            float pitch = Mathf.Lerp(0.75f, 1.2f, speed);

            if (rumbleSource != null)
            {
                rumbleSource.volume = level * (1f - gritBlend);
                rumbleSource.pitch = pitch;
            }

            if (gritSource != null)
            {
                gritSource.volume = level * gritBlend;
                gritSource.pitch = pitch;
            }
        }

        private static void StartLoop(AudioSource source, AudioClip clip)
        {
            if (source == null)
            {
                return;
            }

            source.clip = clip;
            source.loop = true;
            source.volume = 0f;
            source.Play();
        }

        /// <summary>
        /// One impact: a noise burst through two resonators, decaying.
        ///
        /// <para>Two, because a car body hitting a barrier is two things at once — the shell booming
        /// around 80 Hz and a panel ringing about five times higher. One resonator gives either a boom
        /// with no edge or a clank with no weight, and a listener reads both of those as a sound effect
        /// rather than as a car.</para>
        ///
        /// <para>The high partial decays several times faster than the low one. That difference *is* the
        /// impression of mass: the crack is over in fifty milliseconds and the boom carries on under it,
        /// which is what makes the thing that was struck feel heavy.</para>
        ///
        /// <para>Half a second, and not looped, so the whole-cycles rule does not apply. It is
        /// pitch-shifted at playback, which stretches it to about 0.7 s at the heaviest.</para>
        /// </summary>
        private static AudioClip BuildThudClip()
        {
            int length = SampleRate / 2;
            var samples = new float[length];

            uint state = 0x9E3779B9u;

            Resonator boom = Resonator.At(82f, 0.9985f);
            Resonator panel = Resonator.At(430f, 0.992f);

            for (int i = 0; i < length; i++)
            {
                float t = i / (float)SampleRate;

                // The strike itself: a couple of milliseconds of broadband, and everything after it is
                // the two resonators ringing down. Driving them for longer gives a burst of noise with
                // a note somewhere inside it instead of a hit.
                float excite = t < 0.004f ? (NextUnit(ref state) * 2f - 1f) : 0f;

                float low = boom.Step(excite) * Mathf.Exp(-9f * t);
                float high = panel.Step(excite) * Mathf.Exp(-42f * t);

                // A little raw crack on the leading edge, which is the transient the ear locates the
                // hit by. Without it the sound starts a fraction late however hard it is.
                float crack = (NextUnit(ref state) * 2f - 1f) * Mathf.Exp(-160f * t) * 0.35f;

                samples[i] = low * 0.7f + high * 0.45f + crack;
            }

            Normalize(samples, 0.9f);

            AudioClip clip = AudioClip.Create("ContactThud", length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Bodywork dragging along a barrier: bright, broadband, with a resonance to keep it from being
        /// a hiss.
        ///
        /// <para>Higher and thinner than the rumble on purpose. These two loops are the pair most at
        /// risk of collapsing into one noise — both are filtered white — so they are separated by
        /// register rather than by level: metal on steel sits above a kilohertz and tyres on gravel sit
        /// below two hundred hertz. Level would not have told them apart, because they occur together.</para>
        /// </summary>
        private static AudioClip BuildScrapeClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x1F123BB5u;

            Resonator ring = Resonator.At(1450f, 0.972f);
            Resonator grind = Resonator.At(620f, 0.955f);

            for (int i = 0; i < generated; i++)
            {
                float white = NextUnit(ref state) * 2f - 1f;
                raw[i] = ring.Step(white) * 0.22f + grind.Step(white) * 0.22f + white * 0.18f;
            }

            return LoopFrom(raw, fade, "ContactScrape");
        }

        /// <summary>
        /// Tyres on gravel and grass: the same white noise, taken low.
        ///
        /// <para>A two-pole lowpass rather than a resonator. There is nothing ringing about a tyre on
        /// loose ground — what makes the noise is a great many small impacts, and the thing that shapes
        /// it is that the ground absorbs the top of the spectrum. A resonator here gives a hum, which
        /// reads as engine and fights the one already playing.</para>
        /// </summary>
        private static AudioClip BuildRumbleClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x7A5B2C3Du;

            float low1 = 0f;
            float low2 = 0f;
            const float cutoff = 0.035f;

            for (int i = 0; i < generated; i++)
            {
                float white = NextUnit(ref state) * 2f - 1f;

                low1 += (white - low1) * cutoff;
                low2 += (low1 - low2) * cutoff;

                // A trace of the unfiltered noise back on top, or the loop is a pure boom with no
                // grit in it — and grit is the whole reason a driver knows they are on the verge.
                raw[i] = low2 * 3.2f + white * 0.09f;
            }

            return LoopFrom(raw, fade, "ContactRumble");
        }

        /// <summary>
        /// A second of loose stone: a great many small strikes with a hard edge on each.
        ///
        /// <para><b>Granular where the other one is continuous, and that is the whole distinction.</b>
        /// The soft-ground loop is filtered noise — a boom with the top absorbed, because earth and
        /// grass absorb. Chippings do the opposite: each stone thrown up against an arch liner is a
        /// small impact with an attack on it, and what the ear picks gravel out by is that it can very
        /// nearly count them. So this is a scatter of short decaying bursts through a band at 900 Hz
        /// rather than a filter over white, laid down at about nine hundred a second — dense enough to
        /// be continuous, sparse enough to have grain in it. It is the rain's two-layer construction one
        /// register down, for the same reason recorded there: drop the grain and this is a hiss.</para>
        ///
        /// <para><b>Near the scrape in register and nowhere near it in shape, which is deliberate.</b>
        /// Those two can play together — running wide onto a verge and catching the guard rail is one
        /// event — so level could never separate them, exactly as it could not separate the scrape from
        /// the rumble. The scrape is continuous and metallic, through a resonator that rings; this has
        /// no ring in it at all and is made of attacks.</para>
        /// </summary>
        private static AudioClip BuildGritClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x3C79A15Bu;

            // The band the stones ring in. Low enough not to be the rain, high enough to be stone
            // rather than earth.
            Resonator stone = Resonator.At(900f, 0.90f);
            float strike = 0f;

            // And a floor under it, so the gaps between strikes are not silence. A tyre on gravel is
            // also pressing the whole bed of it down.
            float low = 0f;

            for (int i = 0; i < generated; i++)
            {
                float white = NextUnit(ref state) * 2f - 1f;

                if (NextUnit(ref state) < 900f / SampleRate)
                {
                    strike = 1f;
                }

                strike *= 0.9975f;

                low += (white - low) * 0.06f;

                raw[i] = stone.Step(white * strike) * 0.5f + low * 1.5f + white * 0.05f;
            }

            return LoopFrom(raw, fade, "ContactGrit");
        }

        /// <summary>
        /// Cuts one second out of an over-generated buffer and crossfades the surplus tail into the
        /// head, so the loop point has nothing to click on.
        /// </summary>
        private static AudioClip LoopFrom(float[] raw, int fade, string name)
        {
            var samples = new float[SampleRate];
            System.Array.Copy(raw, samples, SampleRate);

            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                samples[i] = samples[i] * t + raw[SampleRate + i] * (1f - t);
            }

            Normalize(samples, 0.8f);

            AudioClip clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static void Normalize(float[] samples, float peakLevel)
        {
            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
            }

            if (peak <= 0.0001f)
            {
                return;
            }

            float scale = peakLevel / peak;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= scale;
            }
        }

        /// <summary>A two-pole resonator. The same one <c>EngineAudio</c> carries, for the same job.</summary>
        private struct Resonator
        {
            private float a1;
            private float a2;
            private float y1;
            private float y2;

            public static Resonator At(float hz, float resonance)
            {
                float w = 2f * Mathf.PI * Mathf.Clamp(hz, 20f, SampleRate * 0.45f) / SampleRate;
                float r = Mathf.Clamp(resonance, 0f, 0.9999f);

                return new Resonator { a1 = -2f * r * Mathf.Cos(w), a2 = r * r };
            }

            public float Step(float x)
            {
                float y = x - a1 * y1 - a2 * y2;
                y2 = y1;
                y1 = y;
                return y;
            }
        }

        private static float NextUnit(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state / (float)uint.MaxValue;
        }
    }
}
