using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// One loadable piece of the world. The prototype has a single chunk that is simply always on —
    /// this type exists now so the streaming boundary is decided before the world grows, and adding
    /// zones later does not mean restructuring scenes.
    ///
    /// A chunk is responsible for nothing except knowing where it is and being cheap to switch off.
    /// </summary>
    public sealed class WorldChunk : MonoBehaviour
    {
        [Tooltip("Chunk centre in world space, used for distance tests. Auto-filled from bounds.")]
        [SerializeField] private Vector3 center;

        [Tooltip("Radius covering the chunk's contents, metres.")]
        [SerializeField] private float radius = 250f;

        [Tooltip("Root that gets switched off when the chunk unloads. Defaults to this object.")]
        [SerializeField] private GameObject content;

        /// <summary>Chunk centre in world space.</summary>
        public Vector3 Center => center;

        /// <summary>Bounding radius in metres.</summary>
        public float Radius => radius;

        /// <summary>Whether the chunk's content is currently active.</summary>
        public bool IsLoaded => content != null && content.activeSelf;

        private void Awake()
        {
            if (content == null)
            {
                content = gameObject;
            }
        }

        /// <summary>Activates or deactivates the chunk's content.</summary>
        public void SetLoaded(bool loaded)
        {
            if (content == null)
            {
                content = gameObject;
            }

            if (content.activeSelf != loaded)
            {
                content.SetActive(loaded);
            }
        }

        /// <summary>Recomputes centre and radius from the renderers underneath. Editor-time helper.</summary>
        public void RecalculateBounds()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                center = transform.position;
                return;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            center = bounds.center;
            radius = bounds.extents.magnitude;
        }

        /// <summary>Distance from a point to the chunk's shell. Zero when inside.</summary>
        public float DistanceTo(Vector3 point)
        {
            return Mathf.Max(0f, Vector3.Distance(point, center) - radius);
        }
    }
}
