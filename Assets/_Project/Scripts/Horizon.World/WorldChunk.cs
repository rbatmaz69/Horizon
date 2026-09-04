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

            bool any = false;
            Bounds bounds = default;

            for (int i = 0; i < found.Length; i++)
            {
                if (!TryWorldBounds(found[i], out Bounds one))
                {
                    continue;
                }

                if (!any)
                {
                    bounds = one;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(one);
                }
            }

            if (!any)
            {
                center = transform.position;
                return;
            }

            center = bounds.center;
            radius = bounds.extents.magnitude;
        }

        /// <summary>
        /// One renderer's world bounds, taken from the mesh it draws rather than from
        /// <see cref="Renderer.bounds"/>.
        ///
        /// <para><b>Because <c>Renderer.bounds</c> is a cached value and it is not always fresh when an
        /// editor script asks.</b> This ran on a mesh assigned moments earlier in the same call, and it
        /// was correct only because the build's asset path happened to force the mesh through the
        /// import pipeline first. Taking that round trip out — it was ninety per cent of the rebuild —
        /// left a minority of chunks reading a stale bounds: the median chunk radius stayed at 123 m
        /// while the mean went from 124 to 408 and the largest from 525 m to 6.4 km, and the draw-call
        /// budget went from 100 chunks resident to 243 because a chunk that thinks it is kilometres
        /// wide is never out of range.</para>
        ///
        /// <para>The mesh's own bounds are serialised with it and cannot be stale, so the eight corners
        /// transformed by this renderer's transform is the answer that does not depend on when it is
        /// asked. It is also what <c>Renderer.bounds</c> is supposed to be. Anything without a
        /// <c>MeshFilter</c> — a particle system, a trail — falls back to the cached value, which is the
        /// only thing there is for it.</para>
        /// </summary>
        private static bool TryWorldBounds(Renderer renderer, out Bounds bounds)
        {
            bounds = default;

            if (renderer == null)
            {
                return false;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;

            if (mesh == null)
            {
                bounds = renderer.bounds;
                return true;
            }

            Bounds local = mesh.bounds;
            Vector3 centre = local.center;
            Vector3 extents = local.extents;
            Transform to = renderer.transform;

            bounds = new Bounds(to.TransformPoint(centre), Vector3.zero);

            for (int corner = 1; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);

                bounds.Encapsulate(to.TransformPoint(centre + offset));
            }

            // The first corner as well, which the loop skipped so the bounds had something to start
            // from. Written out rather than folded into the loop because a Bounds seeded at the centre
            // and then given seven of eight corners is a bounds that is quietly one corner short.
            bounds.Encapsulate(to.TransformPoint(centre - extents));

            return true;
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
