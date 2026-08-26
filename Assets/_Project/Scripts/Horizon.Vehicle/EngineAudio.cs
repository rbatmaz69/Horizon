using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Engine sound, synthesised at runtime — the project carries no audio files, and a generated
    /// harmonic stack is both cheaper and more controllable than a recorded loop.
    ///
    /// Five layers, all generated in code — the engine voices at <c>Awake</c> and again whenever the
    /// player changes car, see <c>RebuildClips</c>; the exhaust, the turbo and the tyres once, because
    /// none of those depends on which engine is fitted:
    ///
    /// <list type="bullet">
    /// <item><b>Two engine voices</b> — the same note played with a different amount of anger,
    /// crossfaded on load. An engine being worked does not get louder so much as it gets <i>rougher</i>,
    /// and no gain curve on a single clip reproduces that. Revs come from the drivetrain's own gearbox
    /// rather than sweeping monotonically to top speed, because a pitch that only ever rises reads as a
    /// scooter or a CVT, and the drop on each upshift is most of what makes it sound like a car.</item>
    /// <item><b>The exhaust</b> — a bang on a hard upshift and a crackle on lift-off, both gated so
    /// they stay events rather than tics.</item>
    /// <item><b>The turbo</b> — a whistle tracking boost and a dump valve when the throttle shuts, both
    /// silent unless the car's config says it has a turbocharger. This is the layer that separates the
    /// two Japanese coupés from the six-cylinder saloon they otherwise share a firing order with.</item>
    /// <item><b>Tyre squeal</b> — on how far the car is sliding rather than how fast it is going, which
    /// is what makes the friction circle audible instead of merely visible.</item>
    /// </list>
    ///
    /// <para><b>What is per car and what is not.</b> Everything about the <i>engine</i> — its firing
    /// note, its rumble, its rolloff, its pitch range, its turbo — comes from <c>VehicleConfig</c>, so a
    /// new car is a new asset and not new code. The thresholds in this file are about the model rather
    /// than the machine: what counts as a hard shift, how long a valve waits before it may speak again.
    /// Those are the same on every car because they describe the rules, not the engine.</para>
    ///
    /// <para><b>There was a wind layer and it was removed on purpose.</b> A speed-driven wind bed
    /// rose with the square of speed and swept its pitch from 0.85 to 1.3 as it went, so every
    /// acceleration came with a whoosh over the engine — which is what it sounds like from outside a
    /// car, not from in one. Wind that only appears when the car is already fast, or that ducks under
    /// throttle, was considered and is worse: the first is a layer you never hear and the second
    /// swells when you lift off, which is backwards. If wind comes back it should come back as
    /// something the player can hear *through*, not over.</para>
    ///
    /// <para>The turbo whistle is the layer most likely to be mistaken for that one coming back, and
    /// the reasons it is not are set out on <c>VehicleConfig.TurboWhistle</c> — chiefly that it is
    /// driven by boost rather than by road speed, so it dies the moment the driver lifts.</para>
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

        [Tooltip("One-shots: the bang on a hard upshift, the crackle on overrun, the dump valve.")]
        [SerializeField] private AudioSource exhaustSource;

        [Tooltip("The continuous exhaust note, looping — the firing pulses through the pipe's low "
               + "resonances and nothing else. Behind the driver, where an exhaust is.")]
        [SerializeField] private AudioSource exhaustToneSource;

        [Tooltip("Tyre squeal, looping, driven by how far the car is sliding.")]
        [SerializeField] private AudioSource tyreSource;

        [Tooltip("The turbo's whistle, looping, driven by boost. Silent on a naturally aspirated car — "
               + "see VehicleConfig.TurboWhistle.")]
        [SerializeField] private AudioSource turboSource;

        [Header("Engine")]
        [SerializeField] private float idleVolume = 0.34f;
        [SerializeField] private float loadVolume = 0.58f;

        [Tooltip("How fast revs follow the wheels. Deliberately slow: a heavy flywheel takes its time, "
               + "and the lag is a large part of why the car sounds big.")]
        [SerializeField] private float revSmoothing = 4.5f;

        [Tooltip("Longitudinal acceleration in m/s² that counts as the engine working flat out. A "
               + "traction-limited launch is about 8.")]
        [SerializeField] private float loadReferenceAcceleration = 6f;

        [Tooltip("How much of the load blend comes from the car actually gaining speed, 0 to 1.\n\n"
               + "Load used to be throttle and revs only — intent and engine speed, neither of which "
               + "knows whether the car is going anywhere. Pulling away on the flat and leaning on the "
               + "throttle against a hill sounded identical, which is the one comparison where an engine "
               + "has something to say.")]
        [Range(0f, 1f)]
        [SerializeField] private float accelerationLoadWeight = 0.3f;

        [Tooltip("How far the engine note falls while the box is between gears, as a fraction of the "
               + "idle-to-redline span. The gearbox has cut the drive, so the engine is spinning down "
               + "against nothing — hearing that fall is what makes a shift an event rather than a gap.")]
        [Range(0f, 0.3f)]
        [SerializeField] private float shiftPitchDrop = 0.08f;

        [Header("Under cover")]
        [Tooltip("Shared cover probe. Found automatically if left empty.")]
        [SerializeField] private VehicleCover cover;

        [Tooltip("Reverb on the engine layer, faded in under cover. This is what actually sells being "
               + "inside a tunnel — more than the darkness does.")]
        [SerializeField] private AudioReverbFilter engineReverb;

        [Tooltip("How much louder the engine gets with walls around it.")]
        [SerializeField] private float coveredEngineBoost = 0.3f;

        [Header("Exhaust")]
        [Tooltip("Ceiling for the continuous exhaust layer, in the same relationship to the config's "
               + "ExhaustLevel that whistleVolume has to TurboWhistle: this is the mix decision, that "
               + "one is the car's share of it.")]
        [Range(0f, 1f)] [SerializeField] private float exhaustVolume = 0.62f;

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

        [Header("Turbo")]
        [Tooltip("Ceiling for the whistle: what a car with TurboWhistle = 1 reaches at full boost.\n\n"
               + "The split is deliberate. This is a mix decision and is the same for every car; the "
               + "config's TurboWhistle is that car's fraction of it. Without a ceiling here the biggest "
               + "turbo in the game would peak at 1.0 against an engine that tops out at 0.80, so the "
               + "car most defined by its engine would be the one you could not hear it in.")]
        [Range(0f, 1f)] [SerializeField] private float whistleVolume = 0.45f;

        [Tooltip("Ceiling for the dump valve, in the same relationship to BlowOffLevel. Kept just under "
               + "the shift bang's 0.55: the valve is the louder event on a turbocharged car but it "
               + "should not be the loudest thing the car does.")]
        [Range(0f, 1f)] [SerializeField] private float blowOffVolume = 0.48f;

        [Tooltip("Boost has to have been at least this high for shutting the throttle to be worth a "
               + "dump-valve report. Below it there is nothing in the pipes to dump, and a valve that "
               + "chirps every time you come off the throttle in traffic is a tic, exactly like an "
               + "exhaust that bangs on every shift.")]
        [Range(0f, 1f)] [SerializeField] private float blowOffBoost = 0.42f;

        [Tooltip("Throttle below which the valve counts as shut.")]
        [Range(0f, 0.5f)] [SerializeField] private float blowOffThrottle = 0.12f;

        [Tooltip("Seconds before it may fire again. A gear change is a lift and a shift arriving one "
               + "frame apart, and without this it is two reports.")]
        [SerializeField] private float blowOffCooldown = 0.30f;

        [Header("Tyres")]
        [Tooltip("Sideways speed at the rear axle, m/s, at which the squeal is at full volume.")]
        [SerializeField] private float squealFullSlip = 6f;

        [SerializeField] private float squealVolume = 0.42f;

        private const int SampleRate = 44100;

        // A V8 at a lazy idle fires around 48 times a second. 48 over exactly one second is 48 whole
        // cycles, and the components at half and quarter order land on 24 and 12 — all integers, so the
        // loop closes without a click. Change this and you must keep that property.
        //
        // Only the fallback now: the note comes from VehicleConfig.EngineFundamentalHz, and this is what
        // is used before a config exists. It is the same 48, so a car with no config sounds like the
        // fastback rather than like silence.
        private const float FallbackFundamental = 48f;

        private float revs;
        private float smoothedThrottle;
        private float load;

        /// <summary>Was the box between gears last frame, so a shift can be caught on its first one.</summary>
        private bool wasShifting;

        private float overrunTimer;

        /// <summary>Boost in the plenum, 0-1. Not a pressure in bar — a fraction of what this turbo makes.</summary>
        private float boostPressure;

        /// <summary>So the dump valve fires on the edge of the lift rather than for every frame after it.</summary>
        private bool wasOnBoost;

        private float blowOffTimer;

        /// <summary>Own xorshift stream, so the crackle does not depend on what else rolled a die.</summary>
        private uint random = 0x51ED2701u;

        private AudioClip bangClip;

        /// <summary>
        /// Kept so <see cref="RebuildClips"/> can destroy them. A generated <c>AudioClip</c> is an
        /// engine object, not managed memory — dropping the reference leaks 350 kB per car change, and
        /// the player swapping cars in the garage is exactly the person who would do it forty times.
        /// </summary>
        private AudioClip engineClip;

        private AudioClip engineLoadClip;

        private AudioClip exhaustToneClip;

        private AudioClip blowOffClip;

        /// <summary>Current revs, 0-1. Useful for a HUD later.</summary>
        public float Revs => revs;

        /// <summary>Selected gear, straight from the drivetrain. There is no separate audio gearbox.</summary>
        public int Gear => vehicle != null ? vehicle.Gear : 1;

        /// <summary>
        /// Boost in the plenum, 0-1 — a fraction of what this turbo makes, not a pressure in bar.
        ///
        /// <para><b>Published rather than modelled twice.</b> <c>BoostGauge</c> draws this, and the
        /// alternative was a boost model of its own somewhere in <c>Horizon.Game</c> or in the
        /// drivetrain. <see cref="UpdateTurbo"/> sets out at length why the number lives here; what
        /// matters for a reader is that the needle and the whistle are then the same number, so a
        /// dial that says the turbo is on song cannot disagree with an engine that sounds like it is
        /// not. It is zero, always, on a car <see cref="VehicleConfig.IsTurbocharged"/> rejects.</para>
        ///
        /// <para>Already smoothed — it is an exponential chase at the car's own spool rate, driven
        /// from this component's <c>Update</c> and therefore frame-paced. <b>A reader should not
        /// smooth it again:</b> that would be lag the car does not have, and collapse is four times
        /// as fast as build precisely so a lift reads as a lift.</para>
        /// </summary>
        public float Boost01 => boostPressure;

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

            if (engineLoadSource != null)
            {
                engineLoadSource.volume = 0f;
            }

            if (exhaustToneSource != null)
            {
                exhaustToneSource.volume = 0f;
            }

            // Builds the three clips and starts all three sources.
            RebuildClips();

            if (tyreSource != null)
            {
                tyreSource.clip = BuildSquealClip();
                tyreSource.loop = true;
                tyreSource.volume = 0f;
                tyreSource.Play();
            }

            // Always playing, at zero on a car with no turbo. Starting and stopping the source on
            // demand would put the whistle's phase wherever the clip happened to be left, and a tone
            // restarting mid-cycle is a click on the one layer in this file with an actual pitch.
            if (turboSource != null)
            {
                turboSource.clip = BuildTurboClip();
                turboSource.loop = true;
                turboSource.volume = 0f;
                turboSource.Play();
            }

            // Built once and fired with PlayOneShot, so overlapping pops mix instead of cutting each
            // other off — an overrun is several bangs deep.
            bangClip = BuildBangClip();
            blowOffClip = BuildBlowOffClip();
        }

        /// <summary>
        /// How long the note takes to die when the tank runs out, seconds.
        ///
        /// <para><b>A fade, not a cut.</b> Every source here is a looping waveform, and dropping a
        /// periodic signal to zero part-way through a cycle is a click — the same fact the whole file
        /// is built around, and the reason the drone is a whole number of cycles at the sample rate.
        /// A third of a second is an engine dying; much longer and it is an engine being turned down.
        /// The same ramp runs backwards when the tank is filled, which is a plausible restart.</para>
        /// </summary>
        private const float IgnitionFade = 0.35f;

        private float runGain = 1f;

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);

            // A dead engine has to stop making a noise, and it does not do so on its own. Revs fall to
            // zero and the pitch follows them down, but the level is idleVolume plus a load term — so
            // without this the car sits silent on the tacho, motionless on the road, and audibly idling
            // in the speaker.
            //
            // Applied as a gain on the engine and exhaust layers rather than as an early return out of
            // this method, and that distinction matters: a coasting car still squeals its tyres, and the
            // reverb still has to follow the car out of a tunnel. Both of those live below.
            float running = vehicle != null && vehicle.IsOutOfFuel ? 0f : 1f;
            runGain = Mathf.MoveTowards(runGain, running, deltaTime / IgnitionFade);
            smoothedThrottle = Mathf.MoveTowards(smoothedThrottle, throttle, 4f * deltaTime);

            float coverAmount = cover != null ? cover.CoverAmount : 0f;
            float targetRevs = ResolveRevs();

            revs = Mathf.Lerp(revs, targetRevs, 1f - Mathf.Exp(-revSmoothing * deltaTime));

            // Throttle and revs say what is being asked for; acceleration says whether the road is
            // giving it. The third term is what separates a car that is pulling from one that is merely
            // revving, and the weights are scaled so the sum still reaches 1 at full effort.
            float effort = 1f - accelerationLoadWeight;
            float pulling = vehicle != null
                ? Mathf.Clamp01(vehicle.LongitudinalAcceleration
                    / Mathf.Max(0.01f, loadReferenceAcceleration))
                : 0f;

            load = Mathf.Clamp01(
                effort * (smoothedThrottle * 0.65f + revs * 0.45f)
                + accelerationLoadWeight * pulling);

            VehicleConfig config = vehicle != null ? vehicle.Config : null;
            float idlePitch = config != null ? config.IdlePitch : 0.46f;
            float redlinePitch = config != null ? config.RedlinePitch : 1.50f;

            float pitch = Mathf.Lerp(idlePitch, redlinePitch, revs);

            // Drop off during the shift. The gearbox has cut the torque, so the engine should go
            // quiet for that moment too — it is half of why a gear change registers.
            //
            // The other half is the pitch. An engine uncoupled from the wheels falls away, and until
            // this was here the 0.35 s of zero drive the drivetrain really does produce was audible
            // only as a dip in volume — the one moment in a full-throttle pull with an actual edge on
            // it, rendered as the note going quiet and carrying on.
            if (vehicle != null && vehicle.IsShifting)
            {
                load *= 0.35f;
                pitch -= (redlinePitch - idlePitch) * shiftPitchDrop;
            }
            // Renamed from "boost" when the turbo arrived: in a file that now models plenum pressure,
            // one name for two things is a bug waiting for whoever reads it next.
            float coverGain = 1f + coveredEngineBoost * coverAmount;
            float level = (idleVolume + loadVolume * load) * coverGain * runGain;

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

            // The pipe follows the same note, and is loudest when the engine is working — but never
            // silent, because an exhaust does not stop existing when you lift. It is also the layer that
            // carries the car when the engine layer is quiet, which is most of a cruise.
            if (exhaustToneSource != null)
            {
                float exhaustLevel = config != null ? config.ExhaustLevel : 0.55f;

                exhaustToneSource.pitch = pitch;
                exhaustToneSource.volume = exhaustVolume * exhaustLevel
                                          * (0.35f + 0.65f * load) * coverGain * runGain;
            }

            if (engineReverb != null)
            {
                // Faded rather than switched: a preset toggling on a frame boundary is audible as a click,
                // and the mouth of a tunnel is a gradual thing anyway.
                engineReverb.reverbLevel = Mathf.Lerp(-10000f, 600f, coverAmount);
                engineReverb.dryLevel = Mathf.Lerp(0f, -280f, coverAmount);
            }

            // Before the exhaust, because the dump valve is a thing the exhaust source plays and it
            // wants this frame's boost rather than the last one's.
            UpdateTurbo(deltaTime, throttle, coverGain, config);

            UpdateExhaust(deltaTime, throttle, coverGain);
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
        /// The turbocharger: a whistle that tracks boost, and a dump valve when the throttle shuts.
        ///
        /// <para><b>Boost is modelled here rather than in the drivetrain, and that is deliberate.</b>
        /// <c>VehicleController</c> already has a torque curve, and the hole at the bottom of a
        /// turbocharged engine's range is expressed there as the shape of <c>TorqueByRpm</c> — flat
        /// until a third of the way up and then everything at once. Adding a second boost model that
        /// fed back into the physics would be two gearboxes again, the mistake <see cref="ResolveRevs"/>
        /// documents. What this needs is not torque, it is a number that rises and falls the way plenum
        /// pressure does so the noise can follow it, and that is cheap and local.</para>
        ///
        /// <para><b>The asymmetry is the whole feel.</b> Boost builds at
        /// <c>TurboSpoolRate</c> and collapses at four times it. Spooling is a turbine accelerating
        /// against inertia; a closed throttle is a wall. Make the two rates equal and what you have is a
        /// volume envelope on a tone, which sounds like a synthesiser and not like a compressor.</para>
        ///
        /// <para>Squared for level, like <see cref="UpdateTyres"/>: barely there at part load, singing
        /// at full boost. The alternative is a car that whistles gently through every roundabout, which
        /// is the failure mode of the wind layer this file's own summary explains was deleted.</para>
        /// </summary>
        private void UpdateTurbo(float deltaTime, float throttle, float coverGain, VehicleConfig config)
        {
            float whistle = config != null ? config.TurboWhistle : 0f;
            float valve = config != null ? config.BlowOffLevel : 0f;

            // A naturally aspirated car pays for none of this, and — more to the point — cannot
            // accumulate boost that a later swap into a turbocharged body would inherit.
            //
            // The test is the config's own, not the pair of comparisons that used to stand here:
            // BoostGauge asks the same question to decide whether to draw a dial at all, and two
            // spellings of "has a turbo" is exactly the kind of duplicate this project keeps paying
            // for. See VehicleConfig.IsTurbocharged.
            if (config == null || !config.IsTurbocharged)
            {
                boostPressure = 0f;
                wasOnBoost = false;

                if (turboSource != null)
                {
                    turboSource.volume = 0f;
                }

                return;
            }

            // Exhaust energy: revs above the spool point, and only as much of it as the driver is
            // asking for. Throttle alone would have the turbo on full song at 900 rpm; revs alone would
            // have it screaming on a trailing overrun, which is the one moment it must not.
            float available = Mathf.InverseLerp(config.TurboSpoolRevs, 1f, revs) * smoothedThrottle;

            float rate = available > boostPressure
                ? config.TurboSpoolRate
                : config.TurboSpoolRate * 4f;

            boostPressure = Mathf.Lerp(boostPressure, available, 1f - Mathf.Exp(-rate * deltaTime));

            if (turboSource != null)
            {
                turboSource.volume = whistleVolume * whistle * boostPressure * boostPressure * coverGain;

                // An octave below the car's note at the bottom to the note itself at full boost. A
                // compressor's whistle is its shaft speed, so it rises with boost and not with rpm —
                // which is why the pitch here is not simply the engine's.
                float note = config.TurboNotePitch;
                turboSource.pitch = Mathf.Lerp(note * 0.5f, note, boostPressure);
            }

            UpdateBlowOff(deltaTime, throttle, coverGain, valve);
        }

        /// <summary>
        /// The dump valve: shut the throttle with boost still in the pipes and it has to go somewhere.
        ///
        /// <para>Fires on the <i>edge</i> — the frame the throttle closes — rather than for as long as
        /// it stays closed, and only if boost had actually built. Both gates are the argument
        /// <see cref="UpdateExhaust"/> makes about the shift bang: a valve that chirps every time you
        /// come off the throttle in a town is not a car with a turbo, it is a car with a hiccup.</para>
        ///
        /// <para>A shift counts as a lift. The drivetrain cuts the drive for <c>ShiftTime</c> and a real
        /// gearbox shuts the throttle to do it, so an upshift under load is exactly when this should
        /// speak — and on a hard one it lands alongside the exhaust bang, which is what an upshift in a
        /// turbocharged car actually sounds like.</para>
        /// </summary>
        private void UpdateBlowOff(float deltaTime, float throttle, float coverGain, float valve)
        {
            blowOffTimer -= deltaTime;

            bool onBoost = boostPressure >= blowOffBoost;
            bool shut = throttle < blowOffThrottle || (vehicle != null && vehicle.IsShifting);

            if (wasOnBoost && shut && valve > 0.001f && blowOffTimer <= 0f && exhaustSource != null
                && blowOffClip != null)
            {
                // Scaled by how much boost there was to lose, so a lift at the top of third is a report
                // and a lift at part load is a sigh.
                exhaustSource.PlayOneShot(
                    blowOffClip, blowOffVolume * valve * (0.35f + 0.65f * boostPressure) * coverGain);
                blowOffTimer = blowOffCooldown;
            }

            wasOnBoost = onBoost && !shut;
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
        /// Re-synthesises the two engine voices from the vehicle's current config.
        ///
        /// <para>Called from <c>Awake</c> and again by <c>VehicleBodySet.Select</c> whenever the player
        /// changes car — a diesel and a turbocharged six are different notes, not the same note at a
        /// different pitch, so nothing short of rebuilding the clip gets there.</para>
        ///
        /// <para><b>Not from Update, and not from anything that runs while driving.</b> Two seconds of
        /// audio is about two million <c>Sin</c> calls and a 350 kB allocation each time. The one caller
        /// does it with the game paused and immediately before a <c>Teleport</c>, where a ten-millisecond
        /// stall is invisible; anywhere else it is a stutter, which is the failure this project's budget
        /// section exists to prevent.</para>
        ///
        /// <para>The sources keep playing across the swap. Assigning a clip to a playing
        /// <c>AudioSource</c> restarts it from zero, which is a discontinuity — but the old car's note
        /// has just been replaced by a different engine, so a clean restart is what it should sound
        /// like.</para>
        /// </summary>
        public void RebuildClips()
        {
            AudioClip previousEngine = engineClip;
            AudioClip previousLoad = engineLoadClip;
            AudioClip previousExhaust = exhaustToneClip;

            engineClip = BuildEngineClip(Voice.OffLoad);
            engineLoadClip = BuildEngineClip(Voice.OnLoad);
            exhaustToneClip = BuildEngineClip(Voice.Exhaust);

            // Assign, then Play. Assigning a clip to a playing AudioSource stops it, and it does not
            // resume on its own — so a rebuild left the engine and the exhaust silent for the rest of
            // the session while the turbo, whose clip is never reassigned, carried on. Every car sounded
            // like it had no engine, because it had none.
            Restart(engineSource, engineClip);
            Restart(engineLoadSource, engineLoadClip);
            Restart(exhaustToneSource, exhaustToneClip);

            // After the sources point at the new ones, never before: destroying a clip an AudioSource is
            // still holding is a stall at best.
            Release(previousEngine);
            Release(previousLoad);
            Release(previousExhaust);
        }

        private static void Restart(AudioSource source, AudioClip clip)
        {
            if (source == null)
            {
                return;
            }

            source.clip = clip;
            source.loop = true;
            source.Play();
        }

        private static void Release(AudioClip clip)
        {
            if (clip == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(clip);
            }
            else
            {
                DestroyImmediate(clip);
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
        /// One second of engine drone, in whatever engine the config describes.
        ///
        /// The character of an old American V8 comes from its **cross-plane crank**: the two banks do
        /// not fire evenly, which puts a strong component at *half* the firing frequency. That
        /// half-order is the rumble — without it you get a smooth six, not a V8. On top of that the
        /// harmonics roll off steeply, because these engines are boomy rather than raspy, and the whole
        /// thing is soft-clipped to add the growl that a pure sine stack lacks.
        ///
        /// <para>All three of those are now <see cref="VehicleConfig"/> fields, and the V8's values are
        /// their defaults — so this paragraph still describes the fastback exactly, and a straight six
        /// is the same paragraph with the rumble set to nothing.</para>
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

        /// <summary>
        /// One second of turbo whistle: a tone with two partials and the air going past it.
        ///
        /// <para>Tonal rather than filtered noise, which is where this differs from
        /// <see cref="BuildSquealClip"/>. A tyre squealing is a resonance in something that is mostly
        /// scrubbing; a compressor whistle is a shaft frequency you could hum, and pushing noise through
        /// a very high-Q filter to fake that gives a warble instead of a note.</para>
        ///
        /// <para><b>1800 Hz, and any whole number would do.</b> The loop rule this file lives by is a
        /// whole number of cycles in exactly one second — an integer frequency satisfies it outright.
        /// The engine drone needs its fundamental divisible by four only because it carries components
        /// at half and quarter order; there are none here. The noise bed does not loop on its own, so it
        /// gets the crossfaded tail the squeal uses.</para>
        ///
        /// <para>Partials at a third and an eighth: enough to stop it being a test tone, little enough
        /// that pitching it down an octave does not turn it into a hum.</para>
        /// </summary>
        private static AudioClip BuildTurboClip()
        {
            const float note = 1800f;
            const int fade = 4096;

            int generated = SampleRate + fade;
            var raw = new float[generated];

            uint state = 0x1B873593u;
            float lowpass = 0f;
            float highpass = 0f;

            for (int i = 0; i < generated; i++)
            {
                float cycles = i / (float)SampleRate * note;

                float value = Mathf.Sin(2f * Mathf.PI * cycles)
                              + Mathf.Sin(2f * Mathf.PI * cycles * 2f) * 0.33f
                              + Mathf.Sin(2f * Mathf.PI * cycles * 3f) * 0.12f;

                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;

                // The air through the compressor housing: a band from roughly 2.5 to 8 kHz, and thin.
                //
                // <b>The band matters more than the level does.</b> The first version of this ran the
                // noise from about 400 Hz up, which put a fifth of the layer's energy straight on top of
                // the engine's own harmonics — a whistle that muddied the drone every time it appeared,
                // which is the wind layer this file deleted wearing a different hat. Measured: 22% of
                // the clip's energy below 1.5 kHz then, 6% now, and nine tenths of it concentrated on
                // the note. Broadband noise under a whistle reads as tape hiss; a whistle with nothing
                // around it reads as a signal generator; this is the narrow strip between them.
                lowpass += (white - lowpass) * 0.75f;
                highpass += (lowpass - highpass) * 0.36f;

                raw[i] = value * 0.30f + (lowpass - highpass) * 0.14f;
            }

            var samples = new float[SampleRate];
            System.Array.Copy(raw, samples, SampleRate);

            // The tonal part is exactly periodic over the second, so crossfading it with itself is the
            // identity. All this actually does is close the noise.
            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                samples[i] = samples[i] * t + raw[SampleRate + i] * (1f - t);
            }

            Normalize(samples, 0.85f);

            AudioClip clip = AudioClip.Create("TurboWhistle", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// One dump-valve report: pressurised air let go at once.
        ///
        /// <para><b>Deliberately not <see cref="BuildBangClip"/> with a different envelope.</b> A bang is
        /// a mid crack over a 70 Hz thump — a pressure pulse, something you feel. This is only air:
        /// no low end at all, a slower attack, and a tail four times as long, because a valve empties a
        /// plenum rather than detonating. Give it a thump and it stops being a hiss and becomes a
        /// second, worse exhaust note.</para>
        ///
        /// <para>The hiss darkens as it decays, which is the tell. Pressure falls, the flow slows, and
        /// the noise loses its top — a spectrally static burst sounds like someone opening a valve on a
        /// tank that never empties.</para>
        ///
        /// <para>Measured, the result puts 94% of its energy above 3 kHz and under 1% below 1.5 kHz,
        /// which is what "only air" means here. Subtracting a lagged copy of white noise is already a
        /// high-pass; adding a further rumble blocker on top was tried and moved energy <i>down</i>
        /// into the engine's range, so there is deliberately nothing else in this loop.</para>
        /// </summary>
        private static AudioClip BuildBlowOffClip()
        {
            const float seconds = 0.32f;

            int count = Mathf.RoundToInt(SampleRate * seconds);
            var samples = new float[count];

            uint state = 0x27D4EB2Fu;
            float lowpass = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;

                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;

                // Coefficient falling from bright to dull over the burst: subtracting a low-passed copy
                // leaves the top, and letting the copy get slower takes the top away with it.
                float cutoff = Mathf.Lerp(0.42f, 0.10f, t);
                lowpass += (white - lowpass) * cutoff;

                float hiss = white - lowpass;

                // 30 ms of opening, then a long decay. A dump valve is a mechanism moving, not a spark.
                float attack = Mathf.Min(1f, i / (SampleRate * 0.030f));

                samples[i] = hiss * attack * Mathf.Exp(-t * 5.5f);
            }

            Normalize(samples, 0.9f);

            AudioClip clip = AudioClip.Create("BlowOff", count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>Scales a buffer so its loudest sample sits at <paramref name="peak"/>.</summary>
        private static void Normalize(float[] samples, float peak)
        {
            float loudest = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                loudest = Mathf.Max(loudest, Mathf.Abs(samples[i]));
            }

            if (loudest <= 0.0001f)
            {
                return;
            }

            float scale = peak / loudest;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= scale;
            }
        }

        /// <summary>Which of the three voices a clip is being built for.</summary>
        private enum Voice
        {
            /// <summary>The engine cruising or trailing.</summary>
            OffLoad,

            /// <summary>The engine being worked: rougher and driven harder, not merely louder.</summary>
            OnLoad,

            /// <summary>The pipe on its own — low resonances only, no mechanical noise.</summary>
            Exhaust,
        }

        /// <summary>
        /// One second of engine, built from firing pulses down one or two exhaust pipes.
        ///
        /// <para><b>This replaced a stack of twelve sine harmonics, and the reason is the only thing
        /// worth knowing about this method.</b> A harmonic stack is what an engine's spectrum looks
        /// like on a meter; it is not what an engine <i>is</i>. An engine is a sequence of explosions at
        /// particular crank angles, ringing a pipe. Synthesised as a sine stack it comes out as a drone
        /// with an engine-shaped spectrum — recognisably a synthesiser, which is what it sounded
        /// like. Synthesised as pulses through resonators it comes out as an engine, because that is
        /// the mechanism rather than the result.</para>
        ///
        /// <para><b>The rumble is no longer a parameter.</b> The old model had a <c>HalfOrderLevel</c>
        /// knob that had to be set to 0.85 for a V8 and 0 for a six, by hand, on trust. Here the crank
        /// angles do it: a cross-plane V8's two banks fire at 90/180/270/180 each, so they beat against
        /// one another, and an inline six's six evenly spaced pulses cannot. Measured on the generated
        /// clips, sub-firing-order energy comes out 3.4 for the cross-plane V8 against 0.12 for a
        /// flat-plane one and 0.63 for the six — a factor of nearly thirty between two engines with the
        /// same number of cylinders, from nothing but where the throws are.</para>
        ///
        /// <para><b>Why the loop closes with no crossfade.</b> The excitation is built as exactly one
        /// engine cycle and tiled, which is what an engine at a steady speed is. The resonators are run
        /// through a warm-up first and discarded, so what is recorded is the steady-state response to a
        /// periodic input — periodic in its turn, and therefore seamless. Every other generated loop in
        /// this file needs its tail crossfaded into its head; this one is exact. That only holds while
        /// <c>VehicleConfig.LoopsCleanly</c> does.</para>
        /// </summary>
        private AudioClip BuildEngineClip(Voice voice)
        {
            VehicleConfig config = vehicle != null ? vehicle.Config : null;

            float firing = config != null ? config.EngineFundamentalHz : FallbackFundamental;
            int cylinders = config != null ? Mathf.Max(2, config.Cylinders) : 8;
            FiringLayout layout = config != null
                ? config.Layout
                : FiringLayout.CrossPlaneV8;

            float[][] banks = BanksFor(layout, cylinders);

            // Samples in one 720° cycle. Rounded rather than asserted: a config that fails
            // LoopsCleanly still has to make a noise, and a sub-sample seam is a far better failure
            // than silence. The reset logs the ones that fail so they get fixed at the source.
            int cycles = Mathf.Max(1, Mathf.RoundToInt(firing / (cylinders * 0.5f)));
            int period = Mathf.Max(64, Mathf.RoundToInt(SampleRate / (float)cycles));

            float noise = config != null ? config.CombustionNoise : 0.45f;
            if (voice == Voice.Exhaust)
            {
                // The pipe carries combustion, not the mechanical clatter that goes with it.
                noise *= 0.55f;
            }
            else if (voice == Voice.OnLoad)
            {
                noise *= 1.5f;
            }

            float drive = config != null ? config.ExhaustDrive : 0.9f;
            drive *= voice == Voice.OnLoad ? 1.6f : (voice == Voice.Exhaust ? 2f : 0.6f);

            var samples = new float[SampleRate];

            uint state = 0x9E3779B9u;
            int width = Mathf.Max(2, Mathf.RoundToInt(SampleRate * 0.0016f));

            for (int bank = 0; bank < banks.Length; bank++)
            {
                float[] cell = BuildCycle(banks[bank], period, width, noise, ref state);

                // Two pipes are never quite the same length, and detuning the second is what lets the
                // banks beat instead of summing into one louder bank.
                float detune = bank == 0 ? 1f : 1.035f;
                AddPipe(samples, cell, firing, config, voice, detune);
            }

            Normalize(samples, 1f);

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = samples[i] / (1f + Mathf.Abs(samples[i] * drive));
            }

            Normalize(samples, 0.85f);

            AudioClip clip = AudioClip.Create(
                voice == Voice.Exhaust ? "ExhaustNote"
                    : voice == Voice.OnLoad ? "EngineDroneLoaded" : "EngineDrone",
                samples.Length, 1, SampleRate, false);

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Crank angles, in degrees of the 720° cycle, grouped by the pipe each cylinder breathes down.
        ///
        /// <para>The V8 tables are one real firing order split across its two banks. Written out rather
        /// than generated because there is nothing to generate from — which cylinder sits on which throw
        /// is a fact about a particular crankshaft, and it is the fact the whole sound rests on.</para>
        /// </summary>
        private static float[][] BanksFor(FiringLayout layout, int cylinders)
        {
            switch (layout)
            {
                case FiringLayout.CrossPlaneV8:
                    return CrossPlaneBanks;

                case FiringLayout.FlatPlaneV8:
                    return FlatPlaneBanks;

                default:
                    var inline = new float[cylinders];
                    for (int i = 0; i < cylinders; i++)
                    {
                        inline[i] = i * 720f / cylinders;
                    }

                    return new[] { inline };
            }
        }

        private static readonly float[][] CrossPlaneBanks =
        {
            new[] { 0f, 270f, 450f, 540f },
            new[] { 90f, 180f, 360f, 630f },
        };

        private static readonly float[][] FlatPlaneBanks =
        {
            new[] { 0f, 180f, 360f, 540f },
            new[] { 90f, 270f, 450f, 630f },
        };

        /// <summary>
        /// One engine cycle of excitation for a single bank: a shaped burst at each crank angle.
        ///
        /// <para>Bursts rather than ideal impulses, about 1.6 ms wide under a raised cosine, because an
        /// ideal impulse is infinite bandwidth and aliases into a click. The per-cylinder gain varies by
        /// three percent and is drawn <b>once per cylinder rather than once per firing</b> — cylinders
        /// genuinely differ a little, but a fresh number every firing would make the excitation
        /// aperiodic, which breaks the seamless loop and smears the firing order into mush. That was
        /// tried; the measured result was an inline six with more rumble than a V8.</para>
        /// </summary>
        private static float[] BuildCycle(
            float[] angles, int period, int width, float noise, ref uint state)
        {
            var cell = new float[period];

            for (int c = 0; c < angles.Length; c++)
            {
                float gain = 0.97f + 0.06f * NextUnit(ref state);
                int at = Mathf.RoundToInt(angles[c] / 720f * period);

                for (int k = 0; k < width; k++)
                {
                    float shape = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * k / width);
                    cell[(at + k) % period] += shape * gain;
                }
            }

            // Roughness scaled by the pulse it sits on, so it rides the combustion rather than hissing
            // in the gaps between them — which is the difference between a rough engine and a bad tape.
            float lowpass = 0f;
            for (int i = 0; i < period; i++)
            {
                lowpass += (NextUnit(ref state) * 2f - 1f - lowpass) * 0.35f;
                cell[i] += lowpass * noise * cell[i];
            }

            return cell;
        }

        /// <summary>
        /// Runs one bank's cycle through four resonances and adds the result in.
        ///
        /// <para><b>The lowest one sits on the firing order</b>, which is where an exhaust's fundamental
        /// actually is. <c>ExhaustPitchHz</c> and its 2.5× partner are the pipe's colour on top, and
        /// <c>ExhaustClatterHz</c> is the edge. Leaving the fundamental out — which this method did —
        /// filters away the only part of the sound anybody calls "the engine".</para>
        ///
        /// <para><b>Each resonance is normalised by its own energy, not its peak.</b> A two-pole
        /// resonator's gain runs as 1/(1−r), so at equal Q a low narrow one is many times louder than a
        /// high broad one; without this the gains below would be labels rather than a balance, and one
        /// resonance would be 84% of the sound whatever the numbers said. It costs a buffer per lane at
        /// build time, which is not a budget anyone is counting.</para>
        ///
        /// <para>The first 0.6 s of excitation is thrown away — it fills the resonators, so what is kept
        /// is the steady state and therefore periodic. That is longer than it was, because the
        /// resonances now ring for tens of milliseconds instead of four.</para>
        /// </summary>
        private static void AddPipe(
            float[] samples, float[] cell, float firingHz, VehicleConfig config, Voice voice,
            float detune)
        {
            float pitch = (config != null ? config.ExhaustPitchHz : 92f) * detune;
            float clatter = (config != null ? config.ExhaustClatterHz : 600f) * detune;
            float ring = config != null ? config.ExhaustRing : 0.6f;
            float rasp = config != null ? config.ExhaustRasp : 0.2f;
            float boom = config != null ? config.ExhaustBoom : 0.8f;

            // Q in cycles of each resonance's own frequency. See the note on VehicleConfig.ExhaustRing
            // for what happens when this is expressed in anything else.
            float q = Mathf.Lerp(2.5f, 10f, ring);

            var lanes = new[]
            {
                new Lane(firingHz * detune, q * 1.2f, boom),
                new Lane(pitch, q, Mathf.Lerp(1.00f, 0.72f, rasp)),
                new Lane(pitch * 2.5f, q * 0.35f, Mathf.Lerp(0.48f, 0.70f, rasp) * 1.5f),
                new Lane(clatter, q * 0.18f, Mathf.Lerp(0.20f, 0.50f, rasp) * 1.5f),
            };

            if (voice == Voice.Exhaust)
            {
                // The pipe layer is the bottom end and nothing else.
                lanes[0].Gain *= 1.15f;
                lanes[2].Gain *= 0.5f;
                lanes[3].Gain = 0f;
            }

            int period = cell.Length;
            int warmUp = period * Mathf.Max(1, Mathf.CeilToInt(SampleRate * 0.6f / period));

            var buffers = new float[lanes.Length][];
            for (int l = 0; l < lanes.Length; l++)
            {
                buffers[l] = lanes[l].Gain > 0f ? new float[samples.Length] : null;
            }

            for (int i = 0; i < warmUp + samples.Length; i++)
            {
                float x = cell[i % period];

                for (int l = 0; l < lanes.Length; l++)
                {
                    float y = lanes[l].Step(x);

                    if (i >= warmUp && buffers[l] != null)
                    {
                        buffers[l][i - warmUp] = y;
                    }
                }
            }

            for (int l = 0; l < lanes.Length; l++)
            {
                if (buffers[l] == null)
                {
                    continue;
                }

                float scale = lanes[l].Gain / Mathf.Max(1e-9f, RootMeanSquare(buffers[l]));

                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] += buffers[l][i] * scale;
                }
            }
        }

        /// <summary>One resonance and how loudly it should end up in the mix.</summary>
        private struct Lane
        {
            public float Gain;

            private Resonator filter;

            public Lane(float hz, float q, float gain)
            {
                Gain = gain;

                // Damping straight out of the definition of Q: an amplitude falling to 1/e after q
                // cycles means 1 - r = frequency / (q * sample rate).
                float clamped = Mathf.Clamp(hz, 18f, SampleRate * 0.45f);
                float damping = clamped / Mathf.Max(0.5f, q) / SampleRate;

                filter = Resonator.At(clamped, 1f - damping);
            }

            public float Step(float x) => filter.Step(x);
        }

        private static float RootMeanSquare(float[] samples)
        {
            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * (double)samples[i];
            }

            return Mathf.Sqrt((float)(sum / samples.Length));
        }

        /// <summary>A two-pole resonator. The same one <see cref="BuildSquealClip"/> writes out inline.</summary>
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
