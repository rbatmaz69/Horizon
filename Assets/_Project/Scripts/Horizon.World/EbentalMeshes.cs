using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What people leave behind in the Ebental: field walls, fences, bales and a cross at the crest.
    ///
    /// <para>Same conventions as <see cref="MillMeshes"/> — authored into a shared
    /// <see cref="VegetationMeshBuffer"/>, submesh constants taken from elsewhere so a tile stays one
    /// mesh. Here they come from <see cref="PlantMeshes"/> rather than from <c>BuildingMeshes</c>,
    /// because these things stand in open country and the plant buffer is the one that already merges to
    /// a single draw call per tile. A prop in the town buffer would want a town.</para>
    ///
    /// <para><b>The walls are what make the fields fields.</b> Colour alone gives a patchwork that
    /// dissolves the moment the ground tilts away; a line of stone along the boundary is an edge that
    /// survives any angle, and at driving speed edges are what the eye actually tracks. That is why this
    /// file exists at all, and why the wall is first in it.</para>
    ///
    /// <para>Unlike the plants, everything here is solid geometry with trustworthy outward directions,
    /// so it uses <see cref="VegetationMeshBuffer.AddQuadFacing"/> and lets the flip counter guard the
    /// winding — the same choice the buildings make, and for the same reason. Every quad below was
    /// written the wrong way round first and reported as 27,044 corrected faces, which is precisely what
    /// that counter is for: the geometry looked perfect either way, because the buffer had already
    /// turned it round.</para>
    /// </summary>
    public static class EbentalMeshes
    {
        /// <summary>Height of a field wall, metres, before jitter.</summary>
        private const float WallHeight = 0.95f;

        /// <summary>Thickness of a field wall, metres. Dry stone is laid thick or it falls over.</summary>
        private const float WallThickness = 0.55f;

        /// <summary>How far a wall or a post foot is sunk, so a coarse ground sample cannot float it.</summary>
        private const float Footing = 0.35f;

        /// <summary>
        /// A dry-stone wall along a sampled ground line. About 6 triangles per segment.
        ///
        /// <para>Three quads per segment — two faces and a cap — and no ends. A wall that closed its ends
        /// would pay two more triangles per run to describe a face nobody ever stands square to, and the
        /// runs are laid end to end along a field boundary anyway.</para>
        ///
        /// <para>The height wobbles per segment and the line is not straightened. A dead-level top on a
        /// dry-stone wall is a garden wall; the whole character of a field wall is that it follows the
        /// ground and is a hand's breadth different everywhere.</para>
        /// </summary>
        public static void AddDryStoneWall(VegetationMeshBuffer buffer, Vector3[] ground, uint seed)
        {
            if (ground == null || ground.Length < 2)
            {
                return;
            }

            var random = new PlantRandom(seed);

            for (int i = 1; i < ground.Length; i++)
            {
                Vector3 a = ground[i - 1];
                Vector3 b = ground[i];

                Vector3 run = b - a;
                run.y = 0f;

                if (run.sqrMagnitude < 0.01f)
                {
                    continue;
                }

                Vector3 side = Vector3.Cross(Vector3.up, run.normalized) * (WallThickness * 0.5f);

                float heightA = WallHeight * random.Range(0.82f, 1.15f);
                float heightB = WallHeight * random.Range(0.82f, 1.15f);

                Vector3 footA = a - Vector3.up * Footing;
                Vector3 footB = b - Vector3.up * Footing;
                Vector3 topA = a + Vector3.up * heightA;
                Vector3 topB = b + Vector3.up * heightB;

                buffer.AddQuadFacing(PlantMeshes.StoneSubmesh,
                    topA + side, topB + side, footB + side, footA + side, side);

                buffer.AddQuadFacing(PlantMeshes.StoneSubmesh,
                    topB - side, topA - side, footA - side, footB - side, -side);

                buffer.AddQuadFacing(PlantMeshes.StoneSubmesh,
                    topB - side, topB + side, topA + side, topA - side, Vector3.up);
            }
        }

        /// <summary>
        /// A post-and-rail fence along a sampled ground line. About 10 triangles per segment.
        ///
        /// <para>The rails are double-sided single quads rather than boxes. A rail is 8 cm thick and
        /// nobody has ever seen its top face; giving it one costs four times the triangles to describe a
        /// surface that is one pixel wide from every angle the game is played from.</para>
        /// </summary>
        public static void AddPostAndRail(VegetationMeshBuffer buffer, Vector3[] ground, uint seed)
        {
            if (ground == null || ground.Length < 2)
            {
                return;
            }

            var random = new PlantRandom(seed);

            const float postHeight = 1.15f;
            const float postHalf = 0.06f;

            for (int i = 1; i < ground.Length; i++)
            {
                Vector3 a = ground[i - 1];
                Vector3 b = ground[i];

                Vector3 run = b - a;
                run.y = 0f;

                if (run.sqrMagnitude < 0.01f)
                {
                    continue;
                }

                Vector3 along = run.normalized;
                Vector3 side = Vector3.Cross(Vector3.up, along);

                float heightA = postHeight * random.Range(0.9f, 1.1f);
                AddPost(buffer, a, along, side, postHalf, heightA);

                // Only the far post of the last segment, so shared posts are not built twice.
                if (i == ground.Length - 1)
                {
                    AddPost(buffer, b, along, side, postHalf, postHeight * random.Range(0.9f, 1.1f));
                }

                for (int rail = 0; rail < 2; rail++)
                {
                    float y = postHeight * (rail == 0 ? 0.42f : 0.82f);
                    const float half = 0.055f;

                    buffer.AddDoubleSided(PlantMeshes.BarkSubmesh,
                        a + Vector3.up * (y - half), b + Vector3.up * (y - half), b + Vector3.up * (y + half));

                    buffer.AddDoubleSided(PlantMeshes.BarkSubmesh,
                        a + Vector3.up * (y - half), b + Vector3.up * (y + half), a + Vector3.up * (y + half));
                }
            }
        }

        /// <summary>A round bale on its side. 20 triangles.</summary>
        public static void AddHayBale(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float radius = random.Range(0.65f, 0.85f);
            float half = radius * random.Range(0.85f, 1.15f);

            // Lying down, so the axis is the placement's forward and the round face is what you see.
            const int sides = 6;
            var near = new Vector3[sides];
            var far = new Vector3[sides];

            for (int i = 0; i < sides; i++)
            {
                float angle = i * (Mathf.PI * 2f / sides) + 0.4f;
                float y = radius + Mathf.Sin(angle) * radius;
                float x = Mathf.Cos(angle) * radius;

                near[i] = place.ToWorld(x, y - Footing * 0.3f, -half);
                far[i] = place.ToWorld(x, y - Footing * 0.3f, half);
            }

            Vector3 nearCentre = place.ToWorld(0f, radius - Footing * 0.3f, -half);
            Vector3 farCentre = place.ToWorld(0f, radius - Footing * 0.3f, half);

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;

                Vector3 outward = (near[i] + near[next] + far[i] + far[next]) * 0.25f
                                  - (nearCentre + farCentre) * 0.5f;

                buffer.AddQuadFacing(PlantMeshes.StrawSubmesh,
                    near[next], far[next], far[i], near[i], outward);

                buffer.AddTriangleFacing(PlantMeshes.StrawSubmesh,
                    nearCentre, near[next], near[i], nearCentre - farCentre);

                buffer.AddTriangleFacing(PlantMeshes.StrawSubmesh,
                    farCentre, far[i], far[next], farCentre - nearCentre);
            }
        }

        /// <summary>
        /// A wayside cross: a stone plinth and a timber cross on it. About 40 triangles.
        ///
        /// <para>Small, and put where a road does something. It is not scenery for its own sake — it is
        /// the one man-made vertical between the pass and the far end of the valley, and a landscape
        /// with a single landmark in it is a landscape people can say where they are in.</para>
        /// </summary>
        public static void AddWaysideCross(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            AddBlock(buffer, place, PlantMeshes.StoneSubmesh, 0f, -Footing, 0f, 0.55f, 0.7f, 0.55f);
            AddBlock(buffer, place, PlantMeshes.BarkSubmesh, 0f, 0.7f, 0f, 0.11f, 2.3f, 0.11f);
            AddBlock(buffer, place, PlantMeshes.BarkSubmesh, 0f, 2.28f, 0f, 0.62f, 0.11f, 0.11f);
        }

        private static void AddPost(
            VegetationMeshBuffer buffer, Vector3 at, Vector3 along, Vector3 side, float half, float height)
        {
            Vector3 foot = at - Vector3.up * Footing;
            Vector3 top = at + Vector3.up * height;

            Vector3 a = along * half;
            Vector3 s = side * half;

            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh,
                top - a + s, top + a + s, foot + a + s, foot - a + s, side);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh,
                top + a - s, top - a - s, foot - a - s, foot + a - s, -side);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh,
                top + a + s, top + a - s, foot + a - s, foot + a + s, along);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh,
                top - a - s, top - a + s, foot - a + s, foot - a - s, -along);
        }

        /// <summary>
        /// A closed box in the placement's own frame, given its centre in x and z, its foot in y, and
        /// half-extents across and along with a full height.
        /// </summary>
        private static void AddBlock(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            float x,
            float y,
            float z,
            float halfWidth,
            float height,
            float halfDepth)
        {
            Vector3 b00 = place.ToWorld(x - halfWidth, y, z - halfDepth);
            Vector3 b10 = place.ToWorld(x + halfWidth, y, z - halfDepth);
            Vector3 b11 = place.ToWorld(x + halfWidth, y, z + halfDepth);
            Vector3 b01 = place.ToWorld(x - halfWidth, y, z + halfDepth);

            Vector3 t00 = place.ToWorld(x - halfWidth, y + height, z - halfDepth);
            Vector3 t10 = place.ToWorld(x + halfWidth, y + height, z - halfDepth);
            Vector3 t11 = place.ToWorld(x + halfWidth, y + height, z + halfDepth);
            Vector3 t01 = place.ToWorld(x - halfWidth, y + height, z + halfDepth);

            Vector3 right = place.Right;
            Vector3 forward = place.Forward;
            Vector3 up = place.Up;

            buffer.AddQuadFacing(submesh, t00, t10, b10, b00, -forward);
            buffer.AddQuadFacing(submesh, t11, t01, b01, b11, forward);
            buffer.AddQuadFacing(submesh, t10, t11, b11, b10, right);
            buffer.AddQuadFacing(submesh, t01, t00, b00, b01, -right);
            buffer.AddQuadFacing(submesh, t01, t11, t10, t00, up);
        }
    }
}
