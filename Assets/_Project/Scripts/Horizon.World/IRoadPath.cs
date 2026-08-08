using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// A road centreline, sampled by arc length so generated geometry is evenly spaced regardless
    /// of how the curve was authored.
    ///
    /// The abstraction exists because the prototype road is authored as plain control points
    /// (<see cref="RoadPath"/>, no package dependency, deterministic) while production roads will be
    /// authored with the Splines package (<c>SplineRoadPath</c>). The mesh generators only ever see
    /// this interface, so switching authoring tools does not touch them.
    /// </summary>
    public interface IRoadPath
    {
        /// <summary>Total length in metres, in world space.</summary>
        float Length { get; }

        /// <summary>True if the road closes back on itself.</summary>
        bool IsLoop { get; }

        /// <summary>World position at <paramref name="distance"/> metres along the road.</summary>
        Vector3 GetPositionAtDistance(float distance);

        /// <summary>Normalized world-space direction of travel at that distance.</summary>
        Vector3 GetDirectionAtDistance(float distance);
    }

    /// <summary>Helpers shared by every <see cref="IRoadPath"/> implementation.</summary>
    public static class RoadPathExtensions
    {
        /// <summary>
        /// Right-hand vector across the road, level with the world. Roads get their banking from the
        /// terrain, not from the centreline, so we do not roll with the curve's own normal here.
        /// </summary>
        public static Vector3 GetRightAtDistance(this IRoadPath path, float distance)
        {
            Vector3 direction = path.GetDirectionAtDistance(distance);
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            return right.sqrMagnitude < 0.0001f ? Vector3.right : right.normalized;
        }

        /// <summary>
        /// Curvature at a distance, in radians per metre — the reciprocal of the corner radius.
        /// Measured from how much the heading swings over a short arc, so it works for any path
        /// implementation without needing a second derivative.
        /// </summary>
        public static float GetCurvatureAtDistance(this IRoadPath path, float distance, float sampleArc = 4f)
        {
            float half = Mathf.Max(0.5f, sampleArc) * 0.5f;

            Vector3 before = path.GetDirectionAtDistance(path.NormalizeDistance(distance - half));
            Vector3 after = path.GetDirectionAtDistance(path.NormalizeDistance(distance + half));

            float swing = Vector3.Angle(before, after) * Mathf.Deg2Rad;
            return swing / Mathf.Max(0.5f, sampleArc);
        }

        /// <summary>
        /// Corner radius at a distance, metres. Returns <see cref="float.MaxValue"/> on a straight,
        /// so callers can compare against a threshold without special-casing zero curvature.
        /// </summary>
        public static float GetRadiusAtDistance(this IRoadPath path, float distance, float sampleArc = 4f)
        {
            float curvature = path.GetCurvatureAtDistance(distance, sampleArc);
            return curvature < 0.00001f ? float.MaxValue : 1f / curvature;
        }

        /// <summary>Wraps or clamps a distance depending on whether the road loops.</summary>
        public static float NormalizeDistance(this IRoadPath path, float distance)
        {
            float length = path.Length;
            if (length <= 0f)
            {
                return 0f;
            }

            return path.IsLoop ? Mathf.Repeat(distance, length) : Mathf.Clamp(distance, 0f, length);
        }
    }
}
