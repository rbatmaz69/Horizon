using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The rev counter: a round dial with a needle, the speed in the middle and the gear under it.
    ///
    /// <para>The car has a five- or six-speed box, a torque curve, an idle speed and a redline, and
    /// until this existed none of it was visible outside <c>DriveDebugOverlay</c> — which is IMGUI,
    /// compiled out of release builds, and says so itself.</para>
    ///
    /// <para><b>The face is built from the car, not printed on it.</b> The ten cars do not share an
    /// engine: the Van revs to 4200 and the Coupe to 8000. A dial marked 0–8 would leave the Van's
    /// needle inside the first half of its travel for the whole game, and one marked 0–4 would peg the
    /// Coupe against the stop. So full scale is the car's own redline rounded up to the next thousand,
    /// the tick labels are written from that, and the red zone starts at the real redline. The player
    /// changes car from the garage while the game is running, so this re-runs whenever the config the
    /// controller is holding is a different object.</para>
    /// </summary>
    public sealed class InstrumentCluster : MonoBehaviour
    {
        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when the scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [Header("Dial")]
        [SerializeField] private RectTransform needle;

        [Tooltip("The red zone. A radial fill, rotated so it starts at the redline.")]
        [SerializeField] private Image redlineArc;

        [SerializeField] private RectTransform[] tickMarks = new RectTransform[0];
        [SerializeField] private Text[] tickLabels = new Text[0];

        [Header("Readouts")]
        [SerializeField] private Text speedLabel;
        [SerializeField] private Text gearLabel;

        /// <summary>
        /// Where the needle sits at zero and at full scale, as a uGUI z rotation.
        ///
        /// <para>240° of sweep, the classic instrument arc — wide enough that a thousand rpm is a
        /// distance the eye can judge, narrow enough that both ends stay clear of the bottom of the
        /// dial where the gear sits.</para>
        /// </summary>
        private const float StartAngle = 120f;
        private const float EndAngle = -120f;

        /// <summary>
        /// Where the marks and their numbers sit, as a fraction of the dial's half-width.
        ///
        /// <para>The rim itself is the outer fifth of the dial, so the marks sit just inside it and
        /// point inward, and the numbers sit inside them again — with enough room left in the middle
        /// for the speed to be read without a number touching a mark.</para>
        /// </summary>
        private const float TickRadius = 0.75f;
        private const float LabelRadius = 0.59f;

        /// <summary>
        /// How fast the needle chases the engine.
        ///
        /// <para><c>EngineRpm</c> is recomputed in <c>FixedUpdate</c> at 50 Hz, so a needle driven
        /// straight off it steps visibly against a 60 fps screen. This is the same exponential form
        /// <c>EngineAudio</c> smooths its own revs with, and deliberately faster than it: the ear
        /// forgives a lazy engine note, the eye reads a lazy needle as the game lagging.</para>
        /// </summary>
        private const float NeedleResponse = 12f;

        private VehicleConfig face;
        private float fullScale = 1f;
        private float displayedRpm;
        private int shownSpeed = -1;
        private int shownGear = -1;

        /// <summary>
        /// Every small whole number this dial can print, made once: the speed, and the thousands on the
        /// tick labels.
        ///
        /// <para><c>label.text = $"{speed:0}"</c> allocates a string on every frame the number changes,
        /// which above walking pace is nearly all of them. That is the per-frame garbage
        /// <c>CLAUDE.md</c> forbids in driving code, and on a phone it is a stutter every few seconds
        /// rather than a number nobody can see.</para>
        /// </summary>
        private static readonly string[] NumberText = BuildNumberText();

        /// <summary>Gear as the dial shows it. Index 0 is reverse; forward gears are 1-based.</summary>
        private static readonly string[] GearText = { "R", "1", "2", "3", "4", "5", "6" };

        private static string[] BuildNumberText()
        {
            // 400 covers every car in the fleet with room to spare — the fastest tops out at 269 km/h.
            var text = new string[401];
            for (int i = 0; i < text.Length; i++)
            {
                text[i] = i.ToString();
            }

            return text;
        }

        private void Update()
        {
            if (vehicle == null)
            {
                // Retried rather than resolved once: this component is in Bootstrap and the car is in
                // the world scene, which is still loading for the first frames.
                vehicle = FindFirstObjectByType<VehicleController>();
                if (vehicle == null)
                {
                    return;
                }
            }

            VehicleConfig config = vehicle.Config;
            if (config == null)
            {
                return;
            }

            // Reference inequality, not a comparison of values: VehicleController.SetConfig raises no
            // event, and a different car means a different asset.
            if (!ReferenceEquals(config, face))
            {
                BuildFace(config);
            }

            displayedRpm = Mathf.Lerp(
                displayedRpm, vehicle.EngineRpm, 1f - Mathf.Exp(-NeedleResponse * Time.deltaTime));

            if (needle != null)
            {
                needle.localRotation = Quaternion.Euler(0f, 0f, AngleFor(displayedRpm / fullScale));
            }

            // Absolute rpm over full scale, not the idle-rebased fraction EngineAudio uses for its
            // pitch curve. A real tacho sits just above zero at idle, and idle is where this engine
            // sits — EngineRpm is clamped to the idle speed and never falls below it.
            ShowSpeed(Mathf.RoundToInt(vehicle.SpeedKmh));
            ShowGear(vehicle.Gear);
        }

        /// <summary>
        /// Writes the numbers and the red zone for a car.
        ///
        /// <para>Only ever called when the car changes, so the string work and the layout here cost
        /// nothing per frame.</para>
        /// </summary>
        private void BuildFace(VehicleConfig config)
        {
            face = config;

            // Up to the next thousand, so the last number on the dial is a round one and the redline
            // always has some dial left after it: 5800 -> 6000, 4200 -> 5000, 8000 -> 8000.
            int thousands = Mathf.Max(1, Mathf.CeilToInt(config.RedlineRpm / 1000f));
            fullScale = thousands * 1000f;

            int majors = Mathf.Min(thousands + 1, Mathf.Min(tickMarks.Length, tickLabels.Length));
            float radius = RectRadius();

            for (int i = 0; i < tickMarks.Length; i++)
            {
                bool used = i < majors;
                bool hasLabel = i < tickLabels.Length && tickLabels[i] != null;

                if (tickMarks[i] != null)
                {
                    tickMarks[i].gameObject.SetActive(used);
                }

                if (hasLabel)
                {
                    tickLabels[i].gameObject.SetActive(used);
                }

                if (!used)
                {
                    continue;
                }

                float angle = AngleFor(majors > 1 ? i / (float)(majors - 1) : 0f);

                // In uGUI a positive z rotation turns counter-clockwise, which sends "up" to
                // (-sin, cos). Both the mark and its number hang off that one direction.
                var direction = new Vector2(-Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad));

                if (tickMarks[i] != null)
                {
                    tickMarks[i].anchoredPosition = direction * (radius * TickRadius);
                    tickMarks[i].localRotation = Quaternion.Euler(0f, 0f, angle);
                }

                if (hasLabel)
                {
                    tickLabels[i].rectTransform.anchoredPosition = direction * (radius * LabelRadius);

                    // Upright, not rotated with the mark. Numbers that lean with the dial are a
                    // decorative choice a real cluster gave up on decades ago, and they are harder to
                    // read at a glance, which is the only way anyone reads this.
                    tickLabels[i].text = NumberText[i];   // thousands, so 6000 rpm reads as "6".
                }
            }

            if (redlineArc != null)
            {
                float redlineFraction = Mathf.Clamp01(config.RedlineRpm / fullScale);

                // A radial fill can only start at a compass point, so the image itself is turned until
                // its top lands on the redline; the fill then grows clockwise over what is left of the
                // sweep. 240 degrees of dial out of the fill's own 360 is where the ratio comes from.
                redlineArc.rectTransform.localRotation =
                    Quaternion.Euler(0f, 0f, AngleFor(redlineFraction));

                redlineArc.fillAmount = (1f - redlineFraction) * ((StartAngle - EndAngle) / 360f);
            }
        }

        /// <summary>The dial's half-width, which is what every radius here is a fraction of.</summary>
        private float RectRadius()
        {
            var rect = (RectTransform)transform;

            return Mathf.Min(rect.rect.width, rect.rect.height) * 0.5f;
        }

        private static float AngleFor(float fraction)
        {
            return Mathf.Lerp(StartAngle, EndAngle, Mathf.Clamp01(fraction));
        }

        private void ShowSpeed(int kmh)
        {
            int clamped = Mathf.Clamp(kmh, 0, NumberText.Length - 1);
            if (clamped == shownSpeed || speedLabel == null)
            {
                return;
            }

            shownSpeed = clamped;

            // Worth knowing what this number is: SpeedKmh is the car's velocity projected on its own
            // forward axis, so sideways in a drift it reads below true ground speed. That is the same
            // figure the debug overlay has always shown and the same one the camera and the fog are
            // driven from — a second definition of speed would be a second thing to disagree.
            speedLabel.text = NumberText[clamped];
        }

        private void ShowGear(int gear)
        {
            int clamped = Mathf.Clamp(gear, 0, GearText.Length - 1);
            if (clamped == shownGear || gearLabel == null)
            {
                return;
            }

            shownGear = clamped;
            gearLabel.text = GearText[clamped];
        }
    }
}
