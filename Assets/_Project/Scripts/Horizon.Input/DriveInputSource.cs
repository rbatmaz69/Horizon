using System.Collections.Generic;
using UnityEngine;

namespace Horizon.Input
{
    /// <summary>A screen region a touch scheme listens to. Exposed so the HUD can draw it.</summary>
    public readonly struct TouchZone
    {
        /// <summary>Label for debug/HUD rendering.</summary>
        public readonly string Label;

        /// <summary>Region in normalized viewport coordinates, origin bottom-left.</summary>
        public readonly Rect Viewport;

        public TouchZone(string label, Rect viewport)
        {
            Label = label;
            Viewport = viewport;
        }
    }

    /// <summary>
    /// Base for the concrete control schemes. Plain classes rather than MonoBehaviours: the router
    /// owns them and drives <see cref="Sample"/>, so there is exactly one Update in the input path.
    /// </summary>
    public abstract class DriveInputSource : IDriveInput
    {
        public float Steer { get; protected set; }
        public float Throttle { get; protected set; }
        public float Brake { get; protected set; }
        public bool Handbrake { get; protected set; }

        /// <summary>Human-readable name, for the scheme switcher.</summary>
        public abstract string DisplayName { get; }

        /// <summary>Touch regions this scheme reads, or an empty list. Never null.</summary>
        public virtual IReadOnlyList<TouchZone> Zones => System.Array.Empty<TouchZone>();

        /// <summary>Called when this scheme becomes active. Enable sensors here, not in the ctor.</summary>
        public virtual void Enable() { }

        /// <summary>Called when this scheme is deactivated. Release sensors here.</summary>
        public virtual void Disable() { }

        /// <summary>Read the raw device state for this frame. Smoothing belongs to the router.</summary>
        public abstract void Sample(float deltaTime);

        protected void Reset()
        {
            Steer = 0f;
            Throttle = 0f;
            Brake = 0f;
            Handbrake = false;
        }
    }
}
