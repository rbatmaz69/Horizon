using Horizon.World;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The page that shows what this player has done.
    ///
    /// <para><b>All three of these were already kept and none of them was shown.</b>
    /// <c>PlayerChoices.VisitedCount</c> had no reader anywhere in the project; a best lap appeared on
    /// screen only while the car was standing on the circuit it belongs to, so a week-old lap was
    /// invisible; and distance was not counted at all. A game that records something and never adds it
    /// up is a game that looks like it has not decided whether the recording means anything.</para>
    ///
    /// <para><b>The viewpoints come out of the baked map, which is where they already live.</b>
    /// <c>Viewpoints</c> reads the same array to decide where the car may stop, and its own remarks give
    /// the reason: a second bake would be a second opinion about where a viewpoint is and what it is
    /// called, and the mark on the map and the place you stop at could then be different places. This is
    /// a second <i>reader</i> of one source, which is not the same thing.</para>
    ///
    /// <para><b>The circuits come from the circuits.</b> There is no baked list of them — a lap time is
    /// keyed by the name <c>LapTiming.SetCircuit</c> was given — so the page finds every
    /// <c>LapTiming</c> in the world and asks each what it is called. That is what <c>LapTimer</c>
    /// already does, and for the same reason: two circuits turn a <c>FindFirstObjectByType</c> into a
    /// coin toss.</para>
    ///
    /// <para><b>On the panel, with <c>OnEnable</c> and a public <c>Open</c>.</b> That is
    /// <c>MapScreen</c>'s shape exactly: the page has to be right every time it is shown, and
    /// <c>OnEnable</c> does not run in the editor, so a preview that merely switched the panel on would
    /// photograph a page of empty rows — a picture of nothing that looks like a picture of a
    /// fault.</para>
    ///
    /// <para><b>Text allocates when it is written</b>, which is why this runs on opening the page and
    /// never per frame. Nothing here changes while it is being looked at: the world is stopped.</para>
    /// </summary>
    public sealed class JourneyScreen : MonoBehaviour
    {
        [Tooltip("The baked map. The same asset the minimap draws and Viewpoints walks.")]
        [SerializeField] private WorldMap map;

        [Tooltip("How many of the viewpoints have been stood at.")]
        [SerializeField] private Text heading;

        [Tooltip("One mark per viewpoint, in the map's own order. Filled when it has been reached.")]
        [SerializeField] private Image[] marks = new Image[0];

        [Tooltip("The name beside each mark.")]
        [SerializeField] private Text[] names = new Text[0];

        [Tooltip("A fixed pool of circuit rows, filled from the top and hidden from where it runs out — "
               + "the map's label pool, one page along.")]
        [SerializeField] private Text[] lapNames = new Text[0];

        [SerializeField] private Text[] lapTimes = new Text[0];

        [Tooltip("The row each pair sits on, which is what has to be switched off when there is no "
               + "circuit to put in it — a label hidden on its own leaves the row standing.")]
        [SerializeField] private GameObject[] lapRows = new GameObject[0];

        [Tooltip("How far this player has driven.")]
        [SerializeField] private Text distance;

        [Tooltip("A viewpoint that has been stood at.")]
        [SerializeField] private Color reachedTint = new Color(0.86f, 0.36f, 0.17f, 0.92f);

        [Tooltip("One that has not. Dim rather than absent: a place you have not been to is still a "
               + "place, and a row with nothing on it reads as a fault.")]
        [SerializeField] private Color unreachedTint = new Color(1f, 0.88f, 0.74f, 0.22f);

        private LapTiming[] circuits;

        private void OnEnable()
        {
            Open();
        }

        /// <summary>
        /// Fills the page. Public for the preview tool, for the reason <c>MapScreen.Open</c> gives.
        /// </summary>
        public void Open()
        {
            FillViewpoints();
            FillCircuits();
            FillDistance();
        }

        private void FillViewpoints()
        {
            if (map == null)
            {
                return;
            }

            int shown = 0;
            int reached = 0;

            for (int i = 0; i < map.MarkerCount && shown < names.Length; i++)
            {
                if (map.MarkerKindOf(i) != MapMarkerKind.Viewpoint)
                {
                    continue;
                }

                string name = map.MarkerNameOf(i);
                bool been = PlayerChoices.HasVisited(name);

                if (been)
                {
                    reached++;
                }

                if (names[shown] != null)
                {
                    names[shown].text = name;
                    names[shown].color = new Color(1f, 1f, 1f, been ? 0.92f : 0.5f);
                }

                if (marks.Length > shown && marks[shown] != null)
                {
                    marks[shown].color = been ? reachedTint : unreachedTint;
                }

                shown++;
            }

            // The rows are built from this same map, so there is never a spare one — but a build that
            // came apart would otherwise leave the last row showing whatever it was last given.
            for (int i = shown; i < names.Length; i++)
            {
                if (names[i] != null)
                {
                    names[i].text = string.Empty;
                }

                if (marks.Length > i && marks[i] != null)
                {
                    marks[i].color = Color.clear;
                }
            }

            if (heading != null)
            {
                heading.text = $"{reached} OF {shown} REACHED";
            }
        }

        private void FillCircuits()
        {
            if (circuits == null)
            {
                circuits = FindObjectsByType<LapTiming>(FindObjectsSortMode.None);
            }

            int shown = 0;

            for (int i = 0; i < circuits.Length && shown < lapNames.Length; i++)
            {
                if (circuits[i] == null || string.IsNullOrEmpty(circuits[i].CircuitName))
                {
                    continue;
                }

                string name = circuits[i].CircuitName;
                float best = PlayerChoices.BestLap(name);

                if (lapNames[shown] != null)
                {
                    lapNames[shown].text = name.ToUpperInvariant();
                }

                if (lapTimes.Length > shown && lapTimes[shown] != null)
                {
                    // An em dash rather than a zero. Nought seconds is a lap somebody drove, and it is
                    // the fastest one there could be — which is exactly what an empty board must not
                    // look like.
                    lapTimes[shown].text = best > 0f ? Clock(best) : "—";
                }

                Show(shown, true);
                shown++;
            }

            // <b>The row, never the label.</b> The spares are built active so the page is measured with
            // the pool full — see MenuUiSetup.LapRows — so something has to take them out afterwards,
            // and hiding a label leaves its row standing in the layout as forty-two units of nothing.
            for (int i = shown; i < lapNames.Length; i++)
            {
                Show(i, false);
            }
        }

        private void Show(int row, bool visible)
        {
            if (lapRows.Length > row && lapRows[row] != null && lapRows[row].activeSelf != visible)
            {
                lapRows[row].SetActive(visible);
            }
        }

        private void FillDistance()
        {
            if (distance == null)
            {
                return;
            }

            float km = PlayerChoices.Distance / 1000f;

            // Metres under a kilometre, because the first drive is the one where this number means the
            // most and "0 km" is what a broken counter says.
            distance.text = km < 1f
                ? $"{PlayerChoices.Distance:0} m"
                : $"{km:0.0} km";
        }

        /// <summary>
        /// A lap time as minutes, seconds and hundredths.
        ///
        /// <para>Spelt out here rather than borrowed from <c>LapTimer</c>: that one writes into a HUD
        /// sixty times a second and is built around not allocating, where this runs once when a page is
        /// opened. Sharing it would mean exporting a formatter from a component nothing else talks
        /// to.</para>
        /// </summary>
        private static string Clock(float seconds)
        {
            int minutes = Mathf.FloorToInt(seconds / 60f);
            float rest = seconds - minutes * 60f;

            return $"{minutes}:{rest:00.00}";
        }
    }
}
