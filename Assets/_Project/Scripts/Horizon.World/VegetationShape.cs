using System;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What grows on the mountain, how thickly, and where it refuses to.
    ///
    /// Every number here is a rejection rule rather than a count, because a count cannot survive the road
    /// changing shape. Density is expressed as the spacing of the candidate grid; what actually ends up on
    /// the hillside is whatever survives the clearances, the slope limits, the tree line and the clumping
    /// noise. That means the pass can be re-routed and the vegetation still lands somewhere sensible.
    ///
    /// The altitude fields are normalised against the course itself — 0 is the lowest point of the road, 1
    /// is the summit — so the tree line moves with the pass instead of being a hard-coded elevation.
    /// </summary>
    [Serializable]
    public struct VegetationShape
    {
        [Tooltip("Spacing of the tree candidate grid, metres. One candidate per cell, jittered inside it.")]
        public float TreeCellSize;

        [Tooltip("Spacing of the shrub candidate grid, metres.")]
        public float ShrubCellSize;

        [Tooltip("Spacing of the grass tuft candidate grid, metres.")]
        public float TuftCellSize;

        [Tooltip("Spacing of the boulder candidate grid, metres.")]
        public float BoulderCellSize;

        [Tooltip("Nothing taller than a shrub within this distance of the centreline, metres. Has to clear "
               + "the carriageway, the shoulder and the guard rail, with room for a canopy that leans.")]
        public float TreeClearance;

        [Tooltip("Shrubs may come almost to the rail — that band is what the driver looks at all the time, "
               + "and leaving it bare is most of what makes a road feel like it is on a grey plane.")]
        public float ShrubClearance;

        [Tooltip("Grass starts just past the shoulder.")]
        public float TuftClearance;

        public float BoulderClearance;

        [Tooltip("Grass is only worth its triangles where it can be seen close up, so it stops here.")]
        public float TuftMaxDistance;

        [Tooltip("Ground steeper than this grows no trees, degrees. Well below the terrain's own rock "
               + "threshold: a tree on a 34° face reads as stuck to a wall.")]
        public float TreeMaxSlopeDegrees;

        [Tooltip("Shrubs hang on rather further up a slope than trees do, degrees.")]
        public float ShrubMaxSlopeDegrees;

        [Tooltip("Grass wants the shallow ground, degrees.")]
        public float TuftMaxSlopeDegrees;

        [Tooltip("Boulders prefer steep ground and screes, degrees. Below this they are unlikely rather "
               + "than forbidden, so the odd erratic still sits in a meadow.")]
        public float BoulderMinSlopeDegrees;

        [Tooltip("Tree line as a fraction of the climb: 0 is the lowest point of the road, 1 the summit.")]
        public float TreeLineHeight;

        [Tooltip("How far the tree line wanders, in the same units. Without it the last row of trees "
               + "follows a contour line, which no mountain does.")]
        public float TreeLineJitter;

        [Tooltip("Below this fraction of the climb the forest is mostly broadleaf, above it mostly conifer.")]
        public float BroadleafBelow;

        [Tooltip("Frequency of the clumping noise. Smaller means broader stands and broader clearings.")]
        public float ClumpScale;

        [Tooltip("Clumping noise below this value grows nothing. This is what makes clearings, forest "
               + "edges and open shoulders instead of an even carpet.")]
        public float ClumpThreshold;

        [Tooltip("Density at the outer edge of the corridor relative to density at the road. The far field "
               + "is seen through fog and edge-on, so it buys almost nothing at full density.")]
        public float FarDensity;

        [Tooltip("Distance from the road at which the thinning starts, metres.")]
        public float FarDensityStart;

        [Tooltip("Distance from the road at which the thinning has reached FarDensity, metres.")]
        public float FarDensityEnd;

        [Tooltip("Keep-out radius around the centreline of a tunnel or gallery, metres. A tunnel is a solid "
               + "body standing on untouched ground and the height field knows nothing about it, so without "
               + "this, trees grow happily inside the rock. Must exceed TunnelBuilder.MoundHalfWidth.")]
        public float TunnelExclusion;

        [Tooltip("How far past each portal the keep-out runs, metres. Must exceed TunnelBuilder.EndOverhang.")]
        public float TunnelEndMargin;

        [Tooltip("Trees are cleared this far around a viewpoint, metres. A viewpoint with a spruce in front "
               + "of it is not a viewpoint.")]
        public float ViewpointClearing;

        /// <summary>
        /// How far back from open water trees are kept off the bank, metres.
        ///
        /// <para><b>This is what makes a coast road a coast road.</b> Nothing here ever kept a plant off
        /// a shore — only out of the water itself — so every bank in the world came up wooded right to
        /// the waterline, and from a road three hundred metres back the sea was behind a wall of
        /// canopy. The picture that found it is <c>WorldPreview_Strait_4_Corniche</c>, which was a
        /// photograph of a forest taken from a road named for the water it cannot see.</para>
        ///
        /// <para><b>Clamped to each body's own bank ease by <see cref="MountainField.IsShore"/>, which
        /// is why one number is safe for a 1200 m strait and a 70 m tarn.</b> What it really says is
        /// "no trees on a bank", and a bank is as wide as that body made it: 340 m on the Meerenge,
        /// 25 m at the Talheimer See. Shrubs, tufts and boulders are deliberately left alone — a bare
        /// bank is as wrong as a wooded one, and none of them is tall enough to stand in a view.</para>
        /// </summary>
        public float ShoreTreeClearing;

        [Tooltip("Nothing at all grows within this of a filling station, metres — grass included, which "
               + "is what separates it from a viewpoint. A forecourt is paved.\n\n"
               + "Must cover the apron FuelStationMeshes lays, or plants come up through the concrete.")]
        public float FuelStationClearing;

        [Tooltip("Above the tree line, the band in which dead snags appear, as a fraction of the climb.")]
        public float SnagBand;

        [Tooltip("Chance that a tree rejected for standing above the tree line becomes a snag instead.")]
        public float SnagChance;

        [Tooltip("Warn if a single tile exceeds this many triangles. Not a hard limit — a warning naming the "
               + "tile is more useful than silently thinned vegetation.")]
        public int MaxTrianglesPerTile;

        /// <summary>
        /// What the whole world costs at the settings below, measured rather than estimated, so the next
        /// person changing a density knows what they are moving away from.
        ///
        /// <para>1894 tiles, 14.01 M triangles of vegetation, densest tile 26 284 — 327 256 shrubs,
        /// 130 551 tufts, 114 056 conifers, 71 002 broadleaves, and 5 683 wildflowers among the grass.
        /// Measured off a full rebuild of the world as it stands: the pass, the Ebental, the Stadtfeld,
        /// the Kalkgrat, the Meerenge, Yalıköy, the Weissjoch, both circuits and the two forest
        /// belts.</para>
        ///
        /// <para><b>The figure this replaced said 11 498 856 and its own prose said 9.50 M</b> — the
        /// constant had been re-measured once and the paragraph above it had not, which is the failure
        /// this doc block exists to warn about, sitting inside the doc block. Both are the same number
        /// now and both were taken off the same build log.</para>
        ///
        /// <para><b>It was 6.83 M, and the two steps that moved it are worth telling apart.</b> The
        /// world's own woods were thickened — <see cref="ClumpThreshold"/> 0.42 → 0.34,
        /// <see cref="TreeClearance"/> 14 m → 11 — which cost twenty per cent of the total and three
        /// per cent of the heaviest tile, and left that tile the one it had always been. Then the
        /// Weissjoch was given a forest of its own through <c>LandRegion</c>, and <b>that moved the
        /// heaviest tile in the world from Terrain_8_6 at 20 750 to a Weissjoch tile at 27 052.</b> The
        /// budget below has been breached since the Kalkgrat; it is now breached by a different road and
        /// by more than twice.</para>
        ///
        /// <para><b><see cref="FarDensity"/> cannot help there, and the reason is the shape of the
        /// road.</b> It thins what stands more than 100 m from a carriageway — but a switchback stack
        /// puts its legs 52 to 72 m apart, so on that mountain there is no ground that is far from a
        /// road and nothing for the falloff to thin. A stacked climb is all near field. The knob that
        /// does work there is <c>LandRegion.TreeDensity</c>, on the region alone, which is where it
        /// belongs.</para>
        ///
        /// <para><b>The figure this replaced said 290 000 over 55 tiles, and it had been wrong by twenty
        /// times for three features.</b> It was measured when the world was one mountain pass and was
        /// never re-taken as four more legs were built on. That is worth recording rather than quietly
        /// correcting: this constant exists to tell somebody what they are moving away from, and a stale
        /// one tells them to move away from a world that no longer exists. Re-measure it whenever a leg
        /// is added — the number is in the build log, on the line beginning "Vegetation:".</para>
        ///
        /// <para><b>The total is not the frame cost, and the distinction is what makes the total
        /// affordable.</b> What is drawn is what the frustum keeps: the far plane is 600 m and a tile is
        /// 168 m, so roughly a dozen tiles are in view — order 50k triangles a frame, near enough
        /// unchanged since the world was a fifth of this size. Growing the world costs streaming and
        /// memory; growing <i>a tile</i> costs frame time. <see cref="MaxTrianglesPerTile"/> is the
        /// number that actually guards anything.</para>
        /// </summary>
        public const int MeasuredWorldTriangles = 14007400;

        public static VegetationShape Default => new VegetationShape
        {
            // 11 m between trees is a fairly open forest once the clumping mask has taken its third. Tighter
            // than this and the near field turns into a wall you cannot see the corners through, which
            // fights the whole point of a pass road.
            TreeCellSize = 11f,

            // 9 m, up from 7. At 7 the first build put fifteen thousand bushes against three thousand
            // trees — a heath with the odd spruce in it rather than a forest with undergrowth. Bushes
            // should read as the floor of the wood, so they want to be a few times the trees, not five.
            ShrubCellSize = 9f,

            // Grass is six triangles apiece and lives only in the band beside the road, so it can afford
            // to be dense — that band is what the eye tracks for the whole climb.
            TuftCellSize = 4f,
            BoulderCellSize = 20f,

            // RoadShape.OuterHalfWidth is 8.5 m and the rails stand a little outside that.
            //
            // <b>13.5 m, and the reason is the picture rather than the number.</b> It was 14 against a
            // 6.75 m road, which left four metres of shoulder and then another seven of nothing before
            // the first trunk: at driver's eye that bald ring reads as a mown verge on both sides of
            // every road in the world — the wood looked like something standing back from the road
            // rather than something the road was cut through. Eleven fixed that, and 13.5 is eleven
            // carried onto a carriageway a quarter wider, which keeps the gap between the guard rail and
            // the first trunk at what that argument settled on. It still clears the rails comfortably
            // and puts the near trunks where they pass the window.
            TreeClearance = 13.5f,
            ShrubClearance = 11f,

            // Grass is the one of these that grows right up to the road, so it is the one a wider
            // carriageway pushes out first: tufts inside the shoulder stand in the strip the clearance
            // report calls the road's own. The rule was already written down here — OuterHalfWidth + 1 —
            // and it is now read rather than restated, because it was restated as 8.5 and the roads then
            // moved. PrototypeSetup warns against exactly this number.
            TuftClearance = RoadShape.Default.OuterHalfWidth + 1f,
            BoulderClearance = 13.5f,
            TuftMaxDistance = 30f,

            TreeMaxSlopeDegrees = 30f,
            ShrubMaxSlopeDegrees = 40f,
            TuftMaxSlopeDegrees = 32f,
            BoulderMinSlopeDegrees = 26f,

            // 0.82 of a 180 m climb puts the tree line about 30 m below the summit — high enough that most
            // of the drive is through forest, low enough that the last hairpins are visibly above it.
            TreeLineHeight = 0.82f,
            TreeLineJitter = 0.09f,

            // 0.26, not 0.38. Most of the corridor's *area* sits low on the mountain — a pass climbs out of
            // a wide valley into a narrow summit — so a threshold placed by eye at the middle of the climb
            // put two broadleaves on the hill for every conifer, which is a lowland wood, not an alpine one.
            BroadleafBelow = 0.26f,

            // 0.012 gives stands and clearings roughly 80-150 m across, which is the scale a corner reveals.
            ClumpScale = 0.012f,

            // <b>0.34, down from 0.42.</b> The mask rejects everything below it, so this is the fraction
            // of the hillside that is clearing rather than wood — and at 0.42 that was most of it. The
            // stands were real and the clearings between them were bare ground, which reads as scrub
            // with the odd copse rather than as forest. Lowering it thickens the stands without touching
            // their scale, which is what ClumpScale is for and this is not.
            //
            // It is the one change here that costs triangles rather than being free, so it is the one to
            // pull back on if MaxTrianglesPerTile starts reporting new tiles rather than the one it has
            // always reported.
            ClumpThreshold = 0.34f,

            FarDensity = 0.5f,
            FarDensityStart = 100f,
            FarDensityEnd = 200f,

            // The widest massif in the world is the motorway bore's, at twice its arch — about 51 m — and
            // TunnelBuilder.EndOverhang is 24 m; both get a margin. Unchanged when the roads were widened
            // for the cars, because the pass's bores keep TunnelBuilder.MoundHalfWidth's 40 m floor and
            // only the motorway's grew, from 40 to 51, which this already covered.
            //
            // It said "MoundHalfWidth is 52 m" for a long time while that number was 40, which is the
            // reason to check it against the builder rather than against this comment.
            TunnelExclusion = 58f,
            TunnelEndMargin = 30f,
            ViewpointClearing = 38f,

            // Large on purpose: MountainField.IsShore clamps it to each body's own bank, so what this
            // actually asks for is "the whole bank, whatever that body's bank is". See the field.
            ShoreTreeClearing = 400f,

            // The apron is 26 m by 17 m from its centre, so its corners are 31.1 m out. 34 covers them
            // with enough over for the scatter's own grid to land outside rather than on the edge.
            //
            // Measured from the centre of the *forecourt*, not from the road — see VegetationContext.
            // Deliberately tighter than the viewpoint's 38 all the same: a viewpoint is clearing a sight
            // line, which reaches, and this is clearing a slab, which does not.
            FuelStationClearing = 34f,

            SnagBand = 0.12f,
            SnagChance = 0.12f,

            // 12000, set when the densest tile was about 9700 — half again as heavy as the worst the
            // design produced, so as to catch a tile that had genuinely run away rather than to flag the
            // design itself. It no longer does that: the world's heaviest tile is Terrain_-16_-6 at
            // 26 284, on the Weissjoch's switchback stack, and the build has been warning about it since
            // the Kalkgrat was added. The number is left alone deliberately. Raising it to stop the
            // warning would be turning off the one check that guards frame time — a tile is what the
            // frustum loads, and this is the only budget in the vegetation system that is not merely
            // about memory. That tile is a real fault to fix, and this line is what remembers it.
            //
            // <b>It also did not move when the two forest belts went in</b>, and that is the number that
            // made them affordable: the world total grew by a tenth and the heaviest tile did not change
            // at all, because the belts are on the pass and the Kalkgrat and the record is held by a
            // mountain neither of them touches. Growing the world costs streaming; growing a tile costs
            // frame time.
            MaxTrianglesPerTile = 12000,
        };
    }
}
