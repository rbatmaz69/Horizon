using UnityEngine;
using UnityEngine.InputSystem;

namespace Horizon.Input
{
    /// <summary>How the player steers.</summary>
    public enum SteeringMethod
    {
        /// <summary>Keyboard or gamepad. The development default and the Editor's.</summary>
        KeyboardGamepad = 0,

        /// <summary>Tilt the device.</summary>
        Tilt = 1,

        /// <summary>A wheel on screen, dragged with a thumb.</summary>
        Wheel = 2,

        /// <summary>Two hold-to-steer buttons.</summary>
        Arrows = 3,
    }

    /// <summary>How the player controls throttle and brake.</summary>
    public enum PedalMethod
    {
        /// <summary>Keyboard or gamepad.</summary>
        KeyboardGamepad = 0,

        /// <summary>
        /// The throttle applies itself; the player only steers and brakes.
        ///
        /// The original tilt scheme's behaviour, kept because it is genuinely the most relaxed way to
        /// play and matches what this game is for.
        /// </summary>
        Auto = 1,

        /// <summary>Hold-to-accelerate and hold-to-brake buttons.</summary>
        Pedals = 2,

        /// <summary>A vertical slider: up from rest is throttle, down is brake.</summary>
        Slider = 3,
    }

    /// <summary>
    /// The steering half of a control scheme.
    ///
    /// <para>Splitting the old monolithic schemes in two is what makes "the wheel with a slider"
    /// something a player can ask for. Four schemes offered four combinations out of the twelve that
    /// actually exist, and the missing ones were missing for no reason other than that nobody had
    /// written that particular class.</para>
    /// </summary>
    public interface ISteerInput
    {
        float Steer { get; }

        string DisplayName { get; }

        void Enable();

        void Disable();

        void Sample(float deltaTime);
    }

    /// <summary>The throttle-and-brake half of a control scheme.</summary>
    public interface IPedalInput
    {
        float Throttle { get; }

        float Brake { get; }

        bool Handbrake { get; }

        string DisplayName { get; }

        void Enable();

        void Disable();

        void Sample(float deltaTime);
    }

    /// <summary>Steering from the keyboard's A/D or a gamepad's left stick.</summary>
    public sealed class KeyboardSteer : ISteerInput
    {
        public float Steer { get; private set; }

        public string DisplayName => "Keyboard / gamepad";

        public void Enable() { }

        public void Disable() { }

        public void Sample(float deltaTime)
        {
            float steer = 0f;

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
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                float stick = gamepad.leftStick.x.ReadValue();
                if (Mathf.Abs(stick) > Mathf.Abs(steer))
                {
                    steer = stick;
                }
            }

