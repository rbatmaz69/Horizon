using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Turns a transform about its own forward axis, at a rate the world's wind sets.
    ///
    /// <para><b>Built for the windmill, which was the one thing in this world that was obviously
    /// broken.</b> Everything else that does not move can be read as still; a mill whose sails are
    /// welded in place is a tower with a cross on it. `MillMeshes` has carried a note saying the sails
    /// would need a transform of their own since the day the wind arrived.</para>
    ///
    /// <para><b>It reads the wind through the shader global rather than through the director.</b>
    /// <c>WindDirector</c> lives in <c>Horizon.Game</c> and this assembly may not see it — but that
    /// class already publishes the one wind as <c>_HorizonWind</c>, and a global is readable from
    /// anywhere. That is the same seam the sky's drift uses, and it is what keeps this from being a
    /// second opinion about the weather: a mill turning while the trees are still is two weathers in
    /// one frame, which is the failure <c>WindDirector</c>'s own remarks are about.</para>
    ///
    /// <para><b>Unscaled time is deliberately not used.</b> A paused game should have still sails —
    /// this is part of the world rather than part of the interface, and the photo mode's whole purpose
    /// is a frame that holds.</para>
    /// </summary>
    public sealed class Spinner : MonoBehaviour
    {
        [Tooltip("Degrees a second at the wind's full strength. A tower mill turns slowly: about "
               + "twelve revolutions a minute is a working sail, and faster reads as a fairground ride.")]
        [SerializeField] private float degreesPerSecond = 72f;

        [Tooltip("What the sails do when the air is perfectly still. Not zero: a mill stopped dead "
               + "looks broken rather than becalmed, and no weather in this world takes the wind to "
               + "nothing anyway.")]
        [SerializeField] private float idleFraction = 0.25f;

        private static readonly int WindId = Shader.PropertyToID("_HorizonWind");

        private float turned;

        private void Update()
        {
            // xyz is the direction scaled by strength, which is what makes the length the strength —
            // the same packing the vegetation shader reads it with.
            Vector4 wind = Shader.GetGlobalVector(WindId);
            float strength = new Vector3(wind.x, wind.y, wind.z).magnitude;

            turned += degreesPerSecond * Mathf.Max(idleFraction, strength) * Time.deltaTime;

            if (turned >= 360f)
            {
                // Wrapped rather than left to grow, because a float angle that has been accumulating
                // for an hour has lost the precision to turn smoothly — and this is a thing somebody
                // may park in front of and photograph.
                turned -= 360f;
            }

            transform.localRotation = Quaternion.Euler(0f, 0f, turned);
        }
    }
}
