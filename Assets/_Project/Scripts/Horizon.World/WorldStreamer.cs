using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Switches world chunks on and off around a viewer.
    ///
    /// Two details are load-bearing:
    ///
    /// **Hysteresis.** A chunk comes in at <see cref="LoadRadius"/> and only goes out again at
    /// <see cref="UnloadRadius"/>. With a single threshold, a car sitting near the boundary toggles the
    /// same chunk every update.
    ///
    /// **Colliders lead renderers.** Enabling a MeshCollider makes Unity cook it, and a cook mid-corner
    /// is a visible hitch. Physics is therefore switched on over a wider radius than rendering, so the
    /// ground is always solid well before it is visible.
    /// </summary>
    public sealed class WorldStreamer : MonoBehaviour, IWorldStreamer
    {
        [Tooltip("Chunks closer than this are rendered. Should sit inside the fog wall so nothing pops "
               + "into view in clear air.")]
        [SerializeField] private float loadRadius = 650f;

        [Tooltip("Chunks are only dropped once they are this far away. The gap is the hysteresis.")]
        [SerializeField] private float unloadRadius = 820f;

        [Tooltip("Extra radius over which colliders stay enabled, so cooking never happens in view.")]
        [SerializeField] private float physicsMargin = 220f;

        private readonly List<WorldChunk> chunks = new List<WorldChunk>(128);

        public float LoadRadius => loadRadius;

        /// <summary>Number of chunks currently rendered. For the debug overlay.</summary>
        public int LoadedCount { get; private set; }

        /// <summary>Total chunks known to the streamer.</summary>
        public int ChunkCount => chunks.Count;

        public void Register(WorldChunk chunk)
        {
            if (chunk != null && !chunks.Contains(chunk))
            {
                chunks.Add(chunk);
            }
        }

        public void Unregister(WorldChunk chunk)
        {
            if (chunks.Remove(chunk) && chunk != null)
            {
                chunk.SetLoaded(false);
            }
        }

        /// <summary>Finds every chunk already in the scene. Called by the scene wiring on startup.</summary>
        public void RegisterExisting()
        {
            WorldChunk[] found = Object.FindObjectsByType<WorldChunk>(FindObjectsSortMode.None);
            for (int i = 0; i < found.Length; i++)
            {
                Register(found[i]);
            }
        }

        public void UpdateStreaming(Vector3 viewerPosition)
        {
            int loaded = 0;

            for (int i = 0; i < chunks.Count; i++)
            {
                WorldChunk chunk = chunks[i];
                if (chunk == null)
                {
                    continue;
                }

                float distance = chunk.DistanceTo(viewerPosition);
                bool visible = chunk.IsLoaded
                    ? distance < unloadRadius
                    : distance < loadRadius;

                chunk.SetLoaded(visible);
                chunk.SetCollisionEnabled(distance < unloadRadius + physicsMargin);

                if (visible)
                {
                    loaded++;
                }
            }

            LoadedCount = loaded;
        }
    }
}
