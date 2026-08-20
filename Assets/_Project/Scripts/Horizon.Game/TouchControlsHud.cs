using Horizon.Input;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Shows the on-screen controls that belong to the chosen steering and pedal methods, and hides
    /// the rest.
    ///
    /// <para>The widgets are all built once by the setup tool and switched on and off here, rather than
    /// created and destroyed as the player changes their mind. Building them is the expensive part and
    /// changing scheme should be instant; it also means a widget's <c>OnDisable</c> runs, which is what
    /// releases anything a finger was holding.</para>
    ///
    /// <para>Everything hides while the game is paused. A pause menu with a live throttle pedal
    /// underneath it is a car that drives away while you are reading the settings.</para>
    /// </summary>
    public sealed class TouchControlsHud : MonoBehaviour
    {
        [SerializeField] private DriveInputRouter router;

        [Header("Steering")]
        [SerializeField] private GameObject wheel;
        [SerializeField] private GameObject arrows;

        [Header("Pedals")]
        [SerializeField] private GameObject pedals;
        [SerializeField] private GameObject slider;

        [Tooltip("The brake and handbrake shown when the throttle looks after itself.")]
        [SerializeField] private GameObject autoPedals;

        [Header("Always on")]
        [Tooltip("Hidden with everything else while paused, so it cannot be leaned on through the menu.")]
        [SerializeField] private GameObject handbrake;

        [Tooltip("The rev counter. Shown in every control scheme — it is a readout, not a control.")]
        [SerializeField] private GameObject instruments;

        private bool paused;

        private void Awake()
        {
            if (router == null)
            {
                router = FindFirstObjectByType<DriveInputRouter>();
            }
        }

        private void OnEnable()
        {
            if (router != null)
            {
                router.SchemeChanged += Refresh;
            }

            Refresh();
        }

        private void OnDisable()
        {
            if (router != null)
            {
                router.SchemeChanged -= Refresh;
            }
        }

        /// <summary>Hides every control, for the pause menu.</summary>
        public void SetPaused(bool value)
        {
            paused = value;
            Refresh();
        }

        private void Refresh()
        {
            if (router == null)
            {
                return;
            }

            bool driving = !paused;

            Show(wheel, driving && router.Steering == SteeringMethod.Wheel);
            Show(arrows, driving && router.Steering == SteeringMethod.Arrows);

            Show(pedals, driving && router.Pedals == PedalMethod.Pedals);
            Show(slider, driving && router.Pedals == PedalMethod.Slider);
            Show(autoPedals, driving && router.Pedals == PedalMethod.Auto);

            // No handbrake on the keyboard layout: there is a space bar, and a button hanging in the
            // corner of a desktop screen with nothing to press it is just clutter.
            Show(handbrake, driving && router.Pedals != PedalMethod.KeyboardGamepad);

            // No scheme test: the dial says nothing about how you steer. It still goes away with the
            // rest of the HUD while paused, which is also what keeps it off the start screen.
            Show(instruments, driving);
        }

        private static void Show(GameObject target, bool visible)
        {
            if (target != null && target.activeSelf != visible)
            {
                target.SetActive(visible);
            }
        }
    }
}
