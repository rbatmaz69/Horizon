namespace Horizon.Input
{
    /// <summary>
    /// What the on-screen controls are currently doing, published for the input sources to read.
    ///
    /// <para>The widgets are <c>MonoBehaviour</c>s living on a canvas; the input sources are plain
    /// classes owned by <see cref="DriveInputRouter"/>. Something has to carry values across that gap,
    /// and this is it — the same published-ambient-value shape as <see cref="DriveInput.Current"/>,
    /// which this project already uses to let <c>Horizon.Vehicle</c> read input without depending on
    /// the router. Reusing the pattern rather than inventing a second one.</para>
    ///
    /// <para>Deliberately dumb: no events, no logic, no allocation. A widget writes on the frame a
    /// finger moves it, a source reads on the frame it samples, and neither knows the other exists.
    /// That is also what makes the whole thing testable from the Editor with a mouse.</para>
    /// </summary>
    public static class TouchControlState
    {
        /// <summary>Steering from the wheel or the arrow buttons, -1 to 1.</summary>
        public static float Steer { get; set; }

        /// <summary>Throttle from the pedal or the slider, 0 to 1.</summary>
        public static float Throttle { get; set; }

        /// <summary>Brake from the pedal or the slider, 0 to 1.</summary>
        public static float Brake { get; set; }

        /// <summary>Whether the handbrake button is held.</summary>
        public static bool Handbrake { get; set; }

        /// <summary>
        /// Clears everything.
        ///
        /// Called when the controls are rebuilt or hidden, because a widget that is switched off while
        /// a finger is on it never gets its release — and a throttle stuck at 1 because the player
        /// opened the pause menu mid-corner is the kind of bug that only shows up on a device.
        /// </summary>
        public static void Clear()
        {
            Steer = 0f;
            Throttle = 0f;
            Brake = 0f;
            Handbrake = false;
        }
    }
}
