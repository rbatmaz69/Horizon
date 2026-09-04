using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The ground colours of one region: the two the terrain has always had, plus the field tones that
    /// make a landscape read as farmed rather than as wild.
    /// </summary>
    public readonly struct GroundPalette
    {
        public readonly Color32 Grass;
        public readonly Color32 Rock;

        /// <summary>
        /// One colour per field, indexed by <see cref="LandRegion.Parcel"/>.
        ///
        /// <para>The whole reason the region has any effect worth the name. A meadow recoloured evenly
        /// is another meadow; what says <i>somebody farms here</i> is rectangles of different crops
        /// meeting along straight edges, and from a crest that reads at a kilometre while an individual
        /// tree does not.</para>
        /// </summary>
        public readonly Color32[] Fields;

        public GroundPalette(Color32 grass, Color32 rock, Color32[] fields)
        {
            Grass = grass;
            Rock = rock;
            Fields = fields;
        }
    }

    /// <summary>
    /// A stretch of the world with its own plants, props and ground colour.
    ///
    /// <para><b>Why this exists.</b> Every choice the scatter and the tile builder make is keyed on
    /// slope, on distance to the nearest road, and on <c>VegetationContext.ClimbFraction</c> — an
    /// altitude normalised against the <i>mountain pass</i>. That is right for the pass and wrong
    /// everywhere else: the Ebental sits at −6…37 m against a pass climbing to 196, so its climb
    /// fraction never leaves 0…0.2, the conifer bias never rises off its floor of 0.45, and half the
    /// trees in the orchard country come out spruce. There was no way to say "here is somewhere else"
    /// at all. This is that.</para>
    ///
    /// <para><b>Membership comes from the road, not from a rectangle.</b> A box round the Ebental
    /// unavoidably clips the pass's descent — the pass runs to x 762 and the Ebental starts there — and
    /// would recolour a hairpin. Distance to the region's own carriageway follows what is actually
    /// seen: the terrain corridor is 200 m wide, and the pass is 300 m away at its nearest. So the
    /// weight is 1 out to <see cref="CoreReach"/> and eases to nothing by <see cref="EdgeReach"/>,
    /// and the pass is untouched without anyone writing down that it should be.</para>
    ///
    /// <para><b>And from arc length, which is the half that is easy to forget.</b> At the join the
    /// distance to both roads is zero, so a weight built on distance alone snaps to full strength on
    /// the seam — a line of colour straight across the road at the exact moment the player is looking
    /// at it. Fading in over <see cref="EntryFade"/> of the region's own road turns that into driving
    /// into another country.</para>
    ///
    /// <para><b>Everything here is a pure function of x and z.</b> The scatter derives every position
    /// from <c>Hash(gx, gz, species)</c> and every shape from a seed, so that a rebuild is byte for
    /// byte the same as the last one. A region that counted anything, or asked
    /// <c>UnityEngine.Random</c>, would quietly end that.</para>
    /// </summary>
    public sealed class LandRegion
    {
        /// <summary>Full strength out to here from the region's road, metres.</summary>
        private const float CoreReach = 160f;

        /// <summary>Nothing left by here, metres. Must stay under the gap to the next road.</summary>
        private const float EdgeReach = 260f;

        /// <summary>How much of the region's road the change fades in over, metres.</summary>
        private const float EntryFade = 400f;

        /// <summary>
        /// Size of a field, metres along and across the furrow.
        ///
        /// <para>90 by 170, up from 70 by 130. The first attempt read perfectly from directly overhead
        /// and not at all from the car, which is the only place it matters: at eye level a field is seen
        /// almost edge-on, so its <i>apparent</i> depth is a fraction of its real one, and at 70 m across
        /// the patchwork collapsed into mottling by the second field. These are wide enough that the
        /// road crosses two or three of them between bends and each one still fills the windscreen.</para>
        /// </summary>
        private const float FieldAlong = 170f;

        private const float FieldAcross = 90f;

        /// <summary>
        /// Which way the fields lie.
        ///
        /// <para>Off both world axes on purpose. Fields aligned to x and z read as the grid the terrain
        /// is built on, which is exactly the thing the art direction spends its whole time hiding.</para>
        /// </summary>
        private const float FieldAngleDegrees = 40f;

        private readonly RoadProximity road;
        private readonly float cosAngle;
        private readonly float sinAngle;

        private LandRegion(string name, RoadProximity road, GroundPalette ground, float wildTreeChance,
            float startAlong = 0f,
            float endAlong = float.PositiveInfinity,
            float spireChance = 0f,
            bool autumnCanopy = false,
            bool farmed = false,
            float treeLineElevation = float.NaN,
            float snowLineElevation = float.NaN,
            float treeDensity = 1f,
            float clumpThreshold = float.NaN,
            float treeMaxSlopeDegrees = float.NaN,
            float blossomChance = 0f,
            float flowerChance = 0f,
            float farDensity = float.NaN)
        {
            Name = name;
            this.road = road;
            Ground = ground;
            WildTreeChance = wildTreeChance;
            StartAlong = startAlong;
            EndAlong = endAlong;
            SpireChance = spireChance;
            AutumnCanopy = autumnCanopy;
            Farmed = farmed;
            TreeLineElevation = treeLineElevation;
            SnowLineElevation = snowLineElevation;
            TreeDensity = treeDensity;
            ClumpThreshold = clumpThreshold;
            TreeMaxSlopeDegrees = treeMaxSlopeDegrees;
            BlossomChance = blossomChance;
            FlowerChance = flowerChance;
            FarDensity = farDensity;

            cosAngle = Mathf.Cos(FieldAngleDegrees * Mathf.Deg2Rad);
            sinAngle = Mathf.Sin(FieldAngleDegrees * Mathf.Deg2Rad);
        }

        public string Name { get; }

        public GroundPalette Ground { get; }

        /// <summary>
        /// What share of the wild trees survives here.
        ///
        /// <para>Low, and that is the point rather than a saving. Farmland is farmland because the wood
        /// has been cleared off it; a scatter at forest density with orchards planted through it is a
        /// forest with rows in it. The triangles this frees are what pays for the avenue.</para>
        /// </summary>
        public float WildTreeChance { get; }

        /// <summary>
        /// How far along its own road the region begins, metres.
        ///
        /// <para><b>Exists because two regions can share a road.</b> Membership here is distance to the
        /// region's own carriageway plus a fade over <see cref="EntryFade"/> of it, which is exactly
        /// right while every region has a road to itself — and wrong the moment one road crosses from
        /// one country into another. The Meerenge is one course from the coast road to the far shore,
        /// and the thing that separates the two is 1250 m of bridge rather than a different piece of
        /// tarmac. Measuring from a distance along the road puts the change where the change is.</para>
        /// </summary>
        public float StartAlong { get; }

        /// <summary>
        /// How far along its own road the region stops, metres — or infinity, which is every region
        /// that was written before this existed.
        ///
        /// <para><b>Without an end there is no such thing as "here and not there".</b>
        /// <see cref="Weight"/> fades a region in over <see cref="EntryFade"/> and never fades it out,
        /// so a region ran from <see cref="StartAlong"/> to wherever its road happened to finish. That
        /// is right for a country — the far shore of the Meerenge is Anadolu for the rest of the drive
        /// — and it is exactly wrong for a wood, which is a place you come out of. A forest belt is the
        /// first thing here that has to end.</para>
        ///
        /// <para>The exit reuses <see cref="EntryFade"/> rather than carrying a length of its own. One
        /// number, because a belt soft on one side and sharp on the other reads as a fault rather than
        /// as an edge, and two would let it become one by drifting.</para>
        /// </summary>
        public float EndAlong { get; }

        /// <summary>
        /// What share of this region's trees are spires rather than round crowns.
        ///
        /// <para>Cypresses, and they reuse the poplar the Ebental's avenue is planted from — a poplar
        /// and a cypress are the same silhouette at the distance either is ever seen from, and a new
        /// species would be a new mesh, a new submesh and a new tint for a shape the project already
        /// has. What makes them read as somewhere else is that they are <i>scattered</i> rather than
        /// planted in a row: the Ebental's poplars say a road was planted, and these say a hillside grew
        /// this way.</para>
        /// </summary>
        public float SpireChance { get; }

        /// <summary>Whether the round-crowned trees here are the autumn canopy. See <c>ScatterTrees</c>.</summary>
        public bool AutumnCanopy { get; }

        /// <summary>
        /// Whether somebody farms here.
        ///
        /// <para><b>Not decoration, and the flag exists because leaving it out was a visible bug.</b>
        /// Orchard rows, hay bales and walled field boundaries were run for any region at all, on the
        /// reasonable-looking grounds that the only region was farmland. The far shore of the Meerenge
        /// came back with post-and-rail fences and round bales on it — the vocabulary of an alpine
        /// valley, laid over the one place in the world built to read as somewhere else. Whatever
        /// eventually stands on a dry hillside, it is not a hay bale.</para>
        /// </summary>
        public bool Farmed { get; }

        /// <summary>
        /// Where the trees stop here, in absolute metres — or <c>NaN</c> where this region has no
        /// opinion and the world's own tree line applies.
        ///
        /// <para><b>Absolute, and that is the whole point of it existing.</b> Everywhere else the tree
        /// line is <c>VegetationShape.TreeLineHeight</c>, a fraction of the span between the mountain
        /// pass's lowest point and its summit — 0.82 of 203 m, which puts it at 160 m. That axis is
        /// normalised against <i>one course</i>, so a mountain four times higher somewhere else clamps
        /// to the top of it and comes out bare from the valley floor up. Stretching the axis to cover
        /// both is worse: it would move the tree line to over seven hundred metres everywhere and wood
        /// the pass to its own summit, which is the failure <c>VegetationBuilder</c>'s own remarks warn
        /// about.</para>
        ///
        /// <para>So a region may hold its own, and <see cref="HasAltitudeBands"/> is what tells the
        /// builders which rule to apply. The fraction stays exactly as it was for every road that had
        /// one, which is what keeps this from being a change to the whole world.</para>
        /// </summary>
        public float TreeLineElevation { get; }

        /// <summary>
        /// Where the snow starts here, absolute metres, or <c>NaN</c> for a region with no snow.
        ///
        /// <para>Read by <c>TerrainTileBuilder</c> in the slot the shoreline's sand already occupies:
        /// one more colour in a per-triangle comparison on a shared vertex-tinted material, so it costs
        /// no material, no draw call and no vertices.</para>
        /// </summary>
        public float SnowLineElevation { get; }

        /// <summary>
        /// Whether this region decides its own tree and snow lines by elevation. See
        /// <see cref="TreeLineElevation"/>.
        /// </summary>
        public bool HasAltitudeBands => !float.IsNaN(TreeLineElevation);

        /// <summary>
        /// How many trees this region wants per unit of ground, against the world's one.
        ///
        /// <para><b>Applied to the candidate grid rather than to a dice roll, which is the only way it
        /// gets a forest rather than a thicker scatter.</b> <c>VegetationShape.TreeCellSize</c> lays one
        /// candidate every 11 m and everything after that only ever removes candidates — so no amount of
        /// relaxing the filters can put more trees down than the grid offered. The cell is divided by the
        /// square root of this, because the grid is two-dimensional and doubling the trees means
        /// tightening the spacing by 1.41 rather than by 2.</para>
        /// </summary>
        public float TreeDensity { get; }

        /// <summary>
        /// This region's share of hillside that is clearing rather than wood, or <c>NaN</c> to use
        /// <c>VegetationShape.ClumpThreshold</c>.
        ///
        /// <para>The world's 0.34 is right for country that is meant to read as open with stands in it.
        /// A mountain forest is the other thing: continuous, with the clearings being the exception, and
        /// at the world's value a winter hillside came out as scattered copses on bare ground.</para>
        /// </summary>
        public float ClumpThreshold { get; }

        /// <summary>
        /// How steep ground may be here before nothing will grow on it, or <c>NaN</c> for the world's
        /// <c>VegetationShape.TreeMaxSlopeDegrees</c>.
        ///
        /// <para><b>This is the one that made the Weissjoch bald and it was invisible in the log.</b>
        /// The world's limit is 30°, chosen against a pass whose face is 63 % — but that is the
        /// <i>mean</i>, and <c>MountainField</c> blends the ground between two stacked legs with an
        /// inverse-fifth power, so the middle of every face is far steeper than its average. On a
        /// twenty-eight hairpin stack that rejected the trees on every face and kept only the ones on
        /// the flat shelves, which is a mountain with stripes on it. Real spruce holds ground no
        /// engineer would build a road on.</para>
        /// </summary>
        public float TreeMaxSlopeDegrees { get; }

        /// <summary>
        /// What share of this region's trees are cherries in flower.
        ///
        /// <para>The exact analogue of <see cref="SpireChance"/>, and it does two jobs from one number:
        /// it picks the wild trees, and where <see cref="Farmed"/> is also set it puts the orchard rows
        /// into blossom instead of the Ebental's rust. One knob, because a region with pink woods and
        /// rust orchards would be two places rather than one.</para>
        ///
        /// <para>Unlike the cypress, this one is a mesh of its own — see <c>PlantMeshes.AddCherry</c>
        /// for why a repaint was not enough here.</para>
        /// </summary>
        public float BlossomChance { get; }

        /// <summary>
        /// What share of this region's grass tufts come up as a flower instead.
        ///
        /// <para><b>It rides on the grass rather than on a scatter of its own</b>, and that is the
        /// whole reason it is affordable. <c>ScatterTufts</c> already works only in the band between
        /// <c>VegetationShape.TuftClearance</c> and <c>TuftMaxDistance</c> — a strip about twenty metres
        /// wide beside the carriageway — which happens to be the only place a thirty-centimetre plant
        /// can be seen from a car at all. A grid of its own would have had to be told the same thing
        /// twice and would have planted flowers across hillsides nobody can resolve them on.</para>
        ///
        /// <para>Nought everywhere by default, so this is a signature rather than a coat of paint: a
        /// world where every verge is in flower says nothing about any of them.</para>
        /// </summary>
        public float FlowerChance { get; }

        /// <summary>
        /// This region's own far-field thinning, or <c>NaN</c> to take the world's.
        ///
        /// <para><b>The global is right for a mountain and wrong for a plain, which is why this is a
        /// region.</b> <c>VegetationShape.FarDensity</c> thins what stands more than a hundred metres
        /// from a carriageway, and its own remarks record that a switchback stack has no such ground and
        /// therefore nothing to thin. The reverse is what makes open country look empty: a road across a
        /// plain has almost nothing <i>but</i> far field, so half of everything past a hundred metres is
        /// removed and the middle distance comes out bare between the verge and the horizon.</para>
        ///
        /// <para>It is a region knob and not a global one because the two mountains own the world's
        /// heaviest tiles, and this is the one setting here that would grow exactly those.</para>
        /// </summary>
        public float FarDensity { get; }

        /// <summary>The region the rest of the world is in: none of it, and the colours it already had.</summary>
        public static LandRegion None { get; } = new LandRegion(
            "None",
            null,
            new GroundPalette(TerrainTileBuilder.GrassTint, TerrainTileBuilder.RockTint, null),
            1f);

        /// <summary>
        /// The Ebental: autumn orchard country along the country road.
        ///
        /// <para><b>Mostly green, and that is a correction.</b> The first palette was three browns and a
        /// green in even quarters, which came back as desert — because the world is lit at golden hour
        /// and warm ground under a warm sun has nowhere left to go. What autumn farmland actually is, is
        /// meadow with worked fields cut into it: so pasture and meadow take five sixths of the ground
        /// between them, and stubble and ploughed earth are the accents that make the rest read as
        /// fields at all. The pasture tone sits within a hair of the world's own grass on purpose — the
        /// Ebental should read as another part of the same country, not as another game.</para>
        /// </summary>
        public static LandRegion Ebental(IRoadPath path)
        {
            var fields = new Color32[]
            {
                new Color(0.38f, 0.50f, 0.26f),  // pasture; all but the world's own green
                new Color(0.50f, 0.54f, 0.28f),  // meadow gone over, the ground note of the region
                new Color(0.72f, 0.64f, 0.34f),  // stubble; bright, and where the hay bales go
                new Color(0.40f, 0.30f, 0.23f),  // ploughed, the darkest thing in the valley
            };

            return new LandRegion(
                "Ebental",
                new RoadProximity(path),
                new GroundPalette(
                    new Color(0.50f, 0.54f, 0.28f),
                    new Color(0.48f, 0.40f, 0.31f),
                    fields),
                wildTreeChance: 0.18f,
                autumnCanopy: true,
                farmed: true,

                // Fewer than the Bahçe's, and different in kind: this valley is at the other end of the
                // year. What flowers along a verge in autumn is the last of it, so the share is low
                // enough to read as scattered rather than as a meadow in bloom.
                flowerChance: 0.14f,

                // Three quarters of the far field kept rather than half, for the reason the Bahçe keeps
                // it: this is farmland on a valley floor, so nearly all of it is the far ground the
                // world's falloff thins, and thinning open country is what makes it read as empty.
                farDensity: 0.75f);
        }

        /// <summary>
        /// Anadolu: the far shore of the Meerenge, and the reason the bridge is worth crossing.
        ///
        /// <para><b>A bridge between two identical places is a long piece of road.</b> The crossing
        /// works as a structure from the first day it was built, and the preview from the far bank
        /// showed the fault immediately anyway: the same spruce, the same green, the same everything,
        /// on both sides of a kilometre of water. What a threshold needs is something on the far side
        /// that the near side does not have.</para>
        ///
        /// <para><b>Warm and dry, and the palette is the first half of it.</b> Where the Ebental is
        /// meadow with worked fields cut into it, this is burnt grass with olive terraces and red earth
        /// in it — the same trick, one climate over. The pasture tone deliberately does <i>not</i> sit
        /// near the world's own green here, which is the opposite of the choice the Ebental made and
        /// for the opposite reason: that region had to read as another part of the same country, and
        /// this one has to read as another country.</para>
        ///
        /// <para><b>Cypresses are the second half, and they do the work at a distance.</b> Half the
        /// trees here are spires, scattered rather than planted in a row — see
        /// <see cref="SpireChance"/>. A round crown and a spire are still distinguishable when both are
        /// four pixels tall, which is more than any ground colour can claim.</para>
        ///
        /// <para>It begins at the eastern anchorage rather than at the start of its road, because its
        /// road is also the coast road on the other side of the water. See <see cref="StartAlong"/>.</para>
        /// </summary>
        public static LandRegion Anadolu(IRoadPath path, float startAlong)
        {
            var fields = new Color32[]
            {
                new Color(0.62f, 0.58f, 0.32f),  // burnt grass, the ground note of the far side
                new Color(0.45f, 0.47f, 0.27f),  // olive terrace, the only green over here
                new Color(0.74f, 0.62f, 0.36f),  // dry stubble
                new Color(0.55f, 0.33f, 0.22f),  // red earth, and the accent nothing west of the water has
            };

            return new LandRegion(
                "Anadolu",
                new RoadProximity(path),
                new GroundPalette(
                    new Color(0.62f, 0.58f, 0.32f),
                    new Color(0.56f, 0.42f, 0.30f),
                    fields),
                // Higher than the Ebental's 0.18: this is hillside rather than farmland, so the wood was
                // never cleared off it — only ever thin because of the climate.
                // Thin, and thinner than the Ebental's farmland for the opposite reason: nobody cleared
                // this, it is simply dry. At 0.55 the far shore came out a cypress forest, which is a
                // different wrong country rather than the right one — and put a tile over the vegetation
                // budget while it was at it.
                wildTreeChance: 0.32f,
                startAlong: startAlong,
                spireChance: 0.3f);
        }

        /// <summary>
        /// The Weissjoch: nine hundred metres of mountain north of the motorway, and the first winter
        /// in the world.
        ///
        /// <para><b>It is the first region that decides anything by altitude</b> — see
        /// <see cref="TreeLineElevation"/> for why it has to, and why the world's own tree line could
        /// not simply be stretched to cover it. Below 460 m it is spruce forest, above it bare rock and
        /// alpine grass, and above 650 m snow.</para>
        ///
        /// <para><b>Not farmed, and no spires.</b> Nobody farms at nine hundred metres, so the orchard
        /// rows, the bales and the walled boundaries stay off — the far shore of the Meerenge is the
        /// recorded case of what happens when that flag is left on for a place it does not belong to.
        /// The trees here are the conifers the vegetation builder already biases towards with height;
        /// what makes this read as a mountain rather than as the pass writ large is the rock and the
        /// snow above them, not a new species.</para>
        ///
        /// <para>The ground is a colder grey than the world's own. Rock at nine hundred metres under
        /// snow is not the warm brown of a valley crag, and the two sitting side by side in one frame —
        /// which they do, on every leg of the stack — is most of what says how high this is.</para>
        /// </summary>
        public static LandRegion Weissjoch(IRoadPath path)
        {
            return new LandRegion(
                "Weissjoch",
                new RoadProximity(path),
                new GroundPalette(
                    // Alpine grass: cooler and paler than the world's 0.36/0.48/0.26, because it spends
                    // half the year under the snow above it.
                    new Color(0.40f, 0.45f, 0.33f),
                    // Cold grey rather than the world's warm brown crag.
                    new Color(0.50f, 0.51f, 0.54f),
                    // No fields. Nobody farms up here, and a null palette is what says so.
                    null),
                // Dense. This is mountain forest below the tree line, not cleared farmland and not a dry
                // hillside — the two reasons the other regions thin their scatter do not apply, and a
                // thin wood under a snow line reads as a mountain that has been logged.
                // No thinning at all. The two regions that thin do it because somebody cleared the
                // wood or because the climate never grew one; neither is true of a mountain flank.
                wildTreeChance: 1f,
                treeLineElevation: WeissjochCourse.TreeLineElevation,
                snowLineElevation: WeissjochCourse.SnowLineElevation,

                // Two and a bit times the world's density, almost no clearings, and trees on ground
                // half again as steep as anywhere else will take them. Together these are what turns a
                // striped hillside into a forest — see each property for which failure it answers.
                treeDensity: 2.4f,
                clumpThreshold: 0.10f,
                treeMaxSlopeDegrees: 44f);
        }

        /// <summary>
        /// The Weissjochring: the same mountain as <see cref="Weissjoch"/>, one shoulder lower, and
        /// deliberately not the same forest.
        ///
        /// <para><b>The palette and both altitude bands are shared, and that is the point.</b> The
        /// circuit is cut into the same massif the climb is, and a region boundary that could be seen
        /// from the car would say there were two mountains. What it inherits is exactly what makes this
        /// place itself: cold grey rock rather than the world's warm crag, alpine grass, no fields, and
        /// the tree line at 700 m with the snow line a hundred metres <i>below</i> it.</para>
        ///
        /// <para><b>What it does not inherit is the density, and that is a tile budget rather than a
        /// taste.</b> The heaviest tile in this world is already a Weissjoch tile, well over
        /// <c>VegetationShape.MaxTrianglesPerTile</c>, and <see cref="TreeDensity"/> 2.4 with a
        /// <see cref="ClumpThreshold"/> of 0.10 is most of the reason. Those two numbers exist to stop a
        /// twenty-eight hairpin switchback stack coming out striped — trees on the flat shelves and
        /// nowhere else. A circuit is not a stack: its rungs are 340 m apart rather than 60, so there is
        /// real ground between them at ordinary slopes, and the world's own density is enough to wood
        /// it. Fifteen kilometres of road at the stack's density would have moved the world total by
        /// more than the whole Ebental.</para>
        ///
        /// <para><b>And unlike the stack, the distance falloff actually works here.</b>
        /// <c>VegetationShape.FarDensity</c> thins what stands more than 100 m from a carriageway; a
        /// switchback stack has no such ground and so nothing to thin, which is why
        /// <see cref="TreeDensity"/> had to be the knob up there. A ladder with 340 m between its rungs
        /// is half falloff country, and it pays for itself.</para>
        ///
        /// <para><see cref="TreeMaxSlopeDegrees"/> is kept at the mountain's 44°. The faces between two
        /// stacked rungs run to about 35°, and the world's 30° would put trees on the rungs and nowhere
        /// in between — the striped hillside, one landform along.</para>
        /// </summary>
        public static LandRegion Weissjochring(IRoadPath path)
        {
            return new LandRegion(
                CircuitName,
                new RoadProximity(path),
                new GroundPalette(
                    new Color(0.40f, 0.45f, 0.33f),
                    new Color(0.50f, 0.51f, 0.54f),
                    null),
                wildTreeChance: 1f,
                treeLineElevation: WeissjochCourse.TreeLineElevation,
                snowLineElevation: WeissjochCourse.SnowLineElevation,
                treeDensity: 1.3f,
                clumpThreshold: 0.22f,
                treeMaxSlopeDegrees: 44f);
        }

        /// <summary>
        /// The Bahçe: the valley the second circuit is cut into, east of Yalıköy, and the only place in
        /// this world that is in flower.
        ///
        /// <para><b>No altitude bands, deliberately.</b> The lap runs between 28 and 60 m, and the
        /// world's own tree line is at 160 — so every metre of this region is below it by construction
        /// and <see cref="TreeLineElevation"/> would be answering a question nobody asks here. Setting
        /// one anyway is the trap <c>VegetationBuilder.ClimbAt</c> exists to make visible: the axis is
        /// shared with the mountain pass, and a region that stretches it moves the vegetation of the
        /// whole world. The build log must still read "Tree line around 160 m" after this.</para>
        ///
        /// <para><b>Farmed, and that is what makes it a place rather than a colour.</b> Anadolu one
        /// road back is dry hillside nobody clears; this is a valley of orchards, so it gets the rows,
        /// the walled boundaries and the cut meadows — and <see cref="BlossomChance"/> puts the rows
        /// into flower rather than into the Ebental's rust. The four parcels keep the meanings the rest
        /// of the project already gives them: orchards stand on 0 and 1, bales on 2, and 3 is the one
        /// that is nothing but ground, which here is petals lying on it.</para>
        ///
        /// <para><see cref="WildTreeChance"/> sits between the Ebental's 0.18 and the mountain's 1.
        /// Cleared enough to read as worked, wooded enough that the groves are groves.</para>
        /// </summary>
        /// <summary>
        /// The Bahçe's rarest parcel: ground with fallen blossom lying on it.
        ///
        /// <para>Its own constant so the build can count it. A parcel colour is decided per triangle
        /// against a region's own weight, which makes it exactly the kind of thing that comes out as
        /// nothing at all, or as the whole valley, with the log saying not a word — the argument
        /// <c>TerrainTileBuilder.SnowTint</c> already carries.</para>
        /// </summary>
        public static readonly Color32 BahcePetal = new Color(0.86f, 0.79f, 0.79f);

        public static LandRegion Bahce(IRoadPath path, float startAlong = 0f)
        {
            var fields = new Color32[]
            {
                new Color(0.46f, 0.60f, 0.31f),  // meadow, the ground note of the valley
                new Color(0.39f, 0.53f, 0.28f),  // orchard floor, mown and a shade deeper
                new Color(0.72f, 0.73f, 0.44f),  // cut for hay, and where the bales stand
                BahcePetal,                      // the rarest parcel, and the signature
            };

            return new LandRegion(
                BlossomName,
                new RoadProximity(path),
                new GroundPalette(
                    new Color(0.46f, 0.60f, 0.31f),
                    // Pale warm stone rather than the world's brown crag or the Weissjoch's cold grey.
                    // There is very little rock showing here; what there is should not read as either
                    // of the two mountains.
                    new Color(0.62f, 0.57f, 0.49f),
                    fields),
                wildTreeChance: 0.7f,
                startAlong: startAlong,
                farmed: true,
                clumpThreshold: 0.30f,
                blossomChance: 0.55f,

                // In flower on the ground as well as in the trees. This is the region the whole idea is
                // for — an orchard valley in spring with a bare verge is a contradiction — so it carries
                // the highest share in the world.
                flowerChance: 0.30f,

                // And it keeps three quarters of its far field rather than half. The lap runs across a
                // floor between 30 and 60 m with nothing steep anywhere on it, so almost all of this
                // region is the ground the world's falloff exists to thin — and thinning a plain is what
                // makes a plain look empty. The tiles here are among the lightest in the world.
                farDensity: 0.78f);
        }

        /// <summary>
        /// A wood, on a stretch of road that has an end to it.
        ///
        /// <para><b>Every other factory here is a country and this one is a place.</b> A country begins
        /// somewhere and is the rest of the drive — Anadolu starts at the eastern anchorage and does not
        /// stop. A wood is something you go into and come out of, which is the whole of why
        /// <see cref="EndAlong"/> had to exist before this could.</para>
        ///
        /// <para><b>It is the same three knobs the Weissjoch is woody by</b>, and it is worth saying
        /// that they are three rather than one. <see cref="TreeDensity"/> divides the candidate grid, so
        /// it is the only one that can put down more trees than were offered — everything after that
        /// point can only remove candidates. <see cref="ClumpThreshold"/> is the share of hillside that
        /// is clearing rather than wood, and at the world's 0.34 two thirds of a forest is a gap.
        /// <see cref="TreeMaxSlopeDegrees"/> is the one nobody expects: the world's 30° was chosen
        /// against a mean face angle, and <c>MountainField</c> blends between legs with an inverse-fifth
        /// power, so the middle of a slope is far steeper than its average and a wood on one comes out
        /// in stripes.</para>
        ///
        /// <para><b>The ground goes dark with it, and that is not decoration.</b> A dense wood standing
        /// on the world's open meadow green reads as trees placed on a field. Spruce country has needle
        /// litter under it, so the palette is a deep cold green and a damp grey-brown crag, and the
        /// change is on the same fade as the trees — one region, one edge.</para>
        ///
        /// <para><b>No fields, no altitude bands, and both are deliberate.</b> A null palette is what
        /// says nobody farms here, which is what keeps hay bales and post-and-rail fences out of a wood
        /// — the failure <see cref="Farmed"/> records. And a belt that carried its own tree line would
        /// re-normalise the elevation axis under it: <c>VegetationBuilder.ClimbAt</c> maps a region with
        /// bands onto an absolute scale and one without onto the mountain pass's, so a line here would
        /// move where the world's own wood stops. It has to stay <c>NaN</c>, and the build log's "Tree
        /// line around 160 m" is what says it did.</para>
        /// </summary>
        public static LandRegion Forest(
            string name,
            IRoadPath path,
            float startAlong,
            float endAlong,
            float treeDensity = 1.8f,
            float clumpThreshold = 0.14f)
        {
            return new LandRegion(
                name,
                new RoadProximity(path),
                new GroundPalette(
                    // Needle litter and deep shade rather than open meadow. Darker and cooler than the
                    // world's 0.36/0.48/0.26 by about as much as the Weissjoch's alpine grass is paler
                    // than it — the two are the same distance from the world in opposite directions,
                    // which is what makes either read as somewhere.
                    new Color(0.24f, 0.34f, 0.21f),
                    // Damp rock. The world's crag is a dry warm brown; stone under a canopy is not.
                    new Color(0.36f, 0.35f, 0.31f),
                    null),
                // Nothing thins a wild wood. The two regions that thin do it because somebody cleared
                // the trees or because the climate never grew them, and a belt exists to say the
                // opposite of both.
                wildTreeChance: 1f,
                startAlong: startAlong,
                endAlong: endAlong,
                treeDensity: treeDensity,
                clumpThreshold: clumpThreshold,

                // The same 44° the mountain carries, and for the same reason rather than by copying:
                // the faces this world builds between two road legs are steeper in the middle than
                // their average, and 30° puts the trees on the shelves and nowhere between them.
                treeMaxSlopeDegrees: 44f);
        }

        /// <summary>Name of the circuit's region. Its own constant so the course and this agree.</summary>
        private const string CircuitName = "Weissjochring";

        /// <summary>Name of the Bahçe's region, for the same reason.</summary>
        private const string BlossomName = "Bahçe";

        /// <summary>How much of this region applies at a point: 0 outside it, 1 well inside.</summary>
        public float Weight(float x, float z)
        {
            if (road == null)
            {
                return 0f;
            }

            road.Nearest(x, z, out float distance, out float along);

            if (distance >= EdgeReach)
            {
                return 0f;
            }

            // Mathf.SmoothStep(a, b, t) interpolates *between* a and b with t clamped to 0..1 — it is
            // not the shader smoothstep(edge0, edge1, x) it is named after. Handing it metres returns
            // metres: this read -103600 at every point in the region until it was written as an
            // InverseLerp inside a SmoothStep, and a weight that is never positive is a region that
            // silently does nothing.
            float across = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CoreReach, EdgeReach, distance));

            // A closed road has no start, so there is nothing for the entry fade to fade in from. On a
            // circuit `along` runs back to zero at the start line, and the fade was therefore thinning
            // the region over the first four hundred metres of the lap — which on both circuits is the
            // main straight and the paddock, the one stretch anybody looks at closely. The fade exists
            // to blend one region into the next along a road they share; a lap shares its road with
            // itself.
            float entry = road.IsLoop
                ? 1f
                : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(StartAlong, StartAlong + EntryFade, along));

            // And out again, where the region has an end. Written as the same fade run backwards, so a
            // belt is symmetric by construction — see EndAlong. A loop is left alone here for the reason
            // above: `along` runs back to zero at the start line, so any test against it cuts the lap.
            float exit = road.IsLoop || float.IsPositiveInfinity(EndAlong)
                ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(EndAlong - EntryFade, EndAlong, along));

            return across * entry * exit;
        }

        /// <summary>
        /// Whether the region reaches anywhere inside a circle — a tile's bounding circle, in practice.
        ///
        /// <para>Exact, and one query rather than a handful of samples. Sampling a tile at its corners
        /// and its middle can miss a region that clips a single corner of it, because the nearest corner
        /// to the road may still be beyond the reach while a point between two of them is not. The
        /// distance to the road is a distance field: subtract the radius and the answer is certain.</para>
        /// </summary>
        public bool Reaches(float x, float z, float radius)
        {
            if (road == null)
            {
                return false;
            }

            road.Nearest(x, z, out float distance, out float along);

            if (distance - radius >= EdgeReach)
            {
                return false;
            }

            // <b>The along test belongs here as much as the distance one, and leaving it out is the
            // quiet half of giving a region an end.</b> This predicate is what picks *which* region a
            // tile gets — PrototypeSetup takes the first in its list that reaches — so a belt that went
            // on claiming tiles past its own end would shadow whatever region lies behind it. The
            // Ebental would lose its parcels, its orchards, its bales and its avenue wherever a wood
            // overlapped, and nothing in the build would say a word: the tiles would simply come out as
            // somebody else's country.
            //
            // Conservative by the same radius, so a tile clipping the boundary is kept rather than
            // dropped. The start is deliberately not tested: a region reaches back over its own entry
            // fade and a tile before it costs one query to find that its weight is zero.
            return road.IsLoop
                || float.IsPositiveInfinity(EndAlong)
                || along - radius <= EndAlong;
        }

        /// <summary>
        /// Which field a point falls in, as an index into <see cref="GroundPalette.Fields"/>.
        ///
        /// <para>Read from the rotated grid rather than from noise, because the edge is the point. Perlin
        /// gives mottling, and mottled ground is ground nobody farms.</para>
        /// </summary>
        public int Parcel(float x, float z)
        {
            if (Ground.Fields == null || Ground.Fields.Length == 0)
            {
                return -1;
            }

            // Weighted, not one field in four each. An even draw put a quarter of the valley under
            // plough and a quarter under stubble, and a landscape that is half bare earth is a landscape
            // nobody lives off. Grazing and meadow carry it; the other two are the accents that say the
            // rest of it is farmed.
            float value = ParcelValue(x, z, 11u);

            if (value < 0.44f)
            {
                return 0;
            }

            if (value < 0.76f)
            {
                return 1;
            }

            return value < 0.90f ? 2 : 3;
        }

        /// <summary>The field grid coordinates of a point, in the rotated frame.</summary>
        public void ParcelCell(float x, float z, out int cellX, out int cellZ)
        {
            ToField(x, z, out float u, out float v);

            cellX = Mathf.FloorToInt(u / FieldAcross);
            cellZ = Mathf.FloorToInt(v / FieldAlong);
        }

        /// <summary>Spacing of the field grid across the furrow, metres.</summary>
        public float PitchAcross => FieldAcross;

        /// <summary>Spacing of the field grid along the furrow, metres.</summary>
        public float PitchAlong => FieldAlong;

        /// <summary>
        /// World position to the rotated frame the fields are laid out in.
        ///
        /// <para>Public because the boundaries, the orchard rows and the ground colour all have to agree
        /// about where a field is. Three separate ideas of the same grid would be three grids.</para>
        /// </summary>
        public void ToField(float x, float z, out float u, out float v)
        {
            u = x * cosAngle + z * sinAngle;
            v = -x * sinAngle + z * cosAngle;
        }

        /// <summary>The inverse of <see cref="ToField"/>.</summary>
        public Vector2 FromField(float u, float v)
        {
            return new Vector2(u * cosAngle - v * sinAngle, u * sinAngle + v * cosAngle);
        }

        /// <summary>A stable value in 0..1 for a named grid cell, for callers that already have one.</summary>
        public float CellValue(int cellX, int cellZ, uint salt)
        {
            return (Hash(cellX, cellZ, salt) & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>
        /// A stable value in 0..1 for a field, so callers can ask it questions the palette does not
        /// answer — whether it is an orchard, whether it is walled.
        /// </summary>
        public float ParcelValue(float x, float z, uint salt)
        {
            ParcelCell(x, z, out int cellX, out int cellZ);
            return (Hash(cellX, cellZ, salt) & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>
        /// FNV-1a over the two cell coordinates and a salt, avalanched.
        ///
        /// <para>Its own rather than <c>VegetationBuilder.Hash</c>, which is private to that file and
        /// keyed on the scatter's own species numbering. Same construction, so it mixes as well.</para>
        /// </summary>
        private static uint Hash(int cellX, int cellZ, uint salt)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)cellX) * 16777619u;
                hash = (hash ^ (uint)cellZ) * 16777619u;
                hash = (hash ^ salt) * 16777619u;

                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                hash *= 0x846CA68Bu;
                hash ^= hash >> 16;

                return hash;
            }
        }
    }

    /// <summary>
    /// How far a point is from a road, and how far along that road the nearest place on it is.
    ///
    /// <para>The same construction <see cref="MountainField"/> uses internally for
    /// <c>DistanceToRoad</c> — samples in a coarse bucket grid — but small, public, and answering the
    /// second question as well. The arc length is what lets a region fade in along the road it belongs
    /// to instead of switching on at its first metre.</para>
    /// </summary>
    public sealed class RoadProximity
    {
        /// <summary>Spacing of the stored samples, metres.</summary>
        private const float Spacing = 8f;

        /// <summary>Side of a bucket, metres. Matches MountainField's, for the same reasons.</summary>
        private const float BucketSize = 32f;

        private readonly Vector2[] points;
        private readonly float[] along;
        private readonly Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();

        /// <summary>
        /// Whether the road this samples closes on itself.
        ///
        /// <para>Read by <see cref="LandRegion.Weight"/> for one reason: a closed road has no start, so
        /// the entry fade has nothing to fade in from. See there.</para>
        /// </summary>
        public bool IsLoop { get; }

        public RoadProximity(IRoadPath path)
        {
            IsLoop = path != null && path.IsLoop;

            float length = path != null ? path.Length : 0f;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Spacing) + 1);

            points = new Vector2[count];
            this.along = new float[count];

            for (int i = 0; i < count; i++)
            {
                float distance = Mathf.Min(length, i * Spacing);
                Vector3 on = path.GetPositionAtDistance(distance);

                points[i] = new Vector2(on.x, on.z);
                this.along[i] = distance;

                long key = Key(points[i].x, points[i].y);
                if (!buckets.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>(8);
                    buckets[key] = bucket;
                }

                bucket.Add(i);
            }
        }

        /// <summary>
        /// Nearest sample to a point: how far away it is, and how far along the road it sits.
        ///
        /// <para>Searches outward a ring of buckets at a time and stops as soon as the ring it has
        /// finished cannot be beaten, so an empty stretch of country costs a handful of dictionary
        /// lookups rather than a walk of the whole road.</para>
        /// </summary>
        public void Nearest(float x, float z, out float distance, out float alongRoad)
        {
            int centreX = Mathf.FloorToInt(x / BucketSize);
            int centreZ = Mathf.FloorToInt(z / BucketSize);

            float bestSquared = float.MaxValue;
            int best = -1;

            for (int ring = 0; ring < 12; ring++)
            {
                for (int cz = centreZ - ring; cz <= centreZ + ring; cz++)
                {
                    for (int cx = centreX - ring; cx <= centreX + ring; cx++)
                    {
                        // Only the shell: everything inside was searched by a smaller ring.
                        if (ring > 0
                            && cx > centreX - ring && cx < centreX + ring
                            && cz > centreZ - ring && cz < centreZ + ring)
                        {
                            continue;
                        }

                        if (!buckets.TryGetValue(KeyOfCell(cx, cz), out List<int> bucket))
                        {
                            continue;
                        }

                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int index = bucket[i];
                            float dx = points[index].x - x;
                            float dz = points[index].y - z;
                            float squared = dx * dx + dz * dz;

                            if (squared < bestSquared)
                            {
                                bestSquared = squared;
                                best = index;
                            }
                        }
                    }
                }

                // A sample found inside this ring cannot be beaten by one outside it once the ring's
                // own inner edge is further away than what has already been found.
                if (best >= 0 && bestSquared <= ring * BucketSize * (ring * BucketSize))
                {
                    break;
                }
            }

            if (best < 0)
            {
                distance = float.MaxValue;
                alongRoad = 0f;
                return;
            }

            distance = Mathf.Sqrt(bestSquared);
            alongRoad = along[best];
        }

        private static long Key(float x, float z)
        {
            return KeyOfCell(Mathf.FloorToInt(x / BucketSize), Mathf.FloorToInt(z / BucketSize));
        }

        private static long KeyOfCell(int cellX, int cellZ)
        {
            return ((long)cellX << 32) ^ (uint)cellZ;
        }
    }
}
