using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// Answers one question for the rest of the car: is there something overhead?
    ///
    /// A single upward ray, rather than trigger volumes placed in the world. Volumes would have to be
    /// authored next to every bore, gallery and overhang and kept in step with them, and they would only
    /// ever answer what this ray answers directly. It also means anything built later that happens to be
    /// over the road works without being told about.
    ///
    /// Shared so the headlights and the engine note agree, and so the cost is one raycast rather than one
    /// per interested component.
    /// </summary>
    public sealed class VehicleCover : MonoBehaviour
    {
        [Tooltip("How far above the car to look for a roof.")]
        [SerializeField] private float probeHeight = 16f;

        [SerializeField] private LayerMask roofMask = ~0;

        [Tooltip("Seconds between probes. Cover changes at portal speed, not per frame.")]
        [SerializeField] private float probeInterval = 0.08f;

        [Tooltip("Seconds for the smoothed amount to follow a change. Gives the sound and the lights "
               + "something to fade with instead of switching on a frame boundary.")]
        [SerializeField] private float responseTime = 0.35f;

        private float countdown;

        /// <summary>True while something is directly overhead.</summary>
        public bool IsCovered { get; private set; }

        /// <summary>Height of whatever is overhead, metres. Zero when open to the sky.</summary>
        public float CoverHeight { get; private set; }

        /// <summary>Eased 0-1 version of <see cref="IsCovered"/>, for anything that should fade.</summary>
        public float CoverAmount { get; private set; }

        private void Update()
        {
            countdown -= Time.deltaTime;
            if (countdown <= 0f)
            {
                countdown = Mathf.Max(0.01f, probeInterval);
                Probe();
            }

            float target = IsCovered ? 1f : 0f;
            CoverAmount = Mathf.MoveTowards(
                CoverAmount, target, Time.deltaTime / Mathf.Max(0.01f, responseTime));
        }

        private void Probe()
        {
            // Start above the bodywork so the car's own collider is not what we find.
            Vector3 origin = transform.position + transform.up * 1.6f;

            if (Physics.Raycast(
                    origin, transform.up, out RaycastHit hit, probeHeight, roofMask,
                    QueryTriggerInteraction.Ignore))
            {
                IsCovered = true;
                CoverHeight = hit.distance + 1.6f;
                return;
            }

            IsCovered = false;
            CoverHeight = 0f;
        }
    }
}
