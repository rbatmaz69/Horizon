namespace Horizon.Input
{
    /// <summary>
    /// The only contract the vehicle sees. It never learns which control scheme produced these
    /// values, which is what lets us add or replace schemes without touching vehicle code.
    /// </summary>
    public interface IDriveInput
    {
        /// <summary>Steering, -1 (full left) to 1 (full right).</summary>
        float Steer { get; }

        /// <summary>Throttle, 0 to 1.</summary>
        float Throttle { get; }

        /// <summary>Brake, 0 to 1. Doubles as reverse when the vehicle is nearly stopped.</summary>
        float Brake { get; }

        /// <summary>Handbrake, for deliberately breaking rear grip.</summary>
        bool Handbrake { get; }
    }

    /// <summary>Zeroed input. Used as a safe default so nothing has to null-check.</summary>
    public sealed class NullDriveInput : IDriveInput
    {
        public static readonly NullDriveInput Instance = new NullDriveInput();

        private NullDriveInput() { }

        public float Steer => 0f;
        public float Throttle => 0f;
        public float Brake => 0f;
        public bool Handbrake => false;
    }

    /// <summary>
    /// Ambient access point for the active input. <see cref="DriveInputRouter"/> publishes itself
    /// here, and <c>Horizon.Vehicle</c> reads it without needing a reference to the concrete
    /// router — this is how we keep the dependency on the interface only.
    /// </summary>
    public static class DriveInput
    {
        private static IDriveInput current = NullDriveInput.Instance;

        public static IDriveInput Current
        {
            get => current ?? NullDriveInput.Instance;
            set => current = value ?? NullDriveInput.Instance;
        }
    }
}
