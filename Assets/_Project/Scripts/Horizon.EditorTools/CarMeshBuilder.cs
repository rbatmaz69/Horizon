using System.Collections.Generic;
using Horizon.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Generates the low-poly car body, wheels and tailpipes — shaped after a late-sixties American
    /// fastback: long hood, short deck, low roof, a roofline that runs unbroken from the cabin to the
    /// tail panel, and wide haunches over the rear wheels.
    ///
    /// The body is a stack of closed cross-sections along Z. <see cref="KeyStations"/> describes the
    /// silhouette at a handful of points; those are interpolated to a fine grid so the shell is smooth
    /// and so the wheel arches can be carved out of the underside.
    ///
    /// Normals are smoothed, with hard creases inserted only where a real car has them (see
    /// <see cref="CreaseZ"/>) by emitting a duplicate ring there so no normal averages across the edge.
    ///
    /// Authoring-only, hence EditorTools.
    /// </summary>
    public static class CarMeshBuilder
    {
        public const int BodySubmesh = 0;
        public const int GlassSubmesh = 1;
        public const int HeadlightSubmesh = 2;
        public const int TaillightSubmesh = 3;
        public const int ChromeSubmesh = 4;
        public const int BodySubmeshCount = 5;

        public const int TyreSubmesh = 0;
        public const int RimSubmesh = 1;
        public const int WheelSubmeshCount = 2;

        /// <summary>
        /// Half the distance between the wheel centres. Must match the prefab's anchors. Set so the
        /// tyre stands a few centimetres proud of the fender — flush wheels read as recessed.
        /// </summary>
        public const float TrackHalfWidth = 0.99f;

        /// <summary>How far the widebody arches blister out beyond the flank, metres.</summary>
        private const float FlareWidth = 0.09f;

        /// <summary>How far either side of a wheel centre the flare fades back to nothing, metres.</summary>
        private const float FlareReach = 0.75f;

        /// <summary>Distance of the wheel centres from the car's middle, along Z.</summary>
        public const float WheelBaseHalf = 1.35f;

        /// <summary>Local position of each tailpipe mouth, for hanging the smoke emitters on.</summary>
        public static readonly Vector3[] ExhaustOutlets =
        {
            new Vector3(0.42f, -0.46f, -2.44f),
            new Vector3(-0.42f, -0.46f, -2.44f),
        };

        /// <summary>
        /// Top of the wheel arch openings. Sized so the wheel nearly fills the opening — an arch much
        /// larger than its wheel makes the car look like it is on the wrong rims.
        ///
        /// Note this is a *request*, not the final height: <see cref="BuildRing"/> clamps the arch to
        /// <c>belt - 0.08</c> so the opening can never reach the beltline. It sat at 0.15 against a
        /// beltline of 0.21 and was therefore doing nothing at all — the clamp won. Raising it only has
        /// an effect because <see cref="KeyStations"/> now lifts the beltline over the wheels to match.
        /// </summary>
        private const float ArchTopY = 0.22f;

        /// <summary>
        /// Half-length of an arch opening along Z. Roughly the wheel radius plus a margin.
        ///
        /// 0.50 exactly, because <c>WheelBaseHalf - 0.50 = 0.85</c> lands on the cowl crease, where the
        /// arch contributes nothing anyway — so the front opening cannot ripple the base of the
        /// windscreen.
        /// </summary>
        private const float ArchHalfLength = 0.50f;

        private readonly struct Station
        {
            public readonly float Z;
            public readonly float HalfWidth;
            public readonly float BeltY;
            public readonly float TopY;
            public readonly float TopHalfWidth;

            /// <summary>
            /// Local floor height. Raised at the extreme ends so the nose and tail dome over instead
            /// of ending in a vertical slab, which is most of what made the old shape read as a block.
            /// </summary>
            public readonly float SillY;

            public Station(float z, float halfWidth, float beltY, float topY, float topHalfWidth, float sillY)
            {
                Z = z;
                HalfWidth = halfWidth;
                BeltY = beltY;
                TopY = topY;
                TopHalfWidth = topHalfWidth;
                SillY = sillY;
            }
        }

        /// <summary>
        /// The silhouette, tail to nose. Note the shape of it: the hood runs flat from z 0.62 to 2.30
        /// (1.7 m of it), the deck behind the cabin is only 0.6 m, and TopY falls continuously from the
        /// roof at 0.68 all the way to the tail — that unbroken slope is the fastback.
        /// </summary>
        private static readonly Station[] KeyStations =
        {
            // Three things matter here.
            //
            // TopY stays at 0.28 from the cowl to the nose — the hood is dead flat and the face is
            // near-vertical rather than drooping, which is the difference between a muscle car and a
            // seventies boat nose.
            //
            // The roof sits at 0.68 against a beltline of 0.22, so the glasshouse is about four tenths of
            // the body's height. At 0.57 it was a third, and the car read as pressed flat: a deep slab of
            // door with a letterbox on top, and a roofline whose fall to the tail was too shallow to see.
            // The fall is now 0.47 over the last 1.9 m and follows a straight line from the roof to the
            // tail panel, bowed out by a couple of centimetres — that line *is* the fastback. Flatten it
            // and the same car reads as an estate.
            //
            // The two fender stations are noticeably wider than the body between them, which is the
            // widebody flare. TrackHalfWidth is set so the tyres still stand proud of it.
            // Two more things the table now does.
            //
            // BeltY rises to about 0.30 over each axle and drops back to 0.22 in the middle. That is the
            // haunch, and it is not only styling: BuildRing caps the wheel arch at belt - 0.08, so the
            // beltline is what physically decides how big an arch opening can be. Without the hips, the
            // 0.42 m wheels stand in the bodywork whatever ArchTopY says.
            //
            // At the tail, TopY falls to 0.29 and then kicks back *up* to 0.36 before cutting off. That
            // is the ducktail — a real one is an upturn pressed into the deck lid, not a part bolted on,
            // so it is built the same way here and the shell stays closed.
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.36f, 0.80f, 0.15f, 0.24f, 0.58f, -0.38f),
            new Station(-2.30f, 0.88f, 0.17f, 0.33f, 0.66f, -0.45f),
            new Station(-2.20f, 0.93f, 0.19f, 0.36f, 0.72f, -0.50f),
            new Station(-2.05f, 0.96f, 0.21f, 0.29f, 0.74f, -0.52f),
            new Station(-1.80f, 0.98f, 0.24f, 0.36f, 0.74f, -0.52f),
            new Station(-1.55f, 1.00f, 0.28f, 0.44f, 0.72f, -0.52f),
            new Station(-1.35f, 1.02f, 0.30f, 0.48f, 0.71f, -0.52f),
            new Station(-1.15f, 1.00f, 0.28f, 0.53f, 0.70f, -0.52f),
            new Station(-0.90f, 0.96f, 0.24f, 0.59f, 0.68f, -0.52f),
            new Station(-0.45f, 0.93f, 0.22f, 0.68f, 0.66f, -0.52f),
            new Station(0.25f, 0.92f, 0.22f, 0.67f, 0.65f, -0.52f),
            new Station(0.85f, 0.93f, 0.24f, 0.31f, 0.78f, -0.52f),
            new Station(1.15f, 0.97f, 0.27f, 0.33f, 0.79f, -0.52f),
            new Station(1.40f, 1.00f, 0.29f, 0.34f, 0.80f, -0.52f),
            new Station(1.70f, 0.99f, 0.27f, 0.33f, 0.80f, -0.52f),

            // The nose. It used to end in a 1.64 m wide, 0.73 m tall flat disc — AddCap forces every
            // vertex of the last ring to one Z, so the whole front shaded as a single plate, and that
            // was most of why it read as a block. HalfWidth also fell only 13 % over the last 0.37 m and
            // SillY rose only 8 cm, so there was nothing curving into it either.
            //
            // Six rings now taper over 0.57 m and the cap is down to about a third of its old area, with
            // the sill rising 0.31 m to dome the underside. Not tapered to a point: a Mustang has a full
            // rounded snout, and a wedge would be the wrong car.
            new Station(1.95f, 0.94f, 0.24f, 0.31f, 0.78f, -0.51f),
            new Station(2.16f, 0.93f, 0.21f, 0.30f, 0.77f, -0.49f),
            new Station(2.30f, 0.90f, 0.18f, 0.29f, 0.74f, -0.45f),
            new Station(2.40f, 0.84f, 0.14f, 0.26f, 0.68f, -0.39f),
            new Station(2.47f, 0.72f, 0.09f, 0.21f, 0.56f, -0.31f),
            new Station(2.52f, 0.54f, 0.03f, 0.14f, 0.40f, -0.20f),
        };

        /// <summary>
        /// Z positions that get a duplicated ring, so no normal averages across the edge: the ducktail's
        /// leading edge, the deck edge, and both ends of the screen.
        ///
        /// The ducktail needs its crease or the upturn reads as a soft swelling in the deck rather than
        /// as a spoiler with a lip.
        /// </summary>
        private static readonly float[] CreaseZ = { -2.20f, -1.80f, 0.25f, 0.85f };

        /// <summary>Spacing of the interpolated cross-sections.</summary>
        private const float StationStep = 0.13f;

        /// <summary>
        /// How much the top surface bulges above its edges, as a fraction of the roof half-width.
        /// Without this the roof and hood are dead-flat plates spanning the full width, and no amount
        /// of rounding elsewhere stops the car reading as a box.
        /// </summary>
        private const float CrownFraction = 0.055f;

        private const int KeyPointCount = 17;
        private const int RingSubdivisions = 3;
        private const int RingVertexCount = KeyPointCount * RingSubdivisions;

        /// <summary>Ring segments forming the top surface — roof, hood, windscreen, rear window.</summary>
        private static readonly HashSet<int> TopKeySegments = new HashSet<int> { 6, 7, 8, 9 };

        /// <summary>
        /// Ring segments forming the side-window band, between the beltline and the roof rail.
        /// Segments 5 and 10 are the rails themselves and stay body colour — including them let the
        /// glass climb over the edge of the roof.
        /// </summary>
        private static readonly HashSet<int> FlankKeySegments = new HashSet<int> { 3, 4, 11, 12 };

        /// <summary>Builds the car body. Five submeshes — see the Submesh constants for the order.</summary>
        public static Mesh BuildBody(string meshName = "CarBodyMesh")
        {
            List<Station> stations = BuildFineStations();

            var vertices = new List<Vector3>(2048);
            var submeshTriangles = new List<int>[BodySubmeshCount];
            for (int i = 0; i < BodySubmeshCount; i++)
            {
                submeshTriangles[i] = new List<int>(1024);
            }

            var rowZ = new List<float>();
            var rowStart = new List<int>();
            var rowIsDuplicate = new List<bool>();

            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                bool interior = i > 0 && i < stations.Count - 1;
                int copies = interior && IsCrease(station.Z) ? 2 : 1;

                Vector3[] ring = BuildRing(station);
                for (int copy = 0; copy < copies; copy++)
                {
                    rowZ.Add(station.Z);
                    rowStart.Add(vertices.Count);
                    rowIsDuplicate.Add(copy > 0);
                    vertices.AddRange(ring);
                }
            }

            for (int row = 0; row < rowStart.Count - 1; row++)
            {
                // Skip the zero-thickness gap between the two copies of a crease station.
                if (rowIsDuplicate[row + 1])
                {
                    continue;
                }

                float midZ = (rowZ[row] + rowZ[row + 1]) * 0.5f;
                int backStart = rowStart[row];
                int frontStart = rowStart[row + 1];

                for (int i = 0; i < RingVertexCount; i++)
                {
                    int next = (i + 1) % RingVertexCount;
                    int submesh = ResolveSubmesh(midZ, i / RingSubdivisions);
                    List<int> triangles = submeshTriangles[submesh];

                    int a = backStart + i;
                    int b = backStart + next;
                    int c = frontStart + i;
                    int d = frontStart + next;

                    // Winding chosen so faces point outwards; RecalculateNormals depends on it.
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);

                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(c);
                }
            }

            // Caps get their own vertices so the tail and nose edges stay crisp.
            AddCap(vertices, submeshTriangles[BodySubmesh], BuildRing(stations[0]), facingForward: false);
            AddCap(vertices, submeshTriangles[BodySubmesh], BuildRing(stations[stations.Count - 1]),
                facingForward: true);

            AddFrontDetails(vertices, submeshTriangles);
            AddRearDetails(vertices, submeshTriangles);

            // Long enough to run back under the tail rather than poke out of it like a peg.
            for (int i = 0; i < ExhaustOutlets.Length; i++)
            {
                AddTube(vertices, submeshTriangles[ChromeSubmesh], ExhaustOutlets[i], 0.075f, 0.38f, 12);
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.subMeshCount = BodySubmeshCount;
            for (int i = 0; i < BodySubmeshCount; i++)
            {
                mesh.SetTriangles(submeshTriangles[i], i);
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Interpolates the key stations onto a fine grid. Key Z values are always included, so the
        /// crease positions land exactly on a station and the windscreen keeps its hard edge.
        /// </summary>
        private static List<Station> BuildFineStations()
        {
            var fine = new List<Station>(64);

            for (int gap = 0; gap < KeyStations.Length - 1; gap++)
            {
                Station from = KeyStations[gap];
                Station to = KeyStations[gap + 1];

                float span = to.Z - from.Z;
                int steps = Mathf.Max(1, Mathf.CeilToInt(span / StationStep));

                for (int step = 0; step < steps; step++)
                {
                    float t = step / (float)steps;
                    fine.Add(new Station(
                        Mathf.Lerp(from.Z, to.Z, t),
                        Mathf.Lerp(from.HalfWidth, to.HalfWidth, t),
                        Mathf.Lerp(from.BeltY, to.BeltY, t),
                        Mathf.Lerp(from.TopY, to.TopY, t),
                        Mathf.Lerp(from.TopHalfWidth, to.TopHalfWidth, t),
                        Mathf.Lerp(from.SillY, to.SillY, t)));
                }
            }

            fine.Add(KeyStations[KeyStations.Length - 1]);
            return fine;
        }

        private static bool IsCrease(float z)
        {
            for (int i = 0; i < CreaseZ.Length; i++)
            {
                if (Mathf.Abs(CreaseZ[i] - z) < 0.001f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How far the bodywork blisters outwards at a given Z — the widebody arches.
        ///
        /// Deliberately a function of position applied to the flank points of the cross-section rather
        /// than new bolt-on geometry. The ring stays a closed loop and the ring-to-ring stitching is
        /// untouched, so there is no seam to leak and nothing to keep in step with the shell. Widening
        /// <see cref="Station.HalfWidth"/> instead would have been simpler still, but that moves the
        /// whole flank from sill to roof rail and gives a car that is uniformly fatter — not one with
        /// arches over its wheels.
        ///
        /// SmoothStep rather than a linear ramp: a cone has a visible kink where it meets the flank,
        /// and the point of a flare is that it swells.
        /// </summary>
        private static float FlareAt(float z)
        {
            float flare = 0f;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / FlareReach;
                if (distance >= 1f)
                {
                    continue;
                }

                flare = Mathf.Max(flare, FlareWidth * Mathf.SmoothStep(0f, 1f, 1f - distance));
            }

            return flare;
        }

        /// <summary>
        /// Underside height at a given Z. Rises into a roughly circular arch over each wheel — that
        /// opening is what stops the wheels looking like castors bolted under a slab.
        /// </summary>
        private static float BottomAt(float z, float sillY)
        {
            float bottom = sillY;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / ArchHalfLength;
                if (distance >= 1f)
                {
                    continue;
                }

                float arch = Mathf.Lerp(sillY, ArchTopY, Mathf.Sqrt(1f - distance * distance));
                bottom = Mathf.Max(bottom, arch);
            }

            return bottom;
        }

        /// <summary>
        /// Fourteen control points, smoothed into a closed loop. Pairs of points sit close together at
        /// the belt line and the shoulder, which tightens those corners — a muscle car needs a crisp
        /// beltline, not an egg. Reuses the road's Catmull-Rom rather than a second copy of it.
        /// </summary>
        private static Vector3[] BuildRing(in Station station)
        {
            float z = station.Z;
            float belt = station.BeltY;
            float top = Mathf.Max(station.TopY, belt + 0.05f);

            // The arch raises the underside only, and is clamped to stay below the beltline. Letting it
            // push the belt up instead — which is what an ordering guard on the belt does — ripples the
            // hood surface right over the front wheel, because the shoulder points hang off the belt.
            float bottom = Mathf.Min(BottomAt(z, station.SillY), belt - 0.08f);
            float half = station.HalfWidth;
            float topHalf = station.TopHalfWidth;
            float crown = topHalf * CrownFraction;

            // The flare is carried by the flank points only. The sill takes a third of it so the arch
            // does not look pinched underneath, the point just above the beltline takes under half so
            // the blister tucks back in towards the glasshouse, and the roof takes none at all — a
            // widebody widens the body, never the cabin.
            float flare = FlareAt(z);
            float flank = half + flare;
            float sillX = half * 0.72f + flare * 0.35f;
            float shoulderX = half * 0.985f + flare * 0.45f;

            // Intermediate heights are fractions of the available span, never fixed offsets. Over a
            // wheel arch the gap between the underside and the belt shrinks to a few centimetres, and
            // fixed offsets would push these points below the underside and twist the section.
            float flankLow = Mathf.Lerp(bottom, belt, 0.30f);
            float flankHigh = Mathf.Lerp(bottom, belt, 0.70f);
            float shoulderLow = Mathf.Lerp(belt, top, 0.12f);
            float shoulderHigh = Mathf.Lerp(belt, top, 0.85f);

            // Three points across the top rather than one straight span, so roof, hood, windscreen and
            // rear window are crowned like pressed sheet metal instead of flat plates.
            var key = new[]
            {
                new Vector3(sillX, bottom, z),
                new Vector3(flank * 0.99f, flankLow, z),
                new Vector3(flank, flankHigh, z),
                new Vector3(flank, belt, z),
                new Vector3(shoulderX, shoulderLow, z),
                new Vector3(topHalf * 1.02f, shoulderHigh, z),
                new Vector3(topHalf, top, z),
                new Vector3(topHalf * 0.58f, top + crown * 0.85f, z),
                new Vector3(0f, top + crown, z),
                new Vector3(-topHalf * 0.58f, top + crown * 0.85f, z),
                new Vector3(-topHalf, top, z),
                new Vector3(-topHalf * 1.02f, shoulderHigh, z),
                new Vector3(-shoulderX, shoulderLow, z),
                new Vector3(-flank, belt, z),
                new Vector3(-flank, flankHigh, z),
                new Vector3(-flank * 0.99f, flankLow, z),
                new Vector3(-sillX, bottom, z),
            };

            var ring = new Vector3[RingVertexCount];
            for (int segment = 0; segment < KeyPointCount; segment++)
            {
                Vector3 p0 = key[((segment - 1) + KeyPointCount) % KeyPointCount];
                Vector3 p1 = key[segment];
                Vector3 p2 = key[(segment + 1) % KeyPointCount];
                Vector3 p3 = key[(segment + 2) % KeyPointCount];

                for (int step = 0; step < RingSubdivisions; step++)
                {
                    float t = step / (float)RingSubdivisions;
                    Vector3 point = RoadPath.CatmullRom(p0, p1, p2, p3, t);
                    point.z = z;
                    ring[segment * RingSubdivisions + step] = point;
                }
            }

            return ring;
        }

        /// <summary>
        /// Glass is decided by position along the car rather than by station index, so reshaping the
        /// silhouette cannot silently move the windows onto the wrong panel.
        /// </summary>
        private static int ResolveSubmesh(float z, int keySegment)
        {
            bool windscreen = z > 0.25f && z < 0.85f;
            bool rearWindow = z > -1.60f && z < -0.45f;
            bool cabin = z > -1.60f && z < 0.27f;

            if (TopKeySegments.Contains(keySegment) && (windscreen || rearWindow))
            {
                return GlassSubmesh;
            }

            if (FlankKeySegments.Contains(keySegment) && cabin)
            {
                return GlassSubmesh;
            }

            return BodySubmesh;
        }

        /// <summary>
        /// Wide grille with the headlights set into its outer ends.
        ///
        /// Sits just *in front of* the nose cap at z 2.44, not at the widest station. A panel placed
        /// back where the body is widest ends up inside the shell and renders nothing.
        /// </summary>
        private static void AddFrontDetails(List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            // Just ahead of the nose cap, which now ends at 2.52 and is ±0.54 wide. A panel placed back
            // where the body is widest ends up buried inside the shell and renders nothing — which is
            // exactly what would have happened if these had been left at the old 2.34 after the nose was
            // extended, and it would have looked like the grille had simply vanished.
            const float z = 2.54f;

            AddPanel(vertices, submeshTriangles[GlassSubmesh], z, -0.30f, 0.30f, -0.13f, 0.02f, true);
            AddPanel(vertices, submeshTriangles[HeadlightSubmesh], z, 0.34f, 0.50f, -0.02f, 0.10f, true);
            AddPanel(vertices, submeshTriangles[HeadlightSubmesh], z, -0.50f, -0.34f, -0.02f, 0.10f, true);
        }

        /// <summary>Three vertical bars each side, which is the tail this car is quoting.</summary>
        private static void AddRearDetails(List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            // Two centimetres behind the tail cap at -2.36, so the bars sit proud of it rather than
            // coplanar with it and z-fighting.
            const float z = -2.38f;
            var barStarts = new[] { 0.18f, 0.34f, 0.50f };
            const float barWidth = 0.13f;

            for (int i = 0; i < barStarts.Length; i++)
            {
                float x0 = barStarts[i];
                float x1 = x0 + barWidth;

                AddPanel(vertices, submeshTriangles[TaillightSubmesh], z, x0, x1, -0.10f, 0.14f, false);
                AddPanel(vertices, submeshTriangles[TaillightSubmesh], z, -x1, -x0, -0.10f, 0.14f, false);
            }
        }

        /// <summary>
        /// Builds a wheel with its axle along **X**, because the controller writes the wheel pivot's
        /// rotation directly as spin-about-X plus steer-about-Y.
        /// </summary>
        public static Mesh BuildWheel(float radius, float width, int sides = 18, string meshName = "WheelMesh")
        {
            float halfWidth = width * 0.5f;
            float rimRadius = radius * 0.58f;

            var vertices = new List<Vector3>(sides * 16);
            var tyreTriangles = new List<int>(sides * 18);
            var rimTriangles = new List<int>(sides * 9);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));

                Vector3 treadRight0 = new Vector3(halfWidth, d0.y * radius, d0.x * radius);
                Vector3 treadRight1 = new Vector3(halfWidth, d1.y * radius, d1.x * radius);
                Vector3 treadLeft0 = new Vector3(-halfWidth, d0.y * radius, d0.x * radius);
                Vector3 treadLeft1 = new Vector3(-halfWidth, d1.y * radius, d1.x * radius);

                AddTriangleOutward(vertices, tyreTriangles, treadLeft0, treadRight0, treadRight1, Vector3.zero);
                AddTriangleOutward(vertices, tyreTriangles, treadLeft0, treadRight1, treadLeft1, Vector3.zero);

                for (int side = 0; side < 2; side++)
                {
                    float x = side == 0 ? halfWidth : -halfWidth;
                    float sign = side == 0 ? -1f : 1f;
                    Vector3 inward = new Vector3(sign, 0f, 0f);

                    // Tyre sidewall: the annulus from the tread in to the rim lip.
                    Vector3 outer0 = new Vector3(x, d0.y * radius, d0.x * radius);
                    Vector3 outer1 = new Vector3(x, d1.y * radius, d1.x * radius);
                    Vector3 lip0 = new Vector3(x, d0.y * rimRadius, d0.x * rimRadius);
                    Vector3 lip1 = new Vector3(x, d1.y * rimRadius, d1.x * rimRadius);

                    AddTriangleOutward(vertices, tyreTriangles, outer0, lip0, outer1, inward);
                    AddTriangleOutward(vertices, tyreTriangles, lip0, lip1, outer1, inward);

                    // Rim lip: a solid metal ring just inside the tyre.
                    float rimX = x + sign * 0.016f;
                    float lipInner = rimRadius * 0.80f;

                    Vector3 rimOuter0 = new Vector3(rimX, d0.y * rimRadius, d0.x * rimRadius);
                    Vector3 rimOuter1 = new Vector3(rimX, d1.y * rimRadius, d1.x * rimRadius);
                    Vector3 rimInner0 = new Vector3(rimX, d0.y * lipInner, d0.x * lipInner);
                    Vector3 rimInner1 = new Vector3(rimX, d1.y * lipInner, d1.x * lipInner);

                    AddTriangleOutward(vertices, rimTriangles, rimOuter0, rimInner0, rimOuter1, inward);
                    AddTriangleOutward(vertices, rimTriangles, rimInner0, rimInner1, rimOuter1, inward);

                    // Brake disc, set deeper and dark, so the gaps between the spokes read as openings
                    // rather than as holes through the car.
                    float brakeX = x + sign * 0.055f;
                    Vector3 brakeCenter = new Vector3(brakeX, 0f, 0f);
                    Vector3 brake0 = new Vector3(brakeX, d0.y * lipInner, d0.x * lipInner);
                    Vector3 brake1 = new Vector3(brakeX, d1.y * lipInner, d1.x * lipInner);

                    AddTriangleOutward(vertices, tyreTriangles, brakeCenter, brake0, brake1, inward);
                }
            }

            AddSpokes(vertices, rimTriangles, halfWidth, rimRadius * 0.80f, radius * 0.16f);

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.subMeshCount = WheelSubmeshCount;
            mesh.SetTriangles(tyreTriangles, TyreSubmesh);
            mesh.SetTriangles(rimTriangles, RimSubmesh);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Five spokes and a centre hub on both faces of the wheel. Five is the count that reads as a
        /// muscle-car wheel; the dark brake disc sits behind, so the gaps between spokes look like
        /// openings onto the brake rather than holes straight through the wheel.
        /// </summary>
        private static void AddSpokes(
            List<Vector3> vertices,
            List<int> triangles,
            float halfWidth,
            float lipInner,
            float hubRadius)
        {
            const int spokeCount = 5;
            const float spokeHalfAngle = 0.30f;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                float x = (side == 0 ? halfWidth : -halfWidth) + sign * 0.022f;
                Vector3 inward = new Vector3(sign, 0f, 0f);
                Vector3 hubCenter = new Vector3(x, 0f, 0f);

                for (int i = 0; i < spokeCount; i++)
                {
                    float centreAngle = i / (float)spokeCount * Mathf.PI * 2f;
                    float a0 = centreAngle - spokeHalfAngle;
                    float a1 = centreAngle + spokeHalfAngle;

                    // Spokes taper towards the hub, which is what stops them looking like pie slices.
                    float hubSpread = spokeHalfAngle * 0.55f;
                    float h0 = centreAngle - hubSpread;
                    float h1 = centreAngle + hubSpread;

                    Vector3 outerA = new Vector3(x, Mathf.Sin(a0) * lipInner, Mathf.Cos(a0) * lipInner);
                    Vector3 outerB = new Vector3(x, Mathf.Sin(a1) * lipInner, Mathf.Cos(a1) * lipInner);
                    Vector3 innerA = new Vector3(x, Mathf.Sin(h0) * hubRadius, Mathf.Cos(h0) * hubRadius);
                    Vector3 innerB = new Vector3(x, Mathf.Sin(h1) * hubRadius, Mathf.Cos(h1) * hubRadius);

                    AddTriangleOutward(vertices, triangles, innerA, outerA, outerB, inward);
                    AddTriangleOutward(vertices, triangles, innerA, outerB, innerB, inward);
                }

                // Hub cap.
                const int hubSides = 10;
                for (int i = 0; i < hubSides; i++)
                {
                    float a0 = i / (float)hubSides * Mathf.PI * 2f;
                    float a1 = (i + 1) / (float)hubSides * Mathf.PI * 2f;

                    Vector3 p0 = new Vector3(x, Mathf.Sin(a0) * hubRadius, Mathf.Cos(a0) * hubRadius);
                    Vector3 p1 = new Vector3(x, Mathf.Sin(a1) * hubRadius, Mathf.Cos(a1) * hubRadius);

                    AddTriangleOutward(vertices, triangles, hubCenter, p0, p1, inward);
                }
            }
        }

        private static void AddCap(List<Vector3> vertices, List<int> triangles, Vector3[] ring, bool facingForward)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < ring.Length; i++)
            {
                center += ring[i];
            }

            center /= ring.Length;

            int centerIndex = vertices.Count;
            vertices.Add(center);

            int ringStart = vertices.Count;
            vertices.AddRange(ring);

            for (int i = 0; i < ring.Length; i++)
            {
                int next = (i + 1) % ring.Length;

                // The ring runs counter-clockwise seen from +Z, so the nose keeps that order and the
                // tail reverses it.
                triangles.Add(centerIndex);
                triangles.Add(ringStart + (facingForward ? i : next));
                triangles.Add(ringStart + (facingForward ? next : i));
            }
        }

        private static void AddPanel(
            List<Vector3> vertices,
            List<int> triangles,
            float z,
            float minX,
            float maxX,
            float minY,
            float maxY,
            bool facingForward)
        {
            var a = new Vector3(minX, minY, z);
            var b = new Vector3(maxX, minY, z);
            var c = new Vector3(maxX, maxY, z);
            var d = new Vector3(minX, maxY, z);

            var inward = new Vector3(0f, (minY + maxY) * 0.5f, facingForward ? z - 1f : z + 1f);

            AddTriangleOutward(vertices, triangles, a, b, c, inward);
            AddTriangleOutward(vertices, triangles, a, c, d, inward);
        }

        /// <summary>A short capped tube along Z, used for the tailpipes.</summary>
        private static void AddTube(
            List<Vector3> vertices,
            List<int> triangles,
            Vector3 mouth,
            float radius,
            float length,
            int sides)
        {
            Vector3 back = mouth;
            Vector3 front = mouth + new Vector3(0f, 0f, length);
            Vector3 axis = (back + front) * 0.5f;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector3 o0 = new Vector3(Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius, 0f);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius, 0f);

                Vector3 b0 = back + o0;
                Vector3 b1 = back + o1;
                Vector3 f0 = front + o0;
                Vector3 f1 = front + o1;

                Vector3 sideReference = new Vector3(axis.x, axis.y, (b0.z + f0.z) * 0.5f);
                AddTriangleOutward(vertices, triangles, b0, b1, f0, sideReference);
                AddTriangleOutward(vertices, triangles, b1, f1, f0, sideReference);

                // Mouth ring, so the pipe reads as hollow rather than a peg.
                AddTriangleOutward(vertices, triangles, back, b0, b1, front);
            }
        }

        private static void AddTriangleOutward(
            List<Vector3> vertices,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 inwardReference)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 0.0000001f)
            {
                return;
            }

            Vector3 centroid = (a + b + c) / 3f;
            if (Vector3.Dot(normal, centroid - inwardReference) < 0f)
            {
                (b, c) = (c, b);
            }

            int baseIndex = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }
}
