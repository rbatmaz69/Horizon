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
        /// stands there comes from <c>TownPlan</c>.
        /// </summary>
        Village = 3,

        /// <summary>
        /// A stretch carried across a valley on piers rather than laid on the ground.
        ///
        /// <para>The visible half of a bridge is the deck and the piers, and that is the easy half. The
        /// half that makes it a bridge is that the ground underneath must be left alone: the terrain
        /// takes its height from the nearest carriageway everywhere, so without this the road would
        /// simply carry a 40 m embankment across the valley and there would be nothing to build a bridge
        /// over. <see cref="MountainField"/> reads this span and drops those samples from the shelf
        /// while keeping them for <c>DistanceToRoad</c>, so the valley stays and the tiles, vegetation
        /// and rails still know there is a road above it.</para>
        ///
        /// <para>Deliberately <b>not</b> counted by <see cref="RoadCourse.IsCovered"/>, which exists to
        /// suppress guard rails and clearance checks under a roof. A bridge has no roof, wants its
        /// parapet, and is exactly where running off the edge matters most.</para>
        /// </summary>
        Bridge = 4,

        /// <summary>
        /// A filling station. Zero length marks the pump island, exactly as
        /// <see cref="Viewpoint"/> marks a point — what actually stands there is laid out around it by
        /// <c>FuelStationBuilder</c>, the way a village's houses come from <c>TownPlan</c> rather than
        /// from this enum.
        ///
        /// <para>Not counted by <see cref="RoadCourse.IsCovered"/> either: a forecourt has a canopy but
        /// no tunnel, and the carriageway beside it still wants its clearance checked. It gets its own
        /// predicate, <see cref="RoadCourse.IsForecourt"/>, because what a forecourt suppresses — the
        /// guard rail and the delineator posts across its frontage — is not what a roof suppresses.</para>
        ///
        /// <para><b>This is the one feature kind that cares which side of the road it is on</b>, which
        /// is what <see cref="RoadFeature.Side"/> is for. A tunnel is the road; a forecourt is beside
        /// it, and the motorway's two carriageways are one course, so without a side there would be no
        /// way to say which of them a station belongs to.</para>
        /// </summary>
        FuelStation = 5,

        /// <summary>
        /// A stretch carried on cables slung between two towers, rather than on piers under the deck.
        ///
        /// <para><b>Why this is a kind of its own and not a long <see cref="Bridge"/>.</b> The two
        /// agree about everything the rest of the world asks — <see cref="RoadCourse.IsBridged"/>
        /// reports both, so the terrain drops its shelf under both, the guard rails stand off both and
        /// the water check permits open water under both. They disagree about exactly one thing, and it
        /// is <c>BridgeBuilder</c>'s: a viaduct plants a pier pair every forty metres, and a pier pair
        /// every forty metres across a shipping channel is the one structure this feature exists to
        /// avoid. <c>BridgeBuilder</c> therefore still matches on <see cref="Bridge"/> alone, and
        /// <c>SuspensionBridgeBuilder</c> matches on this.</para>
        ///
        /// <para>Everything a span of this kind needs beyond its two ends — how high the towers stand,
        /// how far apart, how deep the cable hangs — is the builder's, for the reason
        /// <see cref="RoadFeature.Side"/> gives: a course that wrote those down would be a course to be
        /// re-typed the day the structure was retuned.</para>
        /// </summary>
        Suspension = 6,

        /// <summary>
        /// A stretch where another course leaves this one. Zero length marks the middle of the mouth,
        /// exactly as <see cref="Viewpoint"/> marks a point and <see cref="FuelStation"/> marks a pump
        /// island — what actually stands there is laid out around it by <c>TrunkForkBuilder</c>.
        ///
        /// <para><b>Every road in this world used to end where the next one began, so a fork needed no
        /// name.</b> It does now, and for the same reason a forecourt did: the verge furniture has to
        /// know to stop. A guard rail across the mouth of a fork is a guard rail across the road the
        /// fork exists to reach, and <c>GuardRailBuilder</c> places rails from a drop test that knows
        /// nothing about junctions — the ground beside a mouth is real ground and it does fall away.
        /// <see cref="RoadCourse.IsJunction"/> is what both builders read, and it is
        /// <see cref="RoadCourse.IsForecourt"/> with a different name for exactly that reason.</para>
        ///
        /// <para>Not counted by <see cref="RoadCourse.IsCovered"/> or <see cref="RoadCourse.IsBridged"/>.
        /// A fork has no roof and no drop: the ground under it is the terrain's business as usual, and
        /// the carriageway through it still wants its clearance checked — more than usual, since a mouth
        /// is the one place two courses' shelves are averaged into one.</para>
        ///
        /// <para><b>It carries no side.</b> A fork's mouth opens across both verges whatever hand the
        /// branch leaves on, because the paved throat spans the full width of the road it leaves and
        /// the rail on the far shoulder would end up standing in it.</para>
        /// </summary>
        Junction = 7,
    }

    /// <summary>A stretch of the course that something is built on or into.</summary>
    public readonly struct RoadFeature
    {
        public readonly RoadFeatureKind Kind;
        public readonly float StartDistance;
        public readonly float EndDistance;
        public readonly string Name;

        /// <summary>
        /// Which side of the road it stands on: −1 left, +1 right, 0 for anything with no side — which
        /// is everything except <see cref="RoadFeatureKind.FuelStation"/>.
        ///
        /// <para>A side and not a distance across, deliberately. How far off the centreline a forecourt
        /// sits is a function of the carriageway's own half-width and the depth of the apron, both of
        /// which belong to the builder; a course that wrote down 24.75 m would be a course that had to
        /// be re-typed the day <c>RoadShape.OuterHalfWidth</c> moved.</para>
        /// </summary>
        public readonly float Side;

        public RoadFeature(
            RoadFeatureKind kind, float startDistance, float endDistance, string name, float side = 0f)
        {
            Kind = kind;
            StartDistance = startDistance;
            EndDistance = endDistance;
            Name = name;
            Side = side;
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

        internal RoadCourse(
            List<Vector3> controlPoints,
            List<RoadFeature> features,
            float plannedLength,
            bool isClosed = false)
        {
            this.controlPoints = controlPoints;
            this.features = features;
            PlannedLength = plannedLength;
            IsClosed = isClosed;
        }

        public IReadOnlyList<Vector3> ControlPoints => controlPoints;

        public IReadOnlyList<RoadFeature> Features => features;

        /// <summary>
        /// True if the last control point runs back into the first — a circuit rather than a road with
        /// two ends.
        ///
        /// <para>Set only by <see cref="RoadCourseBuilder.Close"/>, and it exists to be handed straight
        /// to <c>RoadPath.SetControlPoints(points, loop)</c>. Everything downstream of that wraps on its
        /// own: the arc-length table closes on segment zero, <c>NormalizeDistance</c> repeats rather than
        /// clamps, and the Catmull-Rom neighbours come from the far end of the list — so the ribbon, the
        /// guard rails, the height field and the map all cross the start/finish line without a seam.</para>
        ///
        /// <para>The alternative was to let the walk end exactly on its own start and butt the two ends
        /// together under the line. That gives a duplicate ring, a Catmull-Rom curve that is straightened
        /// at both ends by extrapolation rather than by its true neighbours, and a joint that is only
        /// invisible for as long as the start/finish stays dead straight and level. The loop flag was
        /// already written and had simply never been passed by anything.</para>
        /// </summary>
        public bool IsClosed { get; }

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

        /// <summary>
        /// True if <paramref name="distance"/> falls on a bridge of either kind — a viaduct on piers
        /// or a <see cref="RoadFeatureKind.Suspension"/> span — optionally widened by
        /// <paramref name="margin"/> at each end.
        ///
        /// <para>Both kinds, and that is the whole reason the distinction between them is safe to make.
        /// Every caller here is asking the same question — is the ground under this stretch the road's
        /// business — and for both the answer is no. Only the two builders care which is which.</para>
        ///
        /// <para>Separate from <see cref="IsCovered"/> rather than folded into it, because the two
        /// questions have opposite answers for the same callers. A tunnel says "there is a roof, put no
        /// rail here and do not check the sky for clearance"; a bridge says "there is a drop, the
        /// parapet is mine to build and the ground below is not mine at all". The margin is used the
        /// same way <see cref="IsCoveredOrNear"/> uses its own: at the abutment the terrain is still
        /// climbing to meet the deck, and a rail post placed there by a plain drop test lands in the
        /// gap between the two.</para>
        /// </summary>
        public bool IsBridged(float distance, float margin = 0f)
        {
            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];

                bool carried = feature.Kind == RoadFeatureKind.Bridge
                               || feature.Kind == RoadFeatureKind.Suspension;

                if (carried
                    && distance >= feature.StartDistance - margin
                    && distance <= feature.EndDistance + margin)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True within <paramref name="margin"/> of a filling station.
        ///
        /// <para>Guard rails and delineator posts read this. A forecourt's frontage is open by
        /// construction — that is how a car gets onto it — and a line of posts across the entrance is a
        /// line of posts <i>through</i> the entrance. Both verges are suppressed rather than only the
        /// station's own: expressing a one-sided skip would mean threading a side through loops that
        /// have never needed one, for the sake of a rail on the far shoulder of a stretch that was
        /// chosen for being straight and level in the first place.</para>
        /// </summary>
        public bool IsForecourt(float distance, float margin = 0f)
        {
            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];

                if (feature.Kind == RoadFeatureKind.FuelStation
                    && distance >= feature.StartDistance - margin
                    && distance <= feature.EndDistance + margin)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True within <paramref name="margin"/> of a fork where another course leaves this one.
        ///
        /// <para>Guard rails and delineator posts read this, for the reason
        /// <see cref="RoadFeatureKind.Junction"/> gives. Both verges rather than only the branch's own,
        /// which is the same call <see cref="IsForecourt"/> made and is less of a compromise here: the
        /// throat a fork paves reaches across the whole carriageway, so a post on the far shoulder would
        /// be standing on it rather than beside it.</para>
        /// </summary>
        public bool IsJunction(float distance, float margin = 0f)
        {
            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];

                if (feature.Kind == RoadFeatureKind.Junction
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

        /// <summary>
        /// The pose the walk began at, kept so <see cref="Close"/> can aim back at it.
        ///
        /// <para>Not derivable afterwards. <c>points[0]</c> gives the position, but nothing records the
        /// heading — and a closure that arrived at the right place facing the wrong way would be a kink
        /// across the one piece of road every lap crosses.</para>
        /// </summary>
        private readonly Vector3 startPosition;
        private readonly float startHeadingDegrees;

        private bool closed;

        /// <param name="start">Starting position; its Y is the starting elevation.</param>
        /// <param name="startHeadingDegrees">0 faces +Z, increasing turns towards +X.</param>
        public RoadCourseBuilder(Vector3 start, float startHeadingDegrees = 0f)
        {
            position = start;
            headingDegrees = startHeadingDegrees;
            this.startPosition = start;
            this.startHeadingDegrees = startHeadingDegrees;
            points.Add(start);
        }

        /// <summary>Distance walked so far. Use it to bracket a feature around instructions.</summary>
        public float Distance => traveled;

        /// <summary>Current elevation.</summary>
        public float Elevation => position.y;

        /// <summary>
        /// Where the builder currently stands.
        ///
        /// <para>Exposed for the same reason <see cref="HeadingDegrees"/> is: something outside the
        /// course has to be placed against a point on it that only the walk knows. See
        /// <c>EbentalCourse.LakeCentre</c>, which takes the centre of a bend from the pose at its
        /// entry rather than keeping a second copy of the coordinates.</para>
        /// </summary>
        public Vector3 Position => position;

        /// <summary>
        /// Current heading, in the same convention as the constructor: 0 faces +Z, increasing turns
        /// towards +X.
        ///
        /// Exposed so a stretch of course can be walked once as a measurement and then grafted onto
        /// another — see <c>MountainPassCourse</c>, which uses it to work out what start heading puts the
        /// end of its valley approach exactly on the pass it feeds.
        /// </summary>
        public float HeadingDegrees => headingDegrees;

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
        ///
        /// <para>An arc shorter than <see cref="PointSpacing"/> × 0.4 moves the pose but emits nothing —
        /// see the note beside that test for why a corner too short to hold control points is worse than
        /// no corner at all.</para>
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

            // A corner shorter than a fraction of a control point's spacing gets no control points at
            // all, and the pose is carried across it instead.
            //
            // The step count below has a floor of two, so an arc of a metre puts two points half a metre
            // apart with ten-metre neighbours either side — and a Catmull-Rom through that is not a
            // road. Its parameterisation stops being anything like arc length across the short span, so
            // the tangent swings through it and every reader of the curve believes there is a corner
            // there: <c>RoadPathExtensions.GetRadiusAtDistance</c> reports centimetres, and
            // <c>RoadShape</c>'s banking rolls the carriageway right over on the strength of it.
            //
            // Not hypothetical. <see cref="ConnectTo"/> emits a micro-arc whenever the solve is nearly
            // exact — which is exactly what a well-authored closure is — and the Bahçe Ring's put a
            // 1.6 m radius across its own start line, on the fastest part of the lap, with the build
            // reporting it only as one number in <c>ReportCourse</c>'s "tightest radius".
            if (arcLength < PointSpacing * 0.4f)
            {
                Vector3 carried = center + Quaternion.Euler(0f, angleDegrees, 0f) * (position - center);
                carried.y = position.y + rise;

                position = carried;
                headingDegrees += angleDegrees;
                traveled += arcLength;
                return this;
            }
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

        /// <summary>
        /// Walks from wherever the builder stands to a fixed position and heading, as a corner, a
        /// straight and a second corner, all at <paramref name="radius"/>.
        ///
        /// <para><b>Why this had to exist.</b> Every road in the world so far is grafted onto the end of
        /// the one before it and then simply stops, so its far end is wherever the instruction table
        /// left it. Two courses solve the inverse — <see cref="MountainPassCourse.StartPoint"/> and
        /// <c>AutobahnCourse</c>'s two junction solves both work out what <i>start</i> puts a walked
        /// shape's end somewhere fixed. Neither trick helps a road with <b>both</b> ends nailed down,
        /// which is what every road that closes a loop is: it leaves one course at a pose that course
        /// decides, and it has to arrive at another at a pose that one decides. The alternative is
        /// hand-tuning a table of straights and turns until it lands within a metre of the target, and
        /// then re-tuning it every time either end moves — which is a second copy of a road, kept in
        /// step by hand.</para>
        ///
        /// <para><b>It is a Dubins CSC solve and nothing cleverer.</b> Four families — turn right,
        /// straight, turn right; left-straight-left; and the two that cross over — each of which is one
        /// closed-form expression, and the shortest one that exists wins. There is no search and no
        /// tolerance: the tangent points are exact, so the walk ends on the target rather than near it.
        /// What this deliberately cannot do is choose a <i>nice</i> route. It gives the shortest legal
        /// one at the radius asked for, which is why it is meant for the last few hundred metres of a
        /// road whose character was authored by hand above it, and not for a whole leg.</para>
        ///
        /// <para>The grade is derived rather than passed: the planar length is only known once the solve
        /// is done, and a road that arrived at the right place at the wrong height would be a step at
        /// the join. Uniform over all three segments, so there is no kink in the profile either.</para>
        /// </summary>
        /// <param name="target">Where to arrive. Its Y is the elevation to arrive at.</param>
        /// <param name="targetHeadingDegrees">Which way to be facing there, in the builder's convention.</param>
        /// <param name="radius">Radius of both corners. Sets how tight the connection is allowed to be.</param>
        public RoadCourseBuilder ConnectTo(Vector3 target, float targetHeadingDegrees, float radius)
        {
            if (radius <= 0.01f)
            {
                Debug.LogError("[Horizon] ConnectTo needs a radius to turn at. Nothing was built, so the "
                               + "course now ends short of where it was asked to reach.");
                return this;
            }

            if (!Solve(target, targetHeadingDegrees, radius,
                    out float firstAngle, out float straight, out float secondAngle))
            {
                Debug.LogError($"[Horizon] ConnectTo cannot reach ({target.x:0}, {target.z:0}) facing "
                               + $"{targetHeadingDegrees:0}° from ({position.x:0}, {position.z:0}) facing "
                               + $"{headingDegrees:0}° at a {radius:0} m radius — the two poses are closer "
                               + "together than the turns need. Open the radius out, or move the start of "
                               + "the connection further back up its own road.");
                return this;
            }

            // The whole connection's length, so one grade carries all three segments.
            float length = radius * (Mathf.Abs(firstAngle) + Mathf.Abs(secondAngle)) * Mathf.Deg2Rad
                           + straight;

            float grade = length > 0.01f ? (target.y - position.y) / length * 100f : 0f;

            Turn(radius, firstAngle, grade);
            Straight(straight, grade);
            Turn(radius, secondAngle, grade);

            return this;
        }

        /// <summary>
        /// Runs the walk back into its own start pose, turning the course into a circuit.
        ///
        /// <para><b>This is the only way to build a closed road here, and it is deliberately not just a
        /// <see cref="ConnectTo"/> call at the end of a table.</b> Three things have to happen together
        /// and each of them fails silently on its own.</para>
        ///
        /// <para><b>One: the solve can come out as a loop of carriageway and report success.</b>
        /// <see cref="TurnBy"/> goes the long way round rather than crossing zero, so the shortest
        /// Dubins family that <i>exists</i> can turn through three hundred degrees — geometrically
        /// exact, arriving on the target to the millimetre, logging nothing and validating cleanly.
        /// <c>StadtfeldCourse</c> records what that costs on an open road; on a circuit it is a lap with
        /// a spare circle in it. <paramref name="limit"/> is the guard, and it is not optional.</para>
        ///
        /// <para><b>Two: a self-closure can degenerate and build nothing at all.</b> The target pose is
        /// this walk's own start, so if the table above ends near it the two turning circles coincide,
        /// <see cref="TrySame"/> bails out as concentric and <see cref="Solve"/> can fail outright.
        /// <see cref="ConnectTo"/> then logs and emits nothing, which leaves a circuit with a gap in it.
        /// Author the lap to arrive a few hundred metres short of the line and roughly on its bearing,
        /// then close.</para>
        ///
        /// <para><b>Three: the last control point has to go.</b> The solve lands exactly on
        /// <c>points[0]</c>, and a looping <c>RoadPath</c> generates the segment from the last point back
        /// to the first itself — so leaving the duplicate in gives a zero-length span at the one place
        /// on the road every lap crosses. Trimming it here rather than at the call site is the point of
        /// this method: it is the sort of thing that would otherwise be re-derived, differently, in each
        /// place that ever paved a circuit.</para>
        /// </summary>
        /// <param name="radius">Radius of both corners in the solve.</param>
        /// <param name="limit">
        /// Longest closure to accept, metres. Anything past it is the three-hundred-degree family and
        /// wants the instructions above retuned, not a wider radius.
        /// </param>
        public RoadCourseBuilder Close(float radius, float limit)
        {
            float before = traveled;
            int pointsBefore = points.Count;

            ConnectTo(startPosition, startHeadingDegrees, radius);

            float connected = traveled - before;

            if (points.Count == pointsBefore)
            {
                Debug.LogError(
                    $"[Horizon] The circuit's closing solve emitted nothing from ({position.x:0}, "
                    + $"{position.z:0}) facing {headingDegrees:0}°. Its target is this walk's own start, "
                    + "so the two turning circles are close enough to be concentric and the solve has no "
                    + "family left. The road now stops short of its own start line and nothing "
                    + "downstream will say so. End the table further from the line, or on a bearing "
                    + "nearer to it.");

                return this;
            }

            if (connected > limit)
            {
                Debug.LogError(
                    $"[Horizon] The circuit's closing solve came out {connected:0} m long against a "
                    + $"{limit:0} m limit. ConnectTo takes the shortest Dubins family that exists and one "
                    + "of them loops the long way round, so this is a lap with a circle in it rather "
                    + "than a lap that failed to build. Retune the instructions above so the walk ends "
                    + "nearer the start line and closer to its heading.");
            }

            // The solve lands on points[0]; a looping RoadPath draws that segment itself.
            points.RemoveAt(points.Count - 1);

            closed = true;
            return this;
        }

        /// <summary>
        /// The four CSC families, in plan. Returns the shortest that exists.
        ///
        /// <para>Kept apart from <see cref="ConnectTo"/> because it is pure trigonometry over two poses
        /// and touches none of the builder's state — which is also what makes it testable by reading.</para>
        /// </summary>
        private bool Solve(
            Vector3 target,
            float targetHeadingDegrees,
            float radius,
            out float firstAngle,
            out float straight,
            out float secondAngle)
        {
            var from = new Vector2(position.x, position.z);
            var to = new Vector2(target.x, target.z);

            // A right turn puts its centre one radius to the right of the pose, which is the same thing
            // Turn does with a positive angle. The left circles are the same statement negated.
            Vector2 rightFrom = RightOf(headingDegrees);
            Vector2 rightTo = RightOf(targetHeadingDegrees);

            Vector2 rr0 = from + rightFrom * radius;
            Vector2 ll0 = from - rightFrom * radius;
            Vector2 rr1 = to + rightTo * radius;
            Vector2 ll1 = to - rightTo * radius;

            firstAngle = 0f;
            straight = 0f;
            secondAngle = 0f;

            float best = float.MaxValue;

            // --- Same-handed pairs. The straight runs parallel to the line joining the two centres, so
            // its length is simply that distance and there is no configuration that fails.
            TrySame(rr0, rr1, radius, +1f, targetHeadingDegrees, ref best,
                ref firstAngle, ref straight, ref secondAngle);

            TrySame(ll0, ll1, radius, -1f, targetHeadingDegrees, ref best,
                ref firstAngle, ref straight, ref secondAngle);

            // --- Cross-handed pairs. The straight cuts between the circles, which needs them at least
            // two radii apart — that is the case this returns false for.
            TryCross(rr0, ll1, radius, +1f, targetHeadingDegrees, ref best,
                ref firstAngle, ref straight, ref secondAngle);

            TryCross(ll0, rr1, radius, -1f, targetHeadingDegrees, ref best,
                ref firstAngle, ref straight, ref secondAngle);

            return best < float.MaxValue;
        }

        /// <summary>Turn one way, run straight, turn the same way again.</summary>
        /// <param name="sense">+1 for a pair of right-handers, −1 for a pair of left-handers.</param>
        private void TrySame(
            Vector2 centreFrom,
            Vector2 centreTo,
            float radius,
            float sense,
            float targetHeadingDegrees,
            ref float best,
            ref float firstAngle,
            ref float straight,
            ref float secondAngle)
        {
            Vector2 between = centreTo - centreFrom;
            float span = between.magnitude;

            if (span < 0.001f)
            {
                // Concentric: the two poses are on the same circle, so there is no straight between them
                // and this family degenerates. One of the others will answer.
                return;
            }

            float runHeading = HeadingOf(between / span);

            float first = TurnBy(headingDegrees, runHeading, sense);
            float second = TurnBy(runHeading, targetHeadingDegrees, sense);

            Keep(radius, first, span, second, ref best, ref firstAngle, ref straight, ref secondAngle);
        }

        /// <summary>Turn one way, run straight, turn back the other.</summary>
        /// <param name="sense">+1 for right then left, −1 for left then right.</param>
        private void TryCross(
            Vector2 centreFrom,
            Vector2 centreTo,
            float radius,
            float sense,
            float targetHeadingDegrees,
            ref float best,
            ref float firstAngle,
            ref float straight,
            ref float secondAngle)
        {
            Vector2 between = centreTo - centreFrom;
            float span = between.magnitude;

            // Any closer and the internal tangent does not exist — the circles overlap, and there is no
            // straight that touches both from opposite sides.
            if (span < 2f * radius)
            {
                return;
            }

            float run = Mathf.Sqrt(span * span - 4f * radius * radius);

            // The straight leans off the line joining the centres by however much the two radii it has
            // to cross demand. Positive sense crosses one way, negative the other, and that sign is the
            // whole difference between the two cross-handed families.
            float lean = Mathf.Atan2(2f * radius, run) * Mathf.Rad2Deg;
            float runHeading = HeadingOf(between / span) + sense * lean;

            float first = TurnBy(headingDegrees, runHeading, sense);
            float second = TurnBy(runHeading, targetHeadingDegrees, -sense);

            Keep(radius, first, run, second, ref best, ref firstAngle, ref straight, ref secondAngle);
        }

        /// <summary>Keeps a candidate if it is shorter than the best so far.</summary>
        private static void Keep(
            float radius,
            float first,
            float run,
            float second,
            ref float best,
            ref float firstAngle,
            ref float straight,
            ref float secondAngle)
        {
            float length = radius * (Mathf.Abs(first) + Mathf.Abs(second)) * Mathf.Deg2Rad + run;

            if (length >= best)
            {
                return;
            }

            best = length;
            firstAngle = first;
            straight = run;
            secondAngle = second;
        }

        /// <summary>
        /// How far to turn from one heading to another in a given direction, always the long way round
        /// rather than the short one when the short one goes the wrong way.
        ///
        /// <para>Signed the way <see cref="Turn"/> wants it, and never zero-crossing: a right-hander
        /// asked to go from 10° to 0° turns 350° rather than −10°, because the circle it is on only
        /// goes one way. Getting this backwards produces a connection that is geometrically exact and
        /// drives the wrong way round a loop.</para>
        /// </summary>
        private static float TurnBy(float fromDegrees, float toDegrees, float sense)
        {
            float delta = Mathf.Repeat((toDegrees - fromDegrees) * sense, 360f);
            return delta * sense;
        }

        /// <summary>The heading's own right, in plan. Matches what <see cref="Right"/> gives in 3D.</summary>
        private static Vector2 RightOf(float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians));
        }

        /// <summary>The heading a plan direction faces, in the builder's convention.</summary>
        private static float HeadingOf(Vector2 direction)
        {
            return Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
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

        /// <summary>
        /// Marks a filling station at the current distance, on the given side.
        ///
        /// <para>Placed by where it falls in the walk rather than by a distance somebody counted, for
        /// the same reason the viewpoints are: the courses are retuned, and a literal 1150 would rot the
        /// first time a bend above it changed radius. Where a station has to sit part-way along a
        /// straight, split the straight — two of 300 and 340 emit exactly the same control points as one
        /// of 640, because the spacing divides both.</para>
        /// </summary>
        /// <param name="side">−1 for the left of the direction of travel, +1 for the right.</param>
        public RoadCourseBuilder AddFuelStation(string name, float side)
        {
            features.Add(
                new RoadFeature(RoadFeatureKind.FuelStation, traveled, traveled, name, Mathf.Sign(side)));

            return this;
        }

        /// <summary>
        /// Marks a fork at the current distance, where another course leaves this one.
        ///
        /// <para>Placed by where it falls in the walk rather than by a counted distance, for the reason
        /// <see cref="AddFuelStation"/> gives — and it matters more here, because a fork is the one
        /// feature two courses have to agree about. Where the branch is grafted onto this pose, the
        /// branch reads the pose and this records it; a literal in either file would be a fork that
        /// moved on one road and not the other.</para>
        ///
        /// <para><b>The track through a fork has to be straight and level.</b> The throat is laid on
        /// top of both carriageways at <c>MotorwayMergeBuilder.Lift</c>, and laid-on paving only sits
        /// flush where the surface under it has no camber to follow — see <c>FuelStationMeshes</c> for
        /// the commit that unpicked the alternative. Split a straight around this rather than marking a
        /// fork inside a bend or on a grade.</para>
        /// </summary>
        public RoadCourseBuilder AddJunction(string name)
        {
            features.Add(new RoadFeature(RoadFeatureKind.Junction, traveled, traveled, name));
            return this;
        }

        public RoadCourse Build()
        {
            return new RoadCourse(
                new List<Vector3>(points), new List<RoadFeature>(features), traveled, closed);
        }
    }
}
