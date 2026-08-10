using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>Kinds of thing that occupy a stretch of road.</summary>
    public enum RoadFeatureKind
    {
        /// <summary>A widened pull-off with a view. Zero length marks a point.</summary>
        Viewpoint = 0,

        /// <summary>Fully enclosed. The terrain must not be cut open along this span.</summary>
        Tunnel = 1,

        /// <summary>Roofed but open on the valley side, held up by pillars.</summary>
        Gallery = 2,

        /// <summary>
        /// A stretch the road runs through a settlement.
        ///
        /// Note what this deliberately does *not* do. <see cref="IsCovered"/> and
        /// <see cref="IsCoveredOrNear"/> test for Tunnel and Gallery by name, so a village does not
        /// suppress guard rails and is not skipped by the road-clearance check — both of which still
        /// matter with houses beside the road. It marks the stretch and nothing more; what actually
        /// stands there comes from <c>VillagePlan</c>.
        /// </summary>
        Village = 3,
    }

    /// <summary>A stretch of the course that something is built on or into.</summary>
    public readonly struct RoadFeature
    {
        public readonly RoadFeatureKind Kind;
        public readonly float StartDistance;
        public readonly float EndDistance;
        public readonly string Name;

        public RoadFeature(RoadFeatureKind kind, float startDistance, float endDistance, string name)
        {
            Kind = kind;
            StartDistance = startDistance;
            EndDistance = endDistance;
            Name = name;
        }

        public float Length => EndDistance - StartDistance;

        public bool Contains(float distance)
        {
            return distance >= StartDistance && distance <= EndDistance;
        }
    }

    /// <summary>
    /// A finished course: the control points to feed <see cref="RoadPath"/>, plus where along it the
    /// tunnels, galleries and viewpoints are.
    ///
    /// The features travel with the course rather than being worked out afterwards from the geometry.
    /// Guard rails, banking and the terrain all need to know where a tunnel is, and re-deriving that
    /// from a finished mesh would be guesswork.
    /// </summary>
    public sealed class RoadCourse
    {
        private readonly List<Vector3> controlPoints;
        private readonly List<RoadFeature> features;

        internal RoadCourse(List<Vector3> controlPoints, List<RoadFeature> features, float plannedLength)
        {
            this.controlPoints = controlPoints;
            this.features = features;
            PlannedLength = plannedLength;
        }

        public IReadOnlyList<Vector3> ControlPoints => controlPoints;

        public IReadOnlyList<RoadFeature> Features => features;

        /// <summary>
        /// Length as walked by the builder. The finished <see cref="RoadPath"/> is a Catmull-Rom curve
        /// through these points and will differ slightly, so use its own Length for anything exact.
        /// </summary>
        public float PlannedLength { get; }

        /// <summary>Highest point on the course, and where it is.</summary>
        public Vector3 Summit
        {
            get
            {
                Vector3 highest = controlPoints.Count > 0 ? controlPoints[0] : Vector3.zero;
                for (int i = 1; i < controlPoints.Count; i++)
                {
                    if (controlPoints[i].y > highest.y)
                    {
                        highest = controlPoints[i];
                    }
                }

                return highest;
            }
        }

        /// <summary>Lowest point on the course. With the summit, this is the elevation gained.</summary>
        public float LowestElevation
        {
            get
            {
                float lowest = controlPoints.Count > 0 ? controlPoints[0].y : 0f;
                for (int i = 1; i < controlPoints.Count; i++)
                {
                    lowest = Mathf.Min(lowest, controlPoints[i].y);
                }

                return lowest;
            }
        }

        /// <summary>
        /// True if <paramref name="distance"/> is inside a covered stretch or within
        /// <paramref name="margin"/> of one.
        ///
        /// The margin exists for roadside furniture. The ground beside a portal is cut away for the bore, so
        /// a guard rail placed by a plain drop test finds a large drop and puts a post there — standing in
        /// mid-air next to the entrance.
        /// </summary>
        public bool IsCoveredOrNear(float distance, float margin)
        {
            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];
                bool covers = feature.Kind == RoadFeatureKind.Tunnel || feature.Kind == RoadFeatureKind.Gallery;

                if (covers
                    && distance >= feature.StartDistance - margin
                    && distance <= feature.EndDistance + margin)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True if <paramref name="distance"/> falls inside a tunnel or gallery.</summary>
        public bool IsCovered(float distance)
        {
            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];
                bool covered = feature.Kind == RoadFeatureKind.Tunnel || feature.Kind == RoadFeatureKind.Gallery;
                if (covered && feature.Contains(distance))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Builds a course by walking a heading and a grade, one instruction at a time.
    ///
    /// This exists because the previous road was a sine wave, and a sine wave has continuously varying
    /// curvature and no minimum radius — so it can neither express a hairpin nor promise that the road
    /// ribbon will not pinch shut. Here every corner has an explicit radius, which guarantees both.
    /// </summary>
    public sealed class RoadCourseBuilder
    {
        /// <summary>Spacing of emitted control points. The Catmull-Rom pass smooths between them.</summary>
        private const float PointSpacing = 10f;

        private readonly List<Vector3> points = new List<Vector3>(512);
        private readonly List<RoadFeature> features = new List<RoadFeature>(8);

        private Vector3 position;
        private float headingDegrees;
        private float traveled;

        /// <param name="start">Starting position; its Y is the starting elevation.</param>
        /// <param name="startHeadingDegrees">0 faces +Z, increasing turns towards +X.</param>
        public RoadCourseBuilder(Vector3 start, float startHeadingDegrees = 0f)
        {
            position = start;
            headingDegrees = startHeadingDegrees;
            points.Add(start);
        }

        /// <summary>Distance walked so far. Use it to bracket a feature around instructions.</summary>
        public float Distance => traveled;

        /// <summary>Current elevation.</summary>
        public float Elevation => position.y;

        private Vector3 Forward()
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }

        private Vector3 Right()
        {
            return Vector3.Cross(Vector3.up, Forward());
        }

        /// <summary>Straight section. Grade is in percent: 9.5 climbs 9.5 m per 100 m travelled.</summary>
        public RoadCourseBuilder Straight(float length, float gradePercent = 0f)
        {
            if (length <= 0f)
            {
                return this;
            }

            Vector3 forward = Forward();
            Vector3 start = position;
            float rise = length * gradePercent * 0.01f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / PointSpacing));

            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 point = start + forward * (length * t);
                point.y = start.y + rise * t;
                points.Add(point);
            }

            position = points[points.Count - 1];
            traveled += length;
            return this;
        }

        /// <summary>
        /// Constant-radius corner. A positive angle turns right, negative turns left; a hairpin is
        /// simply a small radius and about 170°.
        /// </summary>
        public RoadCourseBuilder Turn(float radius, float angleDegrees, float gradePercent = 0f)
        {
            if (radius <= 0.01f || Mathf.Abs(angleDegrees) < 0.01f)
            {
                return this;
            }

            float sign = Mathf.Sign(angleDegrees);
            float arcLength = radius * Mathf.Abs(angleDegrees) * Mathf.Deg2Rad;
            float rise = arcLength * gradePercent * 0.01f;

            // Centre of the arc sits one radius to the side we are turning towards.
            Vector3 center = position + Right() * (radius * sign);
            Vector3 offset = position - center;
            float startY = position.y;
            float startHeading = headingDegrees;

            // Stepped by angle as well as by arc length. On a 20 m hairpin, 10 m spacing puts control
            // points 28° apart and the Catmull-Rom curve through them cuts the corner by more than half
            // a metre; capping the turn per step at 8° brings that down to a few centimetres.
            const float maxDegreesPerStep = 8f;
            int steps = Mathf.Max(
                2,
                Mathf.Max(
                    Mathf.CeilToInt(arcLength / PointSpacing),
                    Mathf.CeilToInt(Mathf.Abs(angleDegrees) / maxDegreesPerStep)));

            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;

                // Unity's +Y rotation turns +Z towards +X, which is the same sense as the heading.
                Vector3 rotated = Quaternion.Euler(0f, angleDegrees * t, 0f) * offset;
                Vector3 point = center + rotated;
                point.y = startY + rise * t;
                points.Add(point);
            }

            headingDegrees = startHeading + angleDegrees;
            position = points[points.Count - 1];
            traveled += arcLength;
            return this;
        }

        /// <summary>
        /// A leg between hairpins: a gentle sweep rather than a dead straight, which is what real pass
        /// legs look like and stops the climb feeling like a staircase.
        /// </summary>
        public RoadCourseBuilder Leg(float length, float sweepRadius, float sweepAngle, float gradePercent)
        {
            float arc = sweepRadius * Mathf.Abs(sweepAngle) * Mathf.Deg2Rad;
            float straight = Mathf.Max(0f, length - arc) * 0.5f;

            Straight(straight, gradePercent);
            Turn(sweepRadius, sweepAngle, gradePercent);
            Straight(straight, gradePercent);
            return this;
        }

        /// <summary>Records a feature over a distance range. Use <see cref="Distance"/> to bracket it.</summary>
        public RoadCourseBuilder AddFeature(RoadFeatureKind kind, float startDistance, float endDistance, string name)
        {
            features.Add(new RoadFeature(kind, startDistance, endDistance, name));
            return this;
        }

        /// <summary>Marks a viewpoint at the current distance.</summary>
        public RoadCourseBuilder AddViewpoint(string name)
        {
            features.Add(new RoadFeature(RoadFeatureKind.Viewpoint, traveled, traveled, name));
            return this;
        }

        public RoadCourse Build()
        {
            return new RoadCourse(new List<Vector3>(points), new List<RoadFeature>(features), traveled);
        }
    }
}
