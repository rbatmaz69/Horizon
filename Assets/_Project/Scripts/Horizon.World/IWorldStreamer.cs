using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Decides which chunks are live around a viewer. Defined now, implemented when the world
    /// outgrows one chunk — the point is that callers depend on this shape from day one, so swapping
    /// in an Addressables-backed streamer later is a drop-in change.
    ///
    /// Whatever implements this must never load synchronously on the main thread during play: a
    /// hitch while driving is exactly the thing this game cannot afford.
    /// </summary>
    public interface IWorldStreamer
    {
        /// <summary>Distance at which chunks come in, metres. Should sit inside the fog wall.</summary>
        float LoadRadius { get; }

        /// <summary>Registers a chunk as streamable.</summary>
        void Register(WorldChunk chunk);

        /// <summary>Removes a chunk from consideration and unloads it.</summary>
        void Unregister(WorldChunk chunk);

        /// <summary>Updates chunk states for a viewer position. Called at a low rate, not per frame.</summary>
        void UpdateStreaming(Vector3 viewerPosition);
    }
}
