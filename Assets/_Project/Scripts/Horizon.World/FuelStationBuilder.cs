using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Turns the <see cref="RoadFeatureKind.FuelStation"/> marks on a course into places, and then into
    /// meshes.
    ///
    /// <para><b>Two steps, and they happen at different times in the build.</b> <see cref="Sites"/> is
    /// pure arithmetic on the course and the path, so it can be called before the height field exists —
    /// which it has to be, because a forecourt needs level ground and levelling it means feeding samples
    /// into <c>MountainField</c>'s constructor. <see cref="Build"/> comes much later, once the terrain is
    /// there to sit the slab on. Keeping them apart is what lets one description of where the stations
    /// are serve both.</para>
    ///
    /// <para><b>One mesh per station rather than one per road</b>, unlike the guard rails and the
    /// delineator posts. Those get away with it because the road objects they belong to are given a
    /// hundred-kilometre chunk radius and never unload anyway. A station is a local thing: merged into a
    /// road-length mesh its bounds would span kilometres, the streamer would hold every forecourt in the
    /// world resident at once, and nothing would look wrong while it did.</para>
    /// </summary>
    public static class FuelStationBuilder
    {
        /// <summary>
        /// How far the forecourt's near edge sits from the edge of the carriageway, metres.
        ///
        /// <para>Not zero. The road's own shoulder falls away at <c>RoadShape.ShoulderDrop</c> and the
        /// terrain shelf below that, so a slab butted straight against the tarmac would either float over
        /// the verge or cut into it. The entry ramp spans this.</para>
        /// </summary>
        private const float VergeGap = 2f;

        /// <summary>How far before a station its advance sign stands, metres.</summary>
        private const float AdvanceDistance = 250f;

        /// <summary>
        /// And how far back it may be pushed when that spot is blocked.
        ///
        /// <para>600 and not 340, because the number has to be able to clear the thing most likely to
        /// be in the way. The Talbrücke Hochfeld is 320 m of viaduct and both eastern motorway stations
        /// sit 200 m past it, so a window that reached only 340 m back was entirely on the span and
        /// those two got no sign at all. A motorway service signed from before the viaduct it stands
        /// beyond is also simply what a real one looks like.</para>
        /// </summary>
        private const float AdvanceLimit = 600f;

        private const float AdvanceStep = 10f;

        /// <summary>
        /// Where the stations on one course stand.
        ///
        /// <para>Callable with nothing but the course and the path, deliberately — see the class
        /// remarks.</para>
        /// </summary>
        /// <param name="side">
        /// Which side this call is asking about: +1, −1, or 0 for every station on the course. The
        /// motorway is one course serving two carriageways, so each is built from the same table by
        /// asking for its own side; everything else asks for both and gets what it declared.
        /// </param>
        public static List<FuelStationMeshes.StationSite> Sites(
            IRoadPath path, RoadCourse course, in RoadShape roadShape, float side = 0f)
        {
            var sites = new List<FuelStationMeshes.StationSite>(4);

            if (path == null || course == null)
            {
                return sites;
            }

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];

                if (feature.Kind != RoadFeatureKind.FuelStation)
                {
                    continue;
                }

                if (side != 0f && !Mathf.Approximately(feature.Side, side))
                {
                    continue;
                }

                float at = Mathf.Clamp(feature.StartDistance, 0f, path.Length);

                Vector3 on = path.GetPositionAtDistance(at);
                Vector3 forward = path.GetDirectionAtDistance(at);
                Vector3 outward = path.GetRightAtDistance(at) * Mathf.Sign(feature.Side);

                // The slab's centre: out past the shoulder by the verge gap, then by its own half-depth.
                float offset = roadShape.OuterHalfWidth + VergeGap + FuelStationMeshes.ApronHalfDepth;

                Vector3 centre = on + outward * offset;

                // Height from the road rather than from the ground. The ground under this is about to be
                // levelled to the road's own line by the samples Sites feeds into MountainField, so the
                // road is the honest source — and asking the terrain would mean asking it about ground it
                // has not been told to flatten yet. The same argument BuildHarbour makes for its quay.
                centre.y = on.y;

                sites.Add(new FuelStationMeshes.StationSite(
                    centre, forward, outward, offset, feature.Name, Seed(feature.Name),
                    ResolveSign(path, course, roadShape, at, feature.Side)));
            }

            return sites;
        }

        /// <summary>
        /// Finds somewhere up the road to stand this station's advance sign.
        ///
        /// <para><b>The search only ever walks backwards.</b> From 250 m to 340 m, never closer — a
        /// sign that has had to move still gives at least the warning it promised, whereas one nudged
        /// forward to clear a portal would arrive after the turning it was announcing.</para>
        ///
        /// <para>It deliberately has <b>no keep-out against the delineator posts</b>, and that is worth
        /// saying because it is the first thing a reader worries about. The posts stand 45 cm off the
        /// shoulder and are a metre tall; this stands 3.5 m off it with its board starting 2.2 m up.
        /// They cannot touch, and a sign standing behind a run of posts is what a real one looks
        /// like.</para>
        ///
        /// <para>Water is the one hazard it cannot test. That needs <c>MountainField</c>, which does not
        /// exist yet when this runs — see the class remarks — so the caller vetoes afterwards.</para>
        /// </summary>
        private static FuelStationMeshes.AdvanceSign ResolveSign(
            IRoadPath path, RoadCourse course, in RoadShape roadShape, float stationAt, float side)
        {
            for (float back = AdvanceDistance; back <= AdvanceLimit; back += AdvanceStep)
            {
                float at = stationAt - back;

                if (at < 0f)
                {
                    return default;
                }

                // A little more margin than the posts take, because this is a larger object standing
                // further out: a sign at a portal mouth is inside the bore's own cut.
                if (course.IsBridged(at, 12f) || course.IsCoveredOrNear(at, 35f))
                {
                    continue;
                }

                // Not on another station's frontage, which is deliberately kept open.
                if (course.IsForecourt(at, 45f))
                {
                    continue;
                }

                // Square-on to a road that is turning under it is a sign aimed at the trees. The same
                // radius the delineator posts call open.
                if (path.GetRadiusAtDistance(at, 20f) < 90f)
                {
                    continue;
                }

                Vector3 on = path.GetPositionAtDistance(at);
                Vector3 forward = path.GetDirectionAtDistance(at);
                Vector3 outward = path.GetRightAtDistance(at) * Mathf.Sign(side);

                Vector3 foot = on
                               + outward * (roadShape.OuterHalfWidth
                                            + FuelStationMeshes.AdvanceSignStandoff);

                // Off the road's own line rather than off the terrain, for the reason SignBury exists.
                foot.y = on.y - roadShape.ShoulderDrop;

                return new FuelStationMeshes.AdvanceSign(foot, forward, outward, back);
            }

            return default;
        }

        /// <summary>
        /// One road, with the index that answers "how far is this point from it" quickly.
        ///
        /// <para>Paired because the two questions the pad has to ask need both halves:
        /// <see cref="RoadProximity"/> gives the distance and how far along, and only the path can then
        /// say how high the carriageway is there.</para>
        /// </summary>
        public readonly struct NearbyRoad
        {
            public readonly IRoadPath Path;
            public readonly RoadProximity Near;

            /// <summary>Half-width of the paved surface plus its verge, metres.</summary>
            public readonly float Corridor;

            public readonly string Name;

            public NearbyRoad(IRoadPath path, in RoadShape shape, string name)
            {
                Path = path;
                Near = new RoadProximity(path);
                Corridor = shape.OuterHalfWidth;
                Name = name;
            }
        }

        /// <summary>
        /// Level samples for one station's pad, in the shape <c>MountainField</c> takes them.
        ///
        /// <para>Flat across and level along, at the height of the carriageway beside it. A pad that
        /// followed the road's grade would be right for a long stretch and wrong here — a forecourt is
        /// poured level, and at 26 m each way even a 1.5 % grade is 40 cm of step across a slab.</para>
        ///
        /// <para>The ring of samples one pitch outside the slab is what stops the pad ending in a cliff:
        /// the field interpolates between what it is told, so a flattened square with untouched ground
        /// abutting it meets the hillside at a vertical face. Lifting the ring towards the natural ground
        /// gives the blend somewhere to happen. Same idea as <c>TownShape</c>'s skirt.</para>
        ///
        /// <para><b>No sample is ever placed inside a carriageway's corridor, and that is not a nicety.</b>
        /// The skirt ring reaches back to within 4.75 m of the station's own centreline — inside the
        /// 6.75 m the road actually occupies — and it is lifted 35 cm. So every station was pushing a
        /// 28 cm lip of terrain up through the edge of its own asphalt, which the clearance check
        /// reported and which is exactly the kind of number that gets read as noise. A road's own shelf
        /// has to win wherever there is a road; everywhere else the pad may say what it likes.</para>
        /// </summary>
        public static void AddPadSamples(
            in FuelStationMeshes.StationSite site,
            List<Vector3> samples,
            IReadOnlyList<NearbyRoad> roads = null)
        {
            const float pitch = 4f;
            const float skirtLift = 0.35f;

            // A little wider than the paved surface, because the terrain builder drops a shelf either
            // side of it and a sample landing on that shelf fights with it just as one on the asphalt
            // would.
            const float corridorMargin = 3f;

            float halfLength = FuelStationMeshes.ApronHalfLength + pitch;
            float halfDepth = FuelStationMeshes.ApronHalfDepth + pitch;

            int alongSteps = Mathf.CeilToInt(halfLength / pitch);
            int acrossSteps = Mathf.CeilToInt(halfDepth / pitch);

            for (int a = -alongSteps; a <= alongSteps; a++)
            {
                for (int d = -acrossSteps; d <= acrossSteps; d++)
                {
                    bool skirt = Mathf.Abs(a) == alongSteps || Mathf.Abs(d) == acrossSteps;

                    Vector3 at = site.Centre
                                 + site.Forward * (a * pitch)
                                 + site.Outward * (d * pitch);

                    if (OnAnyRoad(roads, at, corridorMargin))
                    {
                        continue;
                    }

                    at.y = site.Centre.y + (skirt ? skirtLift : 0f);
                    samples.Add(at);
                }
            }
        }

        private static bool OnAnyRoad(IReadOnlyList<NearbyRoad> roads, Vector3 at, float margin)
        {
            for (int i = 0; roads != null && i < roads.Count; i++)
            {
                roads[i].Near.Nearest(at.x, at.z, out float distance, out float _);

                if (distance <= roads[i].Corridor + margin)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether this station's pad would bury a road, and which one.
        ///
        /// <para><b>The failure this exists for is a mountain with a road inside it.</b> A pad is
        /// levelled at its own carriageway's height, and on a switchback stack the leg below can pass
        /// within forty metres in plan and fifteen below in height — so a 60 by 42 metre platform laid
        /// at the summit drops fifteen metres of rock straight through the road underneath it. Nothing
        /// in the build says so in a way anyone reads: the road clearance check reports a number among
        /// eighty others, the terrain comes out perfectly formed, and the first sign is a wall across
        /// the carriageway on the way down.</para>
        ///
        /// <para>Skipping the samples over the road is not a fix for this — it leaves the road running
        /// through a slot with fifteen-metre walls a few metres either side. The only fix is to put the
        /// station somewhere else, so this reports rather than repairs.</para>
        /// </summary>
        /// <param name="drop">How far below the pad a road may sit before it counts as buried, metres.</param>
        /// <param name="reach">
        /// How far past the pad's own edge its samples still pull the ground, metres.
        ///
        /// <para>Not a fudge factor and not optional: <c>MountainField</c> says a level sample "behaves
        /// exactly like a road sample — the shelf forms around it", and that shelf is a whole
        /// <c>TerrainShape.VergeWidth</c> wide. So a pad reaches a good twenty-four metres further than
        /// its own rectangle in every direction, which is exactly the mistake the first version of this
        /// check made: it tested the rectangle, found the pass eight metres outside it, and reported
        /// nothing while eleven metres of rock stood in the road.</para>
        /// </param>
        public static bool PadBuriesRoad(
            in FuelStationMeshes.StationSite site,
            IReadOnlyList<NearbyRoad> roads,
            float drop,
            float reach,
            out string what,
            out float worst)
        {
            const float step = 4f;

            what = null;
            worst = 0f;

            float halfLength = FuelStationMeshes.ApronHalfLength + step + reach;
            float halfDepth = FuelStationMeshes.ApronHalfDepth + step + reach;

            for (int i = 0; roads != null && i < roads.Count; i++)
            {
                NearbyRoad road = roads[i];

                for (float at = 0f; at <= road.Path.Length; at += step)
                {
                    Vector3 on = road.Path.GetPositionAtDistance(at);

                    Vector3 offset = on - site.Centre;
                    float along = offset.x * site.Forward.x + offset.z * site.Forward.z;
                    float across = offset.x * site.Outward.x + offset.z * site.Outward.z;

                    if (Mathf.Abs(along) > halfLength || Mathf.Abs(across) > halfDepth)
                    {
                        continue;
                    }

                    float below = site.Centre.y - on.y;
                    if (below > drop && below > worst)
                    {
                        worst = below;
                        what = $"{road.Name} at {at:0} m along it";
                    }
                }
            }

            return what != null;
        }

        /// <summary>
        /// One station's geometry, and which submeshes survived into it.
        ///
        /// <para>The caller needs that second answer and cannot work it out afterwards: <c>ToMesh</c>
        /// drops empty submeshes, so the lit slot's index in the finished renderer is wherever it landed
        /// and not its constant. Registering the constant with <c>TownLights</c> would light whichever
        /// material happened to occupy that slot instead — a wrong colour after dusk, and nothing at all
        /// before it.</para>
        ///
        /// <para>Returns null only if the buffer came back empty, which would mean
        /// <c>FuelStationMeshes</c> emitted nothing. That is a bug rather than a state, but the caller
        /// reports it the way every other builder here reports an empty result.</para>
        /// </summary>
        public static Mesh Build(
            in FuelStationMeshes.StationSite site, string meshName, List<int> usedSubmeshes)
        {
            var buffer = new VegetationMeshBuffer(FuelStationMeshes.SubmeshCount);

            FuelStationMeshes.AddStation(buffer, site);
            buffer.MergeTinted(FuelStationMeshes.Tints());

            return buffer.ToMesh(meshName, usedSubmeshes);
        }

        /// <summary>
        /// The advance sign's own mesh, or null where this station has none.
        ///
        /// <para>Its own mesh and its own object rather than part of the station's, because the station
        /// gets a <c>MeshCollider</c> and this must not. Every other piece of roadside furniture in the
        /// world is pass-through — guard rails, delineator posts, trees — and a signpost with a collider
        /// would be the one solid thing standing on a verge a car can otherwise drive across.</para>
        /// </summary>
        public static Mesh BuildAdvanceSign(
            in FuelStationMeshes.StationSite site, string meshName, List<int> usedSubmeshes)
        {
            if (!site.Sign.Exists)
            {
                return null;
            }

            var buffer = new VegetationMeshBuffer(FuelStationMeshes.SubmeshCount);

            FuelStationMeshes.AddAdvanceSign(buffer, site.Sign);
            buffer.MergeTinted(FuelStationMeshes.Tints());

            return buffer.ToMesh(meshName, usedSubmeshes);
        }

        /// <summary>
        /// A stable seed from the station's name.
        ///
        /// <para>From the name and not from a counter, so that adding a station on the pass does not
        /// reshuffle the one on the coast — the same reproducibility rule the whole world scatter is
        /// built on. FNV-1a because it is four lines and does not need to be good, only fixed.</para>
        /// </summary>
        private static uint Seed(string name)
        {
            uint hash = 2166136261u;

            for (int i = 0; name != null && i < name.Length; i++)
            {
                hash = (hash ^ name[i]) * 16777619u;
            }

            return hash;
        }
    }
}
