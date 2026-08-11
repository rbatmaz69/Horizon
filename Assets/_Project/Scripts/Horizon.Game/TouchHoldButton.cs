using Horizon.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Horizon.Game
{
    /// <summary>
    /// A hold-to-act button that writes into <see cref="TouchControlState"/> for as long as a finger
    /// is on it.
    ///
    /// <para><b>Not a <c>UnityEngine.UI.Button</c>.</b> A Button raises one event on release, which is
    /// right for "open the menu" and useless for "accelerate" — you cannot hold it. These implement the
    /// pointer down/up handlers directly, which is also what lets two of them be held at once: uGUI
    /// tracks a pointer per finger, so steering and throttle arrive on separate pointers and neither
    /// cancels the other. That is the whole reason the controls are uGUI rather than the old invisible
    /// rectangles.</para>
    ///
    /// <para><c>OnDisable</c> releases, because a widget switched off under a finger never receives its
    /// pointer-up — and a throttle stuck on because the player opened the pause menu mid-corner is
    /// exactly the sort of thing that only appears on a device.</para>
    /// </summary>
    public sealed class TouchHoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>What this button feeds while held.</summary>
        public enum Action
        {
            SteerLeft,
            SteerRight,
            Throttle,
            Brake,
            Handbrake,
        }

        [SerializeField] private Action action;

        [Tooltip("Optional graphic tinted while held, so a finger gets some acknowledgement.")]
        [SerializeField] private UnityEngine.UI.Image highlight;

        [SerializeField] private Color restingColour = new Color(1f, 1f, 1f, 0.30f);
        [SerializeField] private Color heldColour = new Color(1f, 1f, 1f, 0.62f);

        private bool held;

        public void OnPointerDown(PointerEventData eventData)
        {
            Set(true);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Set(false);
        }

        private void OnEnable()
        {
            Set(false);
        }

        private void OnDisable()
        {
            Set(false);
        }

        private void Set(bool value)
        {
            held = value;

            switch (action)
            {
                case Action.SteerLeft:
                    TouchControlState.Steer = value ? -1f : 0f;
                    break;
                case Action.SteerRight:
                    TouchControlState.Steer = value ? 1f : 0f;
                    break;
                case Action.Throttle:
                    TouchControlState.Throttle = value ? 1f : 0f;
                    break;
                case Action.Brake:
                    TouchControlState.Brake = value ? 1f : 0f;
                    break;
                case Action.Handbrake:
                    TouchControlState.Handbrake = value;
                    break;
            }

            if (highlight != null)
            {
                highlight.color = held ? heldColour : restingColour;
            }
        }
    }
}
