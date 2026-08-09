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
            Vector3 right = Vector3.Cross(reference, Up);
            if (right.sqrMagnitude < 0.000001f)
            {
                right = Vector3.right;
            }

            right.Normalize();
            Vector3 forward = Vector3.Cross(Up, right);

            float sin = Mathf.Sin(yawRadians);
            float cos = Mathf.Cos(yawRadians);
            Right = right * cos + forward * sin;
            Forward = forward * cos - right * sin;

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

        public VegetationMeshBuffer(int submeshCount)
        {
            submeshes = new List<int>[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                submeshes[i] = new List<int>(2048);
            }
        }

        public int TriangleCount => vertices.Count / 3;

        public bool IsEmpty => vertices.Count == 0;

        /// <summary>
        /// One flat-shaded triangle. The winding is taken as given — unlike the terrain, a plant genuinely
        /// has downward-facing faces and they must keep their own normals.
        /// </summary>
        public void AddTriangle(int submesh, Vector3 a, Vector3 b, Vector3 c)
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

        /// <summary>Both windings of the same triangle, for foliage that has to be visible from either side.</summary>
        public void AddDoubleSided(int submesh, Vector3 a, Vector3 b, Vector3 c)
        {
            AddTriangle(submesh, a, b, c);
            AddTriangle(submesh, a, c, b);
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
    /// </summary>
    public static class PlantMeshes
    {
        public const int BarkSubmesh = 0;
        public const int ConiferSubmesh = 1;
        public const int BroadleafSubmesh = 2;
        public const int UndergrowthSubmesh = 3;
        public const int RockSubmesh = 4;

        public const int SubmeshCount = 5;

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

        /// <summary>A broadleaf: a stem with a jittered eight-sided blob on top. About 28 triangles.</summary>
        public static void AddBroadleaf(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float height = random.Range(5.5f, 9f);
            float trunkHeight = height * random.Range(0.34f, 0.44f);
            float trunkRadius = height * random.Range(0.035f, 0.05f);
            float canopyRadius = height * random.Range(0.28f, 0.38f);
            float phase = random.Range(0f, Mathf.PI * 2f);

            AddTube(buffer, place, BarkSubmesh, 6, trunkRadius, trunkRadius * 0.8f,
                -Burial, trunkHeight + canopyRadius * 0.3f, phase);

            AddCanopy(buffer, place, BroadleafSubmesh, 8, canopyRadius,
                trunkHeight * 0.75f, height, phase, 0.24f, ref random);
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

                    buffer.AddTriangle(BarkSubmesh, p0, p1, tip);
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

                buffer.AddTriangle(submesh, b0, t0, t1);
                buffer.AddTriangle(submesh, b0, t1, b1);
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

                buffer.AddTriangle(submesh, p0, apex, p1);

                if (capped)
                {
                    buffer.AddTriangle(submesh, p0, p1, centre);
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

                buffer.AddTriangle(submesh, lower[i], lower[next], bottom);
                buffer.AddTriangle(submesh, lower[i], upper[i], upper[next]);
                buffer.AddTriangle(submesh, lower[i], upper[next], lower[next]);
                buffer.AddTriangle(submesh, upper[i], top, upper[next]);
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

                buffer.AddTriangle(submesh, p0, top, p1);
                buffer.AddTriangle(submesh, p0, p1, bottom);
            }
        }
    }
}
