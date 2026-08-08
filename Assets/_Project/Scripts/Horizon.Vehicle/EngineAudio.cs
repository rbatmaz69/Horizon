using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Engine and wind sound, synthesised at runtime — the project carries no audio files, and a
    /// generated harmonic stack is both cheaper and more controllable than a recorded loop.
    ///
    /// Two layers: a pitch-modulated engine drone, and a speed-driven wind bed. The engine runs
    /// through a **simulated gearbox** rather than sweeping monotonically to top speed, because a
    /// pitch that only ever rises reads as a scooter or a CVT. The drop on each upshift is most of
    /// what makes it sound like a car.
    /// </summary>
    public sealed class EngineAudio : MonoBehaviour
    {
        [SerializeField] private VehicleController vehicle;
        [SerializeField] private AudioSource engineSource;
        [SerializeField] private AudioSource windSource;

        [Header("Gearbox")]
        [Tooltip("Upper speed of each gear, as a fraction of top speed. Crossing one drops the revs. "
               + "Three long ratios, like the automatics these cars came with.")]
        [SerializeField] private float[] gearTopSpeeds = { 0.30f, 0.62f, 1f };

        [Tooltip("Revs at the bottom of a gear, 0-1. High, because the ratios are long.")]
        [SerializeField] private float revsAtGearBottom = 0.40f;

        [Header("Engine")]
        [Tooltip("Playback pitch at idle.")]
        [SerializeField] private float idlePitch = 0.46f;

        [Tooltip("Playback pitch at the redline. Low — a big V8 never screams.")]
        [SerializeField] private float redlinePitch = 1.50f;

        [SerializeField] private float idleVolume = 0.30f;
        [SerializeField] private float loadVolume = 0.50f;

        [Tooltip("How fast revs follow the wheels. Deliberately slow: a heavy flywheel takes its time, "
               + "and the lag is a large part of why the car sounds big.")]
        [SerializeField] private float revSmoothing = 4.5f;

        [Header("Wind")]
        [SerializeField] private float windVolume = 0.35f;

        [Tooltip("Fraction of top speed at which wind reaches full volume.")]
        [SerializeField] private float windFullSpeed = 0.75f;

        private const int SampleRate = 44100;

        // A V8 at a lazy idle fires around 48 times a second. 48 over exactly one second is 48 whole
        // cycles, and the components at half and quarter order land on 24 and 12 — all integers, so the
        // loop closes without a click. Change this and you must keep that property.
        private const float Fundamental = 48f;

        private float revs;
        private float smoothedThrottle;

        /// <summary>Current revs, 0-1. Useful for a HUD later.</summary>
        public float Revs => revs;

        /// <summary>Gear the simulated box is in, from 1.</summary>
        public int Gear { get; private set; } = 1;

        private void Awake()
        {
            if (vehicle == null)
            {
                vehicle = GetComponentInParent<VehicleController>();
            }

            if (engineSource != null)
            {
                engineSource.clip = BuildEngineClip();
                engineSource.loop = true;
                engineSource.Play();
            }

            if (windSource != null)
            {
                windSource.clip = BuildWindClip();
                windSource.loop = true;
                windSource.volume = 0f;
                windSource.Play();
            }
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);
            smoothedThrottle = Mathf.MoveTowards(smoothedThrottle, throttle, 4f * deltaTime);

            float speed01 = vehicle != null ? vehicle.SpeedNormalized : 0f;
            float targetRevs = ResolveRevs(speed01, smoothedThrottle);

            revs = Mathf.Lerp(revs, targetRevs, 1f - Mathf.Exp(-revSmoothing * deltaTime));

            if (engineSource != null)
            {
                engineSource.pitch = Mathf.Lerp(idlePitch, redlinePitch, revs);
                engineSource.volume = idleVolume + loadVolume * Mathf.Clamp01(smoothedThrottle * 0.65f + revs * 0.45f);
            }

            if (windSource != null)
            {
                float wind = Mathf.Clamp01(speed01 / Mathf.Max(0.01f, windFullSpeed));

                // Squared so wind stays out of the way at town speeds and only builds up high.
                windSource.volume = windVolume * wind * wind;
                windSource.pitch = Mathf.Lerp(0.85f, 1.3f, wind);
            }
        }

        /// <summary>
        /// Maps road speed onto revs through the gear table, so the pitch climbs within a gear and
        /// drops when it changes up.
        /// </summary>
        private float ResolveRevs(float speed01, float throttle)
        {
            if (gearTopSpeeds == null || gearTopSpeeds.Length == 0)
            {
                return Mathf.Max(throttle * 0.5f, speed01);
            }

            int gearIndex = gearTopSpeeds.Length - 1;
            for (int i = 0; i < gearTopSpeeds.Length; i++)
            {
                if (speed01 <= gearTopSpeeds[i])
                {
                    gearIndex = i;
                    break;
                }
            }

            Gear = gearIndex + 1;

            float lower = gearIndex == 0 ? 0f : gearTopSpeeds[gearIndex - 1];
            float upper = gearTopSpeeds[gearIndex];
            float within = upper - lower > 0.0001f ? Mathf.Clamp01((speed01 - lower) / (upper - lower)) : 0f;

            float geared = Mathf.Lerp(revsAtGearBottom, 1f, within);

            // Standing still, the revs should still answer the throttle so blipping it does something.
            float stationaryBlend = Mathf.Clamp01(speed01 / 0.06f);
            float idling = 0.12f + throttle * 0.42f;

            return Mathf.Lerp(idling, geared, stationaryBlend);
        }

        /// <summary>
        /// One second of V8 drone.
        ///
        /// The character of an old American V8 comes from its **cross-plane crank**: the two banks do
        /// not fire evenly, which puts a strong component at *half* the firing frequency. That
        /// half-order is the rumble — without it you get a smooth six, not a V8. On top of that the
        /// harmonics roll off steeply, because these engines are boomy rather than raspy, and the whole
        /// thing is soft-clipped to add the growl that a pure sine stack lacks.
        /// </summary>
        private static AudioClip BuildEngineClip()
        {
            var samples = new float[SampleRate];
            const int harmonics = 12;
            float peak = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float cycles = i / (float)SampleRate * Fundamental;

                // The half-order rumble, loud enough to dominate the low end.
                float value = Mathf.Sin(2f * Mathf.PI * cycles * 0.5f) * 0.85f;

                for (int h = 1; h <= harmonics; h++)
                {
                    // Steep rolloff: boomy, not raspy.
                    float amplitude = 1f / Mathf.Pow(h, 1.35f);

                    if (h == 1)
                    {
                        amplitude *= 1.6f;
                    }
                    else if (h == 2)
                    {
                        amplitude *= 1.25f;
                    }

                    value += Mathf.Sin(2f * Mathf.PI * cycles * h) * amplitude;
                }

                // Quarter-order lope, so it loafs at idle instead of droning.
                value *= 0.78f + 0.22f * Mathf.Sin(2f * Mathf.PI * cycles * 0.25f);

                // Soft clip. Cheap saturation, and where the growl comes from.
                value = value / (1f + Mathf.Abs(value * 0.55f));

                samples[i] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }

            if (peak > 0.0001f)
            {
                float scale = 0.85f / peak;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= scale;
                }
            }

            AudioClip clip = AudioClip.Create("EngineDrone", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Low-passed noise for wind. The tail is crossfaded into the head, otherwise the loop point
        /// is an audible tick every second.
        /// </summary>
        private static AudioClip BuildWindClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x13579BDFu;
            float lowpass = 0f;

            for (int i = 0; i < generated; i++)
            {
                // xorshift rather than UnityEngine.Random: deterministic and independent of game state.
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;
                lowpass += (white - lowpass) * 0.10f;
                raw[i] = lowpass;
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

            AudioClip clip = AudioClip.Create("WindNoise", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
