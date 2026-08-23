namespace Horizon.World
{
    /// <summary>
    /// Yalıköy: a fishing village on the bay behind the eastern cape, hung off the seafront road.
    ///
    /// <para><b>It hangs off the driving road, the way Talheim does, and not off an axis of its own like
    /// Seeburg.</b> Seeburg needed one because the coast road arrives at it across the shore and its
    /// bends are 320 m against a town 380 m deep, which folds the town-local mapping. Here the road runs
    /// <i>along</i> the water for the whole length of the village and
    /// <see cref="YalikoyCourse.CityStart"/> is the middle of a dead straight — so the trunk road can be
    /// the quayside road, and the town's arrival is simply driving through it.</para>
    ///
    /// <para><b>The table is written against <see cref="YalikoyCourse.CityStart"/> rather than in
    /// absolute stations.</b> Talheim's numbers are absolute because the pass publishes constants for
    /// where its village begins; this front's station is the sum of nine instructions in
    /// <c>YalikoyCourse.Append</c>, and hard-coding it would put the whole village in the sea the first
    /// time a corner above it changed radius.</para>
    ///
    /// <para><b>Everything is on one side of the road.</b> Across is positive inland, so the negative
    /// side is water — see <c>TownShape.Yalikoy</c>. What stands between the road and the bay is the
    /// harbour apron and the quay wall, which are <c>HarbourMeshes</c>' business rather than the street
    /// network's: a quay is a retaining structure along the edge of a dredged basin, not a street with
    /// houses on it.</para>
    ///
    /// <para>Smaller than either of the towns before it, and meant to be. Yalıköy is four streets and a
    /// square; it is the place the bridge leads to, not a second Seeburg. The set-piece mosque and the
    /// mill come from <c>TownShape.Landmarks</c> — a village's landmark is a silhouette on the high
    /// ground behind it, which is exactly what <c>TownPlanner.AddMosque</c> looks for.</para>
    /// </summary>
    public static class YalikoyLayout
    {
        public static TownNetworkSpec Build()
        {
            var spec = new TownNetworkSpec();

            // Every station below is metres past the start of the seafront. See the class remarks.
            float front = YalikoyCourse.CityStart;

            // --- Where the streets meet the seafront road. Four mouths, and each is directly below the
            // village-street node it feeds.
            //
            // <b>Directly below, and that is not tidiness.</b> A junction pad is a fan about its node,
            // and the fan folds through itself where two arms meet at a shallow angle — which is what a
            // connector that runs diagonally between two rows does at both of its ends. Squaring them up
            // costs nothing and takes seven folded pads out of the report.
            int quayWest = spec.AddNode(front + 110f, 0f, true, "Batı Yolu");
            int quayMid = spec.AddNode(front + 330f, 0f, true, "Liman Yolu");
            int quayEast = spec.AddNode(front + 545f, 0f, true, "Çarşı Yolu");
            int quayFar = spec.AddNode(front + 760f, 0f, true, "Doğu Yolu");

            // --- The village street, one block back from the water and running the whole front. It
            // drifts from 64 m out to 76 and back: the seafront road is dead straight, and a street held
            // at a constant distance from it would be the one line in the place that is obviously
            // parallel to something.
            int high0 = spec.AddNode(front + 110f, 64f);
            int high1 = spec.AddNode(front + 225f, 70f);
            int high2 = spec.AddNode(front + 330f, 74f);
            int high3 = spec.AddNode(front + 440f, 76f);
            int high4 = spec.AddNode(front + 545f, 70f);
            int high5 = spec.AddNode(front + 655f, 66f);
            int high6 = spec.AddNode(front + 760f, 62f);

            // --- The second row, up the slope. The lanes that climb to it leave the village street at
            // nodes the quay lanes do not use, so no junction here carries four arms at odd angles.
            //
            // <b>At 176 to 188 rather than at 150 to 160, and the square is what moved it.</b> A street
            // shorter than the two junction throats at its ends gets its trims scaled down until it
            // fits, and the build says so: the stubs from this row down to the square were eighteen and
            // twenty metres and came out at 0.6 of the trim they wanted. Everything above the square is
            // twenty-six metres further out so that those stubs are thirty-four.
            int row0 = spec.AddNode(front + 235f, 176f);
            int row1 = spec.AddNode(front + 340f, 184f);
            int rowSqWest = spec.AddNode(front + 385f, 186f);
            int rowSqEast = spec.AddNode(front + 445f, 188f);
            int row2 = spec.AddNode(front + 555f, 186f);
            int row3 = spec.AddNode(front + 650f, 178f);

            // --- The back lane, and the last thing before the terraces.
            int back0 = spec.AddNode(front + 335f, 252f);
            int back1 = spec.AddNode(front + 445f, 258f);
            int back2 = spec.AddNode(front + 550f, 250f);

            // A dead end off the back lane, bowed hard enough to leave a green rather than a sliver —
            // Talheim's crescent, at the one end of this village with room for it.
            int green = spec.AddNode(front + 645f, 262f);

            // --- The square, between the village street and the second row.
            //
            // One block back from the water on purpose. A market on the quay is a quay; put it behind and
            // the village has two centres — the working one at the harbour and the everyday one above it
            // — which is what makes it worth driving into rather than merely along. It is entered off
            // the second row at its two uphill corners, so it is on the way through rather than
            // somewhere you would have to mean to go, and its corners are a few metres out of true
            // because everything else here is.
            int sqSeaWest = spec.AddNode(front + 383f, 108f);
            int sqSeaEast = spec.AddNode(front + 447f, 108f);
            int sqHillEast = spec.AddNode(front + 446f, 152f);
            int sqHillWest = spec.AddNode(front + 384f, 152f);

            // --- The village street, west to east.
            spec.AddStreet(high0, high1, TownStreetKind.HighStreet, TownQuarter.OldTown, 2f);
            spec.AddStreet(high1, high2, TownStreetKind.HighStreet, TownQuarter.Harbour, -2f);
            spec.AddStreet(high2, high3, TownStreetKind.HighStreet, TownQuarter.Market, 2f);
            spec.AddStreet(high3, high4, TownStreetKind.HighStreet, TownQuarter.Market, -1.5f);
            spec.AddStreet(high4, high5, TownStreetKind.HighStreet, TownQuarter.Housing, 2f);
            spec.AddStreet(high5, high6, TownStreetKind.Lane, TownQuarter.Green, -2f);

            // --- Down to the water. Four, and they are what the village is for.
            spec.AddStreet(quayWest, high0, TownStreetKind.Lane, TownQuarter.OldTown);
            spec.AddStreet(quayMid, high2, TownStreetKind.Lane, TownQuarter.Harbour);
            spec.AddStreet(quayEast, high4, TownStreetKind.Lane, TownQuarter.Market);
            spec.AddStreet(quayFar, high6, TownStreetKind.Lane, TownQuarter.Housing);

            // --- And on up the hill. Two, at the two village-street nodes with no quay lane on them.
            spec.AddStreet(high1, row0, TownStreetKind.Lane, TownQuarter.OldTown);
            spec.AddStreet(high4, row2, TownStreetKind.Lane, TownQuarter.Housing);
            spec.AddStreet(high5, row3, TownStreetKind.Lane, TownQuarter.Green);

            // --- The second row and the back lane.
            spec.AddStreet(row0, row1, TownStreetKind.Lane, TownQuarter.Housing, -3f);
            spec.AddStreet(row1, rowSqWest, TownStreetKind.Lane, TownQuarter.Market, 1f);
            spec.AddStreet(rowSqWest, rowSqEast, TownStreetKind.Lane, TownQuarter.Market, -1f);
            spec.AddStreet(rowSqEast, row2, TownStreetKind.Lane, TownQuarter.Housing, 2f);
            spec.AddStreet(row2, row3, TownStreetKind.Lane, TownQuarter.Green, -2f);

            spec.AddStreet(row1, back0, TownStreetKind.Alley, TownQuarter.Housing);
            spec.AddStreet(row2, back2, TownStreetKind.Alley, TownQuarter.Housing);

            spec.AddStreet(back0, back1, TownStreetKind.Alley, TownQuarter.Housing, 3f);
            spec.AddStreet(back1, back2, TownStreetKind.Alley, TownQuarter.Housing, -3f);
            spec.AddStreet(back2, green, TownStreetKind.Alley, TownQuarter.Green, 22f);

            // --- The square. Four straight edges, hung under the second row at its two uphill corners.
            spec.AddStreet(sqSeaWest, sqSeaEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqSeaEast, sqHillEast, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqHillEast, sqHillWest, TownStreetKind.SquareEdge, TownQuarter.Market);
            spec.AddStreet(sqHillWest, sqSeaWest, TownStreetKind.SquareEdge, TownQuarter.Market);

            spec.AddStreet(rowSqWest, sqHillWest, TownStreetKind.Lane, TownQuarter.Market);
            spec.AddStreet(rowSqEast, sqHillEast, TownStreetKind.Lane, TownQuarter.Market);

            spec.AddSquare("Köy Meydanı", sqSeaWest, sqSeaEast, sqHillEast, sqHillWest);

            return spec;
        }
    }
}
