using Horizon.Core;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Kicks the chase camera when the car hits something.
    ///
    /// <para><b>A component of its own, for the reason <see cref="SpeedAtmosphere"/> gives.</b>
    /// <c>Horizon.Core</c> has no references by design, so <c>ChaseCamera</c> cannot ask a car whether
    /// it has just crashed; <c>Horizon.Vehicle</c> is not allowed to know a camera exists. This is the
    /// leaf assembly and therefore the one place both are in scope, so the severity is <i>pushed</i> —
    /// exactly the shape of <c>DriveInput.Current</c> and of the speed haze.</para>
    ///
    /// <para><b>Why not simply let the camera notice.</b> A camera watching the car's velocity for a
    /// step change would fire on every respawn, every placement from the start screen and every hard
    /// landing, and would miss the case that matters most — a glancing blow that barely changes speed
    /// but is unmistakable from inside the car. The vehicle already knows, having been told by PhysX,
    /// and one measurement with two consumers is the rule this project keeps returning to: the needle
    /// and the whistle are the same number.</para>
    ///
    /// <para><b>The camera is wired at build time and the car deliberately is not</b>, which is the
    /// split <c>SpeedAtmosphere</c> already makes for the same two kinds of thing. The rig is built into
    /// the world scene beside this component and never replaced, so an explicit reference is one less
    /// thing to be surprised by; the car the player drives is placed by <see cref="GameBootstrap"/> and
    /// its shell is swapped by the garage, so holding a reference to whichever body existed at build
    /// time is the wrong shape. Both fall back to a run-time search, and the search costs nothing once
    /// it has found what it wants.</para>
    /// </summary>
    public sealed class ImpactEffects : MonoBehaviour
    {
        [Tooltip("The rig to kick. Found at run time if left empty.")]
        [SerializeField] private ChaseCamera chaseCamera;

        [Tooltip("The car to listen to. Found at run time if left empty.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("Scales every impact's kick. Zero leaves the camera alone entirely, which is the "
               + "setting to reach for if the shake ever reads as a bug rather than as a crash.")]
        [Range(0f, 1f)]
        [SerializeField] private float shakeAmount = 1f;

        /// <summary>
        /// How often the vehicle is looked for while it is missing, seconds.
        ///
        /// <para>Polled rather than resolved once in <c>Start</c>: this component lives in the world
        /// scene and the car is spawned into it by <see cref="GameBootstrap"/> after the additive load,
        /// so a single lookup in <c>Start</c> is a lookup that runs before there is anything to find.
        /// Once it has both, <c>Update</c> returns on its first line.</para>
        /// </summary>
        private const float SearchInterval = 0.5f;

        private VehicleController subscribed;
        private float nextSearch;

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (subscribed != null && vehicle == subscribed)
            {
                return;
            }

            if (Time.unscaledTime < nextSearch)
            {
                return;
            }

            nextSearch = Time.unscaledTime + SearchInterval;

            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            if (chaseCamera == null)
            {
                chaseCamera = FindFirstObjectByType<ChaseCamera>();
            }

            if (vehicle != subscribed)
            {
                Unsubscribe();

                subscribed = vehicle;
                if (subscribed != null)
                {
                    subscribed.Impacted += OnImpacted;
                }
            }
        }

        private void Unsubscribe()
        {
            if (subscribed != null)
            {
                subscribed.Impacted -= OnImpacted;
                subscribed = null;
            }
        }

        private void OnImpacted(float severity, Vector3 point)
        {
            if (chaseCamera != null)
            {
                chaseCamera.Shake(severity * shakeAmount);
            }
        }
    }
}
