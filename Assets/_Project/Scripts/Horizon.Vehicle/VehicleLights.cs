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

        [Tooltip("Shared cover probe. Found automatically if left empty.")]
        [SerializeField] private VehicleCover cover;

        [Header("Emission")]
        [SerializeField] private Color headlightColor = new Color(1f, 0.96f, 0.84f);
        [SerializeField] private Color taillightColor = new Color(1f, 0.10f, 0.06f);

        [Tooltip("Tail light glow while coasting.")]
        [SerializeField] private float taillightIdleGlow = 0.5f;

        [Tooltip("Tail light glow at full brake.")]
        [SerializeField] private float taillightBrakeGlow = 3.2f;

        [Tooltip("Headlight panel glow once lit.")]
        [SerializeField] private float headlightGlow = 2.4f;

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private MaterialPropertyBlock headlightBlock;
        private MaterialPropertyBlock taillightBlock;
        private float smoothedBrake;

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

            // Ease the brake glow so tapping the brake does not strobe.
            float brake = Mathf.Clamp01(DriveInput.Current.Brake);
            smoothedBrake = Mathf.MoveTowards(smoothedBrake, brake, 6f * Time.deltaTime);

            float tailGlow = Mathf.Lerp(taillightIdleGlow, taillightBrakeGlow, smoothedBrake);
            taillightBlock.SetColor(EmissionColorId, taillightColor * tailGlow);
            bodyRenderer.SetPropertyBlock(taillightBlock, taillightMaterialIndex);

            float headGlow = HeadlightsOn ? headlightGlow : 0.12f;
            headlightBlock.SetColor(EmissionColorId, headlightColor * headGlow);
            bodyRenderer.SetPropertyBlock(headlightBlock, headlightMaterialIndex);
        }

        private bool IsDark()
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
            {
                return false;
            }

            return !sun.enabled || sun.intensity < nightSunIntensity;
        }

    }
}
