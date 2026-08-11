using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The cross-section of a town street: carriageway, kerb, footway.
    ///
    /// A town street wants two things the trunk road has no concept of — a kerb and a pavement — and
    /// wants none of what the trunk road carries. <see cref="RoadMeshBuilder"/> has a marking atlas whose
    /// v coordinate is arc length, row doubling where the marking variant changes, banking from measured
    /// curvature, and nine vertices every 2.5 m. None of that belongs on a street where the limit is
    /// 30 km/h, all of it would have to keep working through every change made here, and a dashed centre
    /// line down a lane would make it read as a main road.
    /// </summary>
    public struct TownStreetShape
    {
        [Tooltip("Half the driveable width, metres.")]
        public float HalfWidth;

        [Tooltip("How far the top of the kerb stands above the gutter, metres.")]
        public float KerbHeight;

        [Tooltip("Horizontal width of the kerb face itself. Not zero: a vertical face with no width "
               + "renders as a crack, and the strip needs an outward direction to face.")]
        public float KerbFace;

        [Tooltip("Width of the footway behind the kerb, metres.")]
        public float FootwayWidth;

        [Tooltip("Distance between cross-sections. 5 m, not the trunk road's 2.5 — a 30 km/h street with "
               + "no banking and no markings has nothing to facet.")]
        public float StepLength;

        [Tooltip("Where the carriageway sits relative to the street's own centreline height, metres.\n\n"
               + "Normally negative, and that is the whole point. A street path runs at "
               + "TownShape.FloorHeight, but MountainField puts the ground TerrainShape.RoadShelfDrop "
               + "*below* every road and level sample it is given — so a surface built at the path's own "
               + "height stands half a metre proud of the grass beside it, with a vertical face. The "
               + "trunk road hides that behind a 1.5 m shoulder; a town street has no shoulder, and the "
               + "result was a network of plateaux you could drive off but not back onto.\n\n"
               + "How far it clears the shelf is the other half, and it is not free to choose: the "
               + "terrain you see is the field interpolated across TerrainShape.CellSize, and that mesh "
               + "can stand a sixth of a metre above the field near a street. Clear the shelf by less "
               + "than that and the grass grows up through the road. See ValidateStreetClearance.")]
        public float SurfaceLift;

        [Tooltip("Camber: how much higher the centre of the carriageway sits than its gutters.")]
        public float Crown;

        [Tooltip("Width of the grass verge outside the footway, metres. It runs from the back of the "
               + "pavement down to wherever the terrain mesh actually is, which is the only way to be "
               + "flush with it — the shelf the height field lays under a street is not exactly where "
               + "arithmetic says it should be, and the difference is a lip you cannot drive back over.")]
        public float VergeWidth;

        /// <summary>Half the width of everything paved, kerbs and footways included.</summary>
        public float HalfOuter => HalfWidth + KerbFace + FootwayWidth;

        /// <summary>
        /// How far a street's carriageway stands above the shelf <see cref="MountainField"/> lays under
        /// it, metres.
        ///
        /// <para><b>0.26, and it was 0.08.</b> At 0.08 the grass grew up through the road on eleven of
        /// the town's forty-one streets — not because the height field was wrong, but because the terrain
        /// is drawn as a linear interpolation of that field across twelve-metre cells and the mesh
        /// therefore rides above the field between lattice points. Measured, it rode up to 0.16 m above
        /// it near a street, against 0.08 m of room. The field-based clearance check could never see it:
        /// the field is not what gets drawn.</para>
        ///
        /// <para>So this has to exceed the mesh's error, not merely be positive, and 0.26 leaves about a
        /// tenth of a metre in hand. <b>It is coupled to <see cref="TerrainShape.CellSize"/></b> — a
        /// coarser terrain grid interpolates over a longer span and misses by more, so raising that
        /// number means raising this one. <c>ValidateStreetClearance</c> measures the pair rather than
        /// trusting either.</para>
        ///
        /// <para>The cost is that a street now stands a hand above the grass beside it rather than a
        /// finger. That is what the 1.6 m verge is for: it ramps the difference away at about a quarter,
        /// which is a bank you can drive up, not the vertical half-metre plateau the negative lift was
        /// introduced to get rid of. <c>VergeGradient</c> holds the line at 0.6.</para>
        /// </summary>
        public const float ClearsTheShelf = 0.26f;

        /// <summary>
        /// The cross-section for a kind of street.
        ///
        /// The steps between the kinds are what make a street network legible from inside a car: you can
        /// tell you have turned off the high street without being told.
        /// </summary>
        /// <param name="shelfDrop">
        /// How far <see cref="MountainField"/> sets the ground below the roads it is given —
        /// <see cref="TerrainShape.RoadShelfDrop"/>. The carriageway is built that far down and
        /// <see cref="ClearsTheShelf"/> back up.
        /// </param>
        public static TownStreetShape For(TownStreetKind kind, float shelfDrop = 0f)
        {
            var shape = new TownStreetShape
            {
                HalfWidth = 4.0f,
                KerbHeight = 0.14f,
                KerbFace = 0.25f,
                FootwayWidth = 1.8f,
                StepLength = 5f,
                SurfaceLift = ClearsTheShelf - shelfDrop,
                Crown = 0.06f,
                VergeWidth = 1.6f,
            };

            switch (kind)
            {
                case TownStreetKind.HighStreet:
                    shape.HalfWidth = 5.4f;
                    shape.FootwayWidth = 3.2f;
                    shape.KerbHeight = 0.16f;
                    break;

                case TownStreetKind.Avenue:
                    shape.HalfWidth = 4.8f;
                    shape.FootwayWidth = 2.4f;
                    break;

                case TownStreetKind.Lane:
                    break;

                case TownStreetKind.Alley:
                    shape.HalfWidth = 3.1f;
                    shape.FootwayWidth = 0.7f;
                    shape.KerbHeight = 0.10f;
                    break;

                // A 2.8 m pavement, not the 4.5 m this started at. The generous figure was double
                // counting: the square itself is thirty metres of paving on the other side of the kerb,
                // so the footway does not also have to be the widest in the town. It was also breaking
                // the junctions at both ends of it — a 8.75 m half-outer puts a street's outer corner
                // forty degrees off its own axis, and at a node where two branches are under fifty
                // degrees apart the corners cross and the pad polygon folds through itself.
                //
                // It is also the one kind that did *not* take the full widening the rest of the town
                // took: the streets round a square eat their own half-widths out of it at both ends, so
                // every metre added here is two metres off the market place, and these are the junctions
                // that were the last in the town to stop folding.
                case TownStreetKind.SquareEdge:
                    shape.HalfWidth = 4.4f;
                    shape.FootwayWidth = 2.8f;
                    break;
            }

            return shape;
        }
    }

    /// <summary>
    /// Turns a street centreline into a ribbon with kerbs and footways either side.
    ///
    /// <para>Everything the town's streets emit goes into <b>one</b> buffer and ends up as one mesh under
    /// one chunk with a radius large enough never to unload — not one mesh per edge, and not one per
    /// terrain tile. Three kilometres of street is about thirteen thousand triangles and three draw
    /// calls, which is cheap enough that splitting it could only cost. It also makes every
    /// seam-at-a-tile-boundary question disappear, and gives the whole network a single
    /// <c>MeshCollider</c>. Worth revisiting past about eight kilometres of street.</para>
    /// </summary>
    public static class TownStreetBuilder
    {
        /// <summary>Asphalt.</summary>
        public const int SurfaceSubmesh = 0;

        /// <summary>The vertical faces of the kerbs.</summary>
        public const int KerbSubmesh = 1;

        /// <summary>The footways.</summary>
        public const int FootwaySubmesh = 2;

        /// <summary>
        /// The grass verge that runs from the back of the pavement down onto the terrain.
        ///
        /// Its own submesh so it can take the grass material and disappear into the field, rather than
        /// reading as a paved shoulder a metre and a half wide.
        /// </summary>
        public const int VergeSubmesh = 3;

        public const int StreetSubmeshCount = 4;

        /// <summary>Which submesh each of the eight strips across a section belongs to.</summary>
        private static readonly int[] StripSubmesh =
        {
            VergeSubmesh, FootwaySubmesh, KerbSubmesh, SurfaceSubmesh,
            SurfaceSubmesh, KerbSubmesh, FootwaySubmesh, VergeSubmesh,
        };

        /// <summary>Points across one section: two verges, two footways, two kerbs, two half carriageways.</summary>
        private const int SectionPoints = 9;

        /// <summary>
        /// Adds one street's ribbon between two distances along its path.
        ///
        /// The trimmed range matters: a street stops short of its junctions so the pad can fill the
        /// middle. Callers pass the trim points from <see cref="StreetJunctionBuilder.ResolveTrims"/>.
        /// </summary>
        public static void AppendStreet(
            IRoadPath path,
            in TownStreetShape shape,
            float fromDistance,
            float toDistance,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float from = Mathf.Clamp(fromDistance, 0f, path.Length);
            float to = Mathf.Clamp(toDistance, 0f, path.Length);
            if (to - from < 0.5f)
            {
                return;
            }

            int steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / Mathf.Max(1f, shape.StepLength)));

            var previous = new Vector3[SectionPoints];
            var current = new Vector3[SectionPoints];

            CrossSection(path, shape, from, field, terrainShape, previous);

            for (int step = 1; step <= steps; step++)
            {
                float at = Mathf.Lerp(from, to, step / (float)steps);
                CrossSection(path, shape, at, field, terrainShape, current);

                for (int strip = 0; strip < StripSubmesh.Length; strip++)
                {
                    // Outward is up for every strip but the kerb faces, which look sideways — inwards,
                    // at the carriageway they edge, which is the only side of a kerb anyone sees. Taken
                    // as the direction from the kerb towards the crown so it comes out right on both
                    // sides of the street; the first version derived it from the strip's own two points
                    // and was therefore correct on the left kerb and backwards on the right.
                    Vector3 outward = Vector3.up;
                    if (StripSubmesh[strip] == KerbSubmesh)
                    {
                        outward = current[4] - current[strip];
                        outward.y = 0f;
                    }

                    // Along first, then across: Cross(along, across) points up, and the reverse order
                    // wound every face in the network backwards. The buffer corrects them, which is
                    // exactly why it also counts them.
                    into.AddQuadFacing(
                        StripSubmesh[strip],
                        previous[strip], current[strip], current[strip + 1], previous[strip + 1],
                        outward);
                }

                (previous, current) = (current, previous);
            }
        }

        /// <summary>
        /// The seven points across a street at one distance: outer footway edge, kerb top, gutter, crown,
        /// gutter, kerb top, outer footway edge.
        ///
        /// The crown is a point of its own rather than a lift applied to the whole carriageway, because a
        /// flat surface raised bodily is a plate — the camber has to be visible in the silhouette of the
        /// section, which means the middle has to be a vertex.
        /// </summary>
        private static void CrossSection(
            IRoadPath path,
            in TownStreetShape shape,
            float distance,
            MountainField field,
            in TerrainShape terrainShape,
            Vector3[] into)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float half = shape.HalfWidth;
            float kerbTop = half + shape.KerbFace;
            float outer = shape.HalfOuter;
            float lift = shape.SurfaceLift;
            float top = lift + shape.KerbHeight;

            into[1] = Offset(centre, right, -outer, top);
            into[2] = Offset(centre, right, -kerbTop, top);
            into[3] = Offset(centre, right, -half, lift);
            into[4] = Offset(centre, right, 0f, lift + shape.Crown);
            into[5] = Offset(centre, right, half, lift);
            into[6] = Offset(centre, right, kerbTop, top);
            into[7] = Offset(centre, right, outer, top);

            into[0] = Verge(centre, right, -(outer + shape.VergeWidth), top, field, terrainShape);
            into[8] = Verge(centre, right, outer + shape.VergeWidth, top, field, terrainShape);
        }

        /// <summary>
        /// The outer end of a verge, sitting on the terrain mesh rather than at a computed height.
        ///
        /// <para>Sampled, not derived, and that is the point. The shelf the height field lays under a
        /// street can be a fifth of a metre off what the arithmetic says — the field averages nearby
        /// samples and the terrain mesh then interpolates that across twelve-metre cells. Two tenths of a
        /// metre is invisible in a screenshot and is a wall to a raycast wheel, so the verge asks the
        /// mesh where it is instead of assuming.</para>
        ///
        /// <para>Falls back to the paved height when there is no field yet, which keeps the builder
        /// usable before the terrain exists even though nothing does that now.</para>
        /// </summary>
        private static Vector3 Verge(
            Vector3 centre,
            Vector3 right,
            float across,
            float pavedRise,
            MountainField field,
            in TerrainShape terrainShape)
        {
            Vector3 at = centre + right * across;

            if (field == null)
            {
                return at + Vector3.up * pavedRise;
            }

            TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                out Vector3 ground, out Vector3 _);

            // A whisker above the mesh, so the verge is never the thing that z-fights with the ground.
            return new Vector3(at.x, ground.y + 0.02f, at.z);
        }

        private static Vector3 Offset(Vector3 centre, Vector3 right, float across, float rise)
        {
            return centre + right * across + Vector3.up * rise;
        }

        /// <summary>
        /// A point on a street's cross-section: <paramref name="across"/> metres from the centreline,
        /// <paramref name="rise"/> metres above the surface's own datum.
        ///
        /// Public because the junction pads build their corners from it. Heights at a junction come from
        /// the ribbon's own section rather than from a second evaluation of the ground, which is what
        /// makes pad and ribbon flush to the millimetre instead of to within a tolerance.
        /// </summary>
        public static Vector3 PointAcross(
            IRoadPath path, in TownStreetShape shape, float distance, float across, float rise)
        {
            float at = Mathf.Clamp(distance, 0f, path.Length);
            return Offset(path.GetPositionAtDistance(at), path.GetRightAtDistance(at), across, rise);
        }

        /// <summary>
        /// The outer corner of a street's paved surface at a distance, left or right looking along it.
        ///
        /// Junction pads take their corners from here rather than re-deriving them, so a pad and the
        /// ribbon it meets are flush to the millimetre by construction. That is the general form of the
        /// bug the flushness check exists to catch.
        /// </summary>
        public static Vector3 OuterCorner(
            IRoadPath path, in TownStreetShape shape, float distance, bool leftSide)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float across = leftSide ? -shape.HalfOuter : shape.HalfOuter;
            return Offset(centre, right, across, shape.SurfaceLift + shape.KerbHeight);
        }
    }
}
