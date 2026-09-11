using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// The tailpipe: fires the exhaust flame on a hard shift and on overrun, and drives the smoke plume
    /// from throttle and speed — which is switched off.
    ///
    /// <para><b>There is no plume, and there was one.</b> Seven small puffs a second at every pipe, even
    /// parked, fading in and out in a fifth of a second and drifting up into the tail lamps. Driven, it
    /// read as something fizzing on the back of the car rather than as exhaust — the billboards cut into
    /// the bumper as they rose and flickered at the cut — and it was only there on the settings that
    /// draw exhaust particles, which is why it looked like the High setting was broken. None of the cars
    /// in the garage visibly smokes. The rates are zero rather than the emitter removed, so it is one
    /// number to bring back; the flame is untouched.</para>
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class ExhaustSmoke : MonoBehaviour
    {
        [SerializeField] private VehicleController vehicle;

        [Tooltip("Flame emitter at the same tailpipe, fired in bursts rather than run at a rate. Left "
               + "empty there is simply no flame.")]
        [SerializeField] private ParticleSystem flame;

        [Tooltip("The audio that decides when the exhaust lights. Found automatically if left empty.\n\n"
               + "Driven by the sound rather than by the vehicle directly, and deliberately: a flame "
               + "without its bang is a silent film, so the one gate that decides whether this is a hard "
               + "shift lives in one place and the picture follows the noise.")]
        [SerializeField] private EngineAudio engineAudio;

        [Tooltip("Particles in a shift-bang burst.")]
        [SerializeField] private int bangParticles = 14;

        [Tooltip("...and in an overrun crackle, which is a spit rather than a gout.")]
        [SerializeField] private int crackleParticles = 5;

        [Tooltip("Particles per second while idling. Zero: see the class remarks.")]
        [SerializeField] private float idleRate = 0f;

        [Tooltip("Particles per second at full throttle. Zero: see the class remarks.")]
        [SerializeField] private float throttleRate = 0f;

        [Tooltip("Emission fades out above this fraction of top speed — at speed the plume would "
               + "just be a smear behind the car.")]
        [SerializeField] private float fadeOutSpeed = 0.55f;

        private ParticleSystem exhaust;
        private ParticleSystem.EmissionModule emission;

        private void Awake()
        {
            exhaust = GetComponent<ParticleSystem>();
            emission = exhaust.emission;

            if (vehicle == null)
            {
                vehicle = GetComponentInParent<VehicleController>();
            }

            if (engineAudio == null)
            {
                engineAudio = GetComponentInParent<EngineAudio>();
            }
        }

        private void OnEnable()
        {
            if (engineAudio == null)
            {
                return;
            }

            engineAudio.Banged += OnBanged;
            engineAudio.Crackled += OnCrackled;
        }

        private void OnDisable()
        {
            if (engineAudio == null)
            {
                return;
            }

            engineAudio.Banged -= OnBanged;
            engineAudio.Crackled -= OnCrackled;
        }

        private void OnBanged()
        {
            Emit(bangParticles);
        }

        private void OnCrackled()
        {
            Emit(crackleParticles);
        }

        private void Emit(int count)
        {
            if (flame != null)
            {
                flame.Emit(count);
            }
        }

        private void Update()
        {
            // A stopped engine has no exhaust, and this is the one layer that would not work that out
            // for itself: the plume is driven off the pedal, and the pedal is still being pressed by a
            // driver wondering why the car will not move. The comment below about never quite switching
            // off is about an idling engine, and a dry one is not idling.
            if (vehicle != null && vehicle.IsOutOfFuel)
            {
                emission.rateOverTime = 0f;
                return;
            }

            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);
            float rate = Mathf.Lerp(idleRate, throttleRate, throttle);

            if (vehicle != null)
            {
                float speedFade = 1f - Mathf.Clamp01(vehicle.SpeedNormalized / Mathf.Max(0.01f, fadeOutSpeed));

                rate *= Mathf.Lerp(0.25f, 1f, speedFade);
            }

            emission.rateOverTime = rate;
        }
    }
}
