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

        /// <summary>
        /// Signed curvature: positive turning right, negative turning left. The sign comes from which way
        /// the heading swings, taken about world up.
        /// </summary>
        public static float GetSignedCurvatureAtDistance(this IRoadPath path, float distance, float sampleArc = 4f)
        {
            float half = Mathf.Max(0.5f, sampleArc) * 0.5f;

            Vector3 before = path.GetDirectionAtDistance(path.NormalizeDistance(distance - half));
            Vector3 after = path.GetDirectionAtDistance(path.NormalizeDistance(distance + half));

            float swing = Vector3.Angle(before, after) * Mathf.Deg2Rad;

            // Cross(before, after) points up for a right-hand turn in Unity's left-handed frame.
            float direction = Mathf.Sign(Vector3.Dot(Vector3.Cross(before, after), Vector3.up));

            return swing / Mathf.Max(0.5f, sampleArc) * direction;
        }

        /// <summary>
        /// Camber angle in degrees, positive where the road banks into a right-hand turn.
        ///
        /// Averaged over a window rather than read off the curvature at a point: curvature at the mouth
        /// of a hairpin steps from nothing to everything within a couple of metres, and a camber that
        /// followed it exactly would put a visible kink across the carriageway there.
        /// </summary>
        public static float GetBankAngleAtDistance(
            this IRoadPath path,
            float distance,
            float maxBankDegrees,
            float fullBankRadius)
        {
            const int windowSamples = 5;
            const float windowLength = 20f;

            float sum = 0f;

            for (int i = 0; i < windowSamples; i++)
            {
                float offset = (i / (float)(windowSamples - 1) - 0.5f) * windowLength;
                float sampled = path.NormalizeDistance(distance + offset);

                float curvature = path.GetSignedCurvatureAtDistance(sampled, 10f);
                float radius = Mathf.Abs(curvature) < 0.00001f ? float.MaxValue : 1f / Mathf.Abs(curvature);

                float amount = Mathf.Clamp01(fullBankRadius / Mathf.Max(1f, radius));
                sum += maxBankDegrees * amount * Mathf.Sign(curvature);
            }

            return sum / windowSamples;
        }

        /// <summary>
        /// Right-hand vector across the road, rolled to give the carriageway its camber.
        ///
        /// Only the road ribbon should use this. Terrain sampling must keep <see cref="GetRightAtDistance"/>
        /// — banking the hillside along with the asphalt is not a thing that happens.
        /// </summary>
        public static Vector3 GetBankedRightAtDistance(
            this IRoadPath path,
            float distance,
            float maxBankDegrees,
            float fullBankRadius)
        {
            Vector3 right = path.GetRightAtDistance(distance);

            float bank = path.GetBankAngleAtDistance(distance, maxBankDegrees, fullBankRadius);
            if (Mathf.Abs(bank) < 0.01f)
            {
                return right;
            }

            // In a right-hand turn the outer edge is on the left, so it is the +X end that drops. That
            // makes the rotation about the direction of travel negative for a positive (right) bank.
            Vector3 forward = path.GetDirectionAtDistance(distance);
            return Quaternion.AngleAxis(-bank, forward) * right;
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
