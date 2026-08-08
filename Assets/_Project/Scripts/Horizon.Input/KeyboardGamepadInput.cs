using UnityEngine;
using UnityEngine.InputSystem;

namespace Horizon.Input
{
    /// <summary>
    /// Development scheme: WASD / arrows / space, or a gamepad's left stick and triggers.
    /// Auto-selected in the Editor so the vehicle is tunable before any touch UI exists.
    /// </summary>
    public sealed class KeyboardGamepadInput : DriveInputSource
    {
        public override string DisplayName => "Keyboard / Gamepad";

        public override void Sample(float deltaTime)
        {
            Reset();

            float steer = 0f;
            float throttle = 0f;
            float brake = 0f;
            bool handbrake = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                {
                    steer -= 1f;
                }

                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                {
                    steer += 1f;
                }

                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                {
                    throttle = 1f;
                }

                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                {
                    brake = 1f;
                }

                handbrake = keyboard.spaceKey.isPressed;
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                float stick = gamepad.leftStick.x.ReadValue();
                if (Mathf.Abs(stick) > Mathf.Abs(steer))
                {
                    steer = stick;
                }

                throttle = Mathf.Max(throttle, gamepad.rightTrigger.ReadValue());
                brake = Mathf.Max(brake, gamepad.leftTrigger.ReadValue());
                handbrake |= gamepad.buttonSouth.isPressed;
            }

            Steer = Mathf.Clamp(steer, -1f, 1f);
            Throttle = Mathf.Clamp01(throttle);
            Brake = Mathf.Clamp01(brake);
            Handbrake = handbrake;
        }
    }
}
