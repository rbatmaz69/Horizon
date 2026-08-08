using UnityEngine;

namespace Horizon.Atmosphere
{
    /// <summary>
    /// The look of the sky over a full day, keyed on normalized time (0 = midnight, 0.5 = noon).
    /// Cheap to evaluate and it carries most of the mood — the sunset does more for the game's
    /// atmosphere than any mesh will.
    /// </summary>
    [CreateAssetMenu(menuName = "Horizon/Time Of Day Profile", fileName = "TimeOfDayProfile")]
    public sealed class TimeOfDayProfile : ScriptableObject
    {
        [Header("Sun")]
        [Tooltip("Directional light colour over the day.")]
        [GradientUsage(true)] public Gradient SunColor = new Gradient();

        [Tooltip("Directional light intensity over the day.")]
        public AnimationCurve SunIntensity = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.24f, 0f),
            new Keyframe(0.30f, 0.9f),
            new Keyframe(0.5f, 1.15f),
            new Keyframe(0.72f, 0.85f),
            new Keyframe(0.78f, 0f),
            new Keyframe(1f, 0f));

        [Tooltip("Compass direction the sun travels along, degrees.")]
        [Range(0f, 360f)] public float SunAzimuth = 150f;

        [Header("Ambient")]
        [GradientUsage(true)] public Gradient AmbientColor = new Gradient();

        [Header("Fog")]
        [Tooltip("Fog colour over the day. Doubles as the colour the draw distance dissolves into.")]
        public Gradient FogColor = new Gradient();

        [Tooltip("Exponential-squared fog density over the day. Tuned against a 600 m far plane: "
               + "roughly 700 m visibility at noon so the vistas survive, thicker at dawn and dusk "
               + "for mood. Raising these past ~0.006 closes the view down to a few hundred metres.")]
        public AnimationCurve FogDensity = new AnimationCurve(
            new Keyframe(0f, 0.0035f),
            new Keyframe(0.25f, 0.0050f),
            new Keyframe(0.5f, 0.0022f),
            new Keyframe(0.75f, 0.0042f),
            new Keyframe(1f, 0.0035f));

        [Header("Night")]
        [Tooltip("Ambient floor at night, so the world never goes fully black.")]
        [ColorUsage(false, true)] public Color NightAmbientFloor = new Color(0.06f, 0.08f, 0.13f);

        /// <summary>Sun elevation in degrees for a given hour. 0 at sunrise, 90 at noon.</summary>
        public static float SunElevation(float hours)
        {
            return (hours - 6f) / 12f * 180f;
        }
    }
}
