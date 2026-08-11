using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Drives a tailpipe smoke emitter from throttle and speed.
    ///
    /// Puffs harder on throttle, thins out at speed — at a standstill the smoke lingers behind the
    /// car, which is most of what makes it read as exhaust rather than fog. The particle system itself
    /// is configured by the setup tool; this only modulates it.
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

        [Tooltip("Particles per second while idling.")]
        [SerializeField] private float idleRate = 7f;

        [Tooltip("Particles per second at full throttle.")]
        [SerializeField] private float throttleRate = 30f;

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
            float throttle = Mathf.Clamp01(DriveInput.Current.Throttle);
            float rate = Mathf.Lerp(idleRate, throttleRate, throttle);

            if (vehicle != null)
            {
                float speedFade = 1f - Mathf.Clamp01(vehicle.SpeedNormalized / Mathf.Max(0.01f, fadeOutSpeed));

                // Never quite off: a cold idle plume is part of the look.
                rate *= Mathf.Lerp(0.25f, 1f, speedFade);
            }

            emission.rateOverTime = rate;
        }
    }
}
