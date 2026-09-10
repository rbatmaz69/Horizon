using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The one line at the top of the screen, and the only thing allowed to write there.
    ///
    /// <para><b>It was <c>FuelNotice</c> and the rename is the point.</b> When the viewpoints arrived
    /// they wanted a line at the top of the screen too, and the obvious build — a second component with
    /// a second panel — puts two sentences in the same forty pixels and lets the loser be decided by
    /// which one happened to run last. There is one strip and it needs one owner, so the ladder below
    /// grew a rung rather than a rival. A class called <c>FuelNotice</c> that says "Ebenkopf — a new
    /// view" would be a name that lies, which is a thing this project pays for elsewhere.</para>
    ///
    /// <para><b>Six lines, and the order they are tested in is the design.</b> Naming the nearest
    /// station is useful while the car can still be driven to it and a taunt once it cannot — a dry car
    /// reaches no pump under its own power, so telling its driver where one is would be telling them
    /// about somewhere they cannot go. On a forecourt the same applies to the reserve line, which would
    /// helpfully report that the nearest pump is nought point nought kilometres ahead. The ladder in
    /// <c>Update</c> carries the reasoning for each rung.</para>
    ///
    /// <para><b>It is also what makes a filling station feel like a place rather than a trigger.</b>
    /// Before this the tank simply filled, in silence, with nothing to say whether stopping there had
    /// worked. Pull up, stop, refuelling, full — said in that order, it is a transaction.</para>
    ///
    /// <para><b>Top centre, which is the last free strip on the screen.</b> The pause button owns the top
    /// left and the instruments the top right; the wheel and the pedals own the bottom corners. It is
    /// also where a game conventionally puts a notification, and nowhere near a thumb.</para>
    ///
    /// <para><b>The viewpoint rung sits at the bottom, under everything about fuel.</b> A driver who
    /// is dry needs to know it more than they need to be told the name of the place they stopped at,
    /// and the two are simultaneously true exactly where it matters — a car that has coasted to a halt
    /// at a viewpoint on the last of its tank. It is also the only rung that is a reward rather than an
    /// instruction, and a reward can wait.</para>
    ///
    /// <para><b>On allocation.</b> Five of the six lines are constants and cost nothing at all. The
    /// sixth builds a string, and only on the transitions: entering reserve, and each time the distance
    /// crosses a hundred-metre bucket. It is polled twice a second
    /// rather than per frame. That is the honest reading of the rule <c>InstrumentCluster</c> follows —
    /// what that rule forbids is garbage every frame in the driving loop, and the alternative here would
    /// be a prebuilt table of every distance to every station, which is worse in every direction.</para>
    /// </summary>
    public sealed class NoticeLine : MonoBehaviour
    {
        [SerializeField] private GameObject panel;
        [SerializeField] private Text label;

        [Tooltip("Where the pumps are. Found at run time — it lives in the world scene.")]
        [SerializeField] private FillingStations stations;

        [Tooltip("Where the views are. Beside this component rather than in the world scene, because "
               + "what it holds is player state rather than world state.")]
        [SerializeField] private Viewpoints viewpoints;

        /// <summary>
        /// How often the message is reconsidered, seconds.
        ///
        /// <para>Four times a second. It was half that when the only thing here was a distance rounded
        /// to a hundred metres, which changes slowly at any speed worth the name — but most of these
        /// lines are now prompts a driver acts on, and at half a second and 60 km/h the forecourt prompt
        /// arrives eight metres late. Still far from every frame, because a sentence is not a thing that
        /// wants rewriting sixty times a second even when it is free.</para>
        /// </summary>
        private const float PollSeconds = 0.25f;

        /// <summary>Everything the notice can say that is not built from a distance. Fixed, so free.</summary>
        private const string DryText = "Out of fuel — respawn from the pause menu";
        private const string FullText = "Tank full";
        private const string FillingText = "Refuelling…";
        private const string StopText = "Stop to refuel";
        private const string PullUpText = "Pull up to a pump";

        /// <summary>What is appended to a place's name the first time it is reached.</summary>
        private const string ArrivedSuffix = " — a new view";

        /// <summary>Which line is up. Compared rather than the text, so nothing is assigned twice.</summary>
        private enum Line { Hidden, Full, Filling, Stop, Dry, PullUp, Reserve, Arrived, AtView }

        private VehicleController vehicle;
        private FuelTank tank;
        private float nextPoll;
        private bool shown;
        private Line shownLine = Line.Hidden;
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

            // First line that applies wins, and the order is the design — several of these are true at
            // once the moment a car rolls onto a forecourt.
            //
            // At a pump beats dry: a dry car standing at a pump is a car about to be filled, and telling
            // it to respawn would be telling it to throw away the rescue it is parked in. But dry beats
            // "pull up to a pump", because a dry car anywhere else on the slab cannot reach one under its
            // own power — which is why that row sits between the two.
            //
            // And the forecourt rows beat the reserve line, because "the nearest pump is 0.0 km ahead"
            // is not what to say to somebody standing on it.
            bool full = tank.Fraction01 >= 1f;

            if (stations != null && stations.IsAtPump)
            {
                if (full)
                {
                    // Latched rather than timed: it holds until the car leaves the pump, which is the
                    // answer to the question the driver just asked, and a state that persists costs
                    // nothing where one that expires needs a clock.
                    Say(Line.Full, FullText);
                }
                else if (stations.IsStopped)
                {
                    // Covers the settle as well as the fill. Splitting them would be a line that
                    // flickers for eight tenths of a second, and from the driver's side arriving and
                    // being served are one act.
                    Say(Line.Filling, FillingText);
                }
                else
                {
                    Say(Line.Stop, StopText);
                }

                Show(true);
                return;
            }

            if (tank.IsDry)
            {
                Say(Line.Dry, DryText);
                Show(true);
                return;
            }

            if (stations != null && stations.IsOnForecourt && !full)
            {
                Say(Line.PullUp, PullUpText);
                Show(true);
                return;
            }

            if (tank.IsReserve)
            {
                ShowNearest();
                Show(true);
                return;
            }

            // The bottom of the ladder, and the only rung here that is not about running out of
            // something. Two lines rather than one: arriving somewhere for the first time is the thing
            // worth saying, and a place already stood at gets its name and nothing else — otherwise
            // every pass of a lay-by would congratulate the driver again.
            if (viewpoints != null && viewpoints.IsStopped && viewpoints.CurrentName != null)
            {
                if (viewpoints.JustArrived)
                {
                    SayAbout(Line.Arrived, viewpoints.CurrentName, ArrivedSuffix);
                }
                else
                {
                    SayAbout(Line.AtView, viewpoints.CurrentName, string.Empty);
                }

                Show(true);
                return;
            }

            Show(false);
        }

        /// <summary>
        /// Puts a fixed line up, and only when it is not already the one showing.
        ///
        /// <para>Clearing the distance memo here is what makes the reserve line rebuild correctly when
        /// the player comes back to it from one of the forecourt states — otherwise it would still think
        /// it had already printed the bucket it is now being asked for.</para>
        /// </summary>
        private void Say(Line line, string text)
        {
            if (line == shownLine)
            {
                return;
            }

            shownLine = line;
            shownBucket = -1;
            shownName = null;

            if (label != null)
            {
                label.text = text;
            }
        }

        /// <summary>
        /// Puts up a line built from a place's name, and only when the place has changed.
        ///
        /// <para>The name is memoed alongside the rung for the reason <see cref="ShowNearest"/> memoes
        /// its distance bucket: this is the only other line here that allocates, and a viewpoint is
        /// stood at for as long as somebody wants to look at it.</para>
        /// </summary>
        private void SayAbout(Line line, string place, string suffix)
        {
            if (line == shownLine && place == shownName)
            {
                return;
            }

            shownLine = line;
            shownName = place;
            shownBucket = -1;

            if (label != null)
            {
                label.text = place + suffix;
            }
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

            if (viewpoints == null)
            {
                viewpoints = FindFirstObjectByType<Viewpoints>();
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
            shownLine = Line.Reserve;

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
