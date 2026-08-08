namespace Horizon.Input
{
    /// <summary>The control schemes the player can pick between.</summary>
    public enum DriveInputScheme
    {
        /// <summary>Keyboard / gamepad. Development default, auto-selected in the Editor.</summary>
        KeyboardGamepad = 0,

        /// <summary>Tilt the device to steer, throttle applies itself. The relaxed scheme.</summary>
        Tilt = 1,

        /// <summary>Drag to steer on one half of the screen, pedals on the other.</summary>
        TouchSteer = 2,

        /// <summary>Discrete on-screen buttons for left / right / throttle / brake.</summary>
        TouchButtons = 3,
    }
}
