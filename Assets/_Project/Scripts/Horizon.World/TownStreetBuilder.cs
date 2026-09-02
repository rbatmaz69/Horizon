using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The cross-section of a town street: carriageway, kerb, footway.
    ///
    /// A town street wants two things the trunk road has no concept of — a kerb and a pavement — and
    /// wants none of what the trunk road carries. <see cref="RoadMeshBuilder"/> has a marking atlas whose
    /// v coordinate is arc length, row doubling where the marking variant changes, banking from measured
    /// curvature, and nine vertices every 2.5 m. None of that belongs on a street where the limit is
    /// 30 km/h, all of it would have to keep working through every change made here, and a dashed centre
    /// line down a lane would make it read as a main road.
    /// </summary>
    public struct TownStreetShape
    {
        [Tooltip("Half the driveable width, metres.")]
        public float HalfWidth;

        [Tooltip("How far the top of the kerb stands above the gutter, metres.")]
        public float KerbHeight;

        [Tooltip("Horizontal width of the kerb face itself. Not zero: a vertical face with no width "
               + "renders as a crack, and the strip needs an outward direction to face.")]
        public float KerbFace;

        [Tooltip("Width of the footway behind the kerb, metres.")]
        public float FootwayWidth;

        [Tooltip("Distance between cross-sections. 5 m, not the trunk road's 2.5 — a 30 km/h street with "
               + "no banking and no markings has nothing to facet.")]
        public float StepLength;

        [Tooltip("Where the carriageway sits relative to the street's own centreline height, metres.\n\n"
               + "Normally negative, and that is the whole point. A street path runs at "
               + "TownShape.FloorHeight, but MountainField puts the ground TerrainShape.RoadShelfDrop "
               + "*below* every road and level sample it is given — so a surface built at the path's own "
               + "height stands half a metre proud of the grass beside it, with a vertical face. The "
               + "trunk road hides that behind a 1.5 m shoulder; a town street has no shoulder, and the "
               + "result was a network of plateaux you could drive off but not back onto.\n\n"
               + "How far it clears the shelf is the other half, and it is not free to choose: the "
               + "terrain you see is the field interpolated across TerrainShape.CellSize, and that mesh "
               + "can stand a sixth of a metre above the field near a street. Clear the shelf by less "
               + "than that and the grass grows up through the road. See ValidateStreetClearance.")]
        public float SurfaceLift;

        [Tooltip("Camber: how much higher the centre of the carriageway sits than its gutters.")]
        public float Crown;

        [Tooltip("Width of the grass verge outside the footway, metres. It runs from the back of the "
               + "pavement down to wherever the terrain mesh actually is, which is the only way to be "
               + "flush with it — the shelf the height field lays under a street is not exactly where "
               + "arithmetic says it should be, and the difference is a lip you cannot drive back over.")]
        public float VergeWidth;

        /// <summary>Half the width of everything paved, kerbs and footways included.</summary>
        public float HalfOuter => HalfWidth + KerbFace + FootwayWidth;

        /// <summary>
        /// How far a street's carriageway stands above the shelf <see cref="MountainField"/> lays under
        /// it, metres.
        ///
        /// <para><b>0.26, and it was 0.08.</b> At 0.08 the grass grew up through the road on eleven of
        /// the town's forty-one streets — not because the height field was wrong, but because the terrain
        /// is drawn as a linear interpolation of that field across twelve-metre cells and the mesh
        /// therefore rides above the field between lattice points. Measured, it rode up to 0.16 m above
        /// it near a street, against 0.08 m of room. The field-based clearance check could never see it:
        /// the field is not what gets drawn.</para>
        ///
        /// <para>So this has to exceed the mesh's error, not merely be positive, and 0.26 leaves about a
        /// tenth of a metre in hand. <b>It is coupled to <see cref="TerrainShape.CellSize"/></b> — a
        /// coarser terrain grid interpolates over a longer span and misses by more, so raising that
        /// number means raising this one. <c>ValidateStreetClearance</c> measures the pair rather than
        /// trusting either.</para>
        ///
        /// <para>The cost is that a street now stands a hand above the grass beside it rather than a
        /// finger. That is what the 1.6 m verge is for: it ramps the difference away at about a quarter,
        /// which is a bank you can drive up, not the vertical half-metre plateau the negative lift was
        /// introduced to get rid of. <c>VergeGradient</c> holds the line at 0.6.</para>
        /// </summary>
        public const float ClearsTheShelf = 0.26f;

        /// <summary>
        /// The cross-section for a kind of street.
        ///
        /// The steps between the kinds are what make a street network legible from inside a car: you can
        /// tell you have turned off the high street without being told.
        /// </summary>
        /// <param name="shelfDrop">
        /// How far <see cref="MountainField"/> sets the ground below the roads it is given —
        /// <see cref="TerrainShape.RoadShelfDrop"/>. The carriageway is built that far down and
        /// <see cref="ClearsTheShelf"/> back up.
        /// </param>
        public static TownStreetShape For(TownStreetKind kind, float shelfDrop = 0f)
        {
            // Every carriageway here is a quarter wider than it was authored at, for the reason
            // RoadShape.Default gives: the cars grew a quarter in plan in 5bd7396 and what a driver
            // reads is how much of the street the car fills. Scaled from the *authored* widths, not
            // from the ones in the file at the time — Lane and Alley already carried an absolute
            // emergency bump from that commit, and scaling those again would have counted it twice.
            // The alley's proportional width happens to land on the 3.9 it was given.
            //
            // The kerb and the footway did not take it. A pavement is sized for people, and every
            // metre added to a SquareEdge is two metres off the market place — see that case below.
            // The buildings follow anyway, because TownPlanner sets its frontages back from HalfOuter.
            var shape = new TownStreetShape
            {
                HalfWidth = 5.0f,
                KerbHeight = 0.14f,
                KerbFace = 0.25f,
                FootwayWidth = 1.8f,
                StepLength = 5f,
                SurfaceLift = ClearsTheShelf - shelfDrop,
                Crown = 0.06f,
                VergeWidth = 1.6f,
            };

            switch (kind)
            {
                case TownStreetKind.HighStreet:
                    shape.HalfWidth = 6.75f;
                    shape.FootwayWidth = 3.2f;
                    shape.KerbHeight = 0.16f;
                    break;

                case TownStreetKind.Avenue:
                    shape.HalfWidth = 6.0f;
                    shape.FootwayWidth = 2.4f;
                    break;

                // The same width as the fallback above, and stated rather than left to fall through:
                // a lane is the ordinary residential street this town is mostly made of, and it should
                // say so where the other seven kinds do.
                case TownStreetKind.Lane:
                    shape.HalfWidth = 5.0f;
                    break;

                // 3.9 rather than the 3.1 this was authored at, and it arrived twice by two different
                // arguments landing on one number. It was raised to 3.9 in 5bd7396 as an absolute
                // margin — a 2.92 m car in a 3.1 m half width leaves 14 cm a side and 27 cm between two
                // of them passing — and 3.1 × 1.25 is 3.875, which is the same street. The narrowest
                // kind is the one where the proportional answer and the "a driver needs a margin in
                // metres" answer agree, which is worth knowing before either is retuned alone.
                case TownStreetKind.Alley:
                    shape.HalfWidth = 3.9f;
                    shape.FootwayWidth = 0.7f;
                    shape.KerbHeight = 0.10f;
                    break;

                // A 2.8 m pavement, not the 4.5 m this started at. The generous figure was double
                // counting: the square itself is thirty metres of paving on the other side of the kerb,
                // so the footway does not also have to be the widest in the town. It was also breaking
                // the junctions at both ends of it — a 8.75 m half-outer puts a street's outer corner
                // forty degrees off its own axis, and at a node where two branches are under fifty
                // degrees apart the corners cross and the pad polygon folds through itself.
                //
                // <b>This is the kind to pull back on first if a junction pad folds again.</b> The
                // streets round a square eat their own half-widths out of it at both ends, so every
                // metre added here is two metres off the market place, and these are the junctions that
                // were the last in the town to stop folding. It took the carriageway widening because
                // the cars have to fit round a square too; it did not take it in the footway.
                case TownStreetKind.SquareEdge:
                    shape.HalfWidth = 5.5f;
                    shape.FootwayWidth = 2.8f;
                    break;

                // Two lanes each way and a footway you could put café tables on. Note what this costs
                // at a junction: HalfOuter is 14.75 m against the high street's 10.2, and ResolveTrims
                // scales the third of its three terms with it, so a boulevard meeting anything at a
                // shallow angle pulls its trim back a long way. The city's grid is squared up for that
                // reason — a 90° crossing is the cheapest junction there is.
                case TownStreetKind.Boulevard:
                    shape.HalfWidth = 10.0f;
                    shape.FootwayWidth = 4.5f;
                    shape.KerbHeight = 0.17f;
                    shape.Crown = 0.09f;
                    break;

                case TownStreetKind.CityStreet:
                    shape.HalfWidth = 7.5f;
                    shape.FootwayWidth = 3.0f;
                    shape.KerbHeight = 0.16f;
                    break;
            }

            return shape;
        }
    }

    /// <summary>
    /// Turns a street centreline into a ribbon with kerbs and footways either side.
    ///
    /// <para>Everything the town's streets emit goes into <b>one</b> buffer and ends up as one mesh under
    /// one chunk with a radius large enough never to unload — not one mesh per edge, and not one per
    /// terrain tile. Three kilometres of street is about thirteen thousand triangles and three draw
    /// calls, which is cheap enough that splitting it could only cost. It also makes every
    /// seam-at-a-tile-boundary question disappear, and gives the whole network a single
    /// <c>MeshCollider</c>. Worth revisiting past about eight kilometres of street.</para>
    /// </summary>
    public static class TownStreetBuilder
    {
        /// <summary>Asphalt.</summary>
        public const int SurfaceSubmesh = 0;

        /// <summary>The vertical faces of the kerbs.</summary>
        public const int KerbSubmesh = 1;

        /// <summary>The footways.</summary>
        public const int FootwaySubmesh = 2;

        /// <summary>
        /// The grass verge that runs from the back of the pavement down onto the terrain.
        ///
        /// Its own submesh so it can take the grass material and disappear into the field, rather than
        /// reading as a paved shoulder a metre and a half wide.
        /// </summary>
        public const int VergeSubmesh = 3;

        /// <summary>
        /// Painted lines on the carriageway. Geometry rather than a texture.
        ///
        /// <para>The trunk road gets its markings from a baked atlas keyed on arc length, which works
        /// because it is one ribbon of one width. A street network is sixty ribbons of six widths meeting
        /// at thirty-seven junctions, and a shared atlas across that is a UV problem rather than a
        /// drawing one. Laid-on quads cost about two triangles a dash and land in the same merged
        /// submesh as everything else, so they are free at the draw call.</para>
        /// </summary>
        public const int MarkingSubmesh = 4;

        /// <summary>
        /// Traffic light masts, arms and heads — everything about a signal except the lenses.
        ///
        /// <para>In the street mesh rather than with the lenses, and the split is the whole trick. The
        /// body never changes colour, so it rides in the vertices like every other category here and
        /// <see cref="SurfaceTints"/> merges it away to nothing. Only the three lenses have to be
        /// swapped between dark and lit four times a cycle, and those are a handful of quads on their
        /// own renderer — see <c>TrafficSignalMeshes</c>. Putting the whole signal in the second mesh
        /// would have cost a draw call for the masts; putting the lenses in this one would have made
        /// them uncolourable.</para>
        /// </summary>
        public const int SignalBodySubmesh = 5;

        public const int StreetSubmeshCount = 6;

        /// <summary>
        /// The colour each street submesh is tinted with when they are merged into one.
        ///
        /// <para>Same trick as the buildings and the terrain: four flat untextured materials were four
        /// draw calls per town, and a category that is only ever a colour belongs in the vertices. The
        /// numbers are the ones M_Lane, M_Concrete, M_Footway and M_Grass carried, so the streets come
        /// out the colour they already were.</para>
        /// </summary>
        public static Color?[] SurfaceTints()
        {
            var tints = new Color?[StreetSubmeshCount];

            tints[SurfaceSubmesh] = new Color(0.27f, 0.27f, 0.29f);
            tints[KerbSubmesh] = new Color(0.52f, 0.51f, 0.49f);
            tints[FootwaySubmesh] = new Color(0.60f, 0.58f, 0.55f);
            tints[VergeSubmesh] = new Color(0.36f, 0.48f, 0.26f);
            tints[MarkingSubmesh] = new Color(0.82f, 0.80f, 0.74f);

            // Dark grey, not black: a black mast against a low sun is a silhouette with no form in it,
            // and this world's shading is doing all its work in the midtones.
            tints[SignalBodySubmesh] = new Color(0.16f, 0.16f, 0.17f);

            return tints;
        }

        /// <summary>Length of a painted dash and of the gap after it, metres.</summary>
        private const float DashLength = 3f;

        private const float DashGap = 5f;

        /// <summary>Width of a painted line, metres.</summary>
        private const float LineWidth = 0.14f;

        /// <summary>How far a marking floats above the carriageway so it never z-fights with it.</summary>
        private const float MarkingLift = 0.015f;

        /// <summary>
        /// Paints a street's lane lines: a dashed line down the middle, and one between each pair of
        /// lanes on anything wide enough to have them.
        ///
        /// <para>Only the wide kinds are marked. A village lane with a dashed centre line reads as a main
        /// road — which is the reason the street material was left untextured in the first place — so
        /// this is a city thing, and <see cref="LaneLinesFor"/> is where that judgement lives.</para>
        /// </summary>
        public static void AppendMarkings(
            IRoadPath path,
            in TownStreetShape shape,
            TownStreetKind kind,
            float fromDistance,
            float toDistance,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            float[] lines = LaneLinesFor(kind, shape);
            if (lines.Length == 0 || path == null || into == null)
            {
                return;
            }

            float from = Mathf.Clamp(fromDistance, 0f, path.Length);
            float to = Mathf.Clamp(toDistance, 0f, path.Length);

            float cycle = DashLength + DashGap;

            // The approach zones at either end, which go solid. Kept off a street too short to hold two
            // of them plus a dash between: on a forty-metre link the whole thing would be solid, which
            // says "no overtaking for the next forty metres" about a street you cross in four seconds.
            float approach = (to - from) > SolidApproach * 2f + cycle ? SolidApproach : 0f;

            float dashFrom = from + approach;
            float dashTo = to - approach;

            for (int i = 0; i < lines.Length; i++)
            {
                if (approach > 0f)
                {
                    // Solid for the last few metres into a junction, on every line rather than only the
                    // centre one: what the solid stretch says is "you are committed to this lane now",
                    // and that applies as much to a boulevard's lane dividers as to its middle.
                    AppendSolidLine(path, shape, lines[i], from, dashFrom, into);
                    AppendSolidLine(path, shape, lines[i], dashTo, to, into);
                }

                // Started half a gap in, so a dash never begins flush against a junction pad.
                for (float at = dashFrom + DashGap * 0.5f; at + DashLength <= dashTo; at += cycle)
                {
                    AppendDash(path, shape, lines[i], at, at + DashLength, field, terrainShape, into);
                }
            }
        }

        /// <summary>How far a line runs solid before a junction, metres.</summary>
        private const float SolidApproach = 15f;

        /// <summary>An unbroken line down a street, in the same paint as the dashes.</summary>
        public static void AppendSolidLine(
            IRoadPath path,
            in TownStreetShape shape,
            float across,
            float from,
            float to,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null || to - from < 0.05f)
            {
                return;
            }

            // Stepped rather than emitted as one long quad, or a line down a bowed street would cut the
            // corner and leave the carriageway. The step is the ribbon's own, so the paint bends exactly
            // where the surface under it does.
            int steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / Mathf.Max(1f, shape.StepLength)));

            for (int i = 0; i < steps; i++)
            {
                float a = Mathf.Lerp(from, to, i / (float)steps);
                float b = Mathf.Lerp(from, to, (i + 1) / (float)steps);

                AppendStripe(path, shape, across - LineWidth * 0.5f, across + LineWidth * 0.5f, a, b, into);
            }
        }

        /// <summary>
        /// The bar a car stops at, across the driver's own half of the carriageway.
        ///
        /// <para>Half rather than the full width, because the other half is the oncoming lane and it
        /// stops at its own line at the other end of the street. A stop line drawn all the way across
        /// reads as a level crossing.</para>
        /// </summary>
        /// <param name="rightHalf">
        /// Which side of the centreline the approaching traffic uses — the path's right for a lane
        /// travelling with the path, its left for the one coming back. Same sign convention
        /// <c>TrafficNetworkBuilder.AddStreetLane</c> offsets its lanes by, and for the same reason:
        /// getting it from the geometry rather than from the direction of travel is how half the
        /// markings end up on the wrong side.
        /// </param>
        public static void AppendStopLine(
            IRoadPath path,
            in TownStreetShape shape,
            float at,
            bool rightHalf,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float inner = rightHalf ? 0f : -shape.HalfWidth;
            float outer = rightHalf ? shape.HalfWidth : 0f;

            AppendStripe(path, shape, inner, outer, at, at + StopLineWidth, into);
        }

        /// <summary>Width of a stop line along the road, metres.</summary>
        public const float StopLineWidth = 0.32f;

        /// <summary>
        /// A pedestrian crossing: bars running <i>along</i> the street, across its full width.
        ///
        /// <para>Along rather than across, which is what makes it read as a crossing rather than as a
        /// wide stop line — the bars are what a pedestrian walks between, so they point the way the
        /// pedestrian is going, which is across the road and therefore along nothing the car sees.
        /// Purely decorative: nobody walks on it.</para>
        /// </summary>
        public static void AppendCrossing(
            IRoadPath path,
            in TownStreetShape shape,
            float from,
            float to,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float low = Mathf.Min(from, to);
            float high = Mathf.Max(from, to);

            // Bars sized to fit the carriageway exactly rather than laid at a fixed pitch from one kerb:
            // a fixed pitch leaves a sliver against the far kerb on every width that is not a multiple
            // of it, and this town has six of them.
            float width = shape.HalfWidth * 2f;
            int bars = Mathf.Max(3, Mathf.RoundToInt(width / (CrossingBarWidth * 2f)));
            float pitch = width / bars;

            for (int i = 0; i < bars; i++)
            {
                float centre = -shape.HalfWidth + (i + 0.5f) * pitch;
                AppendStripe(
                    path, shape,
                    centre - CrossingBarWidth * 0.5f, centre + CrossingBarWidth * 0.5f,
                    low, high, into);
            }
        }

        /// <summary>Width of one crossing bar, metres, and how deep the crossing runs along the road.</summary>
        public const float CrossingBarWidth = 0.45f;

        public const float CrossingDepth = 2.6f;

        /// <summary>One painted rectangle, given in across/along coordinates on the carriageway.</summary>
        private static void AppendStripe(
            IRoadPath path,
            in TownStreetShape shape,
            float acrossFrom,
            float acrossTo,
            float alongFrom,
            float alongTo,
            VegetationMeshBuffer into)
        {
            float riseFrom = SurfaceRiseAt(shape, acrossFrom) + MarkingLift;
            float riseTo = SurfaceRiseAt(shape, acrossTo) + MarkingLift;

            // Each edge of the stripe takes its own height, so a bar wide enough to span the camber —
            // a crossing bar, or a stop line across a whole half — lies on the road rather than
            // bridging it.
            Vector3 a0 = PointAcross(path, shape, alongFrom, acrossFrom, riseFrom);
            Vector3 a1 = PointAcross(path, shape, alongFrom, acrossTo, riseTo);
            Vector3 b0 = PointAcross(path, shape, alongTo, acrossFrom, riseFrom);
            Vector3 b1 = PointAcross(path, shape, alongTo, acrossTo, riseTo);

            into.AddQuadFacing(MarkingSubmesh, a0, b0, b1, a1, Vector3.up);
        }

        /// <summary>
        /// Where the painted lines go across a street, as offsets from its centre.
        ///
        /// <para>A boulevard is two lanes each way, so it takes a centre line and one lane line either
        /// side of it; a city street is one lane each way and takes only the centre. Everything narrower
        /// takes nothing at all.</para>
        /// </summary>
        private static float[] LaneLinesFor(TownStreetKind kind, in TownStreetShape shape)
        {
            switch (kind)
            {
                case TownStreetKind.Boulevard:
                    return new[] { 0f, -shape.HalfWidth * 0.5f, shape.HalfWidth * 0.5f };

                case TownStreetKind.CityStreet:
                    return new[] { 0f };

                default:
                    return System.Array.Empty<float>();
            }
        }

        private static void AppendDash(
            IRoadPath path,
            in TownStreetShape shape,
            float across,
            float from,
            float to,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            AppendStripe(
                path, shape, across - LineWidth * 0.5f, across + LineWidth * 0.5f, from, to, into);
        }

        /// <summary>
        /// How high the carriageway sits at a point across it, above the street's own datum.
        ///
        /// <para><b>The camber, and every marking has to be told about it.</b> The paint used to be laid
        /// at <see cref="TownStreetShape.SurfaceLift"/> flat, one <see cref="MarkingLift"/> above the
        /// datum — but the carriageway is not flat, it is a tent: <see cref="CrossSection"/> puts a
        /// vertex at the centre <c>Crown</c> metres higher than the two gutters, and the surface between
        /// them is the straight line joining the three. So a centre line was painted a centimetre and a
        /// half over the datum onto a surface six centimetres above it, and a boulevard's was nine — the
        /// markings were not faint or z-fighting, they were <i>underneath the road</i>, on every marked
        /// street in the city.</para>
        ///
        /// <para>Linear rather than the parabola <c>TrafficNetworkBuilder.LanePoint</c> uses on the trunk
        /// road, because that road's ring has nine vertices and can afford a curve, and this one has
        /// three. Matching what the mesh actually does beats matching what a camber ideally is.</para>
        /// </summary>
        private static float SurfaceRiseAt(in TownStreetShape shape, float across)
        {
            float half = Mathf.Max(0.001f, shape.HalfWidth);
            float toEdge = Mathf.Clamp01(Mathf.Abs(across) / half);

            return shape.SurfaceLift + shape.Crown * (1f - toEdge);
        }

        /// <summary>Which submesh each of the eight strips across a section belongs to.</summary>
        private static readonly int[] StripSubmesh =
        {
            VergeSubmesh, FootwaySubmesh, KerbSubmesh, SurfaceSubmesh,
            SurfaceSubmesh, KerbSubmesh, FootwaySubmesh, VergeSubmesh,
        };

        /// <summary>Points across one section: two verges, two footways, two kerbs, two half carriageways.</summary>
        private const int SectionPoints = 9;

        /// <summary>
        /// Adds one street's ribbon between two distances along its path.
        ///
        /// The trimmed range matters: a street stops short of its junctions so the pad can fill the
        /// middle. Callers pass the trim points from <see cref="StreetJunctionBuilder.ResolveTrims"/>.
        /// </summary>
        public static void AppendStreet(
            IRoadPath path,
            in TownStreetShape shape,
            float fromDistance,
            float toDistance,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float from = Mathf.Clamp(fromDistance, 0f, path.Length);
            float to = Mathf.Clamp(toDistance, 0f, path.Length);
            if (to - from < 0.5f)
            {
                return;
            }

            int steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / Mathf.Max(1f, shape.StepLength)));

            var previous = new Vector3[SectionPoints];
            var current = new Vector3[SectionPoints];

            CrossSection(path, shape, from, field, terrainShape, previous);

            for (int step = 1; step <= steps; step++)
            {
                float at = Mathf.Lerp(from, to, step / (float)steps);
                CrossSection(path, shape, at, field, terrainShape, current);

                for (int strip = 0; strip < StripSubmesh.Length; strip++)
                {
                    // Outward is up for every strip but the kerb faces, which look sideways — inwards,
                    // at the carriageway they edge, which is the only side of a kerb anyone sees. Taken
                    // as the direction from the kerb towards the crown so it comes out right on both
                    // sides of the street; the first version derived it from the strip's own two points
                    // and was therefore correct on the left kerb and backwards on the right.
                    Vector3 outward = Vector3.up;
                    if (StripSubmesh[strip] == KerbSubmesh)
                    {
                        outward = current[4] - current[strip];
                        outward.y = 0f;
                    }

                    // Along first, then across: Cross(along, across) points up, and the reverse order
                    // wound every face in the network backwards. The buffer corrects them, which is
                    // exactly why it also counts them.
                    into.AddQuadFacing(
                        StripSubmesh[strip],
                        previous[strip], current[strip], current[strip + 1], previous[strip + 1],
                        outward);
                }

                (previous, current) = (current, previous);
            }
        }

        /// <summary>
        /// The seven points across a street at one distance: outer footway edge, kerb top, gutter, crown,
        /// gutter, kerb top, outer footway edge.
        ///
        /// The crown is a point of its own rather than a lift applied to the whole carriageway, because a
        /// flat surface raised bodily is a plate — the camber has to be visible in the silhouette of the
        /// section, which means the middle has to be a vertex.
        /// </summary>
        private static void CrossSection(
            IRoadPath path,
            in TownStreetShape shape,
            float distance,
            MountainField field,
            in TerrainShape terrainShape,
            Vector3[] into)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float half = shape.HalfWidth;
            float kerbTop = half + shape.KerbFace;
            float outer = shape.HalfOuter;
            float lift = shape.SurfaceLift;
            float top = lift + shape.KerbHeight;

            into[1] = Offset(centre, right, -outer, top);
            into[2] = Offset(centre, right, -kerbTop, top);
            into[3] = Offset(centre, right, -half, lift);
            into[4] = Offset(centre, right, 0f, lift + shape.Crown);
            into[5] = Offset(centre, right, half, lift);
            into[6] = Offset(centre, right, kerbTop, top);
            into[7] = Offset(centre, right, outer, top);

            into[0] = Verge(centre, right, -(outer + shape.VergeWidth), top, field, terrainShape);
            into[8] = Verge(centre, right, outer + shape.VergeWidth, top, field, terrainShape);
        }

        /// <summary>
        /// The outer end of a verge, sitting on the terrain mesh rather than at a computed height.
        ///
        /// <para>Sampled, not derived, and that is the point. The shelf the height field lays under a
        /// street can be a fifth of a metre off what the arithmetic says — the field averages nearby
        /// samples and the terrain mesh then interpolates that across twelve-metre cells. Two tenths of a
        /// metre is invisible in a screenshot and is a wall to a raycast wheel, so the verge asks the
        /// mesh where it is instead of assuming.</para>
        ///
        /// <para>Falls back to the paved height when there is no field yet, which keeps the builder
        /// usable before the terrain exists even though nothing does that now.</para>
        /// </summary>
        private static Vector3 Verge(
            Vector3 centre,
            Vector3 right,
            float across,
            float pavedRise,
            MountainField field,
            in TerrainShape terrainShape)
        {
            Vector3 at = centre + right * across;

            if (field == null)
            {
                return at + Vector3.up * pavedRise;
            }

            TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                out Vector3 ground, out Vector3 _);

            // A whisker above the mesh, so the verge is never the thing that z-fights with the ground.
            return new Vector3(at.x, ground.y + 0.02f, at.z);
        }

        private static Vector3 Offset(Vector3 centre, Vector3 right, float across, float rise)
        {
            return centre + right * across + Vector3.up * rise;
        }

        /// <summary>
        /// A point on a street's cross-section: <paramref name="across"/> metres from the centreline,
        /// <paramref name="rise"/> metres above the surface's own datum.
        ///
        /// Public because the junction pads build their corners from it. Heights at a junction come from
        /// the ribbon's own section rather than from a second evaluation of the ground, which is what
        /// makes pad and ribbon flush to the millimetre instead of to within a tolerance.
        /// </summary>
        public static Vector3 PointAcross(
            IRoadPath path, in TownStreetShape shape, float distance, float across, float rise)
        {
            float at = Mathf.Clamp(distance, 0f, path.Length);
            return Offset(path.GetPositionAtDistance(at), path.GetRightAtDistance(at), across, rise);
        }

        /// <summary>
        /// The outer corner of a street's paved surface at a distance, left or right looking along it.
        ///
        /// Junction pads take their corners from here rather than re-deriving them, so a pad and the
        /// ribbon it meets are flush to the millimetre by construction. That is the general form of the
        /// bug the flushness check exists to catch.
        /// </summary>
        public static Vector3 OuterCorner(
            IRoadPath path, in TownStreetShape shape, float distance, bool leftSide)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float across = leftSide ? -shape.HalfOuter : shape.HalfOuter;
            return Offset(centre, right, across, shape.SurfaceLift + shape.KerbHeight);
        }
    }
}
