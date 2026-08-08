using System.Collections.Generic;
using UnityEngine;

namespace Horizon.Input
{
    /// <summary>
    /// Discrete on-screen buttons. The most predictable scheme and the easiest to learn, at the
    /// cost of screen space. Steering ramps in over time rather than snapping to full lock, or the
    /// car would twitch — the ramp lives here because it is a property of button input, not of
    /// the car (the router's smoothing then applies on top).
    /// </summary>
    public sealed class TouchButtonInput : DriveInputSource
    {
        /// <summary>How fast held buttons ramp steering toward full lock, in units per second.</summary>
        public float SteerRampRate { get; set; } = 2.4f;

        /// <summary>How fast steering returns to centre when neither button is held.</summary>
        public float SteerReturnRate { get; set; } = 4f;

        private static readonly Rect LeftRegion = new Rect(0.03f, 0.05f, 0.14f, 0.22f);
        private static readonly Rect RightRegion = new Rect(0.20f, 0.05f, 0.14f, 0.22f);
        private static readonly Rect BrakeRegion = new Rect(0.66f, 0.05f, 0.14f, 0.22f);
        private static readonly Rect ThrottleRegion = new Rect(0.83f, 0.05f, 0.14f, 0.22f);

        private readonly TouchZone[] zones =
        {
            new TouchZone("Left", LeftRegion),
            new TouchZone("Right", RightRegion),
            new TouchZone("Brake", BrakeRegion),
            new TouchZone("Throttle", ThrottleRegion),
        };

        private float steer;

        public override string DisplayName => "On-screen buttons";

        public override IReadOnlyList<TouchZone> Zones => zones;

        public override void Disable()
        {
            steer = 0f;
        }

        public override void Sample(float deltaTime)
        {
            bool left = TouchSampler.IsPressed(LeftRegion);
            bool right = TouchSampler.IsPressed(RightRegion);

            float target = 0f;
            if (left != right)
            {
                target = left ? -1f : 1f;
            }

            float rate = Mathf.Approximately(target, 0f) ? SteerReturnRate : SteerRampRate;
            steer = Mathf.MoveTowards(steer, target, rate * deltaTime);

            Steer = steer;
            Throttle = TouchSampler.IsPressed(ThrottleRegion) ? 1f : 0f;
            Brake = TouchSampler.IsPressed(BrakeRegion) ? 1f : 0f;
            Handbrake = false;
        }
    }
}
