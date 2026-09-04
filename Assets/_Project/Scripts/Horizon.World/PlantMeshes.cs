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

        private int swayingVertices;

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

            EnsureColours();

            for (int source = 0; source < tints.Count && source < submeshes.Length; source++)
            {
                if (!tints[source].HasValue)
                {
                    continue;
                }

                Color32 colour = tints[source].Value;
                List<int> indices = submeshes[source];

                // Red, green and blue only. Alpha carries how much this vertex sways — see ApplySway —
                // and a tint is about colour, so writing the whole Color32 here would silently flatten
                // every tree in the world back to rigid the moment it was given its green.
                for (int i = 0; i < indices.Count; i++)
                {
                    Color32 existing = colours[indices[i]];
                    colours[indices[i]] = new Color32(colour.r, colour.g, colour.b, existing.a);
                }

                if (source == target)
                {
                    continue;
                }

                submeshes[target].AddRange(indices);
                indices.Clear();
            }
        }

        /// <summary>Vertices written so far. Take one before a plant and hand it to <see cref="ApplySway"/>.</summary>
        public int VertexCount => vertices.Count;

        /// <summary>
        /// Marks the vertices a plant just added with how much of the wind they should feel.
        ///
        /// <para><b>Written into the vertex colour's alpha, which was free.</b> Every plant in this world
        /// goes through one material on one shader, and that shader has only ever read
        /// <c>colour.rgb</c> — so a sway mask costs no extra vertex attribute, no second material and
        /// no draw call, and it reaches every tree in the world at once.</para>
        ///
        /// <para><b>The channel is inverted on purpose: the shader reads 1 - alpha.</b> Everything in
        /// this project writes 255 today, which under that reading means rigid — so terrain, buildings,
        /// roads and anything anybody forgets to mark stay perfectly still. The other way round, one
        /// missed call would set a hillside swaying. That is the rule <c>GroundSurface</c> already states
        /// about untagged geometry: being wrong has to be invisible rather than catastrophic.</para>
        ///
        /// <para>The ramp is squared. A trunk is stiff at the bottom and limber at the top, and a linear
        /// ramp moves the lower branches far more than a tree does.</para>
        /// </summary>
        /// <param name="fromVertex">The <see cref="VertexCount"/> taken before the plant was added.</param>
        /// <param name="baseHeight">World Y the plant stands on. Vertices at this height never move.</param>
        /// <param name="fullHeight">Height above the base at which the plant sways its hardest.</param>
        /// <param name="amount">0 rigid, 1 fully flexible. Scales the whole ramp.</param>
        public void ApplySway(int fromVertex, float baseHeight, float fullHeight, float amount)
        {
            if (amount <= 0f || fullHeight <= 0.0001f || fromVertex >= vertices.Count)
            {
                return;
            }

            EnsureColours();

            for (int i = fromVertex; i < vertices.Count; i++)
            {
                float up = Mathf.Clamp01((vertices[i].y - baseHeight) / fullHeight);
                float sway = Mathf.Clamp01(up * up * amount);

                Color32 existing = colours[i];
                colours[i] = new Color32(
                    existing.r, existing.g, existing.b, (byte)Mathf.RoundToInt((1f - sway) * 255f));

                if (sway > 0.02f)
                {
                    swayingVertices++;
                }
            }
        }

        /// <summary>
        /// How many vertices came out of here able to move.
        ///
        /// <para>Counted so the build can say so, for the reason the snow line already gives: a world
        /// whose wind mask never got written builds, validates and photographs exactly like one that
        /// works, and the only symptom is a forest that is subtly, unaccountably dead. A still frame
        /// cannot tell the two apart at all, which makes this the only evidence there is.</para>
        /// </summary>
        public int SwayingVertices => swayingVertices;

        /// <summary>
        /// Marks a plant with a sway ramp taken from its own extent.
        ///
        /// <para>The overload every caller actually wants. A conifer, a cherry and a grass tuft differ
        /// in height by two orders of magnitude and none of them should have that number written at the
        /// call site — the vertices just added already say where the plant starts and stops, so the ramp
        /// reads it off them. A plant with no height at all (a single flat quad) is left rigid rather
        /// than divided by zero.</para>
        /// </summary>
        public void ApplySway(int fromVertex, float amount)
        {
            if (amount <= 0f || fromVertex >= vertices.Count)
            {
                return;
            }

            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (int i = fromVertex; i < vertices.Count; i++)
            {
                float y = vertices[i].y;
                lowest = Mathf.Min(lowest, y);
                highest = Mathf.Max(highest, y);
            }

            ApplySway(fromVertex, lowest, highest - lowest, amount);
        }

        /// <summary>Backfills the colour list with rigid white, so alpha and tint can be written apart.</summary>
        private void EnsureColours()
        {
            if (colours != null)
            {
                while (colours.Count < vertices.Count)
                {
                    colours.Add(new Color32(255, 255, 255, 255));
                }

                return;
            }

            colours = new List<Color32>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++)
            {
                colours.Add(new Color32(255, 255, 255, 255));
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
        /// <summary>
        /// How much of the wind a tree feels at its crown, 0 rigid and 1 fully flexible.
        ///
        /// <para>Spent on the vertex colour's alpha by <see cref="VegetationMeshBuffer.ApplySway"/>,
        /// squared up the plant so the base stays stiff. Full, because a tree is what the wind is for.
        /// These live here rather than on the scatterer because how far a species bends is a property of
        /// the species.</para>
        /// </summary>
        public const float TreeSway = 1f;

        /// <summary>
        /// Undergrowth moves less than a tree, and not for the reason it first looks.
        ///
        /// <para>A real bush in a real wind moves more than a spruce, not less. But a bush here is two
        /// metres of four facets seen from a passing car, so the same absolute push that reads as a
        /// canopy breathing reads on a shrub as the whole plant sliding along the ground. Set against
        /// what the silhouette can absorb rather than against botany.</para>
        /// </summary>
        public const float ShrubSway = 0.55f;

        /// <summary>
        /// Grass, which genuinely should move most and gets less than a tree anyway.
        ///
        /// <para>The shrub's reason twice over: a tuft is a few centimetres of geometry at the edge of
        /// what the tile budget carries, and at the distance it is seen from, motion past this is a
        /// shimmer rather than a wind.</para>
        /// </summary>
        public const float GrassSway = 0.7f;

        /// <summary>
        /// A snag is a dead tree with no canopy, so it barely moves — and the little it does is worth
        /// having, because a bare trunk standing perfectly still beside a wood that is moving is the one
        /// thing here that would read as broken rather than as dead.
        /// </summary>
        public const float SnagSway = 0.15f;

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

        /// <summary>
        /// The two other greens a conifer can be.
        ///
        /// <para><b>A wood in one colour is a texture, not a wood.</b> Every spruce in the world shared
        /// <see cref="ConiferSubmesh"/>, so a hillside of four hundred of them was four hundred copies of
        /// exactly the same green — the trees varied in height by a factor of nearly two and it made no
        /// difference, because what the eye sorts a forest by at distance is tone before shape. Three
        /// tones and the same stand reads as depth.</para>
        ///
        /// <para>Free, and that is why it is three and not one: a slot with an entry in
        /// <see cref="FoliageTints"/> is merged into the same draw call as the rest. The cost of this is
        /// two lines in a table.</para>
        /// </summary>
        public const int ConiferDarkSubmesh = 11;

        /// <summary>See <see cref="ConiferDarkSubmesh"/>.</summary>
        public const int ConiferPaleSubmesh = 12;

        /// <summary>
        /// The two other greens a broadleaf can be, for the reason <see cref="ConiferDarkSubmesh"/>
        /// gives and with the same arithmetic behind it: forty-five thousand of them shared one colour.
        ///
        /// <para><b>Only the wild ones.</b> The autumn canopy and the orchard keep their single palettes
        /// on purpose — those two <i>are</i> a signature, and the Ebental's gold reads as one country
        /// precisely because it does not vary.</para>
        /// </summary>
        public const int BroadleafDeepSubmesh = 13;

        /// <summary>See <see cref="BroadleafDeepSubmesh"/>.</summary>
        public const int BroadleafLightSubmesh = 14;

        /// <summary>
        /// A second undergrowth green. The floor of a wood is the largest single count in the world —
        /// two hundred and twenty thousand bushes — and it was one flat colour under all of it.
        /// </summary>
        public const int UndergrowthDeepSubmesh = 15;

        /// <summary>
        /// Cherry blossom, and the two tones a tree in the Bahçe can be.
        ///
        /// <para><b>The only pale, cool colours in the world.</b> Everything else growing anywhere here
        /// is between 0.09 and 0.62 of green, plus three warm autumn tones — so a canopy at
        /// 0.93/0.74/0.80 does not have to compete for attention, it simply is not the same kind of
        /// thing. That is the whole reason the Bahçe reads as a place rather than as another hillside,
        /// and by the arithmetic of <see cref="FoliageTints"/> it costs a row in a table.</para>
        ///
        /// <para>Two tones and not one, unlike the cypress and the autumn canopy. Those two are a
        /// signature seen from a moving car at forty metres; a blossom grove is walked past at the
        /// paddock and stood in at a viewpoint, and at that range one flat pink is a wall.</para>
        /// </summary>
        public const int BlossomSubmesh = 16;

        /// <summary>See <see cref="BlossomSubmesh"/>. The near-white one.</summary>
        public const int BlossomPaleSubmesh = 17;

        /// <summary>
        /// Flower heads, in two colours.
        ///
        /// <para><b>Two, for the reason there are three conifer greens.</b> A meadow of one dot colour
        /// repeated is a texture rather than a meadow, and at the size a flower head is read — a few
        /// pixels, at the roadside, at speed — colour is the only thing about it there is to read.</para>
        ///
        /// <para>Both are tinted, so both fold into the one merged draw call and neither costs a
        /// material. That is <see cref="FoliageTints"/>'s whole mechanism, and the rule that goes with
        /// it is that a tint means "fold me in" and a null means "keep me, I have my own material" — a
        /// new slot has to mean one of the two on purpose.</para>
        /// </summary>
        public const int FlowerSubmesh = 18;

        /// <summary>See <see cref="FlowerSubmesh"/>.</summary>
        public const int FlowerPaleSubmesh = 19;

        public const int SubmeshCount = 20;

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

            // Either side of it, and further apart than looks sensible written down. On a flat-shaded
            // low-poly tree under one directional light the whole canopy is a handful of facets, so a
            // subtle difference between two of them is no difference at all by the time it is forty
            // metres away — which is where nearly every tree in this world is seen from.
            tints[ConiferDarkSubmesh] = new Color(0.09f, 0.20f, 0.16f);
            tints[ConiferPaleSubmesh] = new Color(0.27f, 0.41f, 0.25f);

            // The same either side of the broadleaf and the undergrowth.
            tints[BroadleafDeepSubmesh] = new Color(0.31f, 0.44f, 0.20f);
            tints[BroadleafLightSubmesh] = new Color(0.56f, 0.62f, 0.29f);
            tints[UndergrowthDeepSubmesh] = new Color(0.22f, 0.34f, 0.18f);
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

            // Cherry blossom. Pink and near-white, and both of them lighter than they look written down
            // for the reason the three greens are further apart than they look: a flat-shaded canopy
            // under one directional light is a handful of facets, and a subtle difference between two
            // of these is no difference at all by forty metres.
            tints[BlossomSubmesh] = new Color(0.93f, 0.70f, 0.78f);
            tints[BlossomPaleSubmesh] = new Color(0.97f, 0.90f, 0.91f);

            // Warm and cool, rather than two shades of one hue. The two blossom tints above are the only
            // pale cool colours in this world and they belong to one region; these have to work in an
            // alpine meadow and in an orchard valley both, so they are as far apart as two flowers
            // sensibly get.
            tints[FlowerSubmesh] = new Color(0.92f, 0.78f, 0.30f);
            tints[FlowerPaleSubmesh] = new Color(0.86f, 0.86f, 0.90f);

            return tints;
        }

        /// <summary>How far a trunk or a boulder is sunk below the ground point, metres of local space.</summary>
        private const float Burial = 0.5f;

        /// <summary>A spruce: three overlapping cone tiers on a bare stem. About 36 triangles.</summary>
        public static void AddConifer(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            // Which of the three greens, and which of the two shapes. Both are drawn before anything
            // else so that a tree's colour and its build are decided by its own seed and by nothing
            // about its neighbours — a hillside sorted by position would band, which is the failure
            // this is fixing rather than a new one.
            float tone = random.Next();
            int needles = tone < 0.34f ? ConiferDarkSubmesh
                : tone < 0.64f ? ConiferPaleSubmesh
                : ConiferSubmesh;

            // <b>Two silhouettes, and the narrow one is the point.</b> Every conifer here was the same
            // three-tier cone at 0.17–0.23 of its height, which at any distance is one shape repeated —
            // and shape is what survives when the tone has gone grey in the fog. A spruce is tall,
            // narrow and five-tiered against the fir's broad three, and the two read as a mixed wood
            // where one read as wallpaper.
            bool spruce = random.Next() < 0.45f;

            float height = spruce ? random.Range(9f, 15f) : random.Range(6f, 11f);
            float trunkHeight = height * (spruce ? 0.22f : 0.30f);
            float trunkRadius = height * random.Range(0.026f, 0.040f);
            float canopyRadius = height * (spruce ? random.Range(0.11f, 0.15f)
                                                  : random.Range(0.18f, 0.25f));
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.7f,
                -Burial, trunkHeight, phase);

            int tiers = spruce ? 5 : 3;
            float tierBase = trunkHeight * 0.62f;
            float span = height - tierBase;
            float step = 0.90f / tiers;

            for (int tier = 0; tier < tiers; tier++)
            {
                float baseY = tierBase + span * (tier * step);
                float apexY = tierBase + span * (step * 1.4f + tier * step);
                float radius = canopyRadius * (1f - tier * (0.84f / tiers));

                // Only the lowest tier gets an underside; the others sit inside the skirt below them.
                AddCone(buffer, place, needles, 6, radius, baseY, Mathf.Min(apexY, height),
                    phase + tier * 0.4f, tier == 0);
            }
        }

        /// <summary>A broadleaf: a stem with a jittered eight-sided blob on top. 44 triangles.</summary>
        public static void AddBroadleaf(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            // One of three greens, by the tree's own seed. The overload below keeps taking an explicit
            // slot, because the autumn canopy and the orchard are signatures rather than scatter.
            var random = new PlantRandom(place.Seed);
            float tone = random.Next();

            AddBroadleaf(buffer, place,
                tone < 0.33f ? BroadleafDeepSubmesh
                : tone < 0.63f ? BroadleafLightSubmesh
                : BroadleafSubmesh);
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
            AddFruitTree(buffer, place, OrchardSubmesh);
        }

        /// <summary>
        /// The same tree in another crown colour, so a region can plant its own orchards.
        ///
        /// <para>The overload above delegates straight here rather than drawing a tone first, unlike
        /// <see cref="AddBroadleaf"/>: an orchard is a signature and every tree in one is the same
        /// colour, so there is nothing to draw. That also keeps the Ebental's rows byte-identical.</para>
        /// </summary>
        public static void AddFruitTree(
            VegetationMeshBuffer buffer, in PlantPlacement place, int crownSubmesh)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(3.2f, 4.4f);
            float trunkHeight = height * random.Range(0.30f, 0.38f);
            float trunkRadius = height * random.Range(0.045f, 0.06f);
            float crownRadius = height * random.Range(0.38f, 0.48f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.85f,
                -Burial, trunkHeight + crownRadius * 0.25f, phase);

            AddBlob(buffer, place, crownSubmesh, 8, crownRadius,
                trunkHeight + (height - trunkHeight) * 0.38f, height, trunkHeight * 0.7f,
                phase, 0.22f, ref random);
        }

        /// <summary>
        /// A cherry in flower: a short stem under two billows of blossom, five to seven metres. About 44
        /// triangles.
        ///
        /// <para><b>Its own silhouette, not a broadleaf in pink.</b> Tone is what sorts a wood at
        /// distance and shape is what survives when the fog has taken the tone — which is the argument
        /// the spruce and the fir already make against each other. A broadleaf is a stem under one round
        /// crown; this is squat, twice as wide as the stem is tall, and lumpy at the top, so a grove of
        /// them reads as an orchard gone wild rather than as the same wood repainted.</para>
        ///
        /// <para>Two blobs rather than one, and that is the whole of the extra cost. A single wide blob
        /// came out as a mushroom — the jitter is a fraction of the radius, so the wider the crown the
        /// smoother its outline, and a cherry is the opposite of smooth.</para>
        /// </summary>
        public static void AddCherry(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            // The tone first, off the tree's own seed, so a grove is mixed rather than banded by
            // position — the reason AddConifer draws its needles before anything else.
            var random = new PlantRandom(place.Seed);

            AddCherry(buffer, place, random.Next() < 0.62f ? BlossomSubmesh : BlossomPaleSubmesh);
        }

        /// <summary>The same tree in a named blossom colour. See <see cref="AddBroadleaf"/> for why the
        /// slot version re-seeds: the geometry must not depend on which tone was drawn.</summary>
        public static void AddCherry(
            VegetationMeshBuffer buffer, in PlantPlacement place, int blossomSubmesh)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(4.6f, 7f);
            float trunkHeight = height * random.Range(0.24f, 0.32f);
            float trunkRadius = height * random.Range(0.05f, 0.07f);
            float crownRadius = height * random.Range(0.46f, 0.58f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.7f,
                -Burial, trunkHeight + crownRadius * 0.3f, phase);

            AddBlob(buffer, place, blossomSubmesh, 8, crownRadius,
                trunkHeight + (height - trunkHeight) * 0.30f, height * 0.86f, trunkHeight * 0.55f,
                phase, 0.30f, ref random);

            AddBlob(buffer, place, blossomSubmesh, 6, crownRadius * 0.62f,
                height * 0.80f, height, height * 0.62f, phase + 1.9f, 0.26f, ref random);
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

            // Two greens rather than one. A bush is a six-sided blob two metres across, so the only
            // thing distinguishing one from the next at driving speed is its tone, and there are more of
            // these in the world than everything else put together.
            int leaves = random.Next() < 0.42f ? UndergrowthDeepSubmesh : UndergrowthSubmesh;

            AddBlob(buffer, place, leaves, 6, radius,
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

        /// <summary>
        /// A wildflower: three blades and a head. About 10 triangles.
        ///
        /// <para><b>It exists in the grass band and nowhere else, and that is what makes it
        /// affordable.</b> A thirty-centimetre plant is invisible past about forty metres, so the only
        /// place one is worth a triangle is the strip beside the road — which is exactly the band
        /// <c>ScatterTufts</c> already works in, capped by <c>VegetationShape.TuftMaxDistance</c>. It
        /// costs nothing to reach and nothing to draw: the head's two submeshes are tinted, so they
        /// merge into the same call as everything else on the tile.</para>
        ///
        /// <para>Three blades against the tuft's four, and shorter ones, so that a flower reads as a
        /// stem with something on top rather than as a tuft somebody has painted. The head is a flat
        /// four-sided fan carried at the top of the stem and tilted off vertical — laid horizontal it
        /// disappears entirely from a driver's eye, which is roughly its own height above it, and the
        /// whole point of the thing is to be seen from the road.</para>
        /// </summary>
        public static void AddWildflower(VegetationMeshBuffer buffer, in PlantPlacement place, int head)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(0.30f, 0.55f);
            float halfWidth = height * 0.10f;
            float phase = random.Range(0f, Mathf.PI * 2f);

            const int blades = 3;
            for (int blade = 0; blade < blades; blade++)
            {
                float angle = phase + blade * (Mathf.PI * 2f / blades) + random.Range(-0.4f, 0.4f);
                float dirX = Mathf.Cos(angle);
                float dirZ = Mathf.Sin(angle);
                float bladeHeight = height * random.Range(0.5f, 0.85f);
                float spread = height * random.Range(0.2f, 0.4f);

                Vector3 a = place.ToWorld(-dirZ * halfWidth, -0.05f, dirX * halfWidth);
                Vector3 b = place.ToWorld(dirZ * halfWidth, -0.05f, -dirX * halfWidth);
                Vector3 tip = place.ToWorld(dirX * spread, bladeHeight, dirZ * spread);

                buffer.AddDoubleSided(UndergrowthSubmesh, a, b, tip);
            }

            // The head. A fan about a stem tip, leaning over — see the note above about the angle it is
            // looked at from.
            float lean = random.Range(0.35f, 0.6f);
            float leanAngle = random.Range(0f, Mathf.PI * 2f);
            float radius = height * random.Range(0.16f, 0.24f);

            Vector3 stem = place.ToWorld(
                Mathf.Cos(leanAngle) * height * lean * 0.35f,
                height,
                Mathf.Sin(leanAngle) * height * lean * 0.35f);

            Vector3 up = place.ToWorld(0f, height + radius * lean, 0f) - place.ToWorld(0f, height, 0f);
            Vector3 across = place.ToWorld(radius, height, 0f) - place.ToWorld(0f, height, 0f);
            Vector3 along = place.ToWorld(0f, height, radius) - place.ToWorld(0f, height, 0f);

            const int petals = 4;
            for (int petal = 0; petal < petals; petal++)
            {
                float a0 = petal * (Mathf.PI * 2f / petals);
                float a1 = (petal + 1) * (Mathf.PI * 2f / petals);

                Vector3 p0 = stem + across * Mathf.Cos(a0) + along * Mathf.Sin(a0);
                Vector3 p1 = stem + across * Mathf.Cos(a1) + along * Mathf.Sin(a1);

                buffer.AddDoubleSided(head, stem + up, p0, p1);
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
