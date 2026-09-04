using Horizon.Core;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Tells the camera when the car has come back down.
    ///
    /// <para><b>Nothing in this project read <see cref="VehicleController.GroundedWheelCount"/>.</b> It
    /// has been published since the wheel model was written and every consumer of the car ignored it —
    /// while the Stadtfeld leg was deliberately designed around crests ("a corner exit is regularly over
    /// a rise"), the pass has its own, and the Weissjoch's stack is nothing but them. The car goes light
    /// over all of them and the game says nothing.</para>
    ///
    /// <para><b>A landing is not an impact and that is why it was silent.</b> The wheels are raycasts, so
    /// a clean landing never touches the body collider and <c>OnCollisionEnter</c> never fires — the
    /// existing thud only catches the case where the car bottoms out hard enough to put its floor on the
    /// road. Everything short of that arrives with no cue at all.</para>
    ///
    /// <para>Pushed from here rather than noticed by the camera, which is <see cref="ImpactEffects"/>'s
    /// shape and its argument: <c>Horizon.Core</c> has no references so <c>ChaseCamera</c> cannot ask a
    /// car whether its wheels are down, and <c>Horizon.Vehicle</c> may not know a camera exists. It
    /// reuses <see cref="ChaseCamera.Shake"/> rather than adding a second kind of kick, because a
    /// landing and a knock are the same thing happening to the rig and the strongest-wins rule that
    /// class already applies is the right one for both.</para>
    /// </summary>
    public sealed class DriveFeel : MonoBehaviour
    {
        [Tooltip("The rig to kick. Found at run time if left empty.")]
        [SerializeField] private ChaseCamera chaseCamera;

        [Tooltip("The car to watch. Found at run time if left empty.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("Seconds off the ground below which a touchdown is not a landing.\n\n"
               + "There has to be one, and it is most of what makes this read as earned. A wheel lifts "
               + "for a few hundredths of a second on every hairpin exit and over every join in the "
               + "road; kicking the camera for those would be a camera with a twitch rather than a car "
               + "coming down.")]
        [SerializeField] private float minimumAirtime = 0.22f;

        [Tooltip("Airtime at which the landing kick is at full strength.")]
        [SerializeField] private float fullAirtime = 0.9f;

        [Tooltip("Scales every landing. Zero leaves the camera alone, which is the setting to reach for "
               + "if a crest starts reading as a crash.")]
        [Range(0f, 1f)]
        [SerializeField] private float landingShake = 0.55f;

        /// <summary>
        /// How many wheels may still be down and the car still count as flying.
        ///
        /// <para>One rather than zero. A car leaving a crest lifts its nose first and trails a rear wheel
        /// for a good fraction of the jump, so requiring all four in the air misses the front half of
        /// every crest in the world — which is the half the driver is looking at.</para>
        /// </summary>
        private const int AirborneWheelCount = 1;

        /// <summary>How often the car is looked for while it is missing. See <see cref="ImpactEffects"/>.</summary>
        private const float SearchInterval = 0.5f;

        private float airtime;
        private float nextSearch;

        /// <summary>Seconds the car has been off the ground, or zero. Read by nothing yet; see the class remarks.</summary>
        public float Airtime => airtime;

        private void Update()
        {
            if (vehicle == null || chaseCamera == null)
            {
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

                if (vehicle == null || chaseCamera == null)
                {
                    return;
                }
            }

            if (vehicle.GroundedWheelCount <= AirborneWheelCount)
            {
                airtime += Time.deltaTime;
                return;
            }

            if (airtime >= minimumAirtime)
            {
                float severity = Mathf.InverseLerp(minimumAirtime, fullAirtime, airtime);
                chaseCamera.Shake(severity * landingShake);
            }

            airtime = 0f;
        }
    }
}
