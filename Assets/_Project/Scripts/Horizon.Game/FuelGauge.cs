using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The fuel gauge: a small round dial beside the rev counter, empty on the left and full on the
    /// right, with the reserve in red and a pump under the needle.
    ///
    /// <para><b>Its own component rather than part of <see cref="InstrumentCluster"/>, and the reason is
    /// the opposite of the obvious one.</b> Almost all of that class is machinery for rebuilding its face
    /// per car — full scale from the redline, the numbers written from that, the red zone moved to where
    /// the real redline falls. This dial has none of it. It reads a <i>fraction</i>, so E–¼–½–¾–F is its
    /// face for every car in the fleet and for every tank size, and the marks are placed once and never
    /// touched again. Folding a fixed face into the class whose whole point is the variable one would
    /// have obscured both.</para>
    ///
    /// <para><b>120° of sweep, against the tacho's 240°.</b> That wide arc is most of what makes a rev
    /// counter look like a rev counter, and a second one beside it would read as a second rev counter —
    /// the player would have to stop and work out which dial was which, at exactly the moment they are
    /// meant to be looking at the road. A narrow arc with E on the left and F on the right is a fuel
    /// gauge at a glance. Everything else — the ring, the needle, the ticks, the tints — is shared, which
    /// is what makes the two look like one cluster.</para>
    ///
    /// <para><b>It allocates nothing, ever</b>, and needs no help to manage it. There is not a number on
    /// this dial: E and F are captions written once by the setup tool, the needle is a
    /// <c>Quaternion.Euler</c> into a <c>localRotation</c>, and the tint is a <c>Color</c>. Both are
    /// structs. The prebuilt string table <c>InstrumentCluster</c> carries has no equivalent here because
    /// there is nothing to print.</para>
    /// </summary>
    public sealed class FuelGauge : MonoBehaviour
    {
        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when the scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [SerializeField] private RectTransform needle;

        [Tooltip("The reserve band. A radial fill, rotated so it ends where the reserve does.")]
        [SerializeField] private Image reserveArc;

        [SerializeField] private RectTransform[] tickMarks = new RectTransform[0];

        [Tooltip("The pump symbol. Turns red on reserve, which is the half of the warning that "
               + "works without being read.")]
        [SerializeField] private Image pumpGlyph;

        /// <summary>
        /// Where the needle sits at empty and at full, as a uGUI z rotation. See the class note on why
        /// this is not the tacho's 240.
        /// </summary>
        private const float StartAngle = 60f;
        private const float EndAngle = -60f;

        /// <summary>Where the marks sit, as a fraction of the dial's half-width.</summary>
        private const float TickRadius = 0.72f;

        /// <summary>
        /// How fast the needle chases the tank.
        ///
        /// <para>A quarter of the tacho's, and slow on purpose. Fuel moves over minutes, so there is
        /// nothing here for a fast needle to keep up with — and the one moment it does move quickly is a
        /// tank filling, where a needle that snapped to full would look like a value being set rather
        /// than like a tank being filled. Watching it climb is most of what makes stopping at a pump feel
        /// like anything at all.</para>
        /// </summary>
        private const float NeedleResponse = 3f;

        /// <summary>
        /// The dial's colours, duplicated as literals.
        ///
        /// <para><c>TouchUiSetup</c> owns the palette and is Editor-only, so runtime code cannot read it
        /// — the same bind <c>UpdateScreen.AvailableTint</c> is in, and solved the same way. These are
        /// its <c>RedlineTint</c> and <c>GlyphTint</c>; keep them in step by hand.</para>
        /// </summary>
        private static readonly Color ReserveTint = new Color(0.86f, 0.22f, 0.16f, 0.85f);
        private static readonly Color NormalTint = new Color(1f, 1f, 1f, 0.92f);

        private FuelTank tank;
        private bool placed;
        private bool shownReserve;
        private float displayedFraction = 1f;

        private void Update()
        {
            if (tank == null)
            {
                // Retried rather than resolved once: this component is in Bootstrap and the car is in
                // the world scene, which is still loading for the first frames.
                if (vehicle == null)
                {
                    vehicle = FindFirstObjectByType<VehicleController>();
                }

                tank = vehicle != null ? vehicle.GetComponent<FuelTank>() : null;
                if (tank == null)
                {
                    return;
                }
            }

            if (!placed)
            {
                PlaceFace();
            }

            displayedFraction = Mathf.Lerp(
                displayedFraction, tank.Fraction01, 1f - Mathf.Exp(-NeedleResponse * Time.deltaTime));

            if (needle != null)
            {
                needle.localRotation = Quaternion.Euler(0f, 0f, AngleFor(displayedFraction));
            }

            ShowReserve(tank.IsReserve);
        }

        /// <summary>
        /// Lays the marks and the reserve band out, once.
        ///
        /// <para>Not <c>Awake</c>, because it measures the dial's own rect and a layout that has not
        /// been through a frame yet reports nothing useful. Not per car either — see the class note.</para>
        /// </summary>
        private void PlaceFace()
        {
            placed = true;

            var rect = (RectTransform)transform;
            float radius = Mathf.Min(rect.rect.width, rect.rect.height) * 0.5f;

            for (int i = 0; i < tickMarks.Length; i++)
            {
                if (tickMarks[i] == null)
                {
                    continue;
                }

                float angle = AngleFor(tickMarks.Length > 1 ? i / (float)(tickMarks.Length - 1) : 0f);

                // In uGUI a positive z rotation turns counter-clockwise, which sends "up" to
                // (-sin, cos) — the same derivation the rev counter's marks use.
                var direction = new Vector2(
                    -Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad));

                tickMarks[i].anchoredPosition = direction * (radius * TickRadius);
                tickMarks[i].localRotation = Quaternion.Euler(0f, 0f, angle);
            }

            if (reserveArc != null)
            {
                // The mirror of the redline arc. That one starts at the redline and runs to the top of
                // the dial; this one starts at the reserve mark and runs back down to empty, so the
                // image is turned until its top lands on the reserve and the fill grows the other way.
                reserveArc.rectTransform.localRotation =
                    Quaternion.Euler(0f, 0f, AngleFor(FuelTank.ReserveFraction));

                reserveArc.fillClockwise = false;
                reserveArc.fillAmount = FuelTank.ReserveFraction * ((StartAngle - EndAngle) / 360f);
            }
        }

        private static float AngleFor(float fraction)
        {
            return Mathf.Lerp(StartAngle, EndAngle, Mathf.Clamp01(fraction));
        }

        /// <summary>
        /// Reddens the pump on reserve.
        ///
        /// <para>Gated on the state having changed, for the same reason the rev counter gates its labels:
        /// an <c>Image.color</c> write dirties the canvas and forces a rebuild of the batch it is in.
        /// Doing that every frame to set the same colour is a cost paid for nothing.</para>
        /// </summary>
        private void ShowReserve(bool reserve)
        {
            if (reserve == shownReserve || pumpGlyph == null)
            {
                return;
            }

            shownReserve = reserve;
            pumpGlyph.color = reserve ? ReserveTint : NormalTint;
        }
    }
}
