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
        [Tooltip("SmoothDamp time for position at a standstill. Higher lags more and feels heavier.")]
        [SerializeField] private float positionSmoothTime = 0.16f;

        [Tooltip("SmoothDamp time at reference speed. Lower than the standstill figure: the same lag "
               + "that reads as weight when parking reads as the car floating away at speed.")]
        [SerializeField] private float positionSmoothTimeAtSpeed = 0.09f;

        [Tooltip("Rotation catch-up rate. Higher snaps harder to the aim point.")]
        [SerializeField] private float rotationSharpness = 7f;

        [Tooltip("Below this speed the camera stays behind the car's nose instead of following "
               + "velocity, so it does not swing around when rolling to a stop or reversing.")]
        [SerializeField] private float velocityAlignSpeed = 3.5f;

        [Header("Field of view")]
        [SerializeField] private float baseFieldOfView = 60f;

        [Tooltip("Degrees of FOV added by speed alone, at the reference speed.")]
        [SerializeField] private float maxFieldOfViewGain = 16f;

        [Tooltip("Speed in m/s the speed-driven cues are scaled against — the speed above which they "
               + "stop growing.\n\n"
               + "Sits just under the fastback's 65 m/s top speed on purpose. It was 40 (144 km/h), "
               + "which meant the fastest third of the car's range all looked the same from behind the "
               + "wheel; anything short of the real top speed leaves some of that flat spot in place.")]
        [SerializeField] private float fieldOfViewReferenceSpeed = 62f;

        [Header("Acceleration response")]
        [Tooltip("Longitudinal acceleration in m/s² the acceleration-driven cues are scaled against. "
               + "A traction-limited launch is about 8.")]
        [SerializeField] private float referenceAcceleration = 6f;

        [Tooltip("Degrees of FOV added at full acceleration. Small on purpose — this reads as the world "
               + "opening up, and stops being invisible the moment it can be named.")]
        [SerializeField] private float accelerationFieldOfViewGain = 4f;

        [Tooltip("Degrees of FOV removed under full braking. Less than the gain, because a closing frame "
               + "is far more noticeable than an opening one.")]
        [SerializeField] private float brakingFieldOfViewLoss = 2f;

        [Tooltip("Metres the camera falls back under full acceleration, and half that drawn in under "
               + "braking. This is the cue that makes the car look like it is pulling away from you.")]
        [SerializeField] private float accelerationDistanceGain = 0.5f;

        [Tooltip("Metres the whole rig drops by at reference speed — camera and aim point together, so "
               + "the framing angle holds and only the eye height changes.\n\n"
               + "A lower eye steepens the ground's parallax, which is the cheapest honest sense of "
               + "speed there is: it is why a kart at 60 feels faster than a saloon at 120.")]
        [SerializeField] private float heightDropAtSpeed = 0.7f;

        [Tooltip("Filter rate for the camera's own acceleration estimate, per second.")]
        [SerializeField] private float accelerationSmoothing = 8f;

        [Header("High-speed buffeting")]
        [Tooltip("Peak rig tremor at full speed, degrees.\n\n"
               + "Under a quarter of a degree, which is a few pixels — it is meant to be felt and not "
               + "seen. Turned up far enough to notice it stops reading as a car at speed and starts "
               + "reading as a camera with a problem, and there is no value in between that is subtle "
               + "and obvious at once.")]
        [SerializeField] private float highSpeedShake = 0.22f;

        [Tooltip("Speed fraction at which the tremor starts coming in. Below this there is nothing: a "
               + "car is not buffeted at town speeds, and a shake down there reads as a rough camera.")]
        [Range(0f, 1f)]
        [SerializeField] private float shakeOnsetSpeedFraction = 0.45f;

        [Tooltip("Tremor frequency, Hz. Fast enough to read as vibration rather than as the camera "
               + "wandering.")]
        [SerializeField] private float shakeFrequency = 11f;

        [Header("Obstacles")]
        [Tooltip("Layers the camera will not sit inside. Leave empty to disable the check.")]
        [SerializeField] private LayerMask obstacleMask;

        [SerializeField] private float obstacleProbeRadius = 0.35f;

        private Camera cameraComponent;
        private Vector3 followVelocity;
        private Vector3 smoothedBackward;

        /// <summary>
        /// The camera measures acceleration itself rather than being handed the vehicle's.
        ///
        /// <para><see cref="Horizon.Core"/> has no references by design, and this class deliberately
        /// takes a bare <see cref="Transform"/> and <see cref="Rigidbody"/> so it can follow anything.
        /// Differentiating the velocity it already reads keeps that true and costs one subtraction —
        /// an interface to import the number would be more machinery for the same value.</para>
        /// </summary>
        private float smoothedAcceleration;

        /// <summary>Ceiling on the camera's own acceleration estimate, m/s².</summary>
        private const float AccelerationClamp = 8f;

        /// <summary>How fast the FOV opens up, per second.</summary>
        private const float FovOpenRate = 7f;

        /// <summary>How fast it closes again. Deliberately far slower — see UpdateFieldOfView.</summary>
        private const float FovCloseRate = 1.8f;

        private float previousSpeed;
        private bool hasPreviousSpeed;

        /// <summary>
        /// The smoothed aim, before the tremor is added.
        ///
        /// <para>Kept apart from <c>transform.rotation</c> so the offset cannot feed itself: writing the
        /// shaken rotation back would make next frame's Slerp start from a shaken pose and smooth the
        /// tremor into the aim, which turns a vibration into a slow wander.</para>
        /// </summary>
        private Quaternion smoothedRotation = Quaternion.identity;

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

            // A respawn moves the car without accelerating it. Left alone, the next frame would
            // differentiate the whole of the old speed and fire every cue at once.
            smoothedAcceleration = 0f;
            previousSpeed = 0f;
            hasPreviousSpeed = false;

            Vector3 desired = ComputeDesiredPosition(out Vector3 aimPoint);
            smoothedRotation = Quaternion.LookRotation(aimPoint - desired, Vector3.up);
            transform.SetPositionAndRotation(desired, smoothedRotation);
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            UpdateAcceleration();

            Vector3 desired = ComputeDesiredPosition(out Vector3 aimPoint);

            // Damping tightens as the car gets faster, so the rig stops trailing at speed.
            float smoothTime = Mathf.Lerp(
                positionSmoothTime, positionSmoothTimeAtSpeed, ShapedSpeedFraction());

            transform.position = Vector3.SmoothDamp(
                transform.position,
                desired,
                ref followVelocity,
                smoothTime);

            Vector3 toAim = aimPoint - transform.position;
            if (toAim.sqrMagnitude > 0.0001f)
            {
                Quaternion wanted = Quaternion.LookRotation(toAim, Vector3.up);
                smoothedRotation = Quaternion.Slerp(
                    smoothedRotation,
                    wanted,
                    1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));
            }

            transform.rotation = smoothedRotation * ShakeOffset();

            UpdateFieldOfView();
        }

        /// <summary>Flat speed as a 0..1 fraction of <see cref="fieldOfViewReferenceSpeed"/>.</summary>
        private float SpeedFraction()
        {
            if (targetBody == null)
            {
                return 0f;
            }

            Vector3 velocity = targetBody.linearVelocity;
            float speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            return Mathf.Clamp01(speed / Mathf.Max(1f, fieldOfViewReferenceSpeed));
        }

        /// <summary>
        /// The speed fraction shaped so the cues bite hardest where they are needed most.
        ///
        /// <para>Straight speed is the wrong curve, and the reason is what the eye is doing. Below about
        /// 100 km/h the world supplies plenty of its own motion and the camera should stay out of the
        /// way; above it the frame fills with distant geometry that barely moves, and 200 km/h ends up
        /// looking like 120. A linear ramp spends most of its travel in the half of the range that did
        /// not need it and arrives at the top with nothing left.</para>
        ///
        /// <para>t^1.5 is gentler than linear at a potter, matches it through the middle, and puts the
        /// remaining half of its travel above 150 km/h. Written as t·√t because that is one square root
        /// against Pow's logarithm, in a method called every frame.</para>
        /// </summary>
        private float ShapedSpeedFraction()
        {
            float t = SpeedFraction();
            return t * Mathf.Sqrt(t);
        }

        /// <summary>
        /// Differentiates the target's flat speed into <see cref="smoothedAcceleration"/>, in m/s².
        ///
        /// <para>Flat speed rather than the full velocity, so cresting a rise is not read as braking.
        /// Unsigned along the direction of travel: reversing away from a standstill is a gain, and
        /// pretending otherwise would swing the rig the wrong way every time the car backs up.</para>
        /// </summary>
        private void UpdateAcceleration()
        {
            float deltaTime = Time.deltaTime;
            if (targetBody == null || deltaTime <= 0f)
            {
                smoothedAcceleration = 0f;
                return;
            }

            Vector3 velocity = targetBody.linearVelocity;
            float speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;

            if (!hasPreviousSpeed)
            {
                previousSpeed = speed;
                hasPreviousSpeed = true;
                return;
            }

            float raw = (speed - previousSpeed) / deltaTime;
            previousSpeed = speed;

            // Clamped before the filter: a kerb strike is real but it is not acceleration, and letting
            // the spike through would leave its tail in the frame for a tenth of a second afterwards.
            raw = Mathf.Clamp(raw, -AccelerationClamp, AccelerationClamp);

            smoothedAcceleration = Mathf.Lerp(
                smoothedAcceleration,
                raw,
                1f - Mathf.Exp(-accelerationSmoothing * deltaTime));
        }

        /// <summary>Acceleration as a signed 0..±1 fraction of <see cref="referenceAcceleration"/>.</summary>
        private float AccelerationFraction() =>
            Mathf.Clamp(smoothedAcceleration / Mathf.Max(0.01f, referenceAcceleration), -1f, 1f);

        /// <summary>
        /// A small rotational tremor that fades in with speed — the rig being buffeted.
        ///
        /// <para>This is the one cue that says "fast" without moving the framing: FOV, rig height and
        /// damping all change what is composed, and past a point that becomes a look rather than a
        /// sensation. A tremor adds no composition at all, which is why it can carry the very top of
        /// the range where the others have run out of travel.</para>
        ///
        /// <para>Perlin rather than a sine, on two decorrelated lines: a sine at a fixed frequency beats
        /// against the frame rate and reads as a wobble with a period, which is worse than no shake.
        /// Noise has no period to find. Pitch and yaw only — rolling the horizon at speed reads as a
        /// crash, not as velocity.</para>
        /// </summary>
        private Quaternion ShakeOffset()
        {
            if (highSpeedShake <= 0f)
            {
                return Quaternion.identity;
            }

            float onset = Mathf.InverseLerp(shakeOnsetSpeedFraction, 1f, SpeedFraction());
            if (onset <= 0f)
            {
                return Quaternion.identity;
            }

            // Smoothstepped so the tremor arrives without a threshold the player could feel it cross.
            float amount = highSpeedShake * Mathf.SmoothStep(0f, 1f, onset);

            float t = Time.time * shakeFrequency;
            float pitch = (Mathf.PerlinNoise(t, 0f) - 0.5f) * 2f;
            float yaw = (Mathf.PerlinNoise(0f, t + 37f) - 0.5f) * 2f;

            return Quaternion.Euler(pitch * amount, yaw * amount, 0f);
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

            // Camera and aim point drop together. Lowering only the camera would tip the framing
            // *upward* — the aim stays at lookHeight while the eye sinks toward it — so the lower the
            // camera got, the less road surface was left in the frame. Which is the opposite of the
            // point: the road is the thing whose motion is being read.
            float heightDrop = heightDropAtSpeed * ShapedSpeedFraction();

            Vector3 pivot = target.position + Vector3.up * (lookHeight - heightDrop);
            aimPoint = pivot + flatVelocity * lookAheadTime;

            // Under power the rig falls back and the car pulls away from the camera; braking draws it
            // in. Half the travel on the braking side, because the frame closing is the more noticeable
            // of the two directions.
            float acceleration01 = AccelerationFraction();
            float rigDistance = distance + accelerationDistanceGain
                * (acceleration01 > 0f ? acceleration01 : acceleration01 * 0.5f);

            float rigHeight = height - heightDrop;

            Vector3 desired = target.position + smoothedBackward * rigDistance + Vector3.up * rigHeight;
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

            float speedGain = maxFieldOfViewGain * ShapedSpeedFraction();

            // The second term is the one that makes acceleration visible. Speed alone changes smoothly,
            // so a frame driven by it is never seen changing; this one is only there while the car is
            // actually gaining, which is exactly when there is something to say.
            float acceleration01 = AccelerationFraction();
            float accelerationGain = acceleration01 > 0f
                ? accelerationFieldOfViewGain * acceleration01
                : brakingFieldOfViewLoss * acceleration01;

            float wanted = baseFieldOfView + speedGain + accelerationGain;

            // Asymmetric, and this is the whole point of the change rather than a detail of it. Opening
            // promptly under power and closing slowly on the overrun is what turns a lift-off into
            // something the player sees relax; the symmetric rate it replaces made both directions
            // equally unnoticeable.
            float rate = wanted > cameraComponent.fieldOfView ? FovOpenRate : FovCloseRate;

            cameraComponent.fieldOfView = Mathf.Lerp(
                cameraComponent.fieldOfView,
                wanted,
                1f - Mathf.Exp(-rate * Time.deltaTime));
        }
    }
}
