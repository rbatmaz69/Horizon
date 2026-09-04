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

        [Header("Sky")]
        [Tooltip("Colour overhead over the day.\n\n"
               + "The horizon is deliberately not here: it is FogColor. A skybox is not fogged, so the "
               + "sky's horizon and the colour distant terrain dissolves into have to be the same "
               + "colour or there is a seam under every ridge in the world. Reusing it also means the "
               + "sky desaturates with the weather through a term the controller already computes, "
               + "rather than through a second opinion about it.\n\n"
               + "The zenith cannot be derived from the fog the same way. At dusk the fog is a warm "
               + "gold and the sky overhead is a deep violet, and no darken-and-shift produces violet "
               + "from gold — a formula that tried would be this file forming its own opinion about "
               + "colour. So it is authored, beside the three gradients that already are.\n\n"
               + "Every key is kept under 0.5 linear. The bloom knee opens at 0.63, and the sun's disc "
               + "is the only thing in that shader meant to reach it.")]
        [GradientUsage(true)] public Gradient SkyZenith = new Gradient();

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

        [Tooltip("What the ground bounces back up, for the Trilight ambient. Colour only \u2014 the level "
               + "comes from AmbientColor.\n\n"
               + "One colour and not a gradient, and that is the argument rather than a saving: the "
               + "world's albedo does not change with the hour. The light falling on it does, and that "
               + "is AmbientColor's job already. A second gradient here would be a second opinion about "
               + "the time of day.\n\n"
               + "Warm and dark, because it is the average of what this world is made of \u2014 grass, "
               + "rock, sand and red earth. Under it sits the reason the field exists at all: with a "
               + "flat ambient, an overhang and a hilltop are lit identically, which is why a rock face "
               + "here reads as one slab.")]
        [ColorUsage(false)] public Color GroundBounce = new Color(0.30f, 0.27f, 0.21f);

        [Header("Night")]
        [Tooltip("Ambient floor at night, so the world never goes fully black.")]
        [ColorUsage(false, true)] public Color NightAmbientFloor = new Color(0.06f, 0.08f, 0.13f);

        /// <summary>
        /// What this asset's fields were last written for.
        ///
        /// <para><b>The profile is created once and then left alone on purpose</b> — the whole point of
        /// a <c>ScriptableObject</c> here is that a hand-tuned gradient survives a rebuild. Which means
        /// a field added later arrives empty on every existing checkout, and an empty gradient is black:
        /// the sky would still dim, through its horizon, and still be wrong overhead. Half-working is
        /// worse than not working, because nothing reports it.</para>
        ///
        /// <para>So the asset carries what it was built for, and <c>PrototypeSetup</c> refills the
        /// fields a bump introduced. Same mechanism <c>VehicleConfigReset</c> exists for, and for the
        /// same reason: a button nobody presses is not a guard.</para>
        /// </summary>
        public const int CurrentVersion = 1;

        [HideInInspector] public int Version;

        /// <summary>Sun elevation in degrees for a given hour. 0 at sunrise, 90 at noon.</summary>
        public static float SunElevation(float hours)
        {
            return (hours - 6f) / 12f * 180f;
        }
    }
}
