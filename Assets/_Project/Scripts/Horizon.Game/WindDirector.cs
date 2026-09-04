using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The world's one wind, published to every shader that moves.
    ///
    /// <para><b>One vector, one writer.</b> Nothing in this world moved on its own before this: the sun,
    /// the traffic, four material swaps a day and two particle systems were the entire list, and a car
    /// parked at noon in clear weather sat in a completely still picture. The moment more than one thing
    /// sways, they have to agree — trees leaning north-east while a lake ripples south is two weathers in
    /// one frame, and it is the exact failure this project keeps writing down about second opinions.</para>
    ///
    /// <para>Pushed as a global shader vector rather than read per material, because the vegetation is
    /// merged into terrain tiles by the thousand and shares one material with the ground it stands on.
    /// There is nowhere per-object to put it, and a MaterialPropertyBlock would break the SRP batcher
    /// across every tile in the world.</para>
    ///
    /// <para><b>It is a plain component and not part of the weather.</b> Rain would be the obvious place
    /// to drive gusts from, and that is a change to make on purpose: <c>WeatherDirector</c> already owns
    /// four consumers and its own remarks are about why one owner matters. Until wind is a thing the
    /// player chooses, this is a constant and says so.</para>
    /// </summary>
    [ExecuteAlways]
    public sealed class WindDirector : MonoBehaviour
    {
        [Tooltip("Which way the wind blows, in world XZ. Normalised on use, so only the direction here "
               + "matters.")]
        [SerializeField] private Vector3 direction = new Vector3(0.82f, 0f, 0.57f);

        [Tooltip("How far the most flexible part of a plant is pushed, metres.\n\n"
               + "Small, and it has to be. These are flat-shaded low-poly trees with a handful of facets "
               + "in a canopy: past about a quarter of a metre the silhouette visibly shears and a "
               + "spruce reads as rubber rather than as timber.")]
        [SerializeField] private float strength = 0.18f;

        [Tooltip("How fast the gusts run, in radians per second of the base oscillation.\n\n"
               + "Slow on purpose. The reference for this world is a warm, unhurried afternoon, and "
               + "foliage that hurries reads as a storm — which is a weather this game does not have.")]
        [SerializeField] private float speed = 1.15f;

        private static readonly int WindId = Shader.PropertyToID("_HorizonWind");

        private void OnEnable()
        {
            Push();
        }

        private void Update()
        {
            // Every frame rather than once, because a domain reload, a scene load or a shader recompile
            // all drop global vectors and none of them raise anything to hook. It is one Vector4.
            Push();
        }

        private void OnDisable()
        {
            // Hand the world back still. A disabled director that left the last gust standing would keep
            // every tree bent, which reads as a broken shader rather than as no wind.
            Shader.SetGlobalVector(WindId, Vector4.zero);
        }

        private void Push()
        {
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
            flat *= strength;

            Shader.SetGlobalVector(WindId, new Vector4(flat.x, flat.y, flat.z, speed));
        }
    }
}
