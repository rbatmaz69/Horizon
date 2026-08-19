namespace Horizon.World
{
    /// <summary>
    /// Seeburg: the harbour town at the end of the coast road.
    ///
    /// <para><b>Hand-listed like <see cref="TalheimLayout"/>, not looped like
    /// <see cref="HochstadtLayout"/>.</b> A city grid is sixty streets whose only content is which line
    /// of the grid they are on, and writing those out by hand is sixty lines in which a typo is
    /// invisible. This is forty streets on four rows that all bend, thin out and change what they are
    /// called halfway along — every one of them is a decision, and the table is where a decision is
    /// readable in a diff.</para>
    ///
    /// <para><b>Rows parallel to the water, not a grid.</b> A harbour town is organised by distance from
    /// the quay and nothing else: the front is the front, the street behind it is where the town lives,
    /// and it climbs away into housing. Four rows and six ways down to the water, with the rows drifting
    /// a few metres in and out as they go — held at a constant distance they would be the one thing in
    /// the place that is obviously parallel to something.</para>
    ///
    /// <para><b>The whole town is inland of the axis.</b> Across is metres from the waterfront: zero is
    /// the boulevard, positive is uphill, and negative is promenade, beach and harbour. Nothing in this
    /// table is negative. What is out there is built by the harbour geometry and dug by the water plan —
    /// see <see cref="SeeburgCourse"/>.</para>
    /// </summary>
    public static class SeeburgLayout
    {
        /// <summary>
        /// The name of the node the coast road hands over to, one block inland of the waterfront.
        ///
        /// <para>The traffic graph needs the end of the coast road and this node to be <i>the same
        /// node</i>, which is the whole of how the two networks join — without it the coast road's cars
        /// reach the sea and turn round. It is the same join <c>HochstadtLayout.GatewayNode</c>
        /// makes.</para>
        ///
        /// <para><b>A name rather than an index, unlike Hochstadt's.</b> There the grid is generated, so
        /// the gateway's index is arithmetic on the two array lengths and derived is the honest way to
        /// say it. Here the table is hand-listed and an index would be a count of the lines above it —
        /// correct until somebody inserts a node, and wrong in a way that shows up as traffic joining
        /// the town at a random junction rather than as an error.</para>
        /// </summary>
        public const string GatewayNodeName = "Hafentor";

        public static TownNetworkSpec Build()
        {
            var spec = new TownNetworkSpec();

            // --- The waterfront boulevard, on the axis. Eight nodes over seven hundred metres, because
            // every way down to the water lands on it and a node is where streets meet.
            int w0 = spec.AddNode(40f, 0f);
            int w1 = spec.AddNode(120f, 0f);
            int w2 = spec.AddNode(200f, 0f);
            int w3 = spec.AddNode(290f, 0f);
            int w4 = spec.AddNode(380f, 0f);
            int w5 = spec.AddNode(470f, 0f);
            int w6 = spec.AddNode(560f, 0f);
            int w7 = spec.AddNode(650f, 0f);

            // --- The quay street, 55 m back from the front, over the harbour's own length only.
            // Everything on it is a store or a chandler's yard.
            int q0 = spec.AddNode(120f, 55f);
            int qg = spec.AddNode(200f, 56f);
            int q1 = spec.AddNode(290f, 58f);
            int q2 = spec.AddNode(380f, 55f);

            // --- The second row: where the town actually lives. Runs the whole length.
            int r0 = spec.AddNode(45f, 95f);
            int r1 = spec.AddNode(120f, 98f);
            int r2 = spec.AddNode(200f, 102f);
            int r3 = spec.AddNode(255f, 104f);

            // Two nodes of their own for the market's approaches rather than hanging them on r3 and r4.
            // Spaced off r3 and r4 by fifty metres and more, not thirty: a CityStreet junction pad eats
            // twenty-odd metres off each end of the street it sits on, and the first spacing left the
            // junctions wanting more of street 18 than the street had — the trims were scaled back to
            // three quarters, which is a junction drawn smaller than the road it joins.
            // Talheim learned this the expensive way: a fifth street arriving at a node that already
            // carries four leaves branches thirty-odd degrees apart, and no trim can make a junction pad
            // convex at that angle — the pads fold through themselves and the validator says so. The
            // first version of this table did exactly that at three nodes.
            int rm0 = spec.AddNode(312f, 106f);
            int rm1 = spec.AddNode(378f, 102f);

            int r4 = spec.AddNode(470f, 96f);
            int r5 = spec.AddNode(560f, 92f);
            int r6 = spec.AddNode(650f, 88f);

            // --- The arrival spine, at the gateway's station and dead straight: it is the last few
            // hundred metres of a mountain road turned into a street, and it crosses every row on the
            // way down to the water. Both of its middle nodes sit on rows that would otherwise run
            // straight past.
            int gate = spec.AddNode(200f, SeeburgCourse.GatewayAcross, name: GatewayNodeName);
            int a4 = spec.AddNode(200f, 296f);
            int a3 = spec.AddNode(200f, 216f);

            // --- The third row, up the hill. Starts later and finishes earlier: the town is deepest in
            // the middle, which is where the harbour is.
            int t0 = spec.AddNode(150f, 214f);
            int t1 = spec.AddNode(262f, 220f);
            int t2 = spec.AddNode(345f, 222f);
            int t3 = spec.AddNode(440f, 218f);
            int t4 = spec.AddNode(535f, 212f);
            int t5 = spec.AddNode(625f, 204f);

            // --- The back lane, shorter again, and the last thing before the hillside.
            int b0 = spec.AddNode(262f, 296f);
            int b1 = spec.AddNode(350f, 298f);
            int b2 = spec.AddNode(450f, 292f);
            int b3 = spec.AddNode(545f, 285f);

            // The green at the top of the town, as a dead end off the back lane. Talheim's crescent
            // trick — a bowed alley making an open block against the row it leaves — at the one end of
            // Seeburg where there is room for it.
            int green = spec.AddNode(610f, 300f);

            // --- The market square, between the second and third rows and just uphill of the harbour.
            //
            // Not on the front, deliberately. A market on the quay is a quay; put it one block back and
            // the town has two centres — the working one at the water and the civic one behind it — which
            // is both what a harbour town looks like and what makes it worth driving into rather than
            // along.
            //
            // 74 by 43 m with the corners out of true, and the far edge that much further uphill than the
            // near one, which under the cross-fall puts it about half a metre above: that is the side the
            // town hall lands on, measured rather than named. Its uphill corners are degree two, like
            // Talheim's — a ring has to turn, and a bow cannot turn ninety degrees.
            //
            // <b>The third row sits at 214 m rather than 196 because of this square.</b> The hall stands
            // across the street from the uphill edge, about fifteen metres out from it, and TownPlanner
            // then drops any plot that lands within seven tenths of a setback of a street it does not
            // face. At 196 the row was seventeen metres past the square's uphill edge, the hall landed
            // three metres short of it, and Seeburg came out with a market place and no town hall — with
            // nothing in the log to say why, because a plot quietly removed is not a warning.
            int sqSouthWest = spec.AddNode(312f, 142f);
            int sqSouthEast = spec.AddNode(378f, 145f);
            int sqNorthEast = spec.AddNode(380f, 186f);
            int sqNorthWest = spec.AddNode(314f, 183f);

            // --- The waterfront, west to east. Old town at the near end, the working quay in the middle,
            // and a promenade of perimeter blocks along the rest.
            spec.AddStreet(w0, w1, TownStreetKind.Boulevard, TownQuarter.OldTown, 2f);
            spec.AddStreet(w1, w2, TownStreetKind.Boulevard, TownQuarter.Harbour, -1.5f);
            spec.AddStreet(w2, w3, TownStreetKind.Boulevard, TownQuarter.Harbour, 1.5f);
            spec.AddStreet(w3, w4, TownStreetKind.Boulevard, TownQuarter.Harbour, -2f);
            spec.AddStreet(w4, w5, TownStreetKind.Boulevard, TownQuarter.Commercial, 2f);
            spec.AddStreet(w5, w6, TownStreetKind.Boulevard, TownQuarter.Commercial, -2f);
            spec.AddStreet(w6, w7, TownStreetKind.Boulevard, TownQuarter.Green, 3f);

            // --- The quay street. Split at the spine's station, because the spine crosses it: two
            // streets that intersect anywhere but at a node make the graph non-planar, and the block
            // finder walks faces — it cannot find a block whose boundary crosses itself. The validator
            // caught it as 'one pair of streets that share no junction run within a carriageway of each
            // other', which is what a crossing looks like from underneath.
            spec.AddStreet(q0, qg, TownStreetKind.Avenue, TownQuarter.Harbour, -1f);
            spec.AddStreet(qg, q1, TownStreetKind.Avenue, TownQuarter.Harbour, -1f);
            spec.AddStreet(q1, q2, TownStreetKind.Avenue, TownQuarter.Harbour, 1f);

            // --- The arrival spine. The only streets in the town that are dead straight: this is a
            // mountain road carrying on, and a bow in it would read as a swerve.
            spec.AddStreet(gate, a4, TownStreetKind.Avenue, TownQuarter.Housing);
            spec.AddStreet(a4, a3, TownStreetKind.Avenue, TownQuarter.Housing);
            spec.AddStreet(a3, r2, TownStreetKind.Avenue, TownQuarter.OldTown);
            spec.AddStreet(r2, qg, TownStreetKind.Avenue, TownQuarter.Harbour);
            spec.AddStreet(qg, w2, TownStreetKind.Avenue, TownQuarter.Harbour);

            // --- The second row.
            spec.AddStreet(r0, r1, TownStreetKind.CityStreet, TownQuarter.OldTown, 3f);
            spec.AddStreet(r1, r2, TownStreetKind.CityStreet, TownQuarter.OldTown, -2f);
            spec.AddStreet(r2, r3, TownStreetKind.CityStreet, TownQuarter.Market, 2f);
            spec.AddStreet(r3, rm0, TownStreetKind.CityStreet, TownQuarter.Market, -1f);
            spec.AddStreet(rm0, rm1, TownStreetKind.CityStreet, TownQuarter.Market, 1f);
            spec.AddStreet(rm1, r4, TownStreetKind.CityStreet, TownQuarter.Commercial, -2f);
            spec.AddStreet(r4, r5, TownStreetKind.Avenue, TownQuarter.Housing, 3f);
            spec.AddStreet(r5, r6, TownStreetKind.Avenue, TownQuarter.Housing, -2f);

            // --- The third row and the back lane. Both are split at the spine's station.
            spec.AddStreet(t0, a3, TownStreetKind.Avenue, TownQuarter.Housing, -2f);
            spec.AddStreet(a3, t1, TownStreetKind.Avenue, TownQuarter.Housing, 2f);
            spec.AddStreet(t1, t2, TownStreetKind.Avenue, TownQuarter.Housing, -3f);
            spec.AddStreet(t2, t3, TownStreetKind.Avenue, TownQuarter.Housing, 3f);
            spec.AddStreet(t3, t4, TownStreetKind.Lane, TownQuarter.Housing, -3f);
            spec.AddStreet(t4, t5, TownStreetKind.Lane, TownQuarter.Housing, 2f);

            spec.AddStreet(a4, b0, TownStreetKind.Lane, TownQuarter.Housing, -2f);
            spec.AddStreet(b0, b1, TownStreetKind.Lane, TownQuarter.Housing, 3f);
            spec.AddStreet(b1, b2, TownStreetKind.Lane, TownQuarter.Housing, -3f);
            spec.AddStreet(b2, b3, TownStreetKind.Lane, TownQuarter.Housing, 2f);

            // The green: the block between this and the back lane is left open, and the alley bows hard
            // enough that it is a green rather than a sliver.
            spec.AddStreet(b3, green, TownStreetKind.Alley, TownQuarter.Green, 26f);

            // --- Down to the water. Five besides the spine, and they are what the town is for.
            spec.AddStreet(w0, r0, TownStreetKind.Lane, TownQuarter.OldTown, -2f);
            spec.AddStreet(w1, q0, TownStreetKind.Lane, TownQuarter.OldTown);
            spec.AddStreet(q0, r1, TownStreetKind.Lane, TownQuarter.OldTown, 2f);
            spec.AddStreet(w3, q1, TownStreetKind.Lane, TownQuarter.Harbour, 1f);
            spec.AddStreet(q1, rm0, TownStreetKind.Lane, TownQuarter.Market, -2f);
            spec.AddStreet(w4, q2, TownStreetKind.Lane, TownQuarter.Harbour);
            spec.AddStreet(q2, rm1, TownStreetKind.Lane, TownQuarter.Commercial, 2f);
            spec.AddStreet(w5, r4, TownStreetKind.Lane, TownQuarter.Commercial, -2f);
            spec.AddStreet(w6, r5, TownStreetKind.Lane, TownQuarter.Housing, 2f);
            spec.AddStreet(w7, r6, TownStreetKind.Lane, TownQuarter.Green, -2f);

            // --- And on up the hill.
            spec.AddStreet(r1, t0, TownStreetKind.Lane, TownQuarter.Housing, -3f);
            spec.AddStreet(r3, t1, TownStreetKind.Lane, TownQuarter.Housing, 2f);
            spec.AddStreet(t1, b0, TownStreetKind.Lane, TownQuarter.Housing, -2f);
            spec.AddStreet(t2, b1, TownStreetKind.Lane, TownQuarter.Housing, 2f);
            spec.AddStreet(r4, t3, TownStreetKind.Lane, TownQuarter.Housing, 3f);
            spec.AddStreet(t3, b2, TownStreetKind.Lane, TownQuarter.Housing, -2f);
            spec.AddStreet(r5, t4, TownStreetKind.Lane, TownQuarter.Housing, 2f);
            spec.AddStreet(t4, b3, TownStreetKind.Lane, TownQuarter.Housing, -2f);
            spec.AddStreet(r6, t5, TownStreetKind.Lane, TownQuarter.Green, -3f);

            // --- The market square. Four straight edges, entered off the second row at its two downhill
            // corners so it is on the way through rather than somewhere you have to mean to go.
            spec.AddStreet(sqSouthWest, sqSouthEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqSouthEast, sqNorthEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqNorthEast, sqNorthWest, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqNorthWest, sqSouthWest, TownStreetKind.SquareEdge, TownQuarter.Market);

            spec.AddStreet(rm0, sqSouthWest, TownStreetKind.Lane, TownQuarter.Market, 1f);
            spec.AddStreet(sqSouthEast, rm1, TownStreetKind.Lane, TownQuarter.Market, -1f);

            spec.AddSquare("Hafenmarkt", sqSouthWest, sqSouthEast, sqNorthEast, sqNorthWest);

            // --- The Grand Mosque: the thing Seeburg is recognised by.
            //
            // <b>On the waterfront block, not on the hill behind it.</b> The high ground at the back of
            // town is where a village landmark goes, because a village landmark is a silhouette against
            // the sky and nothing else. This one has to work three ways at once — seen along the front
            // from either end of the promenade, seen from the harbour mole across the water, and seen
            // over the roofs from a boat's distance out — and only the seaward edge of town does all
            // three. The block between the boulevard and the second row is the one place with the depth
            // to hold it: ninety metres across, against the sixty a minaret's shadow wants.
            //
            // <b>Clear of the harbour by two hundred and thirty metres.</b> Beside it, the mosque and
            // the lighthouse would be two verticals in the same view arguing about which is the subject.
            // Apart, the harbour is what you arrive at and this is what you drive along the front to
            // reach.
            //
            // At 2.1 the hall is 38 m square and 18 m tall, and the minaret 69 — against Talheim's 18 and
            // 33. <b>The hall's height is the number that matters, not the minaret's.</b> At 1.6 the
            // minaret already stood over everything and the render still read as an ordinary block with
            // a tower behind it, because the hall came out lower than the five-storey perimeter blocks
            // flanking it: a landmark that its neighbours look down on is a landmark in plan only. At 2.1
            // it stands clear of them, and the minaret is the tallest thing in the world by half again.
            //
            // The block holds it with room to spare — 477 to 553 m along between the cross streets, and
            // 13 to 85 m across between the boulevard's footway and the second row's — but not much more
            // than that, which is why this is where the number stops.
            //
            // Facing −90°, which is whichever way `across` runs negative — out to sea. A mosque with its
            // back to the water on a seafront is the one orientation that would look like an accident.
            spec.AddLandmark(TownPlotKind.Mosque, 515f, 48f, 2.1f, -90f, "Große Moschee");

            return spec;
        }
    }
}
