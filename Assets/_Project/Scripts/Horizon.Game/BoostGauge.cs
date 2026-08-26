using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The boost gauge: a small round dial above the fuel gauge, reading plenum pressure from nothing
    /// on the left to full boost on the right, with a compressor under the needle that lights when the
    /// turbo is on song.
    ///
    /// <para><b>It is not there at all on six of the ten cars.</b> The Fastback, the Estate, the
    /// Pickup, the Hatchback, the Saloon and the Notchback are naturally aspirated, and an instrument
    /// pegged at zero for the whole game is worse than no instrument — it reads as something broken
    /// rather than as something absent. <see cref="VehicleConfig.IsTurbocharged"/> decides, and it is
    /// polled rather than read once because the garage changes car while the game is running.</para>
    ///
    /// <para><b>Three states, and the player has to be able to tell all three apart.</b> No
    /// turbocharger is the dial being gone. A turbo fitted but off boost is the needle on the left stop
    /// with a white compressor. On boost is the needle out and the compressor lit. The middle one is
    /// the state a gauge that only lit up would lose, and it is most of the character of a big single
    /// turbo — the hole below <c>TurboSpoolRevs</c> is something you should be able to watch.</para>
    ///
    /// <para><b>120° of sweep, borrowed from <see cref="FuelGauge"/> rather than invented.</b> That
    /// class explains why it is not the tacho's 240: a second wide arc beside the rev counter reads as
    /// a second rev counter. The corollary is this one — the two <i>small</i> dials should share an
    /// arc, so 120° becomes the cluster's word for "secondary readout" instead of a third thing to
    /// learn. What separates this dial from the fuel gauge is its size, its symbol, and the fact that
    /// it moves.</para>
    ///
    /// <para><b>It allocates nothing, ever.</b> There is not a number on this dial and no caption to
    /// write: a <c>Quaternion.Euler</c> into a <c>localRotation</c> and a <c>Color</c>, both structs.
    /// The prebuilt string table <see cref="InstrumentCluster"/> carries has no equivalent here,
    /// for the reason <see cref="FuelGauge"/> gives.</para>
    /// </summary>
    public sealed class BoostGauge : MonoBehaviour
    {
        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when the scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("The dial itself, switched off on a naturally aspirated car. This component lives on "
               + "the instrument group rather than on this object — see the class note.")]
        [SerializeField] private GameObject panel;

        [SerializeField] private RectTransform needle;

        [SerializeField] private RectTransform[] tickMarks = new RectTransform[0];

        [Tooltip("The compressor symbol. Lights when the turbo is making real boost, which is the half "
               + "of the reading that works without being looked at directly.")]
        [SerializeField] private Image turboGlyph;

        /// <summary>
        /// Where the needle sits at no boost and at full, as a uGUI z rotation. The fuel gauge's arc,
        /// deliberately — see the class note.
        /// </summary>
        private const float StartAngle = 60f;
        private const float EndAngle = -60f;

        /// <summary>Where the marks sit, as a fraction of the dial's half-width.</summary>
        private const float TickRadius = 0.72f;

        /// <summary>
        /// Where the compressor lights, as a fraction of what this turbo makes.
        ///
        /// <para><b>A reading, not a threshold in the simulation.</b> Nothing in the physics or the
        /// audio changes at 0.6 — the torque curve does not step and the dump valve has a gate of its
        /// own at 0.42. This number exists only to answer "is it on boost" in the same glance the
        /// needle answers "how much", and it is set high enough that part throttle through a village
        /// does not light it. A gauge that glows all the time is telling you nothing, which is the
        /// argument the tyre squeal and the turbo whistle both make about levels applied
        /// squared.</para>
        /// </summary>
        private const float HardBoost = 0.60f;

        /// <summary>
        /// The dial's colours, duplicated as literals.
        ///
        /// <para><c>TouchUiSetup</c> owns the palette and is Editor-only, so runtime code cannot read
        /// it — the same bind <see cref="FuelGauge"/> and <c>UpdateScreen.AvailableTint</c> are in, and
        /// solved the same way. These are its <c>AccentTint</c> and <c>GlyphTint</c>; keep them in step
        /// by hand.</para>
        /// </summary>
        private static readonly Color HardBoostTint = new Color(0.86f, 0.36f, 0.17f, 0.92f);
        private static readonly Color NormalTint = new Color(1f, 1f, 1f, 0.92f);

        private EngineAudio engine;
        private VehicleConfig fitted;
        private bool placed;
        private bool shownHard;

        private void Update()
        {
            if (engine == null)
            {
                // Retried rather than resolved once: this component is in Bootstrap and the car is in
                // the world scene, which is still loading for the first frames. Both live on the car's
                // root object, and swapping body in the garage rebuilds the clips rather than the
                // component — so one resolution holds for the session.
                if (vehicle == null)
                {
                    vehicle = FindFirstObjectByType<VehicleController>();
                }

                engine = vehicle != null ? vehicle.GetComponent<EngineAudio>() : null;
                if (engine == null)
                {
                    return;
                }
            }

            // Before the config is looked at, so the marks are laid out while the dial is still up.
            // The build leaves it active; the first naturally aspirated car is what puts it away.
            if (!placed)
            {
                LayOutFace();
            }

            VehicleConfig config = vehicle.Config;
            if (config == null)
            {
                return;
            }

            // Reference inequality, not a comparison of values: VehicleController.SetConfig raises no
            // event, and a different car means a different asset. The same poll InstrumentCluster uses
            // to rebuild its face.
            if (!ReferenceEquals(config, fitted))
            {
                fitted = config;

                if (panel != null)
                {
                    panel.SetActive(config.IsTurbocharged);
                }
            }

            if (!config.IsTurbocharged)
            {
                return;
            }

            // Read straight, with no smoothing of its own. EngineAudio.Boost01 is already an
            // exponential chase at this car's spool rate, and collapse is four times as fast as build
            // so that a lift reads as a lift. A second lerp here would be lag the car does not have.
            float boost = engine.Boost01;

            if (needle != null)
            {
                needle.localRotation = Quaternion.Euler(0f, 0f, AngleFor(boost));
            }

            ShowHardBoost(boost >= HardBoost);
        }

        /// <summary>
        /// Lays the marks out, once.
        ///
        /// <para>Not <c>Awake</c>, because it measures the dial's own rect and a layout that has not
        /// been through a frame yet reports nothing useful. Not per car either: this dial reads a
        /// <i>fraction</i>, so nothing on its face depends on which turbo is behind it — which is the
        /// whole of why it needs none of the machinery <see cref="InstrumentCluster"/> carries.</para>
        ///
        /// <para><b>Public for <c>HudPreviewRenderer</c></b>, for the reason set out on
        /// <see cref="FuelGauge.LayOutFace"/>: without it the marks photograph stacked at the centre of
        /// the dial, under the needle's hub, and the one picture this project takes of its own HUD
        /// cannot say whether the face is right.</para>
        /// </summary>
        public void LayOutFace()
        {
            placed = true;

            // The panel's rect, not this component's: this lives on the instrument group, which is
            // stretched across the whole screen.
            var rect = panel != null ? (RectTransform)panel.transform : (RectTransform)transform;
            float radius = Mathf.Min(rect.rect.width, rect.rect.height) * 0.5f;

            for (int i = 0; i < tickMarks.Length; i++)
            {
                if (tickMarks[i] == null)
                {
                    continue;
                }

                float angle = AngleFor(tickMarks.Length > 1 ? i / (float)(tickMarks.Length - 1) : 0f);

                // In uGUI a positive z rotation turns counter-clockwise, which sends "up" to
                // (-sin, cos) — the same derivation both of the other dials' marks use.
                var direction = new Vector2(
                    -Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad));

                tickMarks[i].anchoredPosition = direction * (radius * TickRadius);
                tickMarks[i].localRotation = Quaternion.Euler(0f, 0f, angle);
            }
        }

        private static float AngleFor(float fraction)
        {
            return Mathf.Lerp(StartAngle, EndAngle, Mathf.Clamp01(fraction));
        }

        /// <summary>
        /// Lights the compressor once there is real boost behind it.
        ///
        /// <para>Gated on the state having changed, for the reason <c>FuelGauge.ShowReserve</c> gives:
        /// an <c>Image.color</c> write dirties the canvas and forces a rebuild of the batch it is in,
        /// and doing that every frame to set the same colour is a cost paid for nothing. This is that
        /// method with the sense inverted — the pump reddens as the tank empties, this brightens as
        /// the plenum fills.</para>
        /// </summary>
        private void ShowHardBoost(bool hard)
        {
            if (hard == shownHard || turboGlyph == null)
            {
                return;
            }

            shownHard = hard;
            turboGlyph.color = hard ? HardBoostTint : NormalTint;
        }
    }
}
