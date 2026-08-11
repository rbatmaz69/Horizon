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
        /// <summary>Steering from the wheel, -1 to 1. The arrow buttons use the two flags below.</summary>
        public static float Steer { get; set; }

        /// <summary>
        /// Whether the left arrow button is held. Separate from <see cref="SteerRightHeld"/>, and that
        /// is not tidiness.
        ///
        /// <para>Both buttons used to write <see cref="Steer"/> directly, including a zero on release —
        /// so holding left, pressing right, then letting go of left dropped the steering to centre while
        /// a finger was still on the right arrow. Two sources sharing one field cannot express "one of
        /// us has stopped", only "the last of us to speak has stopped". Throttle and brake never had the
        /// bug because they were always two fields.</para>
        /// </summary>
        public static bool SteerLeftHeld { get; set; }

        /// <summary>Whether the right arrow button is held. See <see cref="SteerLeftHeld"/>.</summary>
        public static bool SteerRightHeld { get; set; }

        /// <summary>
        /// How sharp the player wants the steering, 0 to 1, from the settings menu.
        ///
        /// <para>One number for every scheme rather than one setting each, because it is one question —
        /// "how much hand movement is a full turn" — and the schemes only differ in what they measure
        /// that movement with. Tilt reads it as degrees of roll, the wheel as degrees of rotation, the
        /// arrows as the time it takes to wind on. A player who has found their number keeps it when
        /// they switch scheme.</para>
        /// </summary>
        public static float SteerSensitivity01 { get; set; } = DefaultSensitivity;

        /// <summary>Middle of the range, which is the value a player who never opens the menu gets.</summary>
        public const float DefaultSensitivity = 0.5f;

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
        ///
        /// Sensitivity is deliberately not cleared: it is a setting, not a control.
        /// </summary>
        public static void Clear()
        {
            Steer = 0f;
            SteerLeftHeld = false;
            SteerRightHeld = false;
            Throttle = 0f;
            Brake = 0f;
            Handbrake = false;
        }
    }
}
