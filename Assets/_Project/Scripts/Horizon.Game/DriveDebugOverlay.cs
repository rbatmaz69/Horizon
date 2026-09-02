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
        private FuelTank fuel;
        private TimeOfDayController timeOfDay;
        private TrafficDirector traffic;
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

            if (traffic == null)
            {
                traffic = FindFirstObjectByType<TrafficDirector>();
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
            GUILayout.BeginArea(new Rect(12f, 150f, 290f, 282f), GUIContent.none, panelStyle);

            if (vehicle != null)
            {
                GUILayout.Label($"{vehicle.SpeedKmh:0} km/h    wheels down: {vehicle.GroundedWheelCount}/4");

                string gear = vehicle.Gear == 0 ? "R" : vehicle.Gear.ToString();
                string shifting = vehicle.IsShifting ? "  shifting" : string.Empty;
                GUILayout.Label($"gear {gear}   {vehicle.EngineRpm:0} rpm{shifting}");

                // Litres and the burn behind them. The gauge only ever shows a fraction, so this is the
                // one place the actual numbers can be read against what an engine of this size ought to
                // be using — which is the check that says whether the model is sane or merely plausible.
                if (fuel == null)
                {
                    fuel = vehicle.GetComponent<FuelTank>();
                }

                if (fuel != null)
                {
                    string dry = fuel.IsDry ? "  DRY" : fuel.IsReserve ? "  reserve" : string.Empty;
                    GUILayout.Label($"fuel {fuel.Litres:0.0}/{fuel.Capacity:0} l"
                                  + $"   {fuel.LitresPerHour:0.0} l/h{dry}");
                }

                // The camera FOV, the rig offset and the engine load are all driven off this one
                // number, and none of them can be tuned against a value that only exists in the
                // physics step. Shown in g as well as m/s² because the figure worth recognising is
                // 0.85 g — what the tyres can actually put down on a launch.
                float accel = vehicle.LongitudinalAcceleration;
                GUILayout.Label($"accel {accel:0.0} m/s²   {accel / 9.81f:0.00} g");

                // The tyre model is tuned by watching these while driving — there is no other way to
                // see whether the rear is at its limit, and a number that only exists in the physics
                // step is a number nobody can tune against.
                string drifting = vehicle.IsDrifting ? "  DRIFT" : string.Empty;
                GUILayout.Label($"slip {vehicle.SlipAngle:0}°   rear {vehicle.RearSlip:0.0} m/s"
                              + $"   grip {vehicle.RearGrip:0.00}{drifting}");

                // What all four tyres together could hold, and what each one is doing about it. The
                // capacity moves with load transfer, downforce, the surface and the rain, so a single
                // grip number from the config would be an answer for a car standing still and level.
                // The slip ratios are the only place wheelspin and lock-up are visible as numbers:
                // positive is a wheel outrunning the road, negative is one being dragged.
                GUILayout.Label($"capacity {vehicle.GripCapacityG:0.00} g   slip ratio "
                              + $"{vehicle.SlipRatioAt(0):0.00} {vehicle.SlipRatioAt(1):0.00} "
                              + $"{vehicle.SlipRatioAt(2):0.00} {vehicle.SlipRatioAt(3):0.00}");

                // What the wheels are standing on, which is otherwise unobservable: a surface changes
                // no pixel and makes only a noise. Driving one wheel onto the verge should move this
                // off 1.00 and nothing else in the game will say whether it did.
                string scraping = vehicle.ScrapeSpeed > 0.5f
                    ? $"   scrape {vehicle.ScrapeSpeed:0.0} m/s"
                    : string.Empty;
                GUILayout.Label($"surface {vehicle.SurfaceGrip:0.00}"
                              + $"   rough {vehicle.SurfaceRoughness:0.00}"
                              + $"   grit {vehicle.SurfaceGrit:0.00}{scraping}");
            }
            else
            {
                GUILayout.Label("waiting for world…");
            }

            // What the density control is actually doing. The pair is the whole of the tuning loop for
            // MetresPerCar: drive from the pass through Talheim to Hochstadt and watch whether "near"
            // tracks "target", and whether the target it settles on matches how busy the place looks.
            if (traffic != null)
            {
                GUILayout.Label($"traffic {traffic.NearCount}/{traffic.NearTarget} near"
                              + $"   budget {traffic.ActiveBudget}");
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
