using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Rain, as heard from inside the car. One synthesised loop whose level is pushed in.
    ///
    /// <para><b>Its level is set from outside rather than read from the weather, and that is the module
    /// layout.</b> <c>Horizon.Vehicle</c> may not see <c>Horizon.Game</c>, where the weather is decided,
    /// so <c>WeatherDirector</c> writes <see cref="Level"/> the way <c>SpeedAtmosphere</c> writes
    /// <c>TimeOfDayController.SpeedHaze</c> and for exactly the same reason.</para>
    ///
    /// <para><b>It is on the car rather than on the camera, because of the one thing it has to do
    /// besides play.</b> Rain stops under a bridge, in a tunnel and under a filling station canopy —
    /// and <see cref="VehicleCover"/>, the single upward ray that already fades the engine's reverb, is
    /// the answer to that question and it lives here. That detail is most of what sells the effect and
    /// it costs nothing: the ray is already being cast.</para>
    ///
    /// <para><b>Deliberately not shaped by speed.</b> The obvious next line is to make it louder as the
    /// car goes faster, and <c>EngineAudio</c>'s note about the deleted wind layer is exactly why not:
    /// a broadband noise that rises with the throttle sits over the engine on every acceleration and
    /// turns the one sound this game has into something you listen past. Rain sounds like rain whether
    /// the car is moving or not.</para>
    ///
    /// <para>The clip obeys the loop rule the same way the tyre squeal does — a whole second with its
    /// tail crossfaded into its head, because filtered noise has no cycle count to land on.</para>
    /// </summary>
    public sealed class RainAudio : MonoBehaviour
    {
        [Tooltip("Looping source. Barely spatialised: rain is everywhere, not in a place.")]
        [SerializeField] private AudioSource source;

        [Tooltip("The cover probe, so the rain stops under a roof. Found on this object if left empty.")]
        [SerializeField] private VehicleCover cover;

        [Tooltip("Level of the heaviest rain, in the open.")]
        [Range(0f, 1f)]
        [SerializeField] private float rainVolume = 0.45f;

        [Tooltip("How much of the rain is still heard under a roof.\n\n"
               + "Not zero. A tunnel silences the sky but not the tyres, and cutting the whole layer at "
               + "the portal reads as the sound breaking rather than as shelter — the same argument the "
               + "engine reverb makes for fading rather than switching.")]
        [Range(0f, 1f)]
        [SerializeField] private float coveredLevel = 0.18f;

        private const int SampleRate = 44100;

        /// <summary>How fast the level follows the weather, per second. Slow: rain arrives, it does not switch on.</summary>
        private const float LevelResponse = 1.6f;

        /// <summary>How hard it is raining, 0 to 1. Written by <c>WeatherDirector</c>, never read from here.</summary>
        public float Level { get; set; }

        private float heard;

        private void Awake()
        {
            if (cover == null)
            {
                cover = GetComponent<VehicleCover>();
            }

            if (source == null)
            {
                return;
            }

            source.clip = BuildRainClip();
            source.loop = true;
            source.volume = 0f;
            source.Play();
        }

        private void Update()
        {
            if (source == null)
            {
                return;
            }

            float shelter = cover != null ? cover.CoverAmount : 0f;
            float target = Level * Mathf.Lerp(1f, coveredLevel, shelter);

            heard = Mathf.Lerp(heard, target, 1f - Mathf.Exp(-LevelResponse * Time.deltaTime));

            source.volume = rainVolume * heard;

            // Duller under a roof as well as quieter. What a bridge takes away first is the top of the
            // spectrum — the hiss of drops landing near you — and level alone reads as the rain having
            // moved into the distance rather than as something standing between you and it.
            source.pitch = Mathf.Lerp(1f, 0.82f, shelter);
        }

        /// <summary>
        /// One second of rain: broadband noise with the very top taken off, and a scatter of nearer
        /// drops over it.
        ///
        /// <para><b>Two layers, because rain is not a hiss.</b> The bed is the many-drops-at-a-distance
        /// wash, which on its own is indistinguishable from static or from wind. What makes it read as
        /// water is the sparse foreground — individual drops close enough to have an attack — and those
        /// are short decaying bursts through a high resonator, laid down at random. Drop the second
        /// layer and this is a noise generator; drop the first and it is a leaking tap.</para>
        ///
        /// <para>Deliberately close to <c>ContactAudio</c>'s rumble in construction and nowhere near it
        /// in register. That rumble is a two-pole lowpass under 200 Hz and this sits above two
        /// kilohertz, for the reason recorded there: the two can play at once, so level could never
        /// have separated them and register has to.</para>
        /// </summary>
        private static AudioClip BuildRainClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x4D2B891Fu;

            // A one-pole highpass, as the difference between white and its own lowpass. The bed keeps
            // its hiss but loses the bottom, which is where it would otherwise fight the engine.
            float low = 0f;
            const float cutoff = 0.28f;

            Resonator drop = Resonator.At(2600f, 0.965f);
            float dropEnvelope = 0f;

            for (int i = 0; i < generated; i++)
            {
                float white = NextUnit(ref state) * 2f - 1f;

                low += (white - low) * cutoff;
                float bed = white - low;

                // About sixty drops a second: often enough to be continuous, sparse enough that the ear
                // can pick individual ones out of it. Any denser and the layer merges into the bed it is
                // meant to stand out from.
                if (NextUnit(ref state) < 60f / SampleRate)
                {
                    dropEnvelope = 1f;
                }

                float excite = dropEnvelope > 0.001f ? (NextUnit(ref state) * 2f - 1f) * dropEnvelope : 0f;
                dropEnvelope *= 0.9986f;

                raw[i] = bed * 0.55f + drop.Step(excite) * 0.30f;
            }

            var samples = new float[SampleRate];
            System.Array.Copy(raw, samples, SampleRate);

            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                samples[i] = samples[i] * t + raw[SampleRate + i] * (1f - t);
            }

            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
            }

            if (peak > 0.0001f)
            {
                float scale = 0.8f / peak;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= scale;
                }
            }

            AudioClip clip = AudioClip.Create("Rain", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>A two-pole resonator. The same one <c>EngineAudio</c> and <c>ContactAudio</c> carry.</summary>
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
