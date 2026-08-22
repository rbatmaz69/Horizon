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
                    centre, forward, outward, offset, feature.Name, Seed(feature.Name)));
            }

            return sites;
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
        /// </summary>
        public static void AddPadSamples(in FuelStationMeshes.StationSite site, List<Vector3> samples)
        {
            const float pitch = 4f;
            const float skirtLift = 0.35f;

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

                    at.y = site.Centre.y + (skirt ? skirtLift : 0f);
                    samples.Add(at);
                }
            }
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
