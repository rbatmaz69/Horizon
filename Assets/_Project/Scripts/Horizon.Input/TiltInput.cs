using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Horizon.Input
{
    /// <summary>
    /// Tilt the device to steer; the throttle applies itself. The most relaxed scheme and the one
    /// closest to the mood we are after — the player holds the phone and leans into corners.
    /// </summary>
    public sealed class TiltInput : DriveInputSource
    {
        /// <summary>Tilt away from neutral, in g, that corresponds to full lock (~0.35g ≈ 20°).</summary>
        public float TiltRange { get; set; } = 0.35f;

        /// <summary>Tilt below this fraction of the range is ignored, to stop micro-wobble.</summary>
        public float Deadzone { get; set; } = 0.06f;

        private static readonly Rect BrakeRegion = new Rect(0f, 0f, 0.35f, 0.30f);
        private static readonly Rect HandbrakeRegion = new Rect(0.65f, 0f, 0.35f, 0.30f);

        private readonly TouchZone[] zones =
        {
            new TouchZone("Brake", BrakeRegion),
            new TouchZone("Handbrake", HandbrakeRegion),
        };

        private float neutralOffset;
        private bool sensorAvailable;

        public override string DisplayName => "Tilt to steer";

        public override IReadOnlyList<TouchZone> Zones => zones;

        public override void Enable()
        {
            sensorAvailable = false;

            // Input System sensors are disabled by default — without EnableDevice they read zero
            // and the scheme looks broken rather than uncalibrated.
            if (GravitySensor.current != null)
            {
                if (!GravitySensor.current.enabled)
                {
                    InputSystem.EnableDevice(GravitySensor.current);
                }

                sensorAvailable = true;
            }
            else if (Accelerometer.current != null)
            {
                if (!Accelerometer.current.enabled)
                {
                    InputSystem.EnableDevice(Accelerometer.current);
                }

                sensorAvailable = true;
            }

            // Assume the player is holding the device the way they are holding it right now.
            Calibrate();
        }

        public override void Disable()
        {
            if (GravitySensor.current != null && GravitySensor.current.enabled)
            {
                InputSystem.DisableDevice(GravitySensor.current);
            }

            if (Accelerometer.current != null && Accelerometer.current.enabled)
            {
                InputSystem.DisableDevice(Accelerometer.current);
            }
        }

        /// <summary>
        /// Captures the current hold angle as "straight ahead". Without this the scheme is unusable
        /// on a couch, where nobody holds a phone flat.
        /// </summary>
        public void Calibrate()
        {
            neutralOffset = ReadRawTilt();
        }

        public override void Sample(float deltaTime)
        {
            Reset();

            if (!sensorAvailable)
            {
                return;
            }

            float offset = (ReadRawTilt() - neutralOffset) / Mathf.Max(0.01f, TiltRange);

            // Deadzone rescaled so steering still reaches full lock at the edge of the range.
            float magnitude = Mathf.Abs(offset);
            if (magnitude <= Deadzone)
            {
                Steer = 0f;
            }
            else
            {
                float scaled = (magnitude - Deadzone) / (1f - Deadzone);
                Steer = Mathf.Clamp01(scaled) * Mathf.Sign(offset);
            }

            Brake = TouchSampler.IsPressed(BrakeRegion) ? 1f : 0f;
            Handbrake = TouchSampler.IsPressed(HandbrakeRegion);

            // Auto-throttle: the point of this scheme is that the player only steers.
            Throttle = Brake > 0f ? 0f : 1f;
        }

        /// <summary>
        /// Gravity in device space, mapped to a single steering axis. Which component carries the
        /// roll depends on how the screen is rotated, so we resolve it from the orientation.
        /// </summary>
        private static float ReadRawTilt()
        {
            Vector3 gravity;
            if (GravitySensor.current != null && GravitySensor.current.enabled)
            {
                gravity = GravitySensor.current.gravity.ReadValue();
            }
            else if (Accelerometer.current != null && Accelerometer.current.enabled)
            {
                gravity = Accelerometer.current.acceleration.ReadValue();
            }
            else
            {
                return 0f;
            }

            switch (Screen.orientation)
            {
                case ScreenOrientation.LandscapeLeft:
                    return -gravity.y;
                case ScreenOrientation.LandscapeRight:
                    return gravity.y;
                case ScreenOrientation.PortraitUpsideDown:
                    return -gravity.x;
                default:
                    return gravity.x;
            }
        }
    }
}