            Steer = Mathf.Clamp(steer, -1f, 1f);
        }
    }

    /// <summary>Throttle, brake and handbrake from W/S/space or a gamepad's triggers.</summary>
    public sealed class KeyboardPedals : IPedalInput
    {
        public float Throttle { get; private set; }

        public float Brake { get; private set; }

        public bool Handbrake { get; private set; }

        public string DisplayName => "Keyboard / gamepad";

        public void Enable() { }

        public void Disable() { }

        public void Sample(float deltaTime)
        {
            float throttle = 0f;
            float brake = 0f;
            bool handbrake = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
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
                throttle = Mathf.Max(throttle, gamepad.rightTrigger.ReadValue());
                brake = Mathf.Max(brake, gamepad.leftTrigger.ReadValue());
                handbrake |= gamepad.buttonSouth.isPressed;
            }

            Throttle = Mathf.Clamp01(throttle);
            Brake = Mathf.Clamp01(brake);
            Handbrake = handbrake;
        }
    }

    /// <summary>
    /// Steering from the on-screen wheel or the arrow buttons.
    ///
    /// Both read the same place, because from here they are the same thing: a number a widget put in
    /// <see cref="TouchControlState"/>. What differs is which widget the HUD shows, which is a
    /// question about pixels rather than about input.
    /// </summary>
    public sealed class TouchSteer : ISteerInput
    {
        /// <summary>
        /// Seconds of holding an arrow to reach full lock.
        ///
        /// <para><b>Buttons are the reason this exists.</b> A button is on or off, so without a ramp the
        /// steering is a switch: every press is instant full lock and every release is instant centre,
        /// and there is no way to ask for the third of a turn a corner actually wants. That is what made
        /// it impossible to get round anything — the car was either going straight or scrubbing its
        /// front tyres, never in between.</para>
        ///
        /// <para>A third of a second is long enough to be steerable and short enough not to feel like
        /// the wheel is stuck in treacle. The wheel does not use this: a thumb on a wheel is already
        /// giving a continuous angle, and ramping it would fight the player's own hand.</para>
        ///
        /// <para>The exact figure now comes from the sensitivity setting, since "how long to reach full
        /// lock" is what that question means for a button.</para>
        /// </summary>
        private const float SlowestRampSeconds = 0.55f;

        /// <summary>Ramp at full sensitivity. See <see cref="SlowestRampSeconds"/>.</summary>
        private const float FastestRampSeconds = 0.14f;

        /// <summary>Release is quicker than application: a car should straighten more readily than it turns.</summary>
        private const float ReturnRatio = 0.56f;

        private readonly bool wheel;

        public TouchSteer(bool wheel)
        {
            this.wheel = wheel;
        }

        public float Steer { get; private set; }

        public string DisplayName => wheel ? "Steering wheel" : "Left / right buttons";

        public void Enable()
        {
            Clear();
        }

        public void Disable()
        {
            Clear();
        }

        private void Clear()
        {
            TouchControlState.Steer = 0f;
            TouchControlState.SteerLeftHeld = false;
            TouchControlState.SteerRightHeld = false;
            Steer = 0f;
        }

        public void Sample(float deltaTime)
        {
            if (wheel)
            {
                Steer = Mathf.Clamp(TouchControlState.Steer, -1f, 1f);
                return;
            }

            // From the two held flags rather than a shared value, so releasing one arrow while the other
            // is still down leaves the steering where that other arrow is asking for it.
            float target = (TouchControlState.SteerRightHeld ? 1f : 0f)
                         - (TouchControlState.SteerLeftHeld ? 1f : 0f);

            float ramp = Mathf.Lerp(
                SlowestRampSeconds, FastestRampSeconds, Mathf.Clamp01(TouchControlState.SteerSensitivity01));

            // Toward zero is a release; away from it is a press.
            bool returning = Mathf.Abs(target) < Mathf.Abs(Steer) || target * Steer < 0f;
            float seconds = returning ? ramp * ReturnRatio : ramp;

            Steer = Mathf.MoveTowards(Steer, target, deltaTime / Mathf.Max(0.01f, seconds));
        }
    }

    /// <summary>Throttle and brake from the on-screen pedals or slider.</summary>
    public sealed class TouchPedals : IPedalInput
    {
        private readonly bool slider;

        public TouchPedals(bool slider)
        {
            this.slider = slider;
        }

        public float Throttle { get; private set; }

        public float Brake { get; private set; }

        public bool Handbrake { get; private set; }

        public string DisplayName => slider ? "Throttle slider" : "Pedals";

        public void Enable()
        {
            TouchControlState.Throttle = 0f;
            TouchControlState.Brake = 0f;
            TouchControlState.Handbrake = false;
        }

        public void Disable()
        {
            Enable();
        }

        public void Sample(float deltaTime)
        {
            Throttle = Mathf.Clamp01(TouchControlState.Throttle);
            Brake = Mathf.Clamp01(TouchControlState.Brake);
            Handbrake = TouchControlState.Handbrake;
        }
    }

    /// <summary>
    /// The throttle looks after itself; the player brakes and holds the handbrake.
    ///
    /// The original tilt scheme's behaviour, lifted out so it can be paired with any steering method
    /// rather than only with tilt — driving one-handed with the wheel is a perfectly reasonable thing
    /// to want, and there was no reason beyond the old class layout that it could not be had.
    /// </summary>
    public sealed class AutoPedals : IPedalInput
    {
        public float Throttle { get; private set; }

        public float Brake { get; private set; }

        public bool Handbrake { get; private set; }

        public string DisplayName => "Automatic throttle";

        public void Enable()
        {
            TouchControlState.Brake = 0f;
            TouchControlState.Handbrake = false;
        }

        public void Disable()
        {
            Enable();
        }

        public void Sample(float deltaTime)
        {
            Brake = Mathf.Clamp01(TouchControlState.Brake);
            Handbrake = TouchControlState.Handbrake;

            // Off the throttle whenever the player asks for either kind of brake, or the car fights
            // itself and the handbrake never breaks traction.
            Throttle = Brake > 0f || Handbrake ? 0f : 1f;
        }
    }
}
