using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// A point in town-local coordinates: metres along the trunk road, and metres out from it towards
    /// <see cref="TownShape.TownSide"/>.
    ///
    /// <para>Authoring the layout in these two numbers rather than in world XZ is what lets the table
    /// below survive a change to the course. Reshape the arrival road, move the town along the pass,
    /// alter the cross-fall, and every entry here still means what it meant — the mapping absorbs it. A
    /// table of world coordinates would have to be re-derived by hand each time, which in practice means
    /// it would stop being touched at all.</para>
    ///
    /// <para>The mapping has one limit, and it is worth knowing where: town-local space folds where the
    /// trunk road curves towards the town more tightly than the town is wide. At 260 m out, a left-hand
    /// bend of radius 220 m squeezes the far edge of the basin through the centre of its own arc. The
    /// course keeps its bends outside the town or turns them the other way, and
    /// <c>ValidateTownMapping</c> measures it rather than trusting anyone to remember.</para>
    /// </summary>
    public readonly struct TownPoint
    {
        public readonly float Along;
        public readonly float Across;

        public TownPoint(float along, float across)
        {
            Along = along;
            Across = across;
        }
    }

    /// <summary>What kind of street this is — which decides its cross-section and its lamps.</summary>
    public enum TownStreetKind
    {
        /// <summary>The town's main street. Widest, with the deepest footways.</summary>
        HighStreet = 0,

        /// <summary>A through street between quarters.</summary>
        Avenue = 1,

        /// <summary>An ordinary residential street.</summary>
        Lane = 2,

        /// <summary>Narrow, no footway to speak of. Back lanes and the green.</summary>
        Alley = 3,

        /// <summary>An edge of the market square. Paved to the building line.</summary>
        SquareEdge = 4,

        /// <summary>
        /// A city boulevard: two lanes each way with a broad footway. The widest thing here.
        ///
        /// <para>Wider than <see cref="HighStreet"/> by enough to read as a different order of road
        /// rather than as a generous version of the same one — which is what tells you, from the car,
        /// that you have arrived somewhere bigger than Talheim.</para>
        /// </summary>
        Boulevard = 5,

        /// <summary>A city through street. Between a boulevard and an avenue.</summary>
        CityStreet = 6,
    }

    /// <summary>
    /// Which part of town a street belongs to, and therefore what gets built along it.
    ///
    /// Carried on the street rather than rolled per block, so a quarter is a decision recorded in the
    /// table and readable in a diff — not something that changes when a seed does.
    /// </summary>
    public enum TownQuarter
    {
        OldTown = 0,
        Housing = 1,
        Industry = 2,
        Market = 3,
        Green = 4,

        /// <summary>
        /// The city core: towers on deep plots, set well back from a wide street.
        ///
        /// <para>The plot geometry is what makes this a different kind of place, not the building
        /// recipe. <c>QuarterStyle</c> hands out the frontage, and a tower needs a footprint several
        /// times a house's — so a quarter is where "this is downtown" is actually decided.</para>
        /// </summary>
        Downtown = 5,

        /// <summary>The city's perimeter-block belt: continuous street walls, shallow setback.</summary>
        Commercial = 6,

        /// <summary>
        /// The quayside: sheds, stores and chandlers' yards around a harbour basin.
        ///
        /// <para><b>Not <see cref="Industry"/> renamed.</b> The industrial quarter is written for a
        /// village edge — wide plots, deep setback, a third of them left empty, so it reads as a place
        /// with room to spare. A working quay is the opposite: the whole value of the ground is that it
        /// touches the water, so it is built out to the kerb and there are no gaps in it. Same buildings,
        /// almost the reverse geometry.</para>
        /// </summary>
        Harbour = 7,
    }

    /// <summary>One junction or dead end in the layout table.</summary>
    public readonly struct TownNodeSpec
    {
        public readonly TownPoint At;

        /// <summary>
        /// True where the street meets the trunk road. Those get a bell-mouth throat rather than a pad:
        /// the trunk road is one continuous ribbon with a marking atlas keyed on arc length and cannot be
        /// trimmed back to make room for one.
        /// </summary>
        public readonly bool OnTrunkRoad;

        public readonly string Name;

        public TownNodeSpec(float along, float across, bool onTrunkRoad = false, string name = null)
        {
            At = new TownPoint(along, across);
            OnTrunkRoad = onTrunkRoad;
            Name = name;
        }
    }

    /// <summary>
    /// One street, as an edge between two nodes.
    ///
    /// <para><see cref="Bow"/> is how far the middle of the street is pushed sideways, metres, positive
    /// to the left looking from <see cref="From"/> towards <see cref="To"/>. It is a single number rather
    /// than the radius-and-angle pair a road course takes, and deliberately: both ends of an edge are
    /// already pinned by the node table, so a radius and an angle over-determine it and only one
    /// combination of the two can be satisfied. The offset at the midpoint is exactly the one degree of
    /// freedom that is left, and it is also the thing you actually want to tune.</para>
    ///
    /// <para>A bend is a <b>bowed edge</b>, never two edges meeting at a node. Nodes are junctions and
    /// dead ends only; a degree-two node would need a junction pad shaped like a corridor, which is the
    /// awkward case the whole pad algorithm would otherwise have to carry.</para>
    /// </summary>
    public readonly struct TownStreetSpec
    {
        public readonly int From;
        public readonly int To;
        public readonly TownStreetKind Kind;
        public readonly TownQuarter Quarter;
        public readonly float Bow;

        public TownStreetSpec(int from, int to, TownStreetKind kind, TownQuarter quarter, float bow = 0f)
        {
            From = from;
            To = to;
            Kind = kind;
            Quarter = quarter;
            Bow = bow;
        }
    }

    /// <summary>
    /// A square, as a ring of nodes the streets between which are its edges.
    ///
    /// <para><b>Declared, not discovered.</b> The face walk in <see cref="StreetNetwork.FindBlocks"/>
    /// would find the ring perfectly well and hand back a block like any other — and then the parcelling
    /// would fill it with back gardens, because nothing about a bounded face says whether it is land to
    /// build on or land to leave open. That is the whole reason a square is a thing the table says rather
    /// than a thing the graph works out: it is a decision, and the graph has no way to make it.</para>
    ///
    /// <para>Four to six nodes. Fewer is a junction; more and the paved fan starts to want a real
    /// triangulation rather than a fan from the centroid, which only holds while the ring stays roughly
    /// star-shaped about its middle.</para>
    /// </summary>
    public readonly struct TownSquareSpec
    {
        /// <summary>Node indices in ring order. The street between each consecutive pair is an edge.</summary>
        public readonly int[] Nodes;

        public readonly string Name;

        public TownSquareSpec(string name, int[] nodes)
        {
            Name = name;
            Nodes = nodes;
        }
    }

    /// <summary>
    /// How much room a layout needs, in town-local coordinates.
    ///
    /// <para>Two rectangles rather than one, and the difference is the whole use of this type. The paved
    /// extent is what has to be <b>levelled</b>: centrelines plus paving, kerbs, footways, verges and the
    /// reach of a junction pad. The centreline extent is what has to be <b>mapped</b>: a street whose
    /// centreline runs past the town's declared along-extent is out on the pass's first bend, where
    /// town-local coordinates fold and no amount of levelling will help.</para>
    /// </summary>
    public readonly struct TownLayoutExtent
    {
        public readonly float AlongMin;
        public readonly float AlongMax;
        public readonly float AcrossMin;
        public readonly float AcrossMax;

        public readonly float CentreAlongMin;
        public readonly float CentreAlongMax;

        public TownLayoutExtent(
            float alongMin, float alongMax, float acrossMin, float acrossMax,
            float centreAlongMin, float centreAlongMax)
        {
            AlongMin = alongMin;
            AlongMax = alongMax;
            AcrossMin = acrossMin;
            AcrossMax = acrossMax;
            CentreAlongMin = centreAlongMin;
            CentreAlongMax = centreAlongMax;
        }
    }

    /// <summary>
    /// A set-piece building placed by the table rather than by a rule.
    ///
    /// <para><b>Why a table entry and not a placement rule.</b> Talheim's mosque and windmill are sited
    /// by <c>TownPlanner.AddMosque</c>, which searches a band of the basin for the highest ground — a
    /// rule written for a village, and a good one, because a village's landmark stands wherever the land
    /// offered. A monument does not. Where a town's cathedral or its grand mosque goes is the single most
    /// deliberate decision in its plan, it is the thing the place is recognised by from two kilometres
    /// off, and it belongs in the file where the decisions are — readable in a diff, next to the streets
    /// it faces.</para>
    ///
    /// <para><see cref="Scale"/> is what makes it a monument rather than a large house. The recipes in
    /// <c>LandmarkMeshes</c> are drawn at village size; multiplying the placement scales every offset in
    /// one, so a single recipe covers both without a second set of numbers to keep in step.</para>
    /// </summary>
    public readonly struct TownLandmarkSpec
    {
        public readonly TownPlotKind Kind;

        public readonly TownPoint At;

        /// <summary>How much bigger than the recipe. One is village size.</summary>
        public readonly float Scale;

        /// <summary>
        /// Which way it faces, degrees <b>relative to the trunk road's own heading</b> at this station.
        ///
        /// <para>Relative for the same reason the position is in town-local coordinates: a world bearing
        /// is exact until the course moves, and then it is a building facing a field. Minus ninety is
        /// whichever way the town's <c>across</c> runs negative — for Seeburg, out to sea.</para>
        /// </summary>
        public readonly float Facing;

        public readonly string Name;

        public TownLandmarkSpec(TownPlotKind kind, float along, float across, float scale, float facing,
            string name)
        {
            Kind = kind;
            At = new TownPoint(along, across);
            Scale = scale;
            Facing = facing;
            Name = name;
        }
    }

    /// <summary>The layout table as data: nodes, the streets between them, and any squares.</summary>
    public sealed class TownNetworkSpec
    {
        public readonly List<TownNodeSpec> Nodes = new List<TownNodeSpec>(32);
        public readonly List<TownStreetSpec> Streets = new List<TownStreetSpec>(40);
        public readonly List<TownSquareSpec> Squares = new List<TownSquareSpec>(2);
        public readonly List<TownLandmarkSpec> Landmarks = new List<TownLandmarkSpec>(2);

        public int AddNode(float along, float across, bool onTrunkRoad = false, string name = null)
        {
            Nodes.Add(new TownNodeSpec(along, across, onTrunkRoad, name));
            return Nodes.Count - 1;
        }

        public void AddStreet(
            int from, int to, TownStreetKind kind, TownQuarter quarter, float bow = 0f)
        {
            Streets.Add(new TownStreetSpec(from, to, kind, quarter, bow));
        }

        public void AddSquare(string name, params int[] nodes)
        {
            Squares.Add(new TownSquareSpec(name, nodes));
        }

        /// <summary>A set-piece building at a place the table chooses. See <see cref="TownLandmarkSpec"/>.</summary>
        public void AddLandmark(
            TownPlotKind kind, float along, float across, float scale, float facing, string name)
        {
            Landmarks.Add(new TownLandmarkSpec(kind, along, across, scale, facing, name));
        }

        /// <summary>
        /// The index of a named node, or −1.
        ///
        /// <para>For the one thing outside a layout that has to point at a node inside it: where another
        /// road hands its traffic over. A hand-listed table cannot express that as arithmetic the way a
        /// generated grid can, and a written-down index is a count of the lines above it — right until
        /// somebody inserts a node.</para>
        /// </summary>
        public int IndexOfNode(string name)
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// How far out this layout reaches, so the basin can be sized to it rather than guessed at.
        ///
        /// <para>This exists because the two were separate numbers and drifted: <c>AcrossOuter</c> said the
        /// levelled floor stopped at 260 m while the green's crescent and the lane out to it were authored
        /// past it, so their paving stood over ground the height field had never flattened. Any hand-set
        /// extent has that failure available to it the moment a street moves or a carriageway widens.
        /// Measured, it does not.</para>
        ///
        /// <para>A street is sampled along its bow rather than at its endpoints: the bow is what puts the
        /// crescent 34 m further out than either of the nodes it joins, and a rectangle taken from the node
        /// table alone would miss exactly the street that started this.</para>
        ///
        /// <para>A node's own reach is its widest street's paving swung through a junction pad — a trim of
        /// at most <see cref="StreetJunctionBuilder.MaximumTrimFactor"/> half-widths with the street's
        /// outer corner across it, so <c>hypot(2.5, 1)</c> half-widths — plus the verge. Deliberately the
        /// worst case rather than the trim that node will actually get: the trims are not resolved until
        /// the graph is built, and a basin that is a few metres too generous costs a handful of level
        /// samples while one that is a metre short is the bug above.</para>
        /// </summary>
        /// <param name="shelfDrop">
        /// <c>TerrainShape.RoadShelfDrop</c>, so the cross-sections measured here are the same ones
        /// <see cref="StreetNetwork.Build"/> will give the streets.
        /// </param>
        public TownLayoutExtent MeasureExtent(float shelfDrop)
        {
            float alongMin = float.MaxValue;
            float alongMax = float.MinValue;
            float acrossMin = float.MaxValue;
            float acrossMax = float.MinValue;
            float centreAlongMin = float.MaxValue;
            float centreAlongMax = float.MinValue;

            // The widest paving meeting each node, which is what its pad is built from.
            var nodeHalfOuter = new float[Nodes.Count];

            for (int i = 0; i < Streets.Count; i++)
            {
                TownStreetSpec street = Streets[i];
                if (street.From < 0 || street.From >= Nodes.Count
                    || street.To < 0 || street.To >= Nodes.Count)
                {
                    continue;
                }

                TownStreetShape shape = TownStreetShape.For(street.Kind, shelfDrop);
                float reach = shape.HalfOuter + shape.VergeWidth;

                nodeHalfOuter[street.From] = Mathf.Max(nodeHalfOuter[street.From], shape.HalfOuter);
                nodeHalfOuter[street.To] = Mathf.Max(nodeHalfOuter[street.To], shape.HalfOuter);

                const int steps = 8;
                for (int step = 0; step <= steps; step++)
                {
                    CentrelineAt(street, step / (float)steps, out float along, out float across);

                    centreAlongMin = Mathf.Min(centreAlongMin, along);
                    centreAlongMax = Mathf.Max(centreAlongMax, along);

                    alongMin = Mathf.Min(alongMin, along - reach);
                    alongMax = Mathf.Max(alongMax, along + reach);
                    acrossMin = Mathf.Min(acrossMin, across - reach);
                    acrossMax = Mathf.Max(acrossMax, across + reach);
                }
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (nodeHalfOuter[i] <= 0f)
                {
                    continue;
                }

                TownPoint at = Nodes[i].At;

                // The pad's own verge skirt is the same width the ribbons' is, and it is the last thing
                // standing between the paving and the hillside.
                float reach = nodeHalfOuter[i]
                              * Mathf.Sqrt(StreetJunctionBuilder.MaximumTrimFactor
                                           * StreetJunctionBuilder.MaximumTrimFactor + 1f)
                              + TownStreetShape.For(TownStreetKind.Lane, shelfDrop).VergeWidth;

                centreAlongMin = Mathf.Min(centreAlongMin, at.Along);
                centreAlongMax = Mathf.Max(centreAlongMax, at.Along);

                alongMin = Mathf.Min(alongMin, at.Along - reach);
                alongMax = Mathf.Max(alongMax, at.Along + reach);
                acrossMin = Mathf.Min(acrossMin, at.Across - reach);
                acrossMax = Mathf.Max(acrossMax, at.Across + reach);
            }

            if (alongMin > alongMax)
            {
                return new TownLayoutExtent(0f, 0f, 0f, 0f, 0f, 0f);
            }

            return new TownLayoutExtent(
                alongMin, alongMax, acrossMin, acrossMax, centreAlongMin, centreAlongMax);
        }

        /// <summary>
        /// A point on a street's centreline at <paramref name="t"/> along it, bow included.
        ///
        /// The same expression <see cref="StreetNetwork.BuildPath"/> lays the path out with, down to the
        /// <c>4t(1-t)</c> term — if the two ever disagree, the basin is sized against a street that is not
        /// the one built.
        /// </summary>
        private void CentrelineAt(in TownStreetSpec street, float t, out float along, out float across)
        {
            TownPoint a = Nodes[street.From].At;
            TownPoint b = Nodes[street.To].At;

            float spanAlong = b.Along - a.Along;
            float spanAcross = b.Across - a.Across;
            float span = Mathf.Sqrt(spanAlong * spanAlong + spanAcross * spanAcross);

            along = Mathf.Lerp(a.Along, b.Along, t);
            across = Mathf.Lerp(a.Across, b.Across, t);

            if (span < 0.01f)
            {
                return;
            }

            float bow = street.Bow * 4f * t * (1f - t);

            along += -spanAcross / span * bow;
            across += spanAlong / span * bow;
        }
    }

    /// <summary>
    /// Talheim's street plan, written out by hand.
    ///
    /// <para>Hand-written rather than generated, and that is the load-bearing decision in this stage. A
    /// generator would be a second unproven system stacked on an unproven one, and it would answer the
    /// wrong question: the interesting thing about a town layout is not that it can be produced, it is
    /// that it can be <i>argued with</i>. Thirty-odd streets is tractable by hand, it is the only way the
    /// quarters get any real character, it is deterministic, and it shows up in a diff as a change to a
    /// table rather than as a different roll of a die. This is the same reasoning that makes
    /// <see cref="MountainPassCourse"/> a list of instructions.</para>
    ///
    /// <para>The shape is three streets running the length of the valley floor, crossed by five that run
    /// out from the trunk road, with a market square threaded onto the middle one, and the grid
    /// deliberately out of true — the rows drift a few metres
    /// across as they go, the streets bow, and the far end thins out into dead ends and an industrial
    /// spur. A true grid at this scale reads as graph paper; the drift costs nothing and is most of what
    /// makes it look like a place that grew.</para>
    /// </summary>
    public static class TalheimLayout
    {
        public static TownNetworkSpec Build()
        {
            var spec = new TownNetworkSpec();

            // --- Where the streets meet the trunk road. Five mouths: three into the town proper and two
            // onto the single uphill lane on the other side.
            int trunkWest = spec.AddNode(520f, 0f, true, "Westzufahrt");
            int trunkMarket = spec.AddNode(700f, 0f, true, "Marktzufahrt");
            int trunkEast = spec.AddNode(880f, 0f, true, "Ostzufahrt");
            int trunkUpWest = spec.AddNode(600f, 0f, true, "Bergweg West");
            int trunkUpEast = spec.AddNode(760f, 0f, true, "Bergweg Ost");

            // --- The high street, one block back from the trunk road and running the length of the town.
            // It drifts from 54 m out to 44 m: the trunk road sweeps right through the town and a street
            // held at a constant distance from it would be the one thing in the place that is obviously
            // parallel to something.
            int high0 = spec.AddNode(520f, 54f);
            int high1 = spec.AddNode(605f, 60f);
            int high2 = spec.AddNode(700f, 64f, false, "Markt");
            int high3 = spec.AddNode(790f, 58f);
            int high4 = spec.AddNode(880f, 50f);
            int high5 = spec.AddNode(955f, 44f);

            // --- The second row: housing.
            int row0 = spec.AddNode(515f, 134f);
            int row1 = spec.AddNode(605f, 140f);
            // row2 swings 42 m out around the market square. The block between the high street and the
            // housing row is 82 m deep, and a square wide enough to be a square uses all of it — there was
            // no land left for the buildings that face it, and the validators said so twice over: two
            // streets running within a carriageway of each other, and six junction pads folded through
            // themselves at the pinch. A housing row bulging round the market place is also what a town
            // that grew round one actually looks like.
            //
            // <b>34 m was enough until the streets were widened for the cars, and then it was not.</b>
            // The clearance the planarity check wants is the two streets' paved half-widths added
            // together, so it grew with them: the avenue leaving row2 and the square's north edge went
            // from 14.9 m of paving between their centrelines to 17.2, against 15.7 m of ground. Both
            // numbers are in the warning now, which is what made this a one-line fix rather than a
            // reading of the whole table.
            int row2 = spec.AddNode(700f, 188f);
            int row3 = spec.AddNode(790f, 138f);
            int row4 = spec.AddNode(880f, 128f);
            int row5 = spec.AddNode(958f, 120f);

            // --- The third row, shorter. The town thins out towards the back of the basin.
            int back0 = spec.AddNode(600f, 214f);
            int back1 = spec.AddNode(700f, 220f);
            int back2 = spec.AddNode(790f, 212f);
            int back3 = spec.AddNode(880f, 202f);
            int back4 = spec.AddNode(955f, 200f);

            // 250 m out, not 266. At 266 this lane's paving, its verge and the turning head at the end of
            // it all stood past the levelled basin, on ground the height field had only ever seen as
            // hillside. TownShape.CoverLayout now sizes the basin to whatever the table asks for, so this
            // is no longer load-bearing — but there is no reason to make the town's flattest ground reach
            // fifteen metres further for one dead end, and the green is a green either way.
            int greenWest = spec.AddNode(588f, 250f);
            int crossNorth = spec.AddNode(505f, 208f);

            // --- The market square, between the high street and the housing row.
            //
            // It sits where the Markt avenue used to run straight from high2 to row2: that street is now
            // two, entering the square at its low corner and leaving at its high one, so the square is on
            // the way through rather than a cul-de-sac you would have to mean to visit. Deliberately not
            // square in plan — 80 by 40 m with the corners a few metres out of true, because a true
            // rectangle in a town whose whole grid is out of true is the one thing that would look drawn.
            //
            // The far pair of nodes sit 48 m further out than the near pair, which under the basin's
            // cross-fall puts that edge about 0.9 m uphill. That is where the town hall goes, and it is
            // measured rather than named — see TownSquare.UphillEdge.
            //
            // 48 m of ring is only 30 m of paving: the streets around a square eat their own half-widths
            // out of it at both ends, and a square laid out to the size you want it to read is a third
            // too small by the time it is built.
            int sqSouthWest = spec.AddNode(664f, 90f);
            int sqSouthEast = spec.AddNode(744f, 93f);
            int sqNorthEast = spec.AddNode(746f, 138f);
            int sqNorthWest = spec.AddNode(662f, 135f);

            // The square's two approaches, both off the high street and both on their own T.
            //
            // The first attempt hung the square's corners straight onto high2 and high3, which already
            // carried four streets each; a fifth arriving diagonally left two branches 38° and 33° apart.
            // No trim can make a junction convex at that angle — a street's outer corner sits
            // atan(halfWidth / trim) off its own axis, and the trim would have to be nearly thirty metres
            // — so the pads folded through themselves and the validator said so at three nodes.
            //
            // Two turnings off the high street into the market place is also simply the better town plan,
            // and it is what every market place of this kind actually looks like. No gap at any of these
            // four nodes is now under 70°.
            int highMarket = spec.AddNode(655f, 58f);
            int highMarketEast = spec.AddNode(746f, 60f);

            // --- The uphill side. One lane, because the level samples only reach 90 m that way before the
            // mountain takes over, and a second row there would stand on the hillside.
            int up0 = spec.AddNode(500f, -58f);
            int up1 = spec.AddNode(600f, -62f);
            int up2 = spec.AddNode(760f, -62f);
            int up3 = spec.AddNode(860f, -58f);

            // --- Streets along the valley.
            spec.AddStreet(high0, high1, TownStreetKind.HighStreet, TownQuarter.OldTown, 3f);
            spec.AddStreet(high1, highMarket, TownStreetKind.HighStreet, TownQuarter.Market, -1f);
            spec.AddStreet(highMarket, high2, TownStreetKind.HighStreet, TownQuarter.Market, -1f);
            spec.AddStreet(high2, highMarketEast, TownStreetKind.HighStreet, TownQuarter.Market, 1.5f);
            spec.AddStreet(highMarketEast, high3, TownStreetKind.HighStreet, TownQuarter.Market, 1f);
            spec.AddStreet(high3, high4, TownStreetKind.HighStreet, TownQuarter.OldTown, -3f);
            spec.AddStreet(high4, high5, TownStreetKind.Avenue, TownQuarter.Industry, 2f);

            spec.AddStreet(row0, row1, TownStreetKind.Avenue, TownQuarter.Housing, -4f);
            spec.AddStreet(row1, row2, TownStreetKind.Avenue, TownQuarter.Housing, 3f);
            spec.AddStreet(row2, row3, TownStreetKind.Avenue, TownQuarter.Housing, -3f);
            spec.AddStreet(row3, row4, TownStreetKind.Avenue, TownQuarter.Housing, 4f);
            spec.AddStreet(row4, row5, TownStreetKind.Lane, TownQuarter.Industry, -2f);

            spec.AddStreet(back0, back1, TownStreetKind.Lane, TownQuarter.Housing, 3f);
            spec.AddStreet(back1, back2, TownStreetKind.Lane, TownQuarter.Housing, -4f);
            spec.AddStreet(back2, back3, TownStreetKind.Lane, TownQuarter.Housing, 3f);
            spec.AddStreet(back3, back4, TownStreetKind.Lane, TownQuarter.Industry, 0f);

            // --- The green: a crescent off the back row, bowed hard enough that the block between the two
            // is a proper open space rather than a sliver.
            //
            // 34 m of bow rather than 46, for the reason greenWest moved in: at 46 the deepest point of
            // the crescent carried its paving past the edge of the levelled floor. 34 m is still a green
            // you can see across, and it is measured against the basin now rather than hoped at.
            spec.AddStreet(back1, back2, TownStreetKind.Alley, TownQuarter.Green, 34f);
            spec.AddStreet(back0, greenWest, TownStreetKind.Alley, TownQuarter.Green, -6f);

            // --- Streets out from the trunk road.
            spec.AddStreet(trunkWest, high0, TownStreetKind.Avenue, TownQuarter.OldTown);
            spec.AddStreet(high0, row0, TownStreetKind.Avenue, TownQuarter.Housing, 3f);
            spec.AddStreet(row0, crossNorth, TownStreetKind.Lane, TownQuarter.Housing, -3f);

            spec.AddStreet(high1, row1, TownStreetKind.Lane, TownQuarter.OldTown, -2f);
            spec.AddStreet(row1, back0, TownStreetKind.Lane, TownQuarter.Housing, 3f);

            spec.AddStreet(trunkMarket, high2, TownStreetKind.Avenue, TownQuarter.Market);
            spec.AddStreet(row2, back1, TownStreetKind.Lane, TownQuarter.Housing, -3f);

            // --- The market square: four edges, and the two streets that thread through it.
            //
            // The edges are SquareEdge, which is both a cross-section — 4.4 m carriageway against 2.8 m
            // of footway, and the narrowest carriageway of any through street here for the reason
            // TownStreetShape.For gives — and the mark the builders read to tell that the land inside the
            // ring is a square rather than a block waiting to be parcelled.
            // Dead straight, unlike every other street in the town, because a square is the one place in
            // it where straight is the point. The corners are the town's only degree-two nodes, and the
            // convention elsewhere — a bend is a bowed edge, never two edges and a node — cannot apply:
            // a ring has to turn, and a bow cannot turn ninety degrees.
            spec.AddStreet(sqSouthWest, sqSouthEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqSouthEast, sqNorthEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqNorthEast, sqNorthWest, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqNorthWest, sqSouthWest, TownStreetKind.SquareEdge, TownQuarter.Market);

            spec.AddStreet(highMarket, sqSouthWest, TownStreetKind.Avenue, TownQuarter.Market, 1f);
            spec.AddStreet(sqSouthEast, highMarketEast, TownStreetKind.Avenue, TownQuarter.Market, -1f);

            spec.AddSquare("Marktplatz", sqSouthWest, sqSouthEast, sqNorthEast, sqNorthWest);

            spec.AddStreet(high3, row3, TownStreetKind.Lane, TownQuarter.OldTown, 2f);
            spec.AddStreet(row3, back2, TownStreetKind.Lane, TownQuarter.Housing, -2f);

            spec.AddStreet(trunkEast, high4, TownStreetKind.Avenue, TownQuarter.OldTown);
            spec.AddStreet(high4, row4, TownStreetKind.Avenue, TownQuarter.Industry, -3f);
            spec.AddStreet(row4, back3, TownStreetKind.Lane, TownQuarter.Industry, 2f);

            // --- The uphill lane.
            spec.AddStreet(trunkUpWest, up1, TownStreetKind.Lane, TownQuarter.Green);
            spec.AddStreet(trunkUpEast, up2, TownStreetKind.Lane, TownQuarter.Green);
            spec.AddStreet(up0, up1, TownStreetKind.Alley, TownQuarter.Green, 2f);
            spec.AddStreet(up1, up2, TownStreetKind.Lane, TownQuarter.Green, -4f);
            spec.AddStreet(up2, up3, TownStreetKind.Alley, TownQuarter.Green, 2f);

            return spec;
        }
    }
}
