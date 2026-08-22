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
            float spireChance = 0f,
            bool autumnCanopy = false,
            bool farmed = false)
        {
            Name = name;
            this.road = road;
            Ground = ground;
            WildTreeChance = wildTreeChance;
            StartAlong = startAlong;
            SpireChance = spireChance;
            AutumnCanopy = autumnCanopy;
            Farmed = farmed;

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
                farmed: true);
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
            float entry = Mathf.SmoothStep(
                0f, 1f, Mathf.InverseLerp(StartAlong, StartAlong + EntryFade, along));

            return across * entry;
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

            road.Nearest(x, z, out float distance, out float _);

            return distance - radius < EdgeReach;
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

        public RoadProximity(IRoadPath path)
        {
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
