using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The lap readout: current, last and best, under the minimap, and only while the car is on a
    /// circuit.
    ///
    /// <para><b>It hides itself everywhere else, and that is the whole reason it can exist at all.</b>
    /// This is a game about driving with no objective; three numbers permanently in the corner of the
    /// screen would be a game about beating them. On the two circuits they are the point, and nowhere
    /// else in the world do they appear.</para>
    ///
    /// <para><b>There is more than one circuit, so there is more than one <see cref="LapTiming"/>, and
    /// which one this reads cannot be whichever Unity hands back first.</b> It used to be
    /// <c>FindFirstObjectByType</c> resolved once — correct while the Weissjochring was the only lap in
    /// the world, and a coin toss the moment the Bahçe Ring arrived: half the time the readout would sit
    /// blank on a circuit while faithfully reporting the other one, four hundred kilometres away. They
    /// are all found once now, and the one that says the car is on it is the one that is read.</para>
    ///
    /// <para><b>Text in a HUD allocates, and this is the worst case of it in the project.</b>
    /// <c>label.text = $"{t:0.0}"</c> makes a string on every frame the number changes, and a running
    /// clock changes every frame by construction — so the times come out of a prebuilt table and are
    /// assigned only when the tenth moves. Six thousand strings covers nought to 9:59.9, which is a
    /// quarter of a megabyte held for as long as the app runs.</para>
    ///
    /// <para><b>The table is built the first time a circuit is reached, not at startup.</b> Building it
    /// with the HUD would put a few milliseconds of string formatting into the first frame of every
    /// session, for a readout most sessions never show. Built on arrival, it lands while the car is
    /// still coming down the access road.</para>
    /// </summary>
    public sealed class LapTimer : MonoBehaviour
    {
        [SerializeField] private GameObject panel;
        [SerializeField] private Text currentLabel;
        [SerializeField] private Text lastLabel;
        [SerializeField] private Text bestLabel;
        [SerializeField] private Text gateLabel;

        [Tooltip("Where the laps are counted. Found at run time — they live in the world scene.")]
        [SerializeField] private LapTiming timing;

        /// <summary>
        /// Every circuit in the world, found once. Null until the world scene has finished loading.
        ///
        /// <para>Held rather than re-queried: <c>FindObjectsByType</c> walks the scene, and this runs in
        /// <c>Update</c>. The array is rebuilt only while it is empty, which is exactly the case the
        /// retry below exists for.</para>
        /// </summary>
        private LapTiming[] circuits;

        /// <summary>Tenths of a second the table covers: ten minutes.</summary>
        private const int TableEntries = 6000;

        /// <summary>Shown in place of a time that does not exist yet.</summary>
        private const string NoTime = "--:--.-";

        private static string[] timeText;

        /// <summary>"n/N" for every gate count the circuit could have. Fixed, so free.</summary>
        private static readonly string[] GateText = BuildGateText();

        private static string[] BuildGateText()
        {
            // Twelve is far more than any circuit here will carry; the table costs nothing and the
            // alternative is a string built every time a gate is passed.
            var text = new string[13 * 13];
            for (int passed = 0; passed < 13; passed++)
            {
                for (int total = 0; total < 13; total++)
                {
                    text[passed * 13 + total] = $"{passed}/{total}";
                }
            }

            return text;
        }

        private int gateShown = -1;
        private int currentTenths = -1;
        private int lastTenths = -1;
        private int bestTenths = -1;
        private bool shown;

        private void Update()
        {
            if (circuits == null || circuits.Length == 0)
            {
                // Retried rather than resolved once: this component is in Bootstrap and the circuits are
                // in the world scene, which is still loading for the first frames. A world with no
                // circuit in it simply never finds one, which is correct behaviour and not an error.
                circuits = FindObjectsByType<LapTiming>(FindObjectsSortMode.None);

                if (circuits.Length == 0)
                {
                    return;
                }
            }

            // Whichever one the car is actually on. The panel is only ever shown on a circuit, so
            // "none of them" and "hidden" are the same state — and holding on to the last one while it
            // still reports the car is the cheap path, which is every frame but the handful spent
            // arriving.
            if (timing == null || !timing.OnCircuit)
            {
                timing = null;

                for (int i = 0; i < circuits.Length; i++)
                {
                    if (circuits[i] != null && circuits[i].OnCircuit)
                    {
                        timing = circuits[i];
                        break;
                    }
                }
            }

            bool wanted = timing != null;

            if (wanted != shown)
            {
                shown = wanted;

                if (panel != null)
                {
                    panel.SetActive(wanted);
                }

                if (wanted)
                {
                    EnsureTable();

                    // Forced to redraw on the way in: the numbers may not have moved since the last
                    // visit, and a panel that comes back showing nothing reads as broken.
                    currentTenths = -1;
                    lastTenths = -1;
                    bestTenths = -1;
                    gateShown = -1;
                }
            }

            if (!wanted)
            {
                return;
            }

            Assign(currentLabel, timing.Timing ? timing.Current : 0f, ref currentTenths, timing.Timing);
            Assign(lastLabel, timing.Last, ref lastTenths, timing.Last > 0f);
            Assign(bestLabel, timing.Best, ref bestTenths, timing.Best > 0f);

            // The gates, so a lap that will not count says so while it is still being driven rather
            // than at the line. Assigned only when the count moves.
            if (gateLabel != null)
            {
                int passed = Mathf.Clamp(timing.GatesPassed, 0, 12);
                int total = Mathf.Clamp(timing.GateCount, 0, 12);
                int key = passed * 13 + total;

                if (key != gateShown)
                {
                    gateShown = key;
                    gateLabel.text = GateText[key];
                }
            }
        }

        /// <summary>
        /// Writes a time to a label, and only when the displayed tenth has actually moved.
        /// </summary>
        /// <param name="have">
        /// False for a time that does not exist yet — no lap finished, or the clock not started. Passed
        /// rather than inferred from a zero, because nought seconds and "no time" want different text
        /// and a running clock genuinely passes through zero.
        /// </param>
        private static void Assign(Text label, float seconds, ref int shownTenths, bool have)
        {
            if (label == null)
            {
                return;
            }

            int tenths = have ? Mathf.Clamp(Mathf.FloorToInt(seconds * 10f), 0, TableEntries - 1) : -1;

            if (tenths == shownTenths)
            {
                return;
            }

            shownTenths = tenths;
            label.text = tenths < 0 ? NoTime : timeText[tenths];
        }

        private static void EnsureTable()
        {
            if (timeText != null)
            {
                return;
            }

            timeText = new string[TableEntries];

            for (int i = 0; i < TableEntries; i++)
            {
                int minutes = i / 600;
                int seconds = i / 10 % 60;
                int tenths = i % 10;

                timeText[i] = $"{minutes}:{seconds:00}.{tenths}";
            }
        }
    }
}
