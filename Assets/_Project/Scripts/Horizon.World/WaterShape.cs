using UnityEngine;

namespace Horizon.World
{
    public enum WaterKind
    {
        River = 0,
        Lake = 1,
        Sea = 2,
    }

    /// <summary>
    /// One body of water, resolved: where it is, how far its bank reaches, and what height its
    /// surface came out at.
    ///
    /// <para><b>A spine and a half-width, not a polygon.</b> Both things that read this — the height
    /// field carving the basin and the tile builder laying the surface — ask exactly one question:
    /// how far outside the water is this point. A lake answers that from a centre and a radius, a
    /// river from a polyline and a channel width, and a polygon would answer it more slowly for
    /// both. One point in the spine means a lake; more means a river.</para>
    ///
    /// <para><b>The surface height is solved, never typed.</b> A hand-written value for the mountain
    /// tarn leaks: a hundred metres off the pass the ground is pure <c>MountainAt</c>, which is the
    /// road's own elevation seen from far away plus ±31 m of noise, so a literal that sits nicely
    /// today sits in mid-air the next time somebody opens out a bend. <c>WaterPlan</c> below
    /// describes a body by where it is and how deep it should be; the setup tool samples the rim
    /// before carving anything and derives the surface from what it finds. Same reasoning as the
    /// spawn table being distances along a course rather than coordinates.</para>
    /// </summary>
    public readonly struct WaterBody
    {
        public readonly string Name;

        public readonly WaterKind Kind;

        /// <summary>Lake centre, or river centreline. Plan coordinates — X and Z.</summary>
        public readonly Vector2[] Spine;

        /// <summary>Lake radius, or half the river channel.</summary>
        public readonly float HalfWidth;

        /// <summary>Over how many metres beyond the edge the bank climbs back to natural ground.</summary>
        public readonly float BankEase;

        public readonly float SurfaceY;

        /// <summary>How far the bed sits below <see cref="SurfaceY"/> at the middle.</summary>
        public readonly float Depth;

        /// <summary>
        /// Plan bounds including the bank, cached.
        ///
        /// <para>Every terrain vertex in the world asks every body whether it is near it. That is
        /// 395 tiles × 225 corners against however many bodies exist, and a rectangle test that
        /// rejects almost all of them before any distance maths happens is the difference between
        /// this being free and it being a second on every rebuild.</para>
        /// </summary>
        public readonly Bounds Plan;

        public WaterBody(
            string name,
            WaterKind kind,
            Vector2[] spine,
            float halfWidth,
            float bankEase,
            float surfaceY,
            float depth)
        {
            Name = name;
            Kind = kind;
            Spine = spine;
            HalfWidth = halfWidth;
            BankEase = bankEase;
            SurfaceY = surfaceY;
            Depth = depth;

            float reach = halfWidth + bankEase;

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            for (int i = 0; i < spine.Length; i++)
            {
                min = Vector2.Min(min, spine[i]);
                max = Vector2.Max(max, spine[i]);
            }

            var centre = new Vector3((min.x + max.x) * 0.5f, surfaceY, (min.y + max.y) * 0.5f);
            var size = new Vector3(
                max.x - min.x + reach * 2f, Mathf.Max(1f, depth * 2f), max.y - min.y + reach * 2f);

            Plan = new Bounds(centre, size);
        }

        /// <summary>True if this point is anywhere near the water, bank included. Cheap rejection.</summary>
        public bool Near(float x, float z)
        {
            return x >= Plan.min.x && x <= Plan.max.x && z >= Plan.min.z && z <= Plan.max.z;
        }

        /// <summary>
        /// How far outside the open water this point lies, in metres. Zero anywhere the surface
        /// covers, rising through the bank.
        /// </summary>
        public float DistanceOutside(float x, float z)
        {
            if (Spine == null || Spine.Length == 0)
            {
                return float.MaxValue;
            }

            var point = new Vector2(x, z);
            float best = float.MaxValue;

            if (Spine.Length == 1)
            {
                best = Vector2.Distance(point, Spine[0]);
            }
            else
            {
                for (int i = 1; i < Spine.Length; i++)
                {
                    best = Mathf.Min(best, DistanceToSegment(point, Spine[i - 1], Spine[i]));
                }
            }

            return Mathf.Max(0f, best - HalfWidth);
        }

        /// <summary>
        /// Where the bed sits under this point.
        ///
        /// <para>Deepest along the spine and shallowing to the surface at the edge, so the basin is a
        /// dish rather than a bathtub. That matters for exactly one reason and it is not realism: the
        /// tile builder reads the carved ground back to decide how dark the water is, so the shape of
        /// the bed <i>is</i> the shading. A flat bed gives a flat colour with a hard rim.</para>
        /// </summary>
        public float BedAt(float x, float z)
        {
            if (Spine == null || Spine.Length == 0)
            {
                return SurfaceY;
            }

            var point = new Vector2(x, z);
            float best = float.MaxValue;

            if (Spine.Length == 1)
            {
                best = Vector2.Distance(point, Spine[0]);
            }
            else
            {
                for (int i = 1; i < Spine.Length; i++)
                {
                    best = Mathf.Min(best, DistanceToSegment(point, Spine[i - 1], Spine[i]));
                }
            }

            float across = HalfWidth > 0.01f ? Mathf.Clamp01(best / HalfWidth) : 1f;

            // Cosine rather than linear: a straight taper meets the bank at an angle you can see as a
            // crease in the shading, and this is the one place the bed's shape is visible.
            return SurfaceY - Depth * Mathf.Cos(across * Mathf.PI * 0.5f);
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 along = b - a;
            float lengthSqr = along.sqrMagnitude;

            if (lengthSqr < 0.0001f)
            {
                return Vector2.Distance(point, a);
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, along) / lengthSqr);
            return Vector2.Distance(point, a + along * t);
        }
    }

    /// <summary>
    /// How a body of water is <i>authored</i>, before the height field has been asked where the
    /// ground is.
    ///
    /// <para>A river is described against the bridge it runs under rather than in world coordinates,
    /// for the same reason the spawn points are distances along a course: the viaducts move whenever
    /// somebody retunes the motorway, and a river left behind at a coordinate would be a river beside
    /// a bridge. <see cref="BridgeName"/> is matched against the course's own feature names —
    /// 'Wiesentalbrücke' and 'Talbrücke Hochfeld'.</para>
    /// </summary>
    public readonly struct WaterPlan
    {
        public readonly string Name;

        public readonly WaterKind Kind;

        /// <summary>Which bridge this runs under, for a river. Null for a lake or the sea.</summary>
        public readonly string BridgeName;

        /// <summary>Lake centre in plan. Ignored for a river, which takes its place from the bridge.</summary>
        public readonly Vector2 Centre;

        public readonly float HalfWidth;

        public readonly float BankEase;

        /// <summary>How far a river reaches either side of the road it crosses.</summary>
        public readonly float Reach;

        /// <summary>Degrees the river crosses the road at, away from square on.</summary>
        public readonly float SkewDegrees;

        public readonly float Depth;

        /// <summary>
        /// How far the surface is set below the lowest point of the sampled rim.
        ///
        /// <para>Freeboard, and it is what stops the water leaking out of its own basin. The rim is
        /// sampled from the finished field before anything is carved, so this is a promise about
        /// ground that actually exists rather than about ground somebody expected.</para>
        /// </summary>
        public readonly float Freeboard;

        public WaterPlan(
            string name,
            WaterKind kind,
            string bridgeName,
            Vector2 centre,
            float halfWidth,
            float bankEase,
            float reach,
            float skewDegrees,
            float depth,
            float freeboard)
        {
            Name = name;
            Kind = kind;
            BridgeName = bridgeName;
            Centre = centre;
            HalfWidth = halfWidth;
            BankEase = bankEase;
            Reach = reach;
            SkewDegrees = skewDegrees;
            Depth = depth;
            Freeboard = freeboard;
        }

        /// <summary>A river under a named bridge, crossing the road it spans.</summary>
        public static WaterPlan River(
            string name, string bridgeName, float halfWidth, float bankEase, float reach,
            float skewDegrees, float depth)
        {
            return new WaterPlan(name, WaterKind.River, bridgeName, Vector2.zero,
                halfWidth, bankEase, reach, skewDegrees, depth, 1.5f);
        }

        /// <summary>A lake at a place, as a centre and a radius.</summary>
        public static WaterPlan Lake(
            string name, Vector2 centre, float radius, float bankEase, float depth)
        {
            return new WaterPlan(name, WaterKind.Lake, null, centre,
                radius, bankEase, 0f, 0f, depth, 1.5f);
        }
    }

    /// <summary>
    /// Every body of water in the world.
    ///
    /// <para><b>Bounded bodies, and deliberately no global sea level.</b> The world is mostly a
    /// basin between −20 and −45 m with one mountain in it: 135 terrain tiles dip below −40 m
    /// somewhere and 21 below −50 m. A single water plane at any height that reached the coast would
    /// also fill every hollow it passed, and the motorway would spend eight kilometres running
    /// between puddles nobody put there. So each body carries its own surface and its own
    /// extent.</para>
    /// </summary>
    public static class WaterShape
    {
        /// <summary>
        /// A tarn tucked against a village, on the narrow side of it.
        ///
        /// <para><b>Derived from the town rather than typed beside it.</b> A hand-picked coordinate
        /// next to a settlement is only correct until the settlement is re-planned, and the failure is
        /// not subtle — the first Talheim lake drowned a street and six houses. This takes the town's
        /// own inner extent, steps a clear margin further out, and puts the water there, so it moves
        /// when the village does.</para>
        ///
        /// <para>The <i>inner</i> side, because these basins are asymmetric: Talheim reaches 260 m one
        /// way across the road and 90 m the other. The short side is the only one with room between the
        /// last plot and the edge of the terrain corridor.</para>
        /// </summary>
        /// <param name="freeboard">
        /// How far the surface is set below the rim. Bigger than the 1.5 m a lake normally takes,
        /// because a village basin is levelled on purpose and its streets sit barely above the ground
        /// around them — at 1.5 the water came within 1.8 m of the outermost street, and three metres
        /// is the least a road may stand clear of water.
        /// </param>
        /// <param name="acrossInner">
        /// The town's near edge, signed across the road — negative on the inner side. Comes straight
        /// from <c>TownShape.AcrossInner</c>.
        /// </param>
        public static WaterPlan BesideTown(
            string name,
            IRoadPath road,
            float alongRoad,
            float acrossInner,
            float radius,
            float bankEase,
            float depth,
            float freeboard)
        {
            // Clear of the bank, not just clear of the water — and that distinction is the whole of
            // why this needs saying. A flat twenty metres was tried and failed: the bank eases out over
            // forty, so the ground was still being pulled down under the outermost street and the lake
            // came within 1.6 m of it. The dry margin has to start where the carving stops.
            float margin = bankEase + 15f;

            // Written out rather than folded into a signed expression: the lake goes *further out*
            // than the town edge on whichever side that edge already is, which for a negative inner
            // edge means more negative still.
            float across = acrossInner < 0f
                ? acrossInner - margin - radius
                : acrossInner + margin + radius;

            Vector3 centre = road.GetPositionAtDistance(alongRoad)
                             + road.GetRightAtDistance(alongRoad) * across;

            return new WaterPlan(name, WaterKind.Lake, null, new Vector2(centre.x, centre.z),
                radius, bankEase, 0f, 0f, depth, freeboard);
        }

        /// <summary>
        /// The four bodies inside the existing road corridor. None of them costs a terrain tile.
        ///
        /// <para>The rivers are the point of the exercise: both viaducts were drawn with 280 and
        /// 320 m of span and nine to twenty-two metres of air under them, and they have been standing
        /// over dry grass since they were built.</para>
        /// </summary>
        public static readonly WaterPlan[] Corridor =
        {
            // Channel plus bank must stay inside half the span less 20 m, or the bank fights the
            // abutment shelf the bridge builder puts in. 25 + 70 = 95 against a 140 m half-span.
            //
            // The reach is 200 m either side rather than the 340 first tried. At 340 the water ran
            // 680 m dead straight through a valley barely wider than the viaduct, which reads as a
            // canal somebody cut, not as a river the bridge was built over. 400 m still fills the
            // valley the span was drawn for and stops while it is still a river.
            WaterPlan.River("Wiesen", "Wiesentalbrücke",
                halfWidth: 25f, bankEase: 70f, reach: 200f, skewDegrees: 18f, depth: 4f),

            WaterPlan.River("Hochfelder Bach", "Talbrücke Hochfeld",
                halfWidth: 18f, bankEase: 55f, reach: 200f, skewDegrees: -12f, depth: 3.5f),

            // The lake at Talheim is not in this table, and cannot be: see BesideTown below. It is
            // derived from the village's own extent at build time and appended there.
            //
            // The first attempt was in here, as (-250, -480) with a 110 m radius, described as 'just
            // clear of the town's plots'. It was not clear of them at all — Talheim's streets run
            // x -256..88 by z -632..-113 and that circle covered a quarter of them, so a street and
            // half a dozen houses came out standing in water. The build was perfectly happy about it:
            // the surface solved level, the shading came out right, and the clearance check walked only
            // the four trunk roads.
            //
            // (-250, -480) with a 110 m radius was described as 'just clear of the town's plots'. It
            // is not clear of them at all: Talheim's streets run x -256..88 by z -632..-113, and that
            // circle covers a quarter of them. The build was perfectly happy — the surface solved
            // level, the shading came out right, and a street and half a dozen houses stood in it.
            //
            // Two things have to be true before it comes back. The clearance validator has to check
            // the *town* streets, not only the four trunk roads it walks today; and the placement has
            // to be derived from the town's own footprint rather than typed, the way the rivers are
            // derived from the bridges they run under. Nothing about a hand-picked coordinate beside a
            // village survives the village being re-planned.

            // Well off the pass, and further out than instinct says.
            //
            // The first attempt put this at (470, 140) with an 85 m radius, which reads on a map as
            // 'beside the summit' and is in fact within a stone's throw of the carriageway at 4450 m
            // along — the solver gave it a perfectly level surface 0.9 m *over* the tarmac. The
            // clearance validator caught it, which is the whole reason that check exists: a drowned
            // road is invisible to every other check in the build, because the road mesh is laid from
            // the course and never asks the ground anything.
            WaterPlan.Lake("Bergsee", new Vector2(640f, 330f),
                radius: 70f, bankEase: 45f, depth: 4f),
        };
    }
}
