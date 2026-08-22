using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// One line at the top of the screen, shown only when the tank is low or empty.
    ///
    /// <para><b>Two different messages, because the two states need different things said.</b> Naming the
    /// nearest station is useful while the car can still be driven to it and a taunt once it cannot — a
    /// dry car reaches no pump under its own power, so telling its driver where one is would be telling
    /// them about somewhere they cannot go. On reserve it names the station; dry, it says the thing that
    /// will actually work.</para>
    ///
    /// <para><b>Top centre, which is the last free strip on the screen.</b> The pause button owns the top
    /// left and the instruments the top right; the wheel and the pedals own the bottom corners. It is
    /// also where a game conventionally puts a notification, and nowhere near a thumb.</para>
    ///
    /// <para><b>On allocation.</b> This does build a string, and only on the transitions: entering
    /// reserve, and each time the distance crosses a hundred-metre bucket. It is polled twice a second
    /// rather than per frame. That is the honest reading of the rule <c>InstrumentCluster</c> follows —
    /// what that rule forbids is garbage every frame in the driving loop, and the alternative here would
    /// be a prebuilt table of every distance to every station, which is worse in every direction.</para>
    /// </summary>
    public sealed class FuelNotice : MonoBehaviour
    {
        [SerializeField] private GameObject panel;
        [SerializeField] private Text label;

        [Tooltip("Where the pumps are. Found at run time — it lives in the world scene.")]
        [SerializeField] private FillingStations stations;

        /// <summary>
        /// How often the message is reconsidered, seconds.
        ///
        /// <para>Twice a second. The number it prints is rounded to a hundred metres, which at any speed
        /// worth the name changes far more slowly than a frame — and the driver is reading a sentence,
        /// which is not a thing that wants updating sixty times a second even when it is free.</para>
        /// </summary>
        private const float PollSeconds = 0.5f;

        /// <summary>What a dry car should be told. Fixed, so it costs nothing.</summary>
        private const string DryText = "Out of fuel — respawn from the pause menu";

        private VehicleController vehicle;
        private FuelTank tank;
        private float nextPoll;
        private bool shown;
        private bool shownDry;
        private string shownName;
        private int shownBucket = -1;

        private void Update()
        {
            if (Time.unscaledTime < nextPoll)
            {
                return;
            }

            nextPoll = Time.unscaledTime + PollSeconds;

            if (tank == null && !Resolve())
            {
                return;
            }

            if (!tank.IsReserve && !tank.IsDry)
            {
                Show(false);
                return;
            }

            if (tank.IsDry)
            {
                if (!shownDry)
                {
                    shownDry = true;
                    shownBucket = -1;
                    shownName = null;

                    if (label != null)
                    {
                        label.text = DryText;
                    }
                }

                Show(true);
                return;
            }

            shownDry = false;
            ShowNearest();
            Show(true);
        }

        private bool Resolve()
        {
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            tank = vehicle != null ? vehicle.GetComponent<FuelTank>() : null;

            if (stations == null)
            {
                stations = FindFirstObjectByType<FillingStations>();
            }

            return tank != null;
        }

        /// <summary>Names the nearest pump, and only rewrites the line when the answer has moved.</summary>
        private void ShowNearest()
        {
            if (stations == null || label == null)
            {
                return;
            }

            if (!stations.TryNearest(
                    vehicle.transform.position, vehicle.transform.forward,
                    out string name, out float metres, out bool ahead))
            {
                return;
            }

            int bucket = Mathf.RoundToInt(metres / 100f);

            if (bucket == shownBucket && name == shownName)
            {
                return;
            }

            shownBucket = bucket;
            shownName = name;

            // Kilometres to one decimal, because a fuel warning that reads "1400 m" is asking the driver
            // to do arithmetic at the exact moment they should be looking for a turning.
            label.text = $"Low fuel — {name}, {bucket / 10f:0.0} km {(ahead ? "ahead" : "behind")}";
        }

        private void Show(bool visible)
        {
            if (visible == shown || panel == null)
            {
                return;
            }

            shown = visible;
            panel.SetActive(visible);
        }
    }
}
