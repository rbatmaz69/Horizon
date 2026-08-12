using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Hochstadt: the city at the end of the motorway.
    ///
    /// <para><b>Written as loops, unlike <see cref="TalheimLayout"/>.</b> That file is a hand-listed
    /// table because a village is a hand-made shape — every street in it is a decision, and the table is
    /// where those decisions are readable in a diff. A city grid is the opposite: sixty streets whose
    /// only content is which line of the grid they are on, and hand-listing them would be sixty lines in
    /// which a typo is invisible. The decisions here are the arrays at the top; everything below is
    /// their consequence.</para>
    ///
    /// <para>The plan is a boulevard on the arterial with a grid either side of it and a civic square
    /// off-centre. Quarters come from distance to the middle — towers in the core, perimeter blocks
    /// around them, housing at the edge — which is also the arrival sequence, since the motorway comes
    /// in along the boulevard.</para>
    ///
    /// <para><b>Both sides of the arterial, which Talheim could not do.</b> A town hung on a bending
    /// road has to sit on one side of it, because the inside of the bend is where town-local coordinates
    /// fold. <see cref="HochstadtCourse"/> is straight, so there is no inside — see
    /// <c>TownShape.Hochstadt</c> for the whole of that argument.</para>
    /// </summary>
    public static class HochstadtLayout
    {
        /// <summary>
        /// Distances out from the arterial that carry a street, metres. Zero is the boulevard itself.
        ///
        /// <para>Closer together near the middle — 120 m blocks in the core against 160 at the edge.
        /// Block size is most of what tells a city centre from its outskirts at street level, and
        /// varying it costs nothing.</para>
        /// </summary>
        private static readonly float[] Across = { -440f, -280f, -120f, 0f, 120f, 280f, 440f };

        /// <summary>Distances along the arterial that carry a cross street, metres.</summary>
        private static readonly float[] Along = { 200f, 400f, 600f, 800f, 1000f };

        /// <summary>Where the boulevard starts — the motorway's last metre — and where it ends.</summary>
        private const float BoulevardStart = 0f;

        private const float BoulevardEnd = 1180f;

        /// <summary>The tower core, as a box in town-local coordinates.</summary>
        private const float CoreAcross = 130f;

        private const float CoreAlongFrom = 380f;
        private const float CoreAlongTo = 820f;

        // The civic square, as indices into the two arrays. Both pairs are adjacent, so each of its four
        // sides is exactly one grid segment and no street has to be split to make room for it.
        private const int SquareAcrossFrom = 4;
        private const int SquareAcrossTo = 5;
        private const int SquareAlongFrom = 1;
        private const int SquareAlongTo = 2;

        /// <summary>
        /// The gateway node's index, where the motorway hands over to the boulevard.
        ///
        /// <para>Derived from the two arrays rather than written down, because it is simply the first
        /// node added after the grid. Exposed because the traffic graph needs the motorway's far end and
        /// this node to be <i>the same node</i> — that is the whole of how the two road networks join,
        /// and it is what turns the motorway's traffic from something that U-turns at the end of the
        /// road into something that drives into a city.</para>
        /// </summary>
        public static int GatewayNode => Along.Length * Across.Length;

        public static TownNetworkSpec Build()
        {
            var spec = new TownNetworkSpec();

            // Grid nodes first, so a node's index is a pure function of its two indices and the street
            // loops below can address it arithmetically instead of through a lookup.
            for (int a = 0; a < Along.Length; a++)
            {
                for (int c = 0; c < Across.Length; c++)
                {
                    spec.AddNode(Along[a], Across[c]);
                }
            }

            int gateway = spec.AddNode(BoulevardStart, 0f, name: "Stadttor");
            int boulevardEnd = spec.AddNode(BoulevardEnd, 0f);

            int centre = System.Array.IndexOf(Across, 0f);

            // --- Cross streets: the full width of the city at each along level.
            for (int a = 0; a < Along.Length; a++)
            {
                for (int c = 0; c + 1 < Across.Length; c++)
                {
                    float middle = (Across[c] + Across[c + 1]) * 0.5f;
                    bool square = IsSquareCross(a, c);

                    spec.AddStreet(
                        Node(a, c),
                        Node(a, c + 1),
                        square ? TownStreetKind.SquareEdge : KindFor(Along[a], middle, false),
                        square ? TownQuarter.Market : QuarterFor(Along[a], middle));
                }
            }

            // --- Longitudinal streets, skipping the boulevard's own line — that is built below.
            for (int c = 0; c < Across.Length; c++)
            {
                if (c == centre)
                {
                    continue;
                }

                for (int a = 0; a + 1 < Along.Length; a++)
                {
                    float middle = (Along[a] + Along[a + 1]) * 0.5f;
                    bool square = IsSquareLongitudinal(a, c);

                    spec.AddStreet(
                        Node(a, c),
                        Node(a + 1, c),
                        square ? TownStreetKind.SquareEdge : KindFor(middle, Across[c], true),
                        square ? TownQuarter.Market : QuarterFor(middle, Across[c]));
                }
            }

            // --- The boulevard, segment by segment. A node is where streets meet and every cross street
            // meets this one, so it cannot be a single edge end to end.
            spec.AddStreet(gateway, Node(0, centre), TownStreetKind.Boulevard, TownQuarter.Commercial);

            for (int a = 0; a + 1 < Along.Length; a++)
            {
                spec.AddStreet(Node(a, centre), Node(a + 1, centre),
                    TownStreetKind.Boulevard, QuarterFor((Along[a] + Along[a + 1]) * 0.5f, 0f));
            }

            spec.AddStreet(Node(Along.Length - 1, centre), boulevardEnd,
                TownStreetKind.Boulevard, TownQuarter.Housing);

            spec.AddSquare("Rathausplatz",
                Node(SquareAlongFrom, SquareAcrossFrom),
                Node(SquareAlongTo, SquareAcrossFrom),
                Node(SquareAlongTo, SquareAcrossTo),
                Node(SquareAlongFrom, SquareAcrossTo));

            return spec;
        }

        private static bool IsSquareCross(int a, int c)
        {
            return (a == SquareAlongFrom || a == SquareAlongTo)
                   && c == SquareAcrossFrom
                   && SquareAcrossTo == SquareAcrossFrom + 1;
        }

        private static bool IsSquareLongitudinal(int a, int c)
        {
            return (c == SquareAcrossFrom || c == SquareAcrossTo)
                   && a == SquareAlongFrom
                   && SquareAlongTo == SquareAlongFrom + 1;
        }

        private static int Node(int alongIndex, int acrossIndex)
        {
            return alongIndex * Across.Length + acrossIndex;
        }

        /// <summary>
        /// How wide a street is, from where it sits. The boulevard is decided by its caller; this picks
        /// between a city street and an avenue.
        /// </summary>
        private static TownStreetKind KindFor(float along, float across, bool longitudinal)
        {
            // One main street each way besides the boulevard. A grid with a single important road in it
            // reads as a village high street with houses stacked behind it.
            bool spine = longitudinal
                ? Mathf.Abs(Mathf.Abs(across) - 280f) < 1f
                : Mathf.Abs(along - 600f) < 1f;

            if (spine || InCore(along, across))
            {
                return TownStreetKind.CityStreet;
            }

            return TownStreetKind.Avenue;
        }

        private static TownQuarter QuarterFor(float along, float across)
        {
            if (InCore(along, across))
            {
                return TownQuarter.Downtown;
            }

            bool belt = Mathf.Abs(across) <= 300f && along >= 220f && along <= 980f;
            return belt ? TownQuarter.Commercial : TownQuarter.Housing;
        }

        private static bool InCore(float along, float across)
        {
            return Mathf.Abs(across) <= CoreAcross
                   && along >= CoreAlongFrom
                   && along <= CoreAlongTo;
        }
    }
}
