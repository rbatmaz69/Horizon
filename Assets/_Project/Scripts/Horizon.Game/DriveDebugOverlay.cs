using System.Collections.Generic;
using Horizon.Atmosphere;
using Horizon.Input;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Development HUD: speed, active control scheme, the touch zones the scheme listens to, and a
    /// time-of-day slider. This is the tuning instrument for the whole prototype — being able to
    /// swap schemes and scrub the sun without leaving Play mode is what makes the loop fast.
    ///
    /// IMGUI allocates every frame, so the whole thing is compiled out of release builds. Nothing
    /// here is a template for shipping UI.
    /// </summary>
    public sealed class DriveDebugOverlay : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [SerializeField] private DriveInputRouter inputRouter;
        [SerializeField] private bool showTouchZones = true;

        private VehicleController vehicle;
        private TimeOfDayController timeOfDay;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool spawnCaptured;
        private GUIStyle panelStyle;
        private GUIStyle zoneStyle;

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

            if (showTouchZones && inputRouter != null)
            {
                DrawTouchZones(inputRouter.ActiveZones);
            }

            GUILayout.BeginArea(new Rect(12f, 12f, 290f, 260f), GUIContent.none, panelStyle);

            GUILayout.Label(vehicle != null
                ? $"{vehicle.SpeedKmh:0} km/h    wheels down: {vehicle.GroundedWheelCount}/4"
                : "waiting for world…");

            if (inputRouter != null)
            {
                GUILayout.Label($"Scheme: {inputRouter.ActiveSchemeName}");
                GUILayout.Label($"steer {inputRouter.Steer:0.00}   gas {inputRouter.Throttle:0.00}   "
                              + $"brake {inputRouter.Brake:0.00}");

                if (GUILayout.Button("Next control scheme"))
                {
                    inputRouter.CycleScheme();
                }

                if (inputRouter.Scheme == DriveInputScheme.Tilt && GUILayout.Button("Recalibrate tilt"))
                {
                    inputRouter.CalibrateTilt();
                }
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

        private void DrawTouchZones(IReadOnlyList<TouchZone> zones)
        {
            for (int i = 0; i < zones.Count; i++)
            {
                TouchZone zone = zones[i];
                Rect rect = ViewportToGui(zone.Viewport);
                GUI.Box(rect, zone.Label, zoneStyle);
            }
        }

        /// <summary>Viewport coords are bottom-up, IMGUI is top-down.</summary>
        private static Rect ViewportToGui(Rect viewport)
        {
            return new Rect(
                viewport.x * Screen.width,
                (1f - viewport.y - viewport.height) * Screen.height,
                viewport.width * Screen.width,
                viewport.height * Screen.height);
        }

        private void EnsureStyles()
        {
            if (panelStyle == null)
            {
                panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 10, 10) };
            }

            if (zoneStyle == null)
            {
                zoneStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11,
                };
                zoneStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
            }
        }
#endif
    }
}
