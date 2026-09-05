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

        /// <summary>
        /// Where a chunk finally goes dark. The class remarks above have referred to this by name
        /// since they were written; it had never actually been exposed.
        ///
        /// <para>Read by anything that has to agree with the streamer about how much world exists —
        /// <c>RemoteCarPool</c> hides another player's car past it, because a car drawn over ground
        /// that has not been built is worse than a car that is not drawn.</para>
        /// </summary>
        public float UnloadRadius => unloadRadius;

        /// <summary>
        /// Sets how much world is kept up.
        ///
        /// <para>The first lever to pull for a weak phone, and the cheapest: a chunk that is not
        /// rendered costs nothing at all, and pulling the radius in from 650 to 380 roughly halves the
        /// drawn area. It is bounded below by the fog, not by taste — draw less than the fog hides and
        /// the world visibly ends, which is worse than a low frame rate.</para>
        ///
        /// <para>Taken together rather than as three properties, because the ordering is a rule and not
        /// a preference. <paramref name="unload"/> below <paramref name="load"/> removes the hysteresis
        /// the class exists to provide, and a chunk sitting on the boundary would then toggle every
        /// frame — see the note at the top of this file.</para>
        /// </summary>
        public void SetRadii(float load, float unload, float margin)
        {
            loadRadius = Mathf.Max(100f, load);
            unloadRadius = Mathf.Max(loadRadius * 1.15f, unload);
            physicsMargin = Mathf.Max(0f, margin);
        }

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
