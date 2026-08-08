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
