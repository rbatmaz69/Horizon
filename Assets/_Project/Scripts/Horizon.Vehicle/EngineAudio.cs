using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Engine sound, synthesised at runtime — the project carries no audio files, and a generated
    /// harmonic stack is both cheaper and more controllable than a recorded loop.
    ///
    /// One layer: a pitch-modulated engine drone, whose revs come from the drivetrain's own gearbox
    /// rather than sweeping monotonically to top speed, because a pitch that only ever rises reads as
    /// a scooter or a CVT. The drop on each upshift is most of what makes it sound like a car.
    ///
    /// <para><b>There was a second layer and it was removed on purpose.</b> A speed-driven wind bed
    /// rose with the square of speed and swept its pitch from 0.85 to 1.3 as it went, so every
    /// acceleration came with a whoosh over the engine — which is what it sounds like from outside a
    /// car, not from in one. Wind that only appears when the car is already fast, or that ducks under
    /// throttle, was considered and is worse: the first is a layer you never hear and the second
    /// swells when you lift off, which is backwards. If wind comes back it should come back as
    /// something the player can hear *through*, not over.</para>
    /// </summary>
    public sealed class EngineAudio : MonoBehaviour
    {
        [SerializeField] private VehicleController vehicle;
        [SerializeField] private AudioSource engineSource;

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

        [Header("Under cover")]
        [Tooltip("Shared cover probe. Found automatically if left empty.")]
        [SerializeField] private VehicleCover cover;

        [Tooltip("Reverb on the engine layer, faded in under cover. This is what actually sells being "
               + "inside a tunnel — more than the darkness does.")]
        [SerializeField] private AudioReverbFilter engineReverb;

        [Tooltip("How much louder the engine gets with walls around it.")]
        [SerializeField] private float coveredEngineBoost = 0.3f;

        private const int SampleRate = 44100;

        // A V8 at a lazy idle fires around 48 times a second. 48 over exactly one second is 48 whole
        // cycles, and the components at half and quarter order land on 24 and 12 — all integers, so the
        // loop closes without a click. Change this and you must keep that property.
        private const float Fundamental = 48f;

        private float revs;
        private float smoothedThrottle;

        /// <summary>Current revs, 0-1. Useful for a HUD later.</summary>
        public float Revs => revs;

        /// <summary>Selected gear, straight from the drivetrain. There is no separate audio gearbox.</summary>
        public int Gear => vehicle != null ? vehicle.Gear : 1;

        private void Awake()
        {
            if (vehicle == null)
            {
                vehicle = GetComponentInParent<VehicleController>();
            }

            if (cover == null)
            {
                cover = GetComponentInParent<VehicleCover>();
            }

            if (engineSource != null)
            {
                engineSource.clip = BuildEngineClip();
                engineSource.loop = true;
                engineSource.Play();
            }
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);
            smoothedThrottle = Mathf.MoveTowards(smoothedThrottle, throttle, 4f * deltaTime);

            float coverAmount = cover != null ? cover.CoverAmount : 0f;
            float targetRevs = ResolveRevs();

            revs = Mathf.Lerp(revs, targetRevs, 1f - Mathf.Exp(-revSmoothing * deltaTime));

            if (engineSource != null)
            {
                engineSource.pitch = Mathf.Lerp(idlePitch, redlinePitch, revs);

                float load = Mathf.Clamp01(smoothedThrottle * 0.65f + revs * 0.45f);

                // Drop off during the shift. The gearbox has cut the torque, so the engine should go
                // quiet for that moment too — it is half of why a gear change registers.
                if (vehicle != null && vehicle.IsShifting)
                {
                    load *= 0.35f;
                }

                // Walls put the engine back in your ears.
                engineSource.volume = (idleVolume + loadVolume * load) * (1f + coveredEngineBoost * coverAmount);
            }

            if (engineReverb != null)
            {
                // Faded rather than switched: a preset toggling on a frame boundary is audible as a click,
                // and the mouth of a tunnel is a gradual thing anyway.
                engineReverb.reverbLevel = Mathf.Lerp(-10000f, 600f, coverAmount);
                engineReverb.dryLevel = Mathf.Lerp(0f, -280f, coverAmount);
            }
        }

        /// <summary>
        /// Takes the revs straight from the vehicle's own gearbox.
        ///
        /// This used to model a second, invented gearbox purely for the sound. Now that the drivetrain
        /// has real ratios there is exactly one gearbox, and the note follows the machine instead of
        /// running alongside it and gradually disagreeing.
        /// </summary>
        private float ResolveRevs()
        {
            if (vehicle == null || vehicle.Config == null)
            {
                return 0.15f + smoothedThrottle * 0.4f;
            }

            VehicleConfig config = vehicle.Config;
            float span = Mathf.Max(1f, config.RedlineRpm - config.IdleRpm);

            // Zero at idle rather than at zero rpm, so the pitch range maps onto the usable band.
            return Mathf.Clamp01((vehicle.EngineRpm - config.IdleRpm) / span);
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
    }
}
