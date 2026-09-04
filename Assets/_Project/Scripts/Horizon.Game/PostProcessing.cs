using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.Game
{
    /// <summary>
    /// Owns the post stack's one adjustable number: how much bloom there is.
    ///
    /// <para><b>Why the bloom is a second Volume rather than a switch on one profile.</b> The obvious
    /// spelling is <c>profile.TryGet&lt;Bloom&gt;(out var b); b.active = level.Bloom;</c> — and it is the
    /// trap <see cref="QualityDirector"/>'s own remarks describe, one asset type along. A
    /// <c>VolumeProfile</c> is an asset, Unity does not roll asset edits back when Play mode ends, and a
    /// player who tried Low once would leave the profile modified in their working tree. That is the
    /// same hazard <c>TownLights</c> documents for materials and <c>WetSurfaces</c> for the road.
    /// <c>QualityDirector</c> states the way out in one line — <i>"Everything here is therefore a runtime
    /// value on a component"</i> — and <see cref="Volume.weight"/> is exactly that. The profile is never
    /// written to; the volume blending it in is turned down.</para>
    ///
    /// <para><b>Why this exists at all rather than QualityDirector finding the Volume itself.</b> There
    /// are two global volumes in the world scene and nothing about a <see cref="Volume"/> says which is
    /// which, so a <c>FindFirstObjectByType&lt;Volume&gt;</c> would turn the tone map off half the time
    /// and the bloom off the other half, depending on scene order. This is the same shape as
    /// <c>WeatherDirector.DropScale</c>: one component owns the world-side effect, the quality director
    /// pushes a number at it, and there is exactly one writer.</para>
    ///
    /// <para>Lives on the Atmosphere object beside <c>SpeedAtmosphere</c> and <c>WeatherDirector</c>,
    /// because a tone map is atmosphere and not a property of the camera looking through it.</para>
    /// </summary>
    public sealed class PostProcessing : MonoBehaviour
    {
        [Tooltip("The volume carrying bloom, and nothing else. Its weight is the only thing written "
               + "here — never its profile, which is an asset.")]
        [SerializeField] private Volume bloomVolume;

        private float bloomAmount = 1f;

        /// <summary>
        /// How much of the bloom volume is blended in, 0 to 1. Written by <see cref="QualityDirector"/>.
        ///
        /// <para>A weight rather than a bool, because the two are the same cost and one of them can be
        /// eased. Nothing eases it today.</para>
        /// </summary>
        public float BloomAmount
        {
            get => bloomAmount;
            set
            {
                bloomAmount = Mathf.Clamp01(value);
                Push();
            }
        }

        private void OnEnable()
        {
            Push();
        }

        private void Push()
        {
            if (bloomVolume != null)
            {
                bloomVolume.weight = bloomAmount;
            }
        }
    }
}
