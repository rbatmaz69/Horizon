using UnityEngine;

namespace Horizon.Core
{
    /// <summary>
    /// Smoothed follow camera. Most of what a player calls "feel" is actually camera behaviour, so
    /// this is hand-rolled rather than delegated: we want direct control over how the framing lags
    /// behind the car and how it leads into corners.
    ///
    /// Follows a plain <see cref="Transform"/> plus an optional <see cref="Rigidbody"/> for
    /// velocity, so it has no dependency on the vehicle module.
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform target;
        [SerializeField] private Rigidbody targetBody;

        [Header("Framing")]
        [Tooltip("Distance behind the target, metres.")]
        [SerializeField] private float distance = 6.5f;

        [Tooltip("Height above the target, metres.")]
        [SerializeField] private float height = 2.4f;

        [Tooltip("Height above the target that the camera aims at.")]
        [SerializeField] private float lookHeight = 1.1f;

        [Tooltip("Seconds of velocity the camera looks ahead into. Leads the car into corners.")]
        [SerializeField] private float lookAheadTime = 0.35f;

        [Header("Smoothing")]
        [Tooltip("SmoothDamp time for position. Higher lags more and feels heavier.")]
        [SerializeField] private float positionSmoothTime = 0.16f;

        [Tooltip("Rotation catch-up rate. Higher snaps harder to the aim point.")]
        [SerializeField] private float rotationSharpness = 7f;

        [Tooltip("Below this speed the camera stays behind the car's nose instead of following "
               + "velocity, so it does not swing around when rolling to a stop or reversing.")]
        [SerializeField] private float velocityAlignSpeed = 3.5f;

        [Header("Field of view")]
        [SerializeField] private float baseFieldOfView = 60f;
        [SerializeField] private float maxFieldOfViewGain = 12f;

        [Tooltip("Speed in m/s at which the FOV gain is fully applied.")]
        [SerializeField] private float fieldOfViewReferenceSpeed = 40f;

        [Header("Obstacles")]
        [Tooltip("Layers the camera will not sit inside. Leave empty to disable the check.")]
        [SerializeField] private LayerMask obstacleMask;

        [SerializeField] private float obstacleProbeRadius = 0.35f;

        private Camera cameraComponent;
        private Vector3 followVelocity;
        private Vector3 smoothedBackward;

        /// <summary>Assigns the follow target. Called by the scene bootstrap.</summary>
        public void SetTarget(Transform newTarget, Rigidbody newTargetBody = null)
        {
            target = newTarget;
            targetBody = newTargetBody;
            SnapToTarget();
        }

        private void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            if (cameraComponent != null)
            {
                cameraComponent.fieldOfView = baseFieldOfView;
            }
        }

        private void Start()
        {
            SnapToTarget();
        }

        /// <summary>Jumps straight to the ideal pose, skipping the smoothing. Avoids a fly-in on load.</summary>
        public void SnapToTarget()
        {
            if (target == null)
            {
                return;
            }

            smoothedBackward = -target.forward;
            followVelocity = Vector3.zero;

            Vector3 desired = ComputeDesiredPosition(out Vector3 aimPoint);
            transform.SetPositionAndRotation(desired, Quaternion.LookRotation(aimPoint - desired, Vector3.up));
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            Vector3 desired = ComputeDesiredPosition(out Vector3 aimPoint);

            transform.position = Vector3.SmoothDamp(
                transform.position,
                desired,
                ref followVelocity,
                positionSmoothTime);

            Vector3 toAim = aimPoint - transform.position;
            if (toAim.sqrMagnitude > 0.0001f)
            {
                Quaternion wanted = Quaternion.LookRotation(toAim, Vector3.up);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    wanted,
                    1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));
            }

            UpdateFieldOfView();
        }

        private Vector3 ComputeDesiredPosition(out Vector3 aimPoint)
        {
            Vector3 velocity = targetBody != null ? targetBody.linearVelocity : Vector3.zero;
            Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float speed = flatVelocity.magnitude;

            // Blend from "behind the nose" to "behind the direction of travel" as speed rises. At
            // low speed, following velocity would let the camera whip around during manoeuvring.
            Vector3 wantedBackward = -target.forward;
            if (speed > 0.01f)
            {
                float alignment = Mathf.Clamp01(speed / Mathf.Max(0.01f, velocityAlignSpeed));
                Vector3 travelBackward = -flatVelocity / speed;

                // Reversing must not put the camera in front of the car.
                if (Vector3.Dot(travelBackward, -target.forward) > 0f)
                {
                    wantedBackward = Vector3.Slerp(wantedBackward, travelBackward, alignment);
                }
            }

            smoothedBackward = Vector3.Slerp(
                smoothedBackward,
                wantedBackward,
                1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));

            Vector3 pivot = target.position + Vector3.up * lookHeight;
            aimPoint = pivot + flatVelocity * lookAheadTime;

            Vector3 desired = target.position + smoothedBackward * distance + Vector3.up * height;
            return ResolveObstacles(pivot, desired);
        }

        /// <summary>Pulls the camera in if geometry sits between it and the car.</summary>
        private Vector3 ResolveObstacles(Vector3 pivot, Vector3 desired)
        {
            if (obstacleMask.value == 0)
            {
                return desired;
            }

            Vector3 direction = desired - pivot;
            float length = direction.magnitude;
            if (length < 0.01f)
            {
                return desired;
            }

            direction /= length;

            if (Physics.SphereCast(
                    pivot,
                    obstacleProbeRadius,
                    direction,
                    out RaycastHit hit,
                    length,
                    obstacleMask,
                    QueryTriggerInteraction.Ignore))
            {
                return pivot + direction * Mathf.Max(0.5f, hit.distance);
            }

            return desired;
        }

        private void UpdateFieldOfView()
        {
            if (cameraComponent == null)
            {
                return;
            }

            float speed = targetBody != null ? targetBody.linearVelocity.magnitude : 0f;
            float gain = maxFieldOfViewGain * Mathf.Clamp01(speed / Mathf.Max(1f, fieldOfViewReferenceSpeed));
            float wanted = baseFieldOfView + gain;

            cameraComponent.fieldOfView = Mathf.Lerp(
                cameraComponent.fieldOfView,
                wanted,
                1f - Mathf.Exp(-3f * Time.deltaTime));
        }
    }
}
