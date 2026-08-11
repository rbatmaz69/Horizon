using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Engine sound, synthesised at runtime — the project carries no audio files, and a generated
    /// harmonic stack is both cheaper and more controllable than a recorded loop.
    ///
    /// Four layers, all generated at <c>Awake</c>:
    ///
    /// <list type="bullet">
    /// <item><b>Two engine voices</b> — the same note played with a different amount of anger,
    /// crossfaded on load. An engine being worked does not get louder so much as it gets <i>rougher</i>,
    /// and no gain curve on a single clip reproduces that. Revs come from the drivetrain's own gearbox
    /// rather than sweeping monotonically to top speed, because a pitch that only ever rises reads as a
    /// scooter or a CVT, and the drop on each upshift is most of what makes it sound like a car.</item>
    /// <item><b>The exhaust</b> — a bang on a hard upshift and a crackle on lift-off, both gated so
    /// they stay events rather than tics.</item>
    /// <item><b>Tyre squeal</b> — on how far the car is sliding rather than how fast it is going, which
    /// is what makes the friction circle audible instead of merely visible.</item>
    /// </list>
    ///
    /// <para><b>There was a wind layer and it was removed on purpose.</b> A speed-driven wind bed
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

        [Tooltip("The off-load voice: the engine cruising or trailing.")]
        [SerializeField] private AudioSource engineSource;

        [Tooltip("The on-load voice, crossfaded against the other one. Same clip length, same pitch — "
               + "what differs is the harmonic content, which is what makes an engine sound like it is "
               + "working rather than merely loud.")]
        [SerializeField] private AudioSource engineLoadSource;

        [Tooltip("One-shots: the bang on a hard upshift and the crackle on overrun.")]
        [SerializeField] private AudioSource exhaustSource;

        [Tooltip("Tyre squeal, looping, driven by how far the car is sliding.")]
        [SerializeField] private AudioSource tyreSource;

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

        [Header("Exhaust")]
        [Tooltip("An upshift bangs only if the throttle is at least this far open. A gentle change "
               + "through town should be silent — a car that barks on every shift stops being a car "
               + "with a temper and becomes a car with a tic.")]
        [Range(0f, 1f)] [SerializeField] private float bangThrottle = 0.75f;

        [Tooltip("...and only above this fraction of the redline. The two together are what 'a hard "
               + "shift' means.")]
        [Range(0f, 1f)] [SerializeField] private float bangRevs = 0.62f;

        [SerializeField] private float bangVolume = 0.55f;

        [Tooltip("Lift off above this fraction of the redline and the exhaust crackles for as long as "
               + "the revs stay up.")]
        [Range(0f, 1f)] [SerializeField] private float overrunRevs = 0.60f;

        [Tooltip("Average seconds between overrun pops. Jittered per pop, because a metronome does not "
               + "sound like an exhaust.")]
        [SerializeField] private float overrunInterval = 0.11f;

        [SerializeField] private float overrunVolume = 0.22f;

        [Header("Tyres")]
        [Tooltip("Sideways speed at the rear axle, m/s, at which the squeal is at full volume.")]
        [SerializeField] private float squealFullSlip = 6f;

        [SerializeField] private float squealVolume = 0.42f;

        private const int SampleRate = 44100;

        // A V8 at a lazy idle fires around 48 times a second. 48 over exactly one second is 48 whole
        // cycles, and the components at half and quarter order land on 24 and 12 — all integers, so the
        // loop closes without a click. Change this and you must keep that property.
        private const float Fundamental = 48f;

        private float revs;
        private float smoothedThrottle;
        private float load;

        /// <summary>Was the box between gears last frame, so a shift can be caught on its first one.</summary>
        private bool wasShifting;

        private float overrunTimer;

        /// <summary>Own xorshift stream, so the crackle does not depend on what else rolled a die.</summary>
        private uint random = 0x51ED2701u;

        private AudioClip bangClip;

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
                engineSource.clip = BuildEngineClip(false);
                engineSource.loop = true;
                engineSource.Play();
            }

            if (engineLoadSource != null)
            {
                engineLoadSource.clip = BuildEngineClip(true);
                engineLoadSource.loop = true;
                engineLoadSource.volume = 0f;
                engineLoadSource.Play();
            }

            if (tyreSource != null)
            {
                tyreSource.clip = BuildSquealClip();
                tyreSource.loop = true;
                tyreSource.volume = 0f;
                tyreSource.Play();
            }

            // Built once and fired with PlayOneShot, so overlapping pops mix instead of cutting each
            // other off — an overrun is several bangs deep.
            bangClip = BuildBangClip();
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);
            smoothedThrottle = Mathf.MoveTowards(smoothedThrottle, throttle, 4f * deltaTime);

            float coverAmount = cover != null ? cover.CoverAmount : 0f;
            float targetRevs = ResolveRevs();

            revs = Mathf.Lerp(revs, targetRevs, 1f - Mathf.Exp(-revSmoothing * deltaTime));

            load = Mathf.Clamp01(smoothedThrottle * 0.65f + revs * 0.45f);

            // Drop off during the shift. The gearbox has cut the torque, so the engine should go
            // quiet for that moment too — it is half of why a gear change registers.
            if (vehicle != null && vehicle.IsShifting)
            {
                load *= 0.35f;
            }

            float pitch = Mathf.Lerp(idlePitch, redlinePitch, revs);
            float boost = 1f + coveredEngineBoost * coverAmount;
            float level = (idleVolume + loadVolume * load) * boost;

            // The two voices are the same note played with a different amount of anger, crossfaded on
            // load — so the engine *hardens* as it is worked rather than only getting louder, which is
            // what one clip and a volume curve could ever do. Same pitch on both, or the crossfade
            // beats against itself.
            if (engineSource != null)
            {
                engineSource.pitch = pitch;
                engineSource.volume = level * (1f - load);
            }

            if (engineLoadSource != null)
            {
                engineLoadSource.pitch = pitch;
                engineLoadSource.volume = level * load;
            }

            if (engineReverb != null)
            {
                // Faded rather than switched: a preset toggling on a frame boundary is audible as a click,
                // and the mouth of a tunnel is a gradual thing anyway.
                engineReverb.reverbLevel = Mathf.Lerp(-10000f, 600f, coverAmount);
                engineReverb.dryLevel = Mathf.Lerp(0f, -280f, coverAmount);
            }

            UpdateExhaust(deltaTime, throttle, boost);
            UpdateTyres();
        }

        /// <summary>
        /// The bang on a hard upshift, and the crackle when you lift off with the revs up.
        ///
        /// <para>Both gated, and the gates are the whole design. A car that barks on every gear change
        /// is not a car with a temper, it is a car with a tic — the fourth one is already wallpaper and
        /// the fortieth is an irritation. Full throttle near the redline is rare enough that it stays an
        /// event, and it is exactly the moment the driver is asking for it.</para>
        ///
        /// <para>The overrun is deliberately irregular. An exhaust popping on a metronome sounds like a
        /// metronome; jittering the interval is most of what makes it read as unburnt fuel lighting off
        /// rather than as a sound effect on a timer.</para>
        /// </summary>
        private void UpdateExhaust(float deltaTime, float throttle, float boost)
        {
            if (exhaustSource == null || bangClip == null || vehicle == null)
            {
                return;
            }

            bool shifting = vehicle.IsShifting;

            // The first frame of the shift, not every frame of it.
            if (shifting && !wasShifting && throttle >= bangThrottle && revs >= bangRevs)
            {
                exhaustSource.PlayOneShot(bangClip, bangVolume * boost);
                Banged?.Invoke();
            }

            wasShifting = shifting;

            // Overrun: revs up, foot off, not mid-shift.
            if (!shifting && throttle < 0.05f && revs >= overrunRevs)
            {
                overrunTimer -= deltaTime;

                if (overrunTimer <= 0f)
                {
                    // Quieter than a shift bang and quieter still as the revs fall away.
                    float fade = Mathf.InverseLerp(overrunRevs, 1f, revs);
                    exhaustSource.PlayOneShot(bangClip, overrunVolume * boost * (0.4f + 0.6f * fade));
                    Crackled?.Invoke();

                    overrunTimer = overrunInterval * (0.55f + NextFloat() * 1.1f);
                }
            }
            else
            {
                overrunTimer = 0f;
            }
        }

        /// <summary>
        /// Tyre squeal, on how far the car is actually sliding rather than on how fast it is going.
        ///
        /// Without this the friction circle is something you can see and not hear, and a drift with no
        /// noise reads as the car being on ice rather than as the tyres being at their limit.
        /// </summary>
        private void UpdateTyres()
        {
            if (tyreSource == null || vehicle == null)
            {
                return;
            }

            float slip = Mathf.Clamp01(vehicle.RearSlip / Mathf.Max(0.1f, squealFullSlip));

            // Squared, so a tyre on the edge of grip whispers and one properly alight howls — a linear
            // ramp has the car squealing gently through every roundabout.
            tyreSource.volume = squealVolume * slip * slip;
            tyreSource.pitch = Mathf.Lerp(0.82f, 1.25f, slip);
        }

        /// <summary>Raised on a hard upshift, for the exhaust flame to fire on.</summary>
        public event System.Action Banged;

        /// <summary>Raised on each overrun pop. A smaller flame than <see cref="Banged"/>.</summary>
        public event System.Action Crackled;

        private float NextFloat()
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;

            return (random >> 8) * (1f / 16777216f);
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
        /// <summary>
        /// One exhaust report: a hard crack over a short thump.
        ///
        /// <para>Two things in a fifth of a second. The crack is noise under an exponential decay, and
        /// it is what carries over the engine; the thump is a 70 Hz sine falling to 45, and it is what
        /// you feel rather than hear. A bang made of only the first is a hiss and only the second is a
        /// door closing.</para>
        ///
        /// <para>No loop-point care needed here, unlike every other clip in this file — a one-shot is
        /// over before it can meet its own tail. It does need to *end* at silence, though, or
        /// PlayOneShot clips it into a click, hence the decay running the full length.</para>
        /// </summary>
        private static AudioClip BuildBangClip()
        {
            const float seconds = 0.22f;
            int count = Mathf.RoundToInt(SampleRate * seconds);
            var samples = new float[count];

            uint state = 0x9E3779B9u;
            float lowpass = 0f;
            float peak = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;

                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;

                // Band-passed by taking a low-passed copy away from the raw noise: what is left is the
                // mid, which is where a crack lives. A full-band burst sounds like static.
                lowpass += (white - lowpass) * 0.25f;
                float crack = (white - lowpass) * Mathf.Exp(-t * 22f);

                // The thump, dropping in pitch as it decays the way a pressure pulse does.
                float thumpHz = Mathf.Lerp(70f, 45f, t);
                float thump = Mathf.Sin(2f * Mathf.PI * thumpHz * (i / (float)SampleRate))
                              * Mathf.Exp(-t * 14f) * 0.8f;

                float value = crack + thump;

                // Sharp attack, but not instantaneous: a true step is a click in its own right.
                value *= Mathf.Min(1f, i / (SampleRate * 0.002f));

                samples[i] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }

            if (peak > 0.0001f)
            {
                float scale = 0.9f / peak;
                for (int i = 0; i < count; i++)
                {
                    samples[i] *= scale;
                }
            }

            AudioClip clip = AudioClip.Create("ExhaustBang", count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Tyre squeal: narrow-band noise, resonant enough to have a pitch.
        ///
        /// <para>A squeal is not a hiss. What makes rubber sing is a resonance — the tread block
        /// stick-slipping at a frequency the carcass rings at — so this is noise pushed through a
        /// resonant band around 1.1 kHz rather than a filtered wash. The residual noise around it is
        /// the scrub, and dropping it entirely gives a test tone.</para>
        ///
        /// <para>Looped, so the tail is crossfaded into the head exactly as the old wind bed's was —
        /// otherwise it ticks once a second, which is the failure mode this file already documents.</para>
        /// </summary>
        private static AudioClip BuildSquealClip()
        {
            const int fade = 4096;
            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x2545F491u;

            // A simple two-pole resonator. Q high enough to have a note, low enough to still be noise.
            float resonance = 0.985f;
            float centre = 2f * Mathf.PI * 1100f / SampleRate;
            float a1 = -2f * resonance * Mathf.Cos(centre);
            float a2 = resonance * resonance;
            float y1 = 0f;
            float y2 = 0f;

            for (int i = 0; i < generated; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;

                float y = white - a1 * y1 - a2 * y2;
                y2 = y1;
                y1 = y;

                // The ring plus a little of the raw scrub under it.
                raw[i] = y * 0.35f + white * 0.12f;
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

            AudioClip clip = AudioClip.Create("TyreSqueal", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <param name="onLoad">
        /// The angry one. Same firing frequency and the same half-order rumble — it has to be the same
        /// engine — but the harmonics roll off far more slowly and the soft clip is driven harder, which
        /// is what turns boom into bark. Crossfading the two on load is the whole of "more aggressive":
        /// an engine under power does not get louder so much as it gets *rougher*, and no amount of gain
        /// on one clip reproduces that.
        /// </param>
        private static AudioClip BuildEngineClip(bool onLoad)
        {
            var samples = new float[SampleRate];
            const int harmonics = 12;
            float peak = 0f;

            // 1.35 is boomy and 0.85 is raspy. That exponent is the single number separating the two
            // voices, and everything else here follows it.
            float rolloff = onLoad ? 0.85f : 1.35f;
            float drive = onLoad ? 1.25f : 0.55f;

            for (int i = 0; i < samples.Length; i++)
            {
                float cycles = i / (float)SampleRate * Fundamental;

                // The half-order rumble, loud enough to dominate the low end.
                float value = Mathf.Sin(2f * Mathf.PI * cycles * 0.5f) * 0.85f;

                for (int h = 1; h <= harmonics; h++)
                {
                    // Steep rolloff: boomy, not raspy.
                    float amplitude = 1f / Mathf.Pow(h, rolloff);

                    // Odd harmonics are what a hard-edged exhaust note is made of, so the loaded voice
                    // leans on them. Even ones are the smooth part and stay where they are.
                    if (onLoad && (h & 1) == 1)
                    {
                        amplitude *= 1.35f;
                    }

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
                value = value / (1f + Mathf.Abs(value * drive));

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

            AudioClip clip = AudioClip.Create(
                onLoad ? "EngineDroneLoaded" : "EngineDrone", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
