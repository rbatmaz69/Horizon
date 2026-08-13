using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Turns authored <see cref="WaterPlan"/>s into <see cref="WaterBody"/>s by asking the ground
    /// where it is.
    ///
    /// <para>Two things are resolved here and neither may be typed into the table. A river's place
    /// comes from the bridge it runs under, found by name in the course's own feature list, so it
    /// follows the viaduct whenever the motorway is retuned. And every surface height comes from
    /// sampling the rim of the basin <b>before</b> anything is carved, so the freeboard is measured
    /// against ground that exists rather than against ground somebody expected — the same reason the
    /// spawn table holds distances along a course instead of coordinates.</para>
    /// </summary>
    public static class WaterPlanner
    {
        /// <summary>How many points around a basin's edge are sampled to find its rim.</summary>
        private const int RimSamples = 48;

        /// <summary>
        /// Resolves every plan, and writes a line per body into <paramref name="report"/>.
        ///
        /// <para><paramref name="field"/> must not have had <c>SetWater</c> called on it yet, or the
        /// rim of the first body is sampled through the basin of the second.</para>
        /// </summary>
        public static WaterBody[] Resolve(
            IReadOnlyList<WaterPlan> plans,
            MountainField field,
            IRoadPath motorway,
            RoadCourse motorwayCourse,
            out string report)
        {
            var bodies = new List<WaterBody>(plans.Count);
            var lines = new StringBuilder();

            for (int i = 0; i < plans.Count; i++)
            {
                WaterPlan plan = plans[i];

                Vector2[] spine = plan.Kind == WaterKind.River
                    ? RiverSpine(plan, motorway, motorwayCourse)
                    : new[] { plan.Centre };

                if (spine == null)
                {
                    Debug.LogWarning($"[Horizon] Water '{plan.Name}' names a bridge "
                                     + $"'{plan.BridgeName}' that is not on the motorway. Skipped.");
                    continue;
                }

                float rim = LowestRim(field, spine, plan.HalfWidth);
                float surface = rim - plan.Freeboard;

                bodies.Add(new WaterBody(
                    plan.Name, plan.Kind, spine, plan.HalfWidth, plan.BankEase, surface, plan.Depth));

                lines.Append($"\n  {plan.Name,-16} {plan.Kind,-5} surface {surface,7:0.0} m, "
                             + $"bed {surface - plan.Depth,7:0.0} m, rim {rim,7:0.0} m, "
                             + $"{plan.HalfWidth * 2f:0} m across");
            }

            report = lines.ToString();
            return bodies.ToArray();
        }

        /// <summary>
        /// The centreline of a river, laid across the road the named bridge carries.
        ///
        /// <para>Square to the road plus a skew, because a watercourse that crosses at exactly ninety
        /// degrees reads as a canal. The reach either side is what decides whether this is a river or
        /// a reservoir — see the note in <see cref="WaterShape"/>.</para>
        /// </summary>
        private static Vector2[] RiverSpine(
            in WaterPlan plan, IRoadPath road, RoadCourse course)
        {
            if (road == null || course == null)
            {
                return null;
            }

            float at = -1f;

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];

                if (feature.Kind == RoadFeatureKind.Bridge && feature.Name == plan.BridgeName)
                {
                    at = (feature.StartDistance + feature.EndDistance) * 0.5f;
                    break;
                }
            }

            if (at < 0f)
            {
                return null;
            }

            Vector3 centre = road.GetPositionAtDistance(at);
            Vector3 across = road.GetRightAtDistance(at);

            // Skewed off square, about the vertical.
            Vector3 run = Quaternion.Euler(0f, plan.SkewDegrees, 0f) * across;
            run.y = 0f;
            run = run.normalized;

            var mid = new Vector2(centre.x, centre.z);
            var step = new Vector2(run.x, run.z);

            // Five points rather than two, so the spine can be bent later without changing anything
            // that reads it, and so the plan bounds are not a rectangle around a diagonal.
            var spine = new Vector2[5];
            for (int i = 0; i < spine.Length; i++)
            {
                float t = i / (float)(spine.Length - 1) * 2f - 1f;
                spine[i] = mid + step * (plan.Reach * t);
            }

            return spine;
        }

        /// <summary>
        /// The lowest ground anywhere on the basin's edge.
        ///
        /// <para>The lowest, not the average, and that is the whole point: water finds the lowest
        /// point of its rim and leaves through it. Averaging would give a surface that looks right on
        /// three sides and pours out of the fourth.</para>
        /// </summary>
        private static float LowestRim(MountainField field, Vector2[] spine, float halfWidth)
        {
            float lowest = float.MaxValue;

            if (spine.Length == 1)
            {
                for (int i = 0; i < RimSamples; i++)
                {
                    float angle = i / (float)RimSamples * Mathf.PI * 2f;
                    Vector2 on = spine[0] + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * halfWidth;

                    lowest = Mathf.Min(lowest, field.HeightAt(on.x, on.y));
                }

                return lowest;
            }

            // A river's rim is its two banks and its two ends, walked at the channel's half-width.
            for (int segment = 1; segment < spine.Length; segment++)
            {
                Vector2 a = spine[segment - 1];
                Vector2 b = spine[segment];

                Vector2 along = (b - a).normalized;
                var side = new Vector2(-along.y, along.x);

                const int perSegment = 12;
                for (int i = 0; i <= perSegment; i++)
                {
                    Vector2 on = Vector2.Lerp(a, b, i / (float)perSegment);

                    lowest = Mathf.Min(lowest, field.HeightAt(on.x + side.x * halfWidth,
                                                              on.y + side.y * halfWidth));
                    lowest = Mathf.Min(lowest, field.HeightAt(on.x - side.x * halfWidth,
                                                              on.y - side.y * halfWidth));
                }
            }

            return lowest;
        }
    }
}
