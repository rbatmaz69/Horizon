using Horizon.Input;
using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Switches the headlights on when it gets dark and brightens the tail lights under braking.
    ///
    /// Reads the time of day from <see cref="RenderSettings.sun"/> rather than from
    /// <c>Horizon.Atmosphere</c>. The sun intensity is already the thing that decides whether it looks
    /// like night, and going through RenderSettings keeps this module free of a dependency that would
    /// point the wrong way up the assembly list.
    /// </summary>
    public sealed class VehicleLights : MonoBehaviour
    {
        [Tooltip("Realtime headlight beams. Kept to two — additional realtime lights are the most "
               + "expensive thing on this car for a mobile GPU.")]
        [SerializeField] private Light[] headlights = new Light[0];

        [Tooltip("Renderer carrying the body mesh, so the light submeshes can be made to glow.")]
        [SerializeField] private Renderer bodyRenderer;

        [Tooltip("Submesh index of the headlight panels on the body mesh.")]
        [SerializeField] private int headlightMaterialIndex = 2;

        [Tooltip("Submesh index of the tail light panels.")]
        [SerializeField] private int taillightMaterialIndex = 3;

        [Header("Night detection")]
        [Tooltip("Sun intensity below which the headlights come on.")]
        [SerializeField] private float nightSunIntensity = 0.35f;

        [Tooltip("Sun intensity the lights stay on until. The gap to nightSunIntensity is hysteresis — "
               + "without it, dusk sits on the threshold and the lamps flicker.")]
        [SerializeField] private float dawnSunIntensity = 0.45f;

        [Tooltip("Shared cover probe. Found automatically if left empty.")]
        [SerializeField] private VehicleCover cover;

        [Tooltip("The controller, for telling braking apart from reversing. Found automatically.")]
        [SerializeField] private VehicleController controller;

        [Header("Lamp brightness")]
        [SerializeField] private Color headlightColor = new Color(1f, 0.96f, 0.84f);
        [SerializeField] private Color taillightColor = new Color(1f, 0.10f, 0.06f);

        [Tooltip("Tail light glow while coasting.")]
        [SerializeField] private float taillightIdleGlow = 0.5f;

        [Tooltip("Tail light glow at full brake.")]
        [SerializeField] private float taillightBrakeGlow = 3.2f;

        [Tooltip("Headlight lens brightness once lit.")]
        [SerializeField] private float headlightGlow = 2.4f;

        [Tooltip("Headlight lens brightness while off — dark glass, not black.")]
        [SerializeField] private float headlightOffGlow = 0.35f;

        /// <summary>
        /// The lamp lenses are unlit materials, so their brightness *is* their base colour — driven
        /// above 1 it reads as a lit lamp and blooms. This used to write _EmissionColor into URP Lit
        /// materials, which silently did nothing because the _EMISSION keyword would not stay on the
        /// asset. Unlit needs no keyword, so there is nothing left to go wrong.
        /// </summary>
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private MaterialPropertyBlock headlightBlock;
        private MaterialPropertyBlock taillightBlock;
        private float smoothedBrake;
        private float headlightAmount;

        /// <summary>True while the headlights are lit.</summary>
        public bool HeadlightsOn { get; private set; }

        private void Awake()
        {
            headlightBlock = new MaterialPropertyBlock();
            taillightBlock = new MaterialPropertyBlock();

            if (cover == null)
            {
                cover = GetComponentInParent<VehicleCover>();
            }

            if (controller == null)
            {
                controller = GetComponentInParent<VehicleController>();
            }
        }

        private void Update()
        {
            HeadlightsOn = IsDark() || (cover != null && cover.IsCovered);

            for (int i = 0; i < headlights.Length; i++)
            {
                if (headlights[i] != null && headlights[i].enabled != HeadlightsOn)
                {
                    headlights[i].enabled = HeadlightsOn;
                }
            }

            if (bodyRenderer == null)
            {
                return;
            }

            // The lenses follow the eased cover amount rather than the beams' on/off, so driving into a
            // bore brings them up over the portal instead of snapping at one frame. EngineAudio already
            // reads the same eased value for its reverb, from the same probe.
            float lit = HeadlightsOn ? 1f : 0f;
            if (cover != null)
            {
                lit = Mathf.Max(lit, cover.CoverAmount);
            }

            headlightAmount = Mathf.MoveTowards(headlightAmount, lit, 3f * Time.deltaTime);

            // The brake pedal doubles as reverse below walking pace, and the controller is the only
            // thing that knows which of the two is happening. Reading the raw pedal lit the brake lamps
            // at full glow while reversing out of a parking spot.
            float brake = controller != null
                ? controller.BrakeInput
                : Mathf.Clamp01(DriveInput.Current.Brake);

            // Ease the brake glow so tapping the brake does not strobe.
            smoothedBrake = Mathf.MoveTowards(smoothedBrake, Mathf.Clamp01(brake), 6f * Time.deltaTime);

            float tailGlow = Mathf.Lerp(taillightIdleGlow, taillightBrakeGlow, smoothedBrake);
            taillightBlock.SetColor(BaseColorId, taillightColor * tailGlow);
            bodyRenderer.SetPropertyBlock(taillightBlock, taillightMaterialIndex);

            float headGlow = Mathf.Lerp(headlightOffGlow, headlightGlow, headlightAmount);
            headlightBlock.SetColor(BaseColorId, headlightColor * headGlow);
            bodyRenderer.SetPropertyBlock(headlightBlock, headlightMaterialIndex);
        }

        /// <summary>
        /// Night from the sun's own intensity rather than from the clock, with hysteresis: the lights
        /// come on below <see cref="nightSunIntensity"/> and only go off again above
        /// <see cref="dawnSunIntensity"/>. A single threshold sits right in the middle of dusk, where
        /// the sun is changing slowly, and the lamps chatter across it for minutes.
        /// </summary>
        private bool IsDark()
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
            {
                return false;
            }

            if (!sun.enabled)
            {
                return true;
            }

            float threshold = HeadlightsOn ? dawnSunIntensity : nightSunIntensity;
            return sun.intensity < threshold;
        }

    }
}
