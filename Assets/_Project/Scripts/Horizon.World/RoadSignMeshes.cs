using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The four kinds of road sign this world carries, as geometry.
    ///
    /// <para><b>Until this existed there was exactly one sign in the entire world</b> — the filling
    /// station's advance board — across seventy-five kilometres of road, four towns, three forks, nine
    /// bores and something over fifty hairpins. A road with no signs on it does not read as a quiet
    /// road; it reads as a road nobody has finished building, which is the specific impression this
    /// whole pass exists to remove.</para>
    ///
    /// <para><b>Everything here is a pictogram, and that is a constraint rather than a style.</b> There
    /// is no world-space text rendering anywhere in this project and adding one would mean a font
    /// atlas, a second material and a per-sign mesh. A silhouette costs six triangles, survives being
    /// twenty pixels wide, and needs no language — which is the argument <c>MapGraphic</c> already
    /// makes for drawing its marks as shapes rather than as four colours of one diamond.</para>
    ///
    /// <para><b>Three submeshes and two draw calls.</b> The post and the symbol both carry a tint, so
    /// <c>VegetationMeshBuffer.MergeTinted</c> folds them into one slot with their colours in the
    /// vertices; the face carries a null tint and keeps <c>M_SignFace</c>, which is the always-bright
    /// unlit material the station totems and the bridge beacons already share. That split is not
    /// cosmetic: a sign face is the one part that has to be legible at midnight, and every lit material
    /// in a town belongs to a <c>LitGroup</c> that swaps it at dusk. A sign is not switched on in the
    /// evening.</para>
    /// </summary>
    public static class RoadSignMeshes
    {
        /// <summary>Posts and board backs. Grey steel.</summary>
        public const int PostSubmesh = 0;

        /// <summary>The pictogram standing proud of the face. Near-black.</summary>
        public const int SymbolSubmesh = 1;

        /// <summary>The bright face. Null tint, so it keeps its own always-lit material.</summary>
        public const int FaceSubmesh = 2;

        public const int SubmeshCount = 3;

        /// <summary>
        /// The tint per submesh, or null where the submesh must keep a material of its own.
        ///
        /// <para>The one null is <see cref="FaceSubmesh"/>, and the rule that goes with it is the one
        /// written against the paddock: a tint means "fold me in", a null means "keep me, I have my own
        /// material", and every slot has to mean one of the two on purpose.</para>
        /// </summary>
        public static Color?[] Tints()
        {
            var tints = new Color?[SubmeshCount];

            tints[PostSubmesh] = new Color(0.55f, 0.56f, 0.58f);
            tints[SymbolSubmesh] = new Color(0.12f, 0.12f, 0.13f);

            return tints;
        }

        /// <summary>Which sign to build. Each is a different board on the same post.</summary>
        public enum Kind
        {
            /// <summary>A settlement begins or ends here. Houses in a row.</summary>
            PlaceName = 0,

            /// <summary>The corner ahead is tight. A triangle, pointing round it.</summary>
            Chevron = 1,

            /// <summary>A bore begins. An arch.</summary>
            Portal = 2,

            // There is deliberately no direction sign, and it is the one kind a reader will look for.
            // A board that points has to know which way, and which side a branch leaves on is the one
            // thing about a fork that RoadCourse does not record: `AddJunction` takes a name and a
            // reach, and `RoadFeature.Side` — the field that would carry it — is set by filling
            // stations alone. Guessing the hand would make the single sign in this world whose entire
            // job is to point the single sign that points the wrong way, at three forks, for ever.
            // The honest version is a side threaded through `AddJunction`'s eight call sites, and it is
            // its own change.
        }

        /// <summary>How far below the road line a post is sunk, metres.</summary>
        /// <remarks>
        /// The ground is never queried, for the reason <c>FuelStationMeshes.SignBury</c> states at
        /// length: inside <c>TerrainShape.VergeWidth</c> the field is a dead-flat shelf at the
        /// carriageway's height less <c>RoadShelfDrop</c>, so a straight guess is out by a couple of
        /// decimetres at worst and burying most of a metre swallows it. <c>AddBox</c> emits no bottom
        /// face to give it away.
        /// </remarks>
        private const float Bury = 0.8f;

        private const float PostHalf = 0.09f;
        private const float BoardDeep = 0.07f;

        /// <summary>How far the pictogram stands off the face, so it cannot z-fight with it.</summary>
        private const float Proud = 0.04f;

        /// <summary>
        /// Lays one sign into <paramref name="buffer"/>, standing on <paramref name="foot"/>.
        /// </summary>
        /// <param name="forward">Along the road. The board is thin in this axis, so it faces traffic.</param>
        /// <param name="outward">Across the road, away from it. The board is wide in this axis.</param>
        /// <param name="hand">
        /// Which way the pictogram points, where it points at all: +1 towards <paramref name="outward"/>,
        /// −1 back across the road. Ignored by <see cref="Kind.PlaceName"/> and <see cref="Kind.Portal"/>,
        /// whose symbols are symmetric.
        /// </param>
        public static void Add(
            VegetationMeshBuffer buffer, Kind kind, Vector3 foot, Vector3 forward, Vector3 outward,
            float hand = 1f)
        {
            switch (kind)
            {
                case Kind.PlaceName:
                    AddOnPost(buffer, foot, forward, outward, 1.05f, 0.62f, 1.72f, Kind.PlaceName, hand);
                    break;

                case Kind.Chevron:
                    // Lower and squarer than the rest. A bake is read at the moment the car is already
                    // in the corner, from a few tens of metres, so height buys nothing and standing tall
                    // on the outside of a hairpin puts a board in the sightline across the apex.
                    AddOnPost(buffer, foot, forward, outward, 0.46f, 0.66f, 1.02f, Kind.Chevron, hand);
                    break;

                default:
                    AddOnPost(buffer, foot, forward, outward, 0.62f, 0.72f, 1.85f, Kind.Portal, hand);
                    break;
            }
        }

        /// <summary>A post with a board on it, and the board's own pictogram on both faces.</summary>
        private static void AddOnPost(
            VegetationMeshBuffer buffer, Vector3 foot, Vector3 forward, Vector3 outward,
            float halfAcross, float halfTall, float boardFoot, Kind kind, float hand)
        {
            buffer.AddBox(PostSubmesh, foot - Vector3.up * Bury, forward, outward,
                PostHalf, PostHalf, boardFoot + Bury);

            Vector3 board = foot + Vector3.up * boardFoot;

            buffer.AddBox(FaceSubmesh, board, forward, outward, BoardDeep, halfAcross, halfTall * 2f);

            Vector3 centre = board + Vector3.up * halfTall;

            // Both faces carry the symbol. A sign is passed as well as approached, and a blank back is
            // what an unfinished one looks like — the same argument AddPictogram makes on the station.
            //
            // The same hand on both, deliberately, and it is worth saying because the opposite looks
            // right: an arrow is a direction in the world, not on a screen. A pointer that leans towards
            // `outward` leans towards the same piece of ground whichever face you are reading, so
            // mirroring it for the back would put one of the two faces on a lie. It is what lets a bake
            // serve both directions of travel — see AddPointer.
            for (int face = -1; face <= 1; face += 2)
            {
                Vector3 at = centre + forward * ((BoardDeep + Proud * 0.5f) * face);

                switch (kind)
                {
                    case Kind.PlaceName:
                        AddHouses(buffer, at, forward, outward, halfAcross, halfTall);
                        break;

                    case Kind.Chevron:
                        AddPointer(buffer, at, outward, halfAcross, halfTall, hand);
                        break;

                    default:
                        AddArch(buffer, at, forward, outward, halfAcross, halfTall);
                        break;
                }
            }
        }

        /// <summary>
        /// Three houses in a row, which is what "you are entering somewhere" looks like with no letters.
        ///
        /// <para>Three rather than one, because a single house on a board is a symbol for a house and a
        /// row of them is a symbol for a village. The roof is a bar narrower than the body sitting on
        /// top of it — not a triangle, which at this size comes back as a smudge with a point on it.</para>
        /// </summary>
        private static void AddHouses(
            VegetationMeshBuffer buffer, Vector3 at, Vector3 forward, Vector3 outward,
            float halfAcross, float halfTall)
        {
            float pitch = halfAcross * 0.62f;
            float body = halfAcross * 0.20f;

            for (int i = -1; i <= 1; i++)
            {
                // The middle house is taller, which is the silhouette of a village rather than a terrace.
                float tall = halfTall * (i == 0 ? 0.86f : 0.62f);

                AddBar(buffer, at, forward, outward, i * pitch, -halfTall * 0.72f, body, tall * 0.62f);
                AddBar(buffer, at, forward, outward, i * pitch,
                    -halfTall * 0.72f + tall * 0.62f, body * 0.66f, tall * 0.30f);
            }
        }

        /// <summary>
        /// One big triangle, pointing round the corner.
        ///
        /// <para>A real bake carries three chevrons and this carries one shape, because a chevron is a
        /// bar at an angle and every bar in this file rises in world up — an angled one needs a frame of
        /// its own, three times over, to say what one triangle says at half the triangles and twice the
        /// size. The thing a driver has to read here is *which way*, from a car already turning.</para>
        ///
        /// <para><b>It points at the centre of the curve, which is why one board does for both
        /// directions of travel.</b> A bake stands on the outside of a bend and the inside of that bend
        /// is in the same place whichever way you are driving through it — so the caller hands this
        /// −1, meaning back across the road, and never has to know which way the traffic runs.</para>
        /// </summary>
        private static void AddPointer(
            VegetationMeshBuffer buffer, Vector3 at, Vector3 outward,
            float halfAcross, float halfTall, float hand)
        {
            Vector3 tip = at + outward * (halfAcross * 0.74f * hand);
            Vector3 back = at - outward * (halfAcross * 0.62f * hand);

            AddTriangle(buffer, tip, back + Vector3.up * (halfTall * 0.70f),
                back - Vector3.up * (halfTall * 0.70f));
        }

        /// <summary>Two legs and a lintel — a portal, drawn as the hole rather than as the mountain.</summary>
        private static void AddArch(
            VegetationMeshBuffer buffer, Vector3 at, Vector3 forward, Vector3 outward,
            float halfAcross, float halfTall)
        {
            float leg = halfAcross * 0.52f;
            float thick = halfAcross * 0.16f;

            AddBar(buffer, at, forward, outward, -leg, -halfTall * 0.18f, thick, halfTall * 0.52f);
            AddBar(buffer, at, forward, outward, leg, -halfTall * 0.18f, thick, halfTall * 0.52f);
            AddBar(buffer, at, forward, outward, 0f, halfTall * 0.46f, leg + thick, thick);
        }

        /// <summary>One flat bar of a pictogram, standing off the board's face.</summary>
        private static void AddBar(
            VegetationMeshBuffer buffer, Vector3 faceCentre, Vector3 forward, Vector3 outward,
            float across, float up, float halfAcross, float halfUp)
        {
            Vector3 at = faceCentre + outward * across + Vector3.up * (up - halfUp);

            buffer.AddBox(SymbolSubmesh, at, forward, outward, Proud * 0.5f, halfAcross, halfUp * 2f);
        }

        /// <summary>
        /// One flat triangle on the board's face, drawn both ways round.
        ///
        /// <para>Double-sided rather than wound to face out, because the caller knows which way the
        /// board looks and the winding of three points on a plane is exactly the kind of thing that
        /// comes out right on one face of a sign and inside-out on the other. Two triangles is the
        /// price of not having to be right about it twice.</para>
        /// </summary>
        private static void AddTriangle(VegetationMeshBuffer buffer, Vector3 a, Vector3 b, Vector3 c)
        {
            buffer.AddDoubleSided(SymbolSubmesh, a, b, c);
        }
    }
}
