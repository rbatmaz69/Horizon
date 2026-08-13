using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Ticks the world streamer from the camera's position.
    ///
    /// Deliberately a few times a second rather than every frame. Chunk distances change by metres
    /// between frames while the thresholds are hundreds of metres apart, so per-frame updates would be
    /// pure overhead — and the streaming interface says as much.
    ///
    /// Lives in the world scene alongside the chunks it manages, so a second zone added later brings
    /// its own rather than needing this one to be told about it.
    /// </summary>
    public sealed class WorldStreamingDriver : MonoBehaviour
    {
        [SerializeField] private WorldStreamer streamer;

        [Tooltip("Streaming updates per second.")]
        [SerializeField] private float updatesPerSecond = 4f;

        private Transform viewer;
        private float countdown;

        private void Start()
        {
            if (streamer == null)
            {
                streamer = GetComponent<WorldStreamer>();
            }

            if (streamer != null)
            {
                streamer.RegisterExisting();
            }

            // Ask the quality setting for its radii *before* the immediate pass below, not after.
            //
            // The director lives on the Bootstrap object and would otherwise reach this streamer from
            // GameBootstrap's resumed coroutine, which is not ordered against this Start. Losing that
            // race means the first pass runs at High radii on a phone set to Low — every chunk in the
            // world brought in and then dropped again a quarter of a second later, which is the one
            // moment the setting exists to prevent.
            QualityDirector quality = FindFirstObjectByType<QualityDirector>();
            if (quality != null)
            {
                quality.ApplyToWorld();
            }

            // Run once immediately, or the first few frames render every chunk in the world.
            ResolveViewer();
            if (streamer != null && viewer != null)
            {
                streamer.UpdateStreaming(viewer.position);
            }
        }

        private void Update()
        {
            if (streamer == null)
            {
                return;
            }

            countdown -= Time.deltaTime;
            if (countdown > 0f)
            {
                return;
            }

            countdown = 1f / Mathf.Max(0.5f, updatesPerSecond);

            ResolveViewer();
            if (viewer != null)
            {
                streamer.UpdateStreaming(viewer.position);
            }
        }

        private void ResolveViewer()
        {
            if (viewer != null)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = FindFirstObjectByType<Camera>();
            }

            if (camera != null)
            {
                viewer = camera.transform;
            }
        }
    }
}
