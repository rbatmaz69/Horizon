using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// A deterministic little random stream, seeded per plant.
    ///
    /// Not <c>System.Random</c>, which allocates one object per plant, and not <c>UnityEngine.Random</c>,
    /// whose state is global — with that, the shape of a tree would depend on how many trees happened to be
    /// generated before it, and the world would change whenever the tile order did.
    /// </summary>
    public struct PlantRandom
    {
        private uint state;

        public PlantRandom(uint seed)
        {
            // Zero is a fixed point of xorshift, so it must not be a valid state.
            state = seed == 0u ? 0x9E3779B9u : seed;
        }

        /// <summary>Advances the stream and returns the next value in [0, 1).</summary>
        public float Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            // Top 24 bits: the low bits of a xorshift are the weakest.
            return (state >> 8) * (1f / 16777216f);
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * Next();
        }

        public bool Chance(float probability)
        {
            return Next() < probability;
        }

        /// <summary>A seed for a plant's own shape, drawn from this stream.</summary>
        public uint NextSeed()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }

    /// <summary>
    /// Where one plant stands: the ground point, an up axis, a yaw and a size.
    ///
    /// The species builders work in the plant's own space and this turns that into world space, because
    /// every mesh in this project is built with world-space vertices and hung on an object at the origin.
    ///
    /// <para><b>The basis is right-handed — det[Right, Up, Forward] = +1 — and that is load-bearing.</b>
    /// Every triangle in this world takes its normal from its own winding
    /// (<see cref="VegetationMeshBuffer.AddTriangleRaw"/>), so a mirrored basis silently reverses every face
    /// authored through <see cref="ToWorld"/>: the geometry is then back-face culled *and* lit from behind.
    /// This basis was mirrored once — <c>Cross(reference, Up)</c> rather than <c>Cross(Up, reference)</c> —
    /// and the whole town rendered as open dollhouses, roofs floating over visible interiors, for want of
    /// one swapped argument. Do not reorder these cross products.</para>
    /// </summary>
    public readonly struct PlantPlacement
    {
        public readonly Vector3 Position;
        public readonly Vector3 Right;
        public readonly Vector3 Up;
        public readonly Vector3 Forward;
        public readonly float Scale;
        public readonly uint Seed;

        public PlantPlacement(Vector3 position, Vector3 up, float yawRadians, float scale, uint seed)
        {
            Position = position;
            Up = up.sqrMagnitude < 0.000001f ? Vector3.up : up.normalized;

            // Cross with world up fails exactly when the plant is upright, which is the common case, so the
            // reference axis is swapped for the near-vertical ones rather than the other way round.
            Vector3 reference = Mathf.Abs(Up.y) > 0.99f ? Vector3.forward : Vector3.up;
            Vector3 right = Vector3.Cross(Up, reference);
            if (right.sqrMagnitude < 0.000001f)
            {
                right = Vector3.right;
            }

            right.Normalize();
            Vector3 forward = Vector3.Cross(right, Up);

            // Yaw turns the same way as before the basis was un-mirrored: with these signs Forward comes out
            // as (sin, 0, cos) for an upright placement, exactly as it always did, and the entire change
            // reduces to Right -> -Right. That is why no caller's yaw needed re-deriving.
            float sin = Mathf.Sin(yawRadians);
            float cos = Mathf.Cos(yawRadians);
            Right = right * cos - forward * sin;
            Forward = forward * cos + right * sin;

            Scale = scale;
            Seed = seed;
        }

        public Vector3 ToWorld(float x, float y, float z)
        {
            return Position + (Right * x + Up * y + Forward * z) * Scale;
        }
    }

    /// <summary>
    /// The vertex and index buffers a tile's plants are accumulated into.
    ///
    /// Flat-shaded like everything else in the world, so every triangle owns its three vertices. There are
    /// deliberately no UVs: the foliage materials are untextured flat colours, and dropping the channel
    /// takes a third off the mesh memory, which at a few hundred thousand triangles is worth having.
    /// </summary>
    public sealed class VegetationMeshBuffer
    {
        private readonly List<Vector3> vertices = new List<Vector3>(8192);
        private readonly List<Vector3> normals = new List<Vector3>(8192);
        private readonly List<int>[] submeshes;

        /// <summary>
        /// Per-vertex tint, or null until someone asks for one.
        ///
        /// <para>Lazy because most of what goes through this buffer does not want it. The vegetation is
        /// four flat materials over four hundred thousand triangles, and giving every one of those a
        /// colour it never reads would cost five megabytes of mesh to say white four hundred thousand
        /// times. The buildings do want it — see <see cref="MergeTinted"/> — and they are the minority.</para>
        /// </summary>
        private List<Color32> colours;

        public VegetationMeshBuffer(int submeshCount)
        {
            submeshes = new List<int>[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                submeshes[i] = new List<int>(2048);
            }

            FlipCountBySubmesh = new int[submeshCount];
        }

        public int TriangleCount => vertices.Count / 3;

        /// <summary>
        /// How many triangles have landed in one submesh.
        ///
        /// For build-time reporting rather than for the mesh: the split between the lit and the dark glass
        /// is the one thing about the night that a night render cannot tell you, because a town with every
        /// pane rolled at 50 % looks perfectly plausible.
        /// </summary>
        public int TriangleCountIn(int submesh)
        {
            return submesh >= 0 && submesh < submeshes.Length ? submeshes[submesh].Count / 3 : 0;
        }

        public bool IsEmpty => vertices.Count == 0;

        /// <summary>
        /// How many faces <see cref="AddTriangleFacing"/> had to turn round.
        ///
        /// A build that reports anything other than zero has a helper somewhere authoring its vertices in the
        /// wrong order. The correction keeps the mesh right either way, so this is not an error — it is the
        /// only cheap way to notice that a helper has drifted, and it costs one integer.
        /// </summary>
        public int FlipCount { get; private set; }

        /// <summary>
        /// The same count, split by submesh.
        ///
        /// <para>One number says a builder has drifted; this says <i>which strip of it</i>. A junction
        /// emits four strips through one method — carriageway, kerb face, footway and grass — and
        /// "seven faces are backwards" sends you reading all four, twice, guessing. It cost three wrong
        /// guesses and three world rebuilds to learn that, which is more than an <c>int[]</c>.</para>
        /// </summary>
        public int[] FlipCountBySubmesh { get; }

        /// <summary>
        /// Folds several submeshes into one, writing what each of them was into its vertices' colours.
        ///
        /// <para><b>This is the twelve-draw-calls-to-three change, and it happens here rather than at the
        /// fifty-odd places a face is written.</b> A builder saying "this is a roof tile" and a renderer
        /// saying "this is one draw call" are different concerns, and the builders were right already:
        /// <c>BuildingMeshes</c> names twelve categories because twelve is what a façade has. What was
        /// wrong was that a category had to be a material, because URP/Lit cannot read a vertex colour.
        /// <c>Horizon/VertexTintLit</c> can, so a category becomes a colour and the categories merge.</para>
        ///
        /// <para>Merging at the mesh also means the builders keep working unaltered — several of them
        /// cache a submesh index in a <c>const</c> and reuse it, which a scheme that set a tint
        /// immediately before each emit would have had to unpick, for a mesh that comes out the
        /// same.</para>
        ///
        /// <para>Anything left untinted keeps its own submesh and its own material. That is not a
        /// leftover: it is how the two slots <c>TownLights</c> swaps after dusk survive, because a colour
        /// baked into a mesh is baked for good.</para>
        /// </summary>
        /// <param name="tints">
        /// One entry per submesh, null where that submesh must keep its material. Merged into the lowest
        /// submesh index that has a tint, so the surviving slot is stable across tiles.
        /// </param>
        public void MergeTinted(IReadOnlyList<Color?> tints)
        {
            int target = -1;
            for (int i = 0; i < tints.Count && i < submeshes.Length; i++)
            {
                if (tints[i].HasValue)
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
            {
                return;
            }

            if (colours == null)
            {
                colours = new List<Color32>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    colours.Add(new Color32(255, 255, 255, 255));
                }
            }

            for (int source = 0; source < tints.Count && source < submeshes.Length; source++)
            {
                if (!tints[source].HasValue)
                {
                    continue;
                }

                Color32 colour = tints[source].Value;
                List<int> indices = submeshes[source];

                for (int i = 0; i < indices.Count; i++)
                {
                    colours[indices[i]] = colour;
                }

                if (source == target)
                {
                    continue;
                }

                submeshes[target].AddRange(indices);
                indices.Clear();
            }
        }

        /// <summary>
        /// One flat-shaded triangle. The winding is taken as given — unlike the terrain, a plant genuinely
        /// has downward-facing faces and they must keep their own normals.
        ///
        /// Prefer <see cref="AddTriangleFacing"/> for anything solid. This one is <c>Raw</c> because reading
        /// it in a builder should prompt the question "and how do you know this winding is right?".
        /// </summary>
        public void AddTriangleRaw(int submesh, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 0.0000001f)
            {
                return;
            }

            normal.Normalize();

            int baseIndex = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            List<int> target = submeshes[submesh];
            target.Add(baseIndex);
            target.Add(baseIndex + 1);
            target.Add(baseIndex + 2);
        }

        /// <summary>
        /// One flat-shaded triangle whose winding is derived from where the face is meant to look, rather
        /// than trusted to the order the caller happened to write its corners in.
        ///
        /// <paramref name="outward"/> need be neither normalised nor accurate; only its sign against the
        /// face matters. Every solid face in this world has an obvious one — a box face has its own axis, a
        /// tower segment has its radial direction, a window reveal points into its recess — and passing it
        /// turns a silent, side-dependent bug class into a dot product evaluated once, at edit time.
        ///
        /// The pattern is not new: <c>TerrainTileBuilder</c> and <c>StreetJunctionBuilder</c> both already flip
        /// on <c>normal.y &lt; 0</c>, which is this method with <c>outward = Vector3.up</c> written in.
        /// </summary>
        public void AddTriangleFacing(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                FlipCount++;
                FlipCountBySubmesh[submesh]++;
                AddTriangleRaw(submesh, a, c, b);
                return;
            }

            AddTriangleRaw(submesh, a, b, c);
        }

        /// <summary>
        /// A quad as two facing triangles.
        ///
        /// Each half is tested separately on purpose. Eaves, lean-to roofs and anything seated on terrain
        /// come out non-planar, and one shared normal for both halves would be a guess dressed up as a fact.
        /// </summary>
        public void AddQuadFacing(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
        {
            AddTriangleFacing(submesh, a, b, c, outward);
            AddTriangleFacing(submesh, a, c, d, outward);
        }

        /// <summary>Both windings of the same triangle, for foliage that has to be visible from either side.</summary>
        public void AddDoubleSided(int submesh, Vector3 a, Vector3 b, Vector3 c)
        {
            AddTriangleRaw(submesh, a, b, c);
            AddTriangleRaw(submesh, a, c, b);
        }

        /// <summary>
        /// Bakes the buffers into a mesh, keeping only the submeshes that have anything in them and
        /// reporting which those were.
        ///
        /// The compaction matters: a tile above the tree line has boulders and nothing else, and an empty
        /// submesh is a draw call submitted for no triangles.
        /// </summary>
        public Mesh ToMesh(string meshName, List<int> usedSubmeshes)
        {
            usedSubmeshes.Clear();
            if (vertices.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < submeshes.Length; i++)
            {
                if (submeshes[i].Count > 0)
                {
                    usedSubmeshes.Add(i);
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);

            if (colours != null)
            {
                mesh.SetColors(colours);
            }

            mesh.subMeshCount = usedSubmeshes.Count;

            for (int slot = 0; slot < usedSubmeshes.Count; slot++)
            {
                mesh.SetTriangles(submeshes[usedSubmeshes[slot]], slot);
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// The plants themselves, built from a handful of n-gon cones and prisms.
    ///
    /// Deliberately crude. At the sizes these are actually seen — a spruce forty metres away through fog —
    /// what carries is the silhouette and the colour, not the geometry, and every triangle spent on a
    /// rounder canopy is a triangle spent several thousand times over. The variation that stops a forest
    /// looking stamped comes from per-instance height, width, tier count and vertex jitter rather than from
    /// more faces.
    ///
    /// <para>These helpers stay on <see cref="VegetationMeshBuffer.AddTriangleRaw"/> rather than moving to
    /// the facing variant, and that is a decision rather than an oversight: every ring here is jittered by
    /// up to 45 %, so "outward" is only ever the radial direction to within a wide margin, and a hint that
    /// is occasionally wrong is worse than no hint at all. Each helper below was checked by hand against the
    /// right-handed basis instead. Solid geometry — buildings, mills, landmarks — uses the hint, and the
    /// flip counter guards it.</para>
    /// </summary>
    public static class PlantMeshes
    {
        public const int BarkSubmesh = 0;
        public const int ConiferSubmesh = 1;
        public const int BroadleafSubmesh = 2;
        public const int UndergrowthSubmesh = 3;
        public const int RockSubmesh = 4;

        /// <summary>
        /// The Ebental's own colours. Five more slots, and they are free.
        ///
        /// <para><see cref="MergeTinted"/> folds every submesh that has a colour into the lowest one that
        /// does, so a species with an entry in <see cref="FoliageTints"/> costs no draw call, no material
        /// and no extra chunk — it costs one more empty list per tile, which <see cref="VegetationMeshBuffer.ToMesh"/>
        /// throws away again. That is the whole reason a region can have its own palette at all.</para>
        ///
        /// <para>The one thing that must not be done here is adding a slot with a <c>null</c> tint. Rock
        /// is the only one, deliberately, and it is the reason a tile with boulders on it costs two draw
        /// calls instead of one.</para>
        /// </summary>
        public const int PoplarSubmesh = 5;

        public const int OrchardSubmesh = 6;

        public const int AutumnCanopySubmesh = 7;

        public const int StrawSubmesh = 8;

        public const int StoneSubmesh = 9;

        /// <summary>
        /// A cypress: <see cref="AddPoplar"/>'s spindles in a colour that is not the Ebental's.
        ///
        /// <para><b>This slot exists because leaving it out was a bug nobody could see from the code.</b>
        /// <see cref="AddPoplar"/> wrote <see cref="PoplarSubmesh"/> unconditionally, and that slot is
        /// tinted the autumn gold of the country road's avenue. Anadolu's spires go through the same
        /// method — deliberately, and the reasoning is sound: a poplar and a cypress are the same
        /// silhouette at any distance either is seen from. What came with the silhouette was the paint.
        /// Half the trees on the far shore of the Meerenge were gold, in the one region whose entire
        /// stated purpose is to read as another country.</para>
        ///
        /// <para>The fix is a submesh and not a mesh, which is the whole argument this file makes about
        /// <see cref="FoliageTints"/>: a slot with a tint is folded away by <c>MergeTinted</c>, so a
        /// species that differs only in colour costs no draw call, no material and no triangles.</para>
        /// </summary>
        public const int CypressSubmesh = 10;

        public const int SubmeshCount = 11;

        /// <summary>
        /// The colour each plant submesh is tinted with when they are merged, or null to keep its own
        /// material.
        ///
        /// <para>The four foliage colours are the same trick the town's façades use, and the same win:
        /// bark, conifer, broadleaf and undergrowth are four flat colours that appear on nearly every
        /// tile in the world, so they were four draw calls on nearly every tile in the world. As vertex
        /// colours they are one. The numbers are the ones the materials had, moved rather than
        /// re-chosen.</para>
        ///
        /// <para>Rock keeps its own material. It is the one plant-buffer submesh with a genuinely
        /// different surface — dry, matte stone against wet foliage — and merging it would mean either
        /// the boulders take the leaves' smoothness or the leaves take the boulders'.</para>
        /// </summary>
        public static Color?[] FoliageTints()
        {
            var tints = new Color?[SubmeshCount];

            tints[BarkSubmesh] = new Color(0.29f, 0.21f, 0.16f);
            tints[ConiferSubmesh] = new Color(0.16f, 0.29f, 0.22f);
            tints[BroadleafSubmesh] = new Color(0.43f, 0.53f, 0.24f);
            tints[UndergrowthSubmesh] = new Color(0.32f, 0.44f, 0.22f);

            // The Ebental. Gold, rust and amber against a world that is otherwise entirely between 0.16
            // and 0.53 of green — the contrast is the point, and it is the cheapest kind there is.
            tints[PoplarSubmesh] = new Color(0.85f, 0.63f, 0.24f);
            tints[OrchardSubmesh] = new Color(0.71f, 0.38f, 0.18f);
            tints[AutumnCanopySubmesh] = new Color(0.80f, 0.60f, 0.25f);

            // Straw and limestone. Not foliage, but they ride the same merge for the same reason, and a
            // dry-stone wall on the boulders' untinted slot would have cost every tile with a wall on it
            // a second draw call.
            tints[StrawSubmesh] = new Color(0.83f, 0.75f, 0.50f);
            tints[StoneSubmesh] = new Color(0.62f, 0.59f, 0.52f);

            // The far shore. Darker and greyer than anything west of the water, which is what a cypress
            // is and, more to the point here, is the one thing on that hillside that cannot be mistaken
            // for the avenue five kilometres back up the road.
            tints[CypressSubmesh] = new Color(0.18f, 0.26f, 0.19f);

            return tints;
        }

        /// <summary>How far a trunk or a boulder is sunk below the ground point, metres of local space.</summary>
        private const float Burial = 0.5f;

        /// <summary>A spruce: three overlapping cone tiers on a bare stem. About 36 triangles.</summary>
        public static void AddConifer(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(6.5f, 11.5f);
            float trunkHeight = height * 0.30f;
            float trunkRadius = height * random.Range(0.028f, 0.042f);
            float canopyRadius = height * random.Range(0.17f, 0.23f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.7f,
                -Burial, trunkHeight, phase);

            const int tiers = 3;
            float tierBase = trunkHeight * 0.62f;
            float span = height - tierBase;

            for (int tier = 0; tier < tiers; tier++)
            {
                float baseY = tierBase + span * (tier * 0.30f);
                float apexY = tierBase + span * (0.42f + tier * 0.29f);
                float radius = canopyRadius * (1f - tier * 0.28f);

                // Only the lowest tier gets an underside; the others sit inside the skirt below them.
                AddCone(buffer, place, ConiferSubmesh, 6, radius, baseY, apexY,
                    phase + tier * 0.4f, tier == 0);
            }
        }

        /// <summary>A broadleaf: a stem with a jittered eight-sided blob on top. 44 triangles.</summary>
        public static void AddBroadleaf(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            AddBroadleaf(buffer, place, BroadleafSubmesh);
        }

        /// <summary>
        /// The same tree in another colour.
        ///
        /// <para>One mesh, two palettes. The Ebental's woods are the same species as the valley's below
        /// the pass — what makes them autumn is the leaf colour and nothing else, and re-authoring the
        /// geometry to say so would be forty more triangles to maintain for no visible gain.</para>
        /// </summary>
        public static void AddBroadleaf(VegetationMeshBuffer buffer, in PlantPlacement place, int canopySubmesh)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(5.5f, 9f);
            float trunkHeight = height * random.Range(0.34f, 0.44f);
            float trunkRadius = height * random.Range(0.035f, 0.05f);
            float canopyRadius = height * random.Range(0.28f, 0.38f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.8f,
                -Burial, trunkHeight + canopyRadius * 0.3f, phase);

            AddCanopy(buffer, place, canopySubmesh, 8, canopyRadius,
                trunkHeight * 0.75f, height, phase, 0.24f, ref random);
        }

        /// <summary>
        /// A Lombardy poplar: a bare stem under three stacked spindles, 18 to 22 m tall and under four
        /// across. About 48 triangles.
        ///
        /// <para><b>The proportion is the entire species.</b> Nothing else in this world is more than
        /// four times as tall as it is wide, so at any distance where a spruce has become a dark blob a
        /// poplar is still a vertical stroke — and a row of vertical strokes at even spacing is the one
        /// thing in a landscape that reads instantly as planted by somebody. That is what the avenue is
        /// for, and why this is thin rather than merely tall.</para>
        ///
        /// <para><b>Four spindles, and they overlap by more than half.</b> Three at a quarter's overlap
        /// was tried and came back as a stack of diamonds on a pole: each spindle showed its own apex and
        /// its own waist, so the tree read as three objects rather than one. A poplar has no waist at
        /// all — it is a single flame — and the only way to get that out of stacked bipyramids is to bury
        /// every internal apex inside the skirt of the tier above it. Eight sides rather than six for the
        /// same reason: at this slenderness a hexagon shows its flats as facets down the silhouette.</para>
        /// </summary>
        public static void AddPoplar(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            AddPoplar(buffer, place, PoplarSubmesh);
        }

        /// <summary>
        /// The same spire in another colour — a cypress rather than a poplar.
        ///
        /// <para>One mesh, two palettes, exactly as <see cref="AddBroadleaf(VegetationMeshBuffer, in PlantPlacement, int)"/>
        /// serves both the valley's woods and the Ebental's autumn. See <see cref="CypressSubmesh"/> for
        /// what went wrong while this overload did not exist.</para>
        /// </summary>
        public static void AddPoplar(VegetationMeshBuffer buffer, in PlantPlacement place, int spireSubmesh)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(18f, 22f);
            float radius = height * random.Range(0.075f, 0.092f);
            float trunkHeight = height * 0.15f;
            float trunkRadius = height * random.Range(0.016f, 0.021f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.75f,
                -Burial, trunkHeight * 1.7f, phase);

            const int tiers = 4;
            float from = trunkHeight * 0.9f;
            float span = height - from;

            for (int tier = 0; tier < tiers; tier++)
            {
                // Each tier covers half the tree and they step by a fifth of it, so any one apex sits
                // deep inside its neighbour.
                float bottomY = from + span * (tier * 0.19f);
                float topY = from + span * Mathf.Min(1f, 0.52f + tier * 0.19f);
                float ringY = Mathf.Lerp(bottomY, topY, 0.44f);

                // Barely tapering. The tip is the only place a poplar narrows, and it does it late.
                float tierRadius = radius * (tier < tiers - 1 ? 1f - tier * 0.07f : 0.55f);

                AddBlob(buffer, place, spireSubmesh, 8, tierRadius,
                    ringY, topY, bottomY, phase + tier * 0.55f, 0.12f, ref random);
            }
        }

        /// <summary>
        /// A fruit tree: a short stem under one wide flat crown, four to five metres. About 28 triangles.
        ///
        /// <para>Deliberately squat — wider than it is tall above the stem. An orchard is read from the
        /// road as a low even ceiling in rows, and a tree with a tall crown breaks the ceiling and turns
        /// the rows back into a wood.</para>
        /// </summary>
        public static void AddFruitTree(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(3.2f, 4.4f);
            float trunkHeight = height * random.Range(0.30f, 0.38f);
            float trunkRadius = height * random.Range(0.045f, 0.06f);
            float crownRadius = height * random.Range(0.38f, 0.48f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.85f,
                -Burial, trunkHeight + crownRadius * 0.25f, phase);

            AddBlob(buffer, place, OrchardSubmesh, 8, crownRadius,
                trunkHeight + (height - trunkHeight) * 0.38f, height, trunkHeight * 0.7f,
                phase, 0.22f, ref random);
        }

        /// <summary>A low bush: one squashed blob, no stem. About 12 triangles.</summary>
        public static void AddShrub(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            // Height first, radius derived from it. Sizing the other way round — a radius with the height as
            // a multiple of it — made every bush wider than it was tall, and a squat hexagonal bipyramid
            // seen from a car is a dark plate lying on the grass, not a shrub.
            float height = random.Range(0.9f, 2.1f);
            float radius = height * random.Range(0.42f, 0.62f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddBlob(buffer, place, UndergrowthSubmesh, 6, radius,
                height * 0.42f, height, -Burial * 0.4f, phase, 0.35f, ref random);
        }

        /// <summary>
        /// A tuft of grass: four blades radiating from a point, each a single triangle drawn from both
        /// sides. About 8 triangles.
        ///
        /// Double-sided rather than a plain card because every material in the project back-face culls, and
        /// a one-sided blade simply vanishes from half the directions you drive past it.
        /// </summary>
        public static void AddGrassTuft(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(0.35f, 0.7f);
            float spread = height * random.Range(0.35f, 0.6f);
            float halfWidth = height * 0.22f;
            float phase = random.Range(0f, Mathf.PI * 2f);

            const int blades = 4;
            for (int blade = 0; blade < blades; blade++)
            {
                float angle = phase + blade * (Mathf.PI * 2f / blades) + random.Range(-0.3f, 0.3f);
                float dirX = Mathf.Cos(angle);
                float dirZ = Mathf.Sin(angle);
                float bladeHeight = height * random.Range(0.7f, 1.2f);

                Vector3 a = place.ToWorld(-dirZ * halfWidth, -0.05f, dirX * halfWidth);
                Vector3 b = place.ToWorld(dirZ * halfWidth, -0.05f, -dirX * halfWidth);
                Vector3 tip = place.ToWorld(dirX * spread, bladeHeight, dirZ * spread);

                buffer.AddDoubleSided(UndergrowthSubmesh, a, b, tip);
            }
        }

        /// <summary>An erratic: a heavily jittered six-sided blob with its foot buried. About 12 triangles.</summary>
        public static void AddBoulder(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float radius = random.Range(0.8f, 2.6f);
            float height = radius * random.Range(0.7f, 1.3f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddBlob(buffer, place, RockSubmesh, 6, radius,
                height * 0.4f, height, -radius * 0.7f, phase, 0.45f, ref random);
        }

        /// <summary>A dead standing trunk with two broken limbs. About 18 triangles.</summary>
        public static void AddSnag(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(2.8f, 5f);
            float radius = height * random.Range(0.04f, 0.06f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, radius, radius * 0.35f, -Burial, height, phase);

            // Two stubs, as three-sided spikes off the trunk. Cheap, and they are what stops a snag
            // reading as a fence post.
            for (int stub = 0; stub < 2; stub++)
            {
                float angle = phase + stub * 2.4f + random.Range(-0.5f, 0.5f);
                float at = height * random.Range(0.45f, 0.8f);
                float reach = height * random.Range(0.16f, 0.28f);
                float rise = reach * random.Range(-0.2f, 0.5f);

                float dirX = Mathf.Cos(angle);
                float dirZ = Mathf.Sin(angle);
                Vector3 tip = place.ToWorld(dirX * reach, at + rise, dirZ * reach);

                for (int i = 0; i < 3; i++)
                {
                    float a0 = i * (Mathf.PI * 2f / 3f) + phase;
                    float a1 = (i + 1) * (Mathf.PI * 2f / 3f) + phase;
                    float stubRadius = radius * 0.6f;

                    Vector3 p0 = place.ToWorld(Mathf.Cos(a0) * stubRadius, at, Mathf.Sin(a0) * stubRadius);
                    Vector3 p1 = place.ToWorld(Mathf.Cos(a1) * stubRadius, at, Mathf.Sin(a1) * stubRadius);

                    // (p0, tip, p1), matching AddCone. The reverse order was wound inside out, which on a
                    // three-sided spike reads as a thinner spike rather than as a hole — which is why it
                    // survived this long.
                    buffer.AddTriangleRaw(BarkSubmesh, p0, tip, p1);
                }
            }
        }

        /// <summary>An n-gon tube. Two triangles per side, no caps — both ends are always hidden.</summary>
        private static void AddTube(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float bottomRadius,
            float topRadius,
            float bottomY,
            float topY,
            float phase)
        {
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i++)
            {
                float a0 = phase + i * step;
                float a1 = phase + (i + 1) * step;

                Vector3 b0 = place.ToWorld(Mathf.Cos(a0) * bottomRadius, bottomY, Mathf.Sin(a0) * bottomRadius);
                Vector3 b1 = place.ToWorld(Mathf.Cos(a1) * bottomRadius, bottomY, Mathf.Sin(a1) * bottomRadius);
                Vector3 t0 = place.ToWorld(Mathf.Cos(a0) * topRadius, topY, Mathf.Sin(a0) * topRadius);
                Vector3 t1 = place.ToWorld(Mathf.Cos(a1) * topRadius, topY, Mathf.Sin(a1) * topRadius);

                buffer.AddTriangleRaw(submesh, b0, t0, t1);
                buffer.AddTriangleRaw(submesh, b0, t1, b1);
            }
        }

        /// <summary>An n-gon cone standing on a ring, optionally closed underneath.</summary>
        private static void AddCone(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY,
            float phase,
            bool capped)
        {
            float step = Mathf.PI * 2f / sides;
            Vector3 apex = place.ToWorld(0f, apexY, 0f);
            Vector3 centre = place.ToWorld(0f, baseY, 0f);

            for (int i = 0; i < sides; i++)
            {
                float a0 = phase + i * step;
                float a1 = phase + (i + 1) * step;

                Vector3 p0 = place.ToWorld(Mathf.Cos(a0) * radius, baseY, Mathf.Sin(a0) * radius);
                Vector3 p1 = place.ToWorld(Mathf.Cos(a1) * radius, baseY, Mathf.Sin(a1) * radius);

                buffer.AddTriangleRaw(submesh, p0, apex, p1);

                if (capped)
                {
                    buffer.AddTriangleRaw(submesh, p0, p1, centre);
                }
            }
        }

        /// <summary>
        /// A broadleaf crown: two stacked rings between a bottom and a top apex.
        ///
        /// The second ring is the whole reason this is not just <see cref="AddBlob"/>. One ring gives a
        /// bipyramid, and a bipyramid silhouettes as a diamond from every angle — at any distance that reads
        /// as a kite stuck on a pole rather than as a tree. Sixteen extra triangles buy a rounded crown, and
        /// there are only ever a thousand or so of these.
        /// </summary>
        private static void AddCanopy(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float bottomY,
            float topY,
            float phase,
            float jitter,
            ref PlantRandom random)
        {
            float step = Mathf.PI * 2f / sides;
            float span = topY - bottomY;

            var lower = new Vector3[sides];
            var upper = new Vector3[sides];

            for (int i = 0; i < sides; i++)
            {
                float angle = phase + i * step;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                float lowerRadius = radius * 0.88f * (1f + random.Range(-jitter, jitter));
                float upperRadius = radius * (1f + random.Range(-jitter, jitter));

                lower[i] = place.ToWorld(
                    cos * lowerRadius,
                    bottomY + span * (0.30f + random.Range(-jitter, jitter) * 0.15f),
                    sin * lowerRadius);

                upper[i] = place.ToWorld(
                    cos * upperRadius,
                    bottomY + span * (0.66f + random.Range(-jitter, jitter) * 0.15f),
                    sin * upperRadius);
            }

            Vector3 top = place.ToWorld(
                radius * random.Range(-jitter, jitter) * 0.4f,
                topY,
                radius * random.Range(-jitter, jitter) * 0.4f);
            Vector3 bottom = place.ToWorld(0f, bottomY, 0f);

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;

                buffer.AddTriangleRaw(submesh, lower[i], lower[next], bottom);
                buffer.AddTriangleRaw(submesh, lower[i], upper[i], upper[next]);
                buffer.AddTriangleRaw(submesh, lower[i], upper[next], lower[next]);
                buffer.AddTriangleRaw(submesh, upper[i], top, upper[next]);
            }
        }

        /// <summary>
        /// A bipyramid: a ring with an apex above and below, with the ring vertices pushed about at random.
        /// Serves for bushes and boulders — they differ only in proportion and jitter.
        /// </summary>
        private static void AddBlob(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float ringY,
            float topY,
            float bottomY,
            float phase,
            float jitter,
            ref PlantRandom random)
        {
            float step = Mathf.PI * 2f / sides;

            var ring = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float angle = phase + i * step;
                float r = radius * (1f + random.Range(-jitter, jitter));
                float y = ringY + radius * random.Range(-jitter, jitter) * 0.6f;
                ring[i] = place.ToWorld(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r);
            }

            Vector3 top = place.ToWorld(
                radius * random.Range(-jitter, jitter) * 0.5f,
                topY,
                radius * random.Range(-jitter, jitter) * 0.5f);
            Vector3 bottom = place.ToWorld(0f, bottomY, 0f);

            for (int i = 0; i < sides; i++)
            {
                Vector3 p0 = ring[i];
                Vector3 p1 = ring[(i + 1) % sides];

                buffer.AddTriangleRaw(submesh, p0, top, p1);
                buffer.AddTriangleRaw(submesh, p0, p1, bottom);
            }
        }
    }
}
