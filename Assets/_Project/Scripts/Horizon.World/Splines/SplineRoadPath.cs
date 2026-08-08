#if HORIZON_SPLINES
using UnityEngine;
using UnityEngine.Splines;

namespace Horizon.World
{
    /// <summary>
    /// Adapts a <see cref="SplineContainer"/> to <see cref="IRoadPath"/>. This is the authoring path
    /// for real world content: splines give scene-view handles and tangent control, which hand-placed
    /// control points do not.
    ///
    /// Compiled only when com.unity.splines is installed — see the versionDefines entry in
    /// Horizon.World.asmdef. The prototype uses <see cref="RoadPath"/> and does not need this.
    /// </summary>
    [RequireComponent(typeof(SplineContainer))]
    public sealed class SplineRoadPath : MonoBehaviour, IRoadPath
    {
        [SerializeField] private SplineContainer container;

        private void Awake()
        {
            EnsureContainer();
        }

        private void EnsureContainer()
        {
            if (container == null)
            {
                container = GetComponent<SplineContainer>();
            }
        }

        public float Length
        {
            get
            {
                EnsureContainer();
                return container != null ? container.CalculateLength() : 0f;
            }
        }

        public bool IsLoop
        {
            get
            {
                EnsureContainer();
                return container != null && container.Spline != null && container.Spline.Closed;
            }
        }

        public Vector3 GetPositionAtDistance(float distance)
        {
            EnsureContainer();
            return container != null
                ? (Vector3)container.EvaluatePosition(DistanceToNormalized(distance))
                : transform.position;
        }

        public Vector3 GetDirectionAtDistance(float distance)
        {
            EnsureContainer();
            if (container == null)
            {
                return transform.forward;
            }

            Vector3 tangent = container.EvaluateTangent(DistanceToNormalized(distance));
            return tangent.sqrMagnitude < 0.000001f ? transform.forward : tangent.normalized;
        }

        private float DistanceToNormalized(float distance)
        {
            float length = Length;
            if (length <= 0f)
            {
                return 0f;
            }

            return this.NormalizeDistance(distance) / length;
        }
    }
}
#endif
