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
