using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Catmull-Rom road centreline through a list of control points. Deliberately dependency-free so
    /// the prototype road is deterministic and reproducible from code; the Splines package is the
    /// upgrade path for hand-authoring in the scene view.
    ///
    /// Control points are local to this transform. An arc-length lookup table is built on demand so
    /// sampling by distance is uniform — without it, geometry bunches up in tight corners.
    /// </summary>
    public sealed class RoadPath : MonoBehaviour, IRoadPath
    {
        [Tooltip("Control points in local space. Needs at least four for a usable curve.")]
        [SerializeField] private List<Vector3> controlPoints = new List<Vector3>();

        [SerializeField] private bool isLoop;

        [Tooltip("Samples per control-point segment used to build the arc-length table. Higher is "
               + "more accurate and only costs rebuild time.")]
        [SerializeField, Range(4, 64)] private int samplesPerSegment = 24;

        private readonly List<Vector3> samplePositions = new List<Vector3>();
        private readonly List<float> sampleDistances = new List<float>();
        private float totalLength;
        private bool lookupValid;

        public float Length
        {
            get
            {
                EnsureLookup();
                return totalLength;
            }
        }

        public bool IsLoop => isLoop;

        /// <summary>Read-only view of the control points, in local space.</summary>
        public IReadOnlyList<Vector3> ControlPoints => controlPoints;

        /// <summary>Replaces the control points and invalidates the cached table.</summary>
        public void SetControlPoints(IEnumerable<Vector3> points, bool loop = false)
        {
            controlPoints.Clear();
            controlPoints.AddRange(points);
            isLoop = loop;
            Invalidate();
        }

        /// <summary>Forces the arc-length table to rebuild on next access.</summary>
        public void Invalidate()
        {
            lookupValid = false;
        }

        public Vector3 GetPositionAtDistance(float distance)
        {
            EnsureLookup();
            if (samplePositions.Count == 0)
            {
                return transform.position;
            }

            distance = this.NormalizeDistance(distance);
            FindSpan(distance, out int index, out float fraction);
            return Vector3.Lerp(samplePositions[index], samplePositions[index + 1], fraction);
        }

        public Vector3 GetDirectionAtDistance(float distance)
        {
            EnsureLookup();
            if (samplePositions.Count < 2)
            {
                return transform.forward;
            }

            distance = this.NormalizeDistance(distance);
            FindSpan(distance, out int index, out _);
            Vector3 delta = samplePositions[index + 1] - samplePositions[index];
            return delta.sqrMagnitude < 0.000001f ? transform.forward : delta.normalized;
        }

        /// <summary>
        /// Locates the sample span containing <paramref name="distance"/>. Binary search, so a road
        /// with thousands of samples still costs nothing per query.
        /// </summary>
        private void FindSpan(float distance, out int index, out float fraction)
        {
            int low = 0;
            int high = sampleDistances.Count - 1;

            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (sampleDistances[mid] <= distance)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            index = Mathf.Clamp(low, 0, samplePositions.Count - 2);
            float spanStart = sampleDistances[index];
            float spanLength = sampleDistances[index + 1] - spanStart;
            fraction = spanLength > 0.000001f ? (distance - spanStart) / spanLength : 0f;
        }

        private void EnsureLookup()
        {
            if (lookupValid)
            {
                return;
            }

            samplePositions.Clear();
            sampleDistances.Clear();
            totalLength = 0f;
            lookupValid = true;

            int pointCount = controlPoints.Count;
            if (pointCount < 2)
            {
                return;
            }

            int segmentCount = isLoop ? pointCount : pointCount - 1;

            for (int segment = 0; segment < segmentCount; segment++)
            {
                for (int step = 0; step < samplesPerSegment; step++)
                {
                    float t = step / (float)samplesPerSegment;
                    AppendSample(EvaluateSegment(segment, t));
                }
            }

            // Close the sample list so the final span is well defined.
            AppendSample(isLoop ? EvaluateSegment(0, 0f) : EvaluateSegment(segmentCount - 1, 1f));
        }

        private void AppendSample(Vector3 worldPosition)
        {
            if (samplePositions.Count > 0)
            {
                totalLength += Vector3.Distance(samplePositions[samplePositions.Count - 1], worldPosition);
            }

            samplePositions.Add(worldPosition);
            sampleDistances.Add(totalLength);
        }

        private Vector3 EvaluateSegment(int segment, float t)
        {
            Vector3 p0 = GetControlPoint(segment - 1);
            Vector3 p1 = GetControlPoint(segment);
            Vector3 p2 = GetControlPoint(segment + 1);
            Vector3 p3 = GetControlPoint(segment + 2);
            return transform.TransformPoint(CatmullRom(p0, p1, p2, p3, t));
        }

        private Vector3 GetControlPoint(int index)
        {
            int count = controlPoints.Count;
            if (count == 0)
            {
                return Vector3.zero;
            }

            if (isLoop)
            {
                return controlPoints[((index % count) + count) % count];
            }

            return controlPoints[Mathf.Clamp(index, 0, count - 1)];
        }

        /// <summary>Uniform Catmull-Rom interpolation between <paramref name="p1"/> and <paramref name="p2"/>.</summary>
        public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                2f * p1
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            Invalidate();
        }

        private void OnDrawGizmos()
        {
            EnsureLookup();

            Gizmos.color = new Color(1f, 0.72f, 0.25f);
            for (int i = 0; i < samplePositions.Count - 1; i++)
            {
                Gizmos.DrawLine(samplePositions[i], samplePositions[i + 1]);
            }

            Gizmos.color = Color.white;
            for (int i = 0; i < controlPoints.Count; i++)
            {
                Gizmos.DrawWireSphere(transform.TransformPoint(controlPoints[i]), 1.5f);
            }
        }
#endif
    }
}
