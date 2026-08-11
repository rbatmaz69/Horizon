using UnityEngine;
using UnityEngine.InputSystem;

namespace Horizon.Input
{
    /// <summary>
    /// Tilt the device to steer. The most relaxed way to play and the one closest to the mood this
    /// game is after — the player holds the phone and leans into corners.
    ///
    /// <para>Steering only. It used to carry a brake zone, a handbrake zone and an automatic throttle
    /// as well, which meant tilt could not be combined with pedals or a slider without writing a
    /// second class. Those moved to <see cref="AutoPedals"/> and the on-screen controls, and this is
    /// now one half of a scheme rather than all of one.</para>
    /// </summary>
    public sealed class TiltInput : ISteerInput
    {
        /// <summary>Degrees of roll away from neutral that corresponds to full lock.</summary>
        public float TiltRange { get; set; } = 22f;

        /// <summary>Tilt below this fraction of the range is ignored, to stop micro-wobble.</summary>
        public float Deadzone { get; set; } = 0.06f;

        private float neutralOffset;
        private bool sensorAvailable;

        /// <summary>
        /// True until a real sensor reading has been taken as neutral.
        ///
        /// <para><b>This flag is the whole of a bug that made the game unplayable on a phone.</b>
        /// <see cref="Enable"/> used to calibrate immediately, on the same frame it called
        /// <c>InputSystem.EnableDevice</c> — and a sensor enabled on this frame has not delivered
        /// anything yet, so the reading was 0 and <i>flat on a table</i> got stored as the neutral hold.
        /// Every natural way of holding a phone then read as a large permanent offset and the car drove
        /// itself into the scenery at full lock.</para>
        ///
        /// <para>It could not be caught in the Editor, where there is no sensor and
        /// <see cref="TouchSampler"/>'s mouse fallback is used instead. It needed a real device, which
        /// is worth remembering about the rest of this class.</para>
        /// </summary>
        private bool awaitingCalibration = true;

        public float Steer { get; private set; }

        public string DisplayName => "Tilt to steer";

        public void Enable()
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

            // Arms the calibration rather than performing it — Calibrate only raises a flag, and the
            // neutral hold is taken by the first Sample that gets a real reading out of the sensor.
            // Capturing it here instead is the bug described on awaitingCalibration.
            Calibrate();
        }

        public void Disable()
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
        /// Takes the way the phone is being held right now as "straight ahead".
        ///
        /// <para>Without this the scheme is unusable on a sofa, where nobody holds a phone flat. It is
        /// a request rather than an act: the actual capture happens on the next <see cref="Sample"/>
        /// that has a reading to capture, because asking a sensor for its value in the same frame it
        /// was switched on gets a zero, and a zero neutral is the bug this class carried to a phone.</para>
        ///
        /// <para>Reachable from the pause menu, because where neutral is depends on how the player is
        /// sitting, and that changes.</para>
        /// </summary>
        public void Calibrate()
        {
            awaitingCalibration = true;
        }

        public void Sample(float deltaTime)
        {
            Steer = 0f;

            if (!sensorAvailable)
            {
                return;
            }

            if (!TryReadRoll(out float raw))
            {
                // Sensor enabled but not reporting yet. Steering stays at zero, which is the safe way
                // to be wrong: a car that briefly will not turn is recoverable, one that turns by
                // itself is not.
                return;
            }

            if (awaitingCalibration)
            {
                neutralOffset = raw;
                awaitingCalibration = false;
            }

            // DeltaAngle, because roll is an angle: neutral in landscape is near ±90°, and a player who
            // leans past ±180° would otherwise get full lock the wrong way as the value wrapped.
            float offset = Mathf.DeltaAngle(neutralOffset, raw) / Mathf.Max(1f, TiltRange);

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

        }

        /// <summary>
        /// How far the device is rolled about the axis coming out of its screen, in degrees, or false
        /// if the sensor has not reported yet.
        ///
        /// <para><b>No longer picks an axis by screen orientation, and that was the bug that survived
        /// the first fix.</b> The old code chose <c>gravity.y</c> in landscape and <c>gravity.x</c>
        /// otherwise — and it was landing in the otherwise. In landscape, <c>x</c> is the <i>large</i>
        /// component, the one carrying most of a g, and it moves as the cosine of the roll. A cosine is
        /// symmetric about its peak: leaning twenty degrees left and twenty degrees right both read
        /// -0.94. So the steering went the same way whichever way the phone was tilted, which is exactly
        /// what "it only turns right" means.</para>
        ///
        /// <para>An <c>atan2</c> of the two in-plane components has no such failure. It is the roll angle
        /// itself, monotonic through any neutral, and it does not care which way up the screen thinks it
        /// is — so there is no orientation table to get wrong, and portrait would work too if it were
        /// ever allowed. It also makes <see cref="TiltRange"/> a number in degrees, which is a thing a
        /// player can be offered a slider for and a thing anyone can check with a protractor.</para>
        /// </summary>
        private static bool TryReadRoll(out float degrees)
        {
            degrees = 0f;
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
                return false;
            }

            // Only the two components in the plane of the screen matter, and only their ratio — which
            // is why the units the two sensors disagree about (m/s² against g) stop mattering here.
            Vector2 plane = new Vector2(gravity.x, gravity.y);

            // Gravity is always about 1 g in some direction. Near zero is not a level phone, it is a
            // sensor that has not delivered anything — and a caller that cannot tell those apart will
            // store "no reading" as the neutral hold, which is the other half of this bug's history.
            if (plane.sqrMagnitude < 0.02f)
            {
                return false;
            }

            // Zero when the screen is upright, ±90° in the two landscapes, and continuous between.
            degrees = Mathf.Atan2(plane.x, -plane.y) * Mathf.Rad2Deg;
            return true;
        }
    }
}
