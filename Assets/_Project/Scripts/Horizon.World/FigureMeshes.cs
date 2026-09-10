using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// A standing person, in about thirty triangles.
    ///
    /// <para><b>This world has had four towns, a market square, a promenade and a harbour, and not one
    /// person in any of them.</b> A town with nobody in it does not read as quiet — it reads as
    /// evacuated, which is the same thing the empty roadside was doing before the poles went up.</para>
    ///
    /// <para><b>Standing, and never walking, and that is the same argument the ambient audio was deleted
    /// on.</b> The note against <c>EngineAudio</c> says a sound with no visible source reads as the
    /// device rather than as the world; a person with no collision, no sound and no reason to be walking
    /// where they are walking is the same fault seen from the other side. A figure at a stall is
    /// furniture that happens to be a person — it costs no draw call, it goes in the merged tile mesh
    /// with the stall it belongs to, and it cannot walk through a wall because it does not walk.</para>
    ///
    /// <para><b>Boxes and not a silhouette, unlike every sign in this world.</b> A sign is read flat-on
    /// from a car at a known angle; a figure is walked round, and a billboard seen edge-on disappears —
    /// which is worse than no figure at all. Five boxes have a front and a back from every side, and at
    /// the distance anybody sees one of these the head and the shoulders are the whole of it.</para>
    /// </summary>
    public static class FigureMeshes
    {
        /// <summary>Height of the whole figure, metres. Short of the 1.8 a person is, on purpose.</summary>
        /// <remarks>
        /// 1.72 is the middle of the range below. These stand beside a 2.2 m stall counter and under a
        /// 2.65 m awning, and a figure built at a generous 1.9 puts a head through the canopy of every
        /// stall in the world — which is the sort of thing that is obvious in a picture and invisible in
        /// a count.
        /// </remarks>
        private const float MinHeight = 1.62f;

        private const float MaxHeight = 1.82f;

        /// <summary>
        /// One figure standing at <paramref name="x"/>, <paramref name="z"/> in the placement's frame.
        ///
        /// <para>Two tones, because that is what separates a person from a bollard at forty metres: a
        /// body in whatever the caller's palette calls a painted colour, and a head and legs in
        /// something dark.</para>
        ///
        /// <para><b>The two submeshes are arguments and not constants, and the first version had them
        /// as constants.</b> It named <c>BuildingMeshes.TrimSubmesh</c> and
        /// <c>BuildingMeshes.AccentSubmesh</c> directly, which made this usable inside a town buffer and
        /// nowhere else — and the promenade builds into <c>HarbourMeshes</c>' five-slot scheme, where
        /// slot 11 does not exist. The rebuild threw an <c>IndexOutOfRangeException</c> from inside
        /// <c>AddQuadFacing</c>, halfway through the world, having already built four towns' worth of
        /// figures correctly. <b>A mesh helper that names one builder's submeshes is a helper that
        /// belongs to that builder.</b></para>
        /// </summary>
        /// <param name="facing">Radians about the placement's up axis. Which way they are looking.</param>
        /// <param name="bodySubmesh">The painted colour, in the caller's own scheme.</param>
        /// <param name="darkSubmesh">Something dark, for the head and the legs.</param>
        public static void AddFigure(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float x,
            float z,
            float facing,
            int bodySubmesh,
            int darkSubmesh,
            ref PlantRandom random)
        {
            float height = random.Range(MinHeight, MaxHeight);

            float legs = height * 0.46f;
            float torso = height * 0.36f;
            float head = height * 0.13f;

            float halfWide = height * 0.115f;
            float halfDeep = height * 0.075f;

            // Turned inside the plot's own frame rather than given a placement of its own, so a figure
            // costs no second PlantPlacement and a stall's four of them can look four ways.
            float cos = Mathf.Cos(facing);
            float sin = Mathf.Sin(facing);

            // Copied out of the `in` parameter because a local function may not capture one. A
            // PlantPlacement is a readonly struct of five vectors, so the copy is what passing it by
            // value would have cost anyway.
            PlantPlacement frame = place;

            void Box(int submesh, float ox, float y, float oz, float hx, float tall, float hz)
            {
                // Rotate the offset and the half-extents together, which for a box turned about up is
                // the same as swapping its width and depth as it passes forty-five degrees. Done with
                // the corner points instead so any angle works.
                Vector3 centre = frame.ToWorld(x + ox * cos - oz * sin, y, z + ox * sin + oz * cos);

                Vector3 right = frame.Right * cos + frame.Forward * sin;
                Vector3 forward = frame.Forward * cos - frame.Right * sin;
                Vector3 up = frame.Up;

                Vector3 a = right * hx;
                Vector3 d = forward * hz;
                Vector3 rise = up * tall;

                Vector3 b0 = centre - a - d;
                Vector3 b1 = centre + a - d;
                Vector3 b2 = centre + a + d;
                Vector3 b3 = centre - a + d;

                buffer.AddQuadFacing(submesh, b0 + rise, b1 + rise, b2 + rise, b3 + rise, up);
                buffer.AddQuadFacing(submesh, b0, b1, b1 + rise, b0 + rise, -forward);
                buffer.AddQuadFacing(submesh, b2, b3, b3 + rise, b2 + rise, forward);
                buffer.AddQuadFacing(submesh, b1, b2, b2 + rise, b1 + rise, right);
                buffer.AddQuadFacing(submesh, b3, b0, b0 + rise, b3 + rise, -right);
            }

            Box(darkSubmesh, 0f, 0f, 0f, halfWide * 0.78f, legs, halfDeep);
            Box(bodySubmesh, 0f, legs, 0f, halfWide, torso, halfDeep);

            // Arms, as one box either side of the torso and slightly narrower. Without them the
            // silhouette is a post with a hat on and reads as a bollard, which is the one thing a
            // standing figure must not be mistaken for.
            Box(bodySubmesh, -halfWide, legs + torso * 0.12f, 0f,
                halfWide * 0.28f, torso * 0.82f, halfDeep * 0.7f);
            Box(bodySubmesh, halfWide, legs + torso * 0.12f, 0f,
                halfWide * 0.28f, torso * 0.82f, halfDeep * 0.7f);

            Box(darkSubmesh, 0f, legs + torso, 0f, halfWide * 0.62f, head, halfDeep * 0.9f);
        }
    }
}
