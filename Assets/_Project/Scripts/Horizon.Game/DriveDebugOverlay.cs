using Horizon.Atmosphere;
using Horizon.Input;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Development HUD: speed, slip, the active control methods and a time-of-day slider. This is the
    /// tuning instrument for the whole prototype — being able to watch the friction circle and scrub
    /// the sun without leaving Play mode is what makes the loop fast.
    ///
    /// It no longer draws touch zones or switches schemes. Both moved to the on-screen controls and
    /// the pause menu, which exist in real builds — where this class, being behind its #if, does not.
    ///
    /// IMGUI allocates every frame, so the whole thing is compiled out of release builds. Nothing
    /// here is a template for shipping UI.
    /// </summary>
    public sealed class DriveDebugOverlay : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [SerializeField] private DriveInputRouter inputRouter;

        private VehicleController vehicle;
        private TimeOfDayController timeOfDay;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool spawnCaptured;
        private GUIStyle panelStyle;

        private void Awake()
        {
            if (inputRouter == null)
            {
                inputRouter = FindFirstObjectByType<DriveInputRouter>();
            }
        }

        private void Update()
        {
            // The world loads additively, so these appear a frame or two after we do.
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            if (timeOfDay == null)
            {
                timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            }

            if (vehicle != null && !spawnCaptured)
            {
                spawnPosition = vehicle.transform.position;
                spawnRotation = vehicle.transform.rotation;
                spawnCaptured = true;
            }
        }

        private void OnGUI()
        {
            EnsureStyles();

            // Clear of the top-left corner, which belongs to the pause button. IMGUI always draws over
            // the canvas, so at 12 the overlay hid a button it could not be clicked through — the
            // clicks landed, but you had to know the button was under there.
            GUILayout.BeginArea(new Rect(12f, 150f, 290f, 260f), GUIContent.none, panelStyle);

            if (vehicle != null)
            {
                GUILayout.Label($"{vehicle.SpeedKmh:0} km/h    wheels down: {vehicle.GroundedWheelCount}/4");

                string gear = vehicle.Gear == 0 ? "R" : vehicle.Gear.ToString();
                string shifting = vehicle.IsShifting ? "  shifting" : string.Empty;
                GUILayout.Label($"gear {gear}   {vehicle.EngineRpm:0} rpm{shifting}");

                // The friction circle is tuned by watching these while driving — there is no other way
                // to see whether the rear is at its limit, and a number that only exists in the physics
                // step is a number nobody can tune against.
                string drifting = vehicle.IsDrifting ? "  DRIFT" : string.Empty;
                GUILayout.Label($"slip {vehicle.SlipAngle:0}°   rear {vehicle.RearSlip:0.0} m/s"
                              + $"   grip {vehicle.RearGrip:0.00}{drifting}");
            }
            else
            {
                GUILayout.Label("waiting for world…");
            }

            if (inputRouter != null)
            {
                GUILayout.Label($"Scheme: {inputRouter.ActiveSchemeName}");
                GUILayout.Label($"steer {inputRouter.Steer:0.00}   gas {inputRouter.Throttle:0.00}   "
                              + $"brake {inputRouter.Brake:0.00}");

                // The scheme is chosen in the pause menu now, which exists in real builds where this
                // overlay does not. This is left as a readout so the tuning loop can still see which
                // pair is live without opening a menu.
            }

            if (vehicle != null && spawnCaptured && GUILayout.Button("Back to start"))
            {
                vehicle.Teleport(spawnPosition, spawnRotation);
            }

            if (timeOfDay != null)
            {
                GUILayout.Space(6f);
                GUILayout.Label($"Time of day: {timeOfDay.TimeOfDayHours:00.0}h");
                timeOfDay.TimeOfDayHours = GUILayout.HorizontalSlider(timeOfDay.TimeOfDayHours, 0f, 24f);
                timeOfDay.Running = GUILayout.Toggle(timeOfDay.Running, " clock running");

                GUILayout.Label($"Overcast: {timeOfDay.Overcast:0.00}");
                timeOfDay.Overcast = GUILayout.HorizontalSlider(timeOfDay.Overcast, 0f, 1f);
            }

            GUILayout.EndArea();
        }



        private void EnsureStyles()
        {
            if (panelStyle == null)
            {
                panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 10, 10) };
            }
        }
#endif
    }
}
