using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Keeps the ring of distant ridges around whichever camera is looking.
    ///
    /// <para>It follows the rig's position and never its rotation. A backdrop parented to the camera
    /// would turn with it, which pins the silhouette to the view — drive round a hairpin and the same
    /// mountain is in front of you at both ends of it.</para>
    ///
    /// <para><b>The vertical follow is deliberately incomplete, and that is the one interesting number
    /// here.</b> Followed exactly in Y the rings behave as a skybox does: correct, and completely inert.
    /// Not followed at all, a 640 m ring seen from a col 900 m up would sit more than fifty degrees
    /// below the horizon. So the drop is a small fraction of the altitude with a hard ceiling on it — climbing
    /// the Weissjoch sinks the range by about seven degrees, which reads as having got above something,
    /// and no amount of further climbing can turn it into a hole in the sky.</para>
    ///
    /// <para><b>Deliberately not <c>[ExecuteAlways]</c>.</b> It would be the obvious attribute — the
    /// ring would then follow the scene view and look right while working in the editor — and it writes
    /// a transform every frame, so it would leave both scenes permanently dirty and hand the author a
    /// modified working tree for having looked at the world. That is the hazard this project documents
    /// against materials and asset writes, met in the one place where the fix is simply not to. At edit
    /// time the ring sits at the origin, and the tools that photograph the world put it where it
    /// belongs for the length of one frame.</para>
    ///
    /// <para><b>It is placed by a static call as well as by <c>LateUpdate</c>, and the static call is
    /// the one that matters for this project.</b> Every picture here is taken by an editor tool that
    /// builds a camera of its own and renders a saved scene in which no <c>Update</c> has ever run —
    /// so a backdrop that only followed <c>Camera.main</c> would sit at the parked car in every frame
    /// the project takes of itself, and the one feature added to fix an empty horizon would be absent
    /// from every photograph of that horizon. <c>PreviewCapture</c> calls <see cref="PlaceFor"/>, for
    /// the same reason <c>VehicleCover.RoofedAt</c> is static and the gauges expose
    /// <c>LayOutFace</c>: a frame has to be produced by the code that produces the game.</para>
    /// </summary>
    public sealed class Backdrop : MonoBehaviour
    {
        /// <summary>Share of the camera's height the ring sinks by.</summary>
        private const float AltitudeParallax = 0.12f;

        /// <summary>And the most it may ever sink, metres.</summary>
        private const float MaximumSink = 70f;

        private void LateUpdate()
        {
            Camera camera = Camera.main;
            if (camera != null)
            {
                Place(transform, camera.transform.position);
            }
        }

        /// <summary>
        /// Puts every backdrop in the scene around <paramref name="camera"/>.
        ///
        /// <para>Static and by search rather than by a cached reference, because the caller that needs
        /// it is an editor tool holding a camera this component has never heard of. There is one of
        /// these in the world and the call happens once per photograph.</para>
        /// </summary>
        public static void PlaceFor(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            Backdrop[] backdrops = FindObjectsByType<Backdrop>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < backdrops.Length; i++)
            {
                Place(backdrops[i].transform, camera.transform.position);
            }
        }

        /// <summary>
        /// Shows or hides every backdrop in the scene.
        ///
        /// <para>For the tools that photograph the world from above. A plan or overview frame stands
        /// hundreds of metres up looking down, and the ring follows the camera — so it would hang round
        /// that camera as a wall of mountains laid across the middle of the very thing the frame exists
        /// to show. <c>PreviewCapture</c> ties this to whether the shot keeps the fog, because those are
        /// the same shots for the same reason: both the fog and the backdrop are answers to the question
        /// "what is beyond the far distance", and a diagram of the world in plan is not asking it.</para>
        /// </summary>
        public static void SetShown(bool shown)
        {
            Backdrop[] backdrops = FindObjectsByType<Backdrop>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < backdrops.Length; i++)
            {
                var renderer = backdrops[i].GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.enabled = shown;
                }
            }
        }

        private static void Place(Transform ring, Vector3 viewer)
        {
            float sink = Mathf.Clamp(viewer.y * AltitudeParallax, 0f, MaximumSink);

            // Rotation is left alone rather than set to identity: the object is built unrotated and
            // nothing else writes it, and zeroing it here every frame would quietly undo anybody who
            // ever wanted the range turned to put its highest massif somewhere in particular.
            ring.position = new Vector3(viewer.x, viewer.y - sink, viewer.z);
        }
    }
}
