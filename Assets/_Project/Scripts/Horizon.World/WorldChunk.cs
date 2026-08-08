using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// One loadable piece of the world, and the unit the streamer works in.
    ///
    /// Rendering and collision are switched **separately**, not by activating the whole object.
    /// Enabling a MeshCollider makes Unity cook it, which is a visible hitch if it happens while
    /// driving, so <see cref="WorldStreamer"/> keeps physics alive over a wider radius than graphics.
    /// Toggling the GameObject would make that impossible.
    /// </summary>
    public sealed class WorldChunk : MonoBehaviour
    {
        [Tooltip("Chunk centre in world space, used for distance tests. Auto-filled from bounds.")]
        [SerializeField] private Vector3 center;

        [Tooltip("Radius covering the chunk's contents, metres.")]
        [SerializeField] private float radius = 250f;

        private Renderer[] renderers;
        private Collider[] colliders;

        /// <summary>Chunk centre in world space.</summary>
        public Vector3 Center => center;

        /// <summary>Bounding radius in metres.</summary>
        public float Radius => radius;

        /// <summary>Whether the chunk is currently being drawn.</summary>
        public bool IsLoaded { get; private set; } = true;

        /// <summary>Whether the chunk's colliders are live.</summary>
        public bool IsCollidable { get; private set; } = true;

        private void Awake()
        {
            CacheComponents();
        }

        private void CacheComponents()
        {
            if (renderers == null)
            {
                renderers = GetComponentsInChildren<Renderer>(true);
            }

            if (colliders == null)
            {
                colliders = GetComponentsInChildren<Collider>(true);
            }
        }

        /// <summary>Shows or hides the chunk's geometry.</summary>
        public void SetLoaded(bool loaded)
        {
            if (IsLoaded == loaded && renderers != null)
            {
                return;
            }

            CacheComponents();
            IsLoaded = loaded;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = loaded;
                }
            }
        }

        /// <summary>Enables or disables the chunk's colliders.</summary>
        public void SetCollisionEnabled(bool enabled)
        {
            if (IsCollidable == enabled && colliders != null)
            {
                return;
            }

            CacheComponents();
            IsCollidable = enabled;

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = enabled;
                }
            }
        }

        /// <summary>Recomputes centre and radius from the renderers underneath. Editor-time helper.</summary>
        public void RecalculateBounds()
        {
            Renderer[] found = GetComponentsInChildren<Renderer>(true);
            if (found.Length == 0)
            {
                center = transform.position;
                return;
            }

            Bounds bounds = found[0].bounds;
            for (int i = 1; i < found.Length; i++)
            {
                bounds.Encapsulate(found[i].bounds);
            }

            center = bounds.center;
            radius = bounds.extents.magnitude;
        }

        /// <summary>Sets the bounds directly, for chunks whose extent is known before they are filled.</summary>
        public void SetBounds(Vector3 worldCenter, float worldRadius)
        {
            center = worldCenter;
            radius = worldRadius;
        }

        /// <summary>Distance from a point to the chunk's shell. Zero when inside.</summary>
        public float DistanceTo(Vector3 point)
        {
            return Mathf.Max(0f, Vector3.Distance(point, center) - radius);
        }
    }
}
