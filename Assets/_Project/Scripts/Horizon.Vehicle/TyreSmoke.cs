using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Smoke off the tyres when they slide — drifting, wheelspin off the line, a locked wheel under the
    /// handbrake.
    ///
    /// <para><b>One emitter for four wheels.</b> The obvious build is a particle system parented to each
    /// corner, and it is wrong twice over: four systems are four draw calls on a mobile GPU, and a
    /// system parented to the car has to be dragged to the contact patch every frame anyway, because
    /// the patch is on the road and the wheel is not. Emitting manually into a single world-space
    /// system costs one call and puts every puff exactly where the rubber is.</para>
    ///
    /// <para><b>Why it reads the physics rather than the drift flag.</b> <c>IsDrifting</c> is a
    /// threshold on the whole car, and smoke is a property of one tyre: the inside rear lights up on
    /// corner exit while the other three are still gripping. Per-wheel slip is also what makes
    /// wheelspin and lock-up come out for free — the friction circle refuses force the same way
    /// whichever direction it was asked in, so there is no separate case to write.</para>
    ///
    /// <para>Modelled on <see cref="ExhaustSmoke"/>: the particle system's own look is configured by
    /// the setup tool, and this only decides when and where.</para>
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class TyreSmoke : MonoBehaviour
    {
        [SerializeField] private VehicleController vehicle;

        [Tooltip("Slip speed in m/s below which nothing comes out at all.\n\n"
               + "Not zero, and this is the number that decides whether the effect looks earned. Every "
               + "tyre carries a little slip all the time — that is how a tyre makes force — so a "
               + "threshold at zero puts a permanent haze under a car doing nothing wrong.")]
        [SerializeField] private float slipThreshold = 1.8f;

        [Tooltip("Slip speed in m/s at which the tyre is fully alight and the rate stops climbing.")]
        [SerializeField] private float slipFullSmoke = 7f;

        [Tooltip("Particles per second from one fully sliding tyre.")]
        [SerializeField] private float maxRatePerWheel = 35f;

        [Tooltip("Particle size at the smoke threshold, metres.")]
        [SerializeField] private float minSize = 0.35f;

        [Tooltip("...and once the tyre is fully alight. Big, because tyre smoke billows rather than "
               + "puffs — this is the difference between smoke and dust.")]
        [SerializeField] private float maxSize = 1.5f;

        [Tooltip("How fast a puff is thrown backwards out of the contact patch, m/s at full slip.")]
        [SerializeField] private float ejectSpeed = 2.5f;

        [Tooltip("Ceiling on particles emitted per wheel in a single frame. Stops a long frame — a "
               + "streaming hitch, say — from spending the whole particle budget in one burst.")]
        [SerializeField] private int maxParticlesPerFramePerWheel = 6;

        private ParticleSystem smoke;

        /// <summary>
        /// Fractional particles owed per wheel, carried between frames.
        ///
        /// <para>A rate below one particle per frame truncates to nothing without this, so light slip —
        /// the first chirp of a tyre, which is the moment worth seeing — would emit nothing at all
        /// while heavy slip worked fine.</para>
        /// </summary>
        private readonly float[] pending = new float[4];

        private void Awake()
        {
            smoke = GetComponent<ParticleSystem>();

            if (vehicle == null)
            {
                vehicle = GetComponentInParent<VehicleController>();
            }
        }

        private void OnDisable()
        {
            for (int i = 0; i < pending.Length; i++)
            {
                pending[i] = 0f;
            }
        }

        private void LateUpdate()
        {
            if (vehicle == null || smoke == null)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            int wheelCount = Mathf.Min(pending.Length, VehicleController.WheelSlipCount);

            for (int i = 0; i < wheelCount; i++)
            {
                if (!vehicle.TryGetWheelSlip(i, out Vector3 contact, out float slip)
                    || slip <= slipThreshold)
                {
                    // Reset rather than hold: a wheel that stopped sliding should not fire a puff it
                    // banked up several seconds ago the next time it steps out.
                    pending[i] = 0f;
                    continue;
                }

                float amount = Mathf.Clamp01(
                    (slip - slipThreshold) / Mathf.Max(0.01f, slipFullSmoke - slipThreshold));

                pending[i] += maxRatePerWheel * amount * deltaTime;

                int count = Mathf.Min((int)pending[i], maxParticlesPerFramePerWheel);
                if (count <= 0)
                {
                    continue;
                }

                pending[i] -= count;
                EmitAt(contact, amount, count);
            }
        }

        /// <summary>Puts <paramref name="count"/> puffs at one contact patch.</summary>
        private void EmitAt(Vector3 contact, float amount, int count)
        {
            // Lifted clear of the surface by half its own size, or the billboard is half-buried in the
            // road and reads as a stain rather than a cloud.
            float size = Mathf.Lerp(minSize, maxSize, amount);

            var parameters = new ParticleSystem.EmitParams
            {
                position = contact + Vector3.up * (size * 0.35f),
                startSize = size,
                applyShapeToPosition = false,
            };

            // Thrown up and back off the patch, with the spread coming from the system's own start
            // speed. Straight up would read as steam from a grating.
            Vector3 eject = Vector3.up * (ejectSpeed * amount);
            parameters.velocity = eject;

            smoke.Emit(parameters, count);
        }
    }
}
