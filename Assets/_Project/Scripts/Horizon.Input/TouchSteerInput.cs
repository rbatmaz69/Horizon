using System.Collections.Generic;
using UnityEngine;

namespace Horizon.Input
{
    /// <summary>
    /// Drag anywhere on the left half of the screen to steer — steering is relative to where the
    /// finger landed, so the player never has to find a fixed control. Pedals on the right half.
    /// </summary>
    public sealed class TouchSteerInput : DriveInputSource
    {
        /// <summary>Horizontal drag, as a fraction of screen width, that gives full lock.</summary>
        public float DragRange { get; set; } = 0.16f;

        private static readonly Rect SteerRegion = new Rect(0f, 0f, 0.5f, 1f);
        private static readonly Rect ThrottleRegion = new Rect(0.5f, 0.35f, 0.5f, 0.65f);
        private static readonly Rect BrakeRegion = new Rect(0.5f, 0f, 0.5f, 0.35f);

        private readonly TouchZone[] zones =
        {
            new TouchZone("Drag to steer", SteerRegion),
            new TouchZone("Throttle", ThrottleRegion),
            new TouchZone("Brake", BrakeRegion),
        };

        private bool tracking;
        private int trackedPressId;
        private float anchorX;

        public override string DisplayName => "Drag to steer";

        public override IReadOnlyList<TouchZone> Zones => zones;

        public override void Disable()
        {
            tracking = false;
        }

        public override void Sample(float deltaTime)
        {
            Reset();

            if (tracking)
            {
                if (TouchSampler.TryGetPressById(trackedPressId, out Vector2 current))
                {
                    float offset = (current.x - anchorX) / Mathf.Max(0.01f, DragRange);
                    Steer = Mathf.Clamp(offset, -1f, 1f);
                }
                else
                {
                    tracking = false;
                }
            }

            if (!tracking && TouchSampler.TryGetPress(SteerRegion, out Vector2 start, out int pressId))
            {
                tracking = true;
                trackedPressId = pressId;
                anchorX = start.x;
            }

            Throttle = TouchSampler.IsPressed(ThrottleRegion) ? 1f : 0f;
            Brake = TouchSampler.IsPressed(BrakeRegion) ? 1f : 0f;
        }
    }
}
