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

    /// <summary>The layout table as data: nodes, and the streets between them.</summary>
    public sealed class TownNetworkSpec
    {
        public readonly List<TownNodeSpec> Nodes = new List<TownNodeSpec>(32);
        public readonly List<TownStreetSpec> Streets = new List<TownStreetSpec>(40);

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
    /// out from the trunk road, with the grid deliberately out of true — the rows drift a few metres
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
            int row2 = spec.AddNode(700f, 146f);
            int row3 = spec.AddNode(790f, 138f);
            int row4 = spec.AddNode(880f, 128f);
            int row5 = spec.AddNode(958f, 120f);

            // --- The third row, shorter. The town thins out towards the back of the basin.
            int back0 = spec.AddNode(600f, 214f);
            int back1 = spec.AddNode(700f, 220f);
            int back2 = spec.AddNode(790f, 212f);
            int back3 = spec.AddNode(880f, 202f);
            int back4 = spec.AddNode(955f, 200f);

            int greenWest = spec.AddNode(590f, 266f);
            int crossNorth = spec.AddNode(505f, 208f);

            // --- The uphill side. One lane, because the level samples only reach 90 m that way before the
            // mountain takes over, and a second row there would stand on the hillside.
            int up0 = spec.AddNode(500f, -58f);
            int up1 = spec.AddNode(600f, -62f);
            int up2 = spec.AddNode(760f, -62f);
            int up3 = spec.AddNode(860f, -58f);

            // --- Streets along the valley.
            spec.AddStreet(high0, high1, TownStreetKind.HighStreet, TownQuarter.OldTown, 3f);
            spec.AddStreet(high1, high2, TownStreetKind.HighStreet, TownQuarter.Market, -2f);
            spec.AddStreet(high2, high3, TownStreetKind.HighStreet, TownQuarter.Market, 2.5f);
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
            spec.AddStreet(back1, back2, TownStreetKind.Alley, TownQuarter.Green, 46f);
            spec.AddStreet(back0, greenWest, TownStreetKind.Alley, TownQuarter.Green, -6f);

            // --- Streets out from the trunk road.
            spec.AddStreet(trunkWest, high0, TownStreetKind.Avenue, TownQuarter.OldTown);
            spec.AddStreet(high0, row0, TownStreetKind.Avenue, TownQuarter.Housing, 3f);
            spec.AddStreet(row0, crossNorth, TownStreetKind.Lane, TownQuarter.Housing, -3f);

            spec.AddStreet(high1, row1, TownStreetKind.Lane, TownQuarter.OldTown, -2f);
            spec.AddStreet(row1, back0, TownStreetKind.Lane, TownQuarter.Housing, 3f);

            spec.AddStreet(trunkMarket, high2, TownStreetKind.Avenue, TownQuarter.Market);
            spec.AddStreet(high2, row2, TownStreetKind.Avenue, TownQuarter.Market, 2f);
            spec.AddStreet(row2, back1, TownStreetKind.Lane, TownQuarter.Housing, -3f);

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
