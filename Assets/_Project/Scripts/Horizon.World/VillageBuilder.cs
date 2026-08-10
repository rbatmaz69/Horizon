using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>What stands on a plot.</summary>
    public enum VillagePlotKind
    {
        House = 0,

        /// <summary>The village's landmark, and the only building visible from the pass road above.</summary>
        Windmill = 1,

        Barn = 2,
        Sawmill = 3,
    }

    /// <summary>
    /// Where everything in the village goes, worked out once and then read by several passes: the mesh
    /// builder puts geometry on it, the vegetation scatter keeps out of it, and the setup tool hangs a
    /// collider on each plot.
    ///
    /// One plan rather than each pass deciding for itself, because the three would drift apart and the
    /// result would be a hedge growing through a wall.
    /// </summary>
    public sealed class VillagePlan
    {
        public readonly struct Plot
        {
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly float HalfWidth;
            public readonly float HalfDepth;
            public readonly VillagePlotKind Kind;
            public readonly bool HasCar;
            public readonly bool Fenced;
            public readonly uint Seed;

            public Plot(Vector3 position, float yaw, float halfWidth, float halfDepth,
                VillagePlotKind kind, bool hasCar, bool fenced, uint seed)
            {
                Position = position;
                Yaw = yaw;
                HalfWidth = halfWidth;
                HalfDepth = halfDepth;
                Kind = kind;
                HasCar = hasCar;
                Fenced = fenced;
                Seed = seed;
            }

            /// <summary>Radius that covers the whole plot — garden and all.</summary>
            public float Radius => Mathf.Sqrt(HalfWidth * HalfWidth + HalfDepth * HalfDepth);

            /// <summary>
            /// Radius of the building alone, not of its garden.
            ///
            /// The distinction is what lets anything grow in the village. Blocking planting over the whole
            /// plot radius meant a 14.9 m circle per plot, and with plots 26 m apart those circles
            /// overlapped into one continuous bare strip down every street. A wall needs 8 m of clearance;
            /// a lawn needs none.
            /// </summary>
            public float BuildingRadius => Kind == VillagePlotKind.Windmill ? 11f
                : Kind == VillagePlotKind.Barn ? 9f
                : 7.5f;
        }

        private readonly List<Plot> plots;
        private readonly List<Vector3> lamps;
        private readonly List<float> lampYaws;

        internal VillagePlan(List<Plot> plots, List<Vector3> lamps, List<float> lampYaws, Bounds footprint)
        {
            this.plots = plots;
            this.lamps = lamps;
            this.lampYaws = lampYaws;
            Footprint = footprint;
        }

        public IReadOnlyList<Plot> Plots => plots;

        public IReadOnlyList<Vector3> Lamps => lamps;

        public IReadOnlyList<float> LampYaws => lampYaws;

        /// <summary>Plan bounds of everything in the village, for a cheap early-out.</summary>
        public Bounds Footprint { get; }

        public int HouseCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < plots.Count; i++)
                {
                    if (plots[i].Kind == VillagePlotKind.House)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>True if a point stands on a building, with <paramref name="margin"/> to spare.</summary>
        public bool IsBuiltOn(float x, float z, float margin)
        {
            return Within(x, z, margin, buildingOnly: true);
        }

        /// <summary>True if a point falls on a plot — building or garden.</summary>
        public bool IsOccupied(float x, float z, float margin)
        {
            return Within(x, z, margin, buildingOnly: false);
        }

        private bool Within(float x, float z, float margin, bool buildingOnly)
        {
            for (int i = 0; i < plots.Count; i++)
            {
                Plot plot = plots[i];
                float dx = plot.Position.x - x;
                float dz = plot.Position.z - z;
                float reach = (buildingOnly ? plot.BuildingRadius : plot.Radius) + margin;

                if (dx * dx + dz * dz <= reach * reach)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Counts from a village pass, for the build log.</summary>
    public sealed class VillageStats
    {
        public int Houses;
        public int Windmills;
        public int Barns;
        public int Sawmills;
        public int Fences;
        public int Lamps;
        public int Cars;
        public int Triangles;

        /// <summary>
        /// Faces that had to be turned round on the way into the buffer. Must be zero.
        ///
        /// Anything else means a helper in <see cref="BuildingMeshes"/> or <see cref="MillMeshes"/> is
        /// authoring its corners in an order that disagrees with the direction it says the face looks in.
        /// The mesh comes out right either way — this is the cheap tripwire that says which helper to go
        /// and read, and it is here because the village once shipped with every wall inside out.
        /// </summary>
        public int Flips;

        public readonly List<int> Submeshes = new List<int>(BuildingMeshes.SubmeshCount);

        public void Add(VillageStats other)
        {
            Houses += other.Houses;
            Windmills += other.Windmills;
            Barns += other.Barns;
            Sawmills += other.Sawmills;
            Fences += other.Fences;
            Lamps += other.Lamps;
            Cars += other.Cars;
            Triangles += other.Triangles;
            Flips += other.Flips;
        }
    }

    /// <summary>
    /// Lays out the village: where its lanes run and where its plots sit.
    ///
    /// Built in two stages on purpose. The lanes have to exist before <see cref="MountainField"/> is
    /// constructed, because they are what flattens the ground the village stands on; the plots can only
    /// be placed afterwards, because they need the finished terrain to sit on. So this class hands out a
    /// list of lane courses first and a <see cref="VillagePlan"/> second, and nothing in between has to
    /// know why.
    /// </summary>
    public static class VillageBuilder
    {
        /// <summary>
        /// The village street layout: two lanes running off the main road into the valley floor, joined
        /// at their far ends by a back lane.
        ///
        /// A loop rather than two dead ends, because a dead end reads as unfinished the moment a player
        /// drives down it, and because the back lane gives a second row of plots something to face.
        /// </summary>
        public static List<RoadCourse> LayOutLanes(IRoadPath main, in VillageShape shape)
        {
            var lanes = new List<RoadCourse>(3);
            if (main == null)
            {
                return lanes;
            }

            float side = Mathf.Sign(shape.LaneSide == 0f ? -1f : shape.LaneSide);

            Vector3 firstStart = LaneStart(main, shape, shape.FirstLaneAt, side, out float firstHeading);
            Vector3 secondStart = LaneStart(main, shape, shape.SecondLaneAt, side, out float secondHeading);

            // +90 times the side, not minus. With side = -1 the old sign turned the lanes to +90°, which
            // is straight back across the main carriageway and out over the mountain — the opposite of the
            // levelled valley floor they were meant to serve, and coplanar with the road the whole way.
            // ValidateVillageStreets exists because nothing caught that.
            float turnOff = 90f * side;

            RoadCourse first = StraightLane(firstStart, firstHeading + turnOff, shape.LaneLength, 0.4f);
            RoadCourse second = StraightLane(secondStart, secondHeading + turnOff, shape.LaneLength, -0.4f);
            lanes.Add(first);
            lanes.Add(second);

            // Endpoints read back out of the finished courses. Recomputing them from a heading vector is
            // what put 0.42 m and 0.98 m steps at the back lane's junctions: HeadingVector returns y = 0,
            // so the back lane inherited the *start* height of each branch and then ran dead flat.
            Vector3 firstEnd = LastPoint(first);
            Vector3 secondEnd = LastPoint(second);

            RoadCourse back = ConnectingLane(firstEnd, secondEnd);
            if (back != null)
            {
                lanes.Add(back);
            }

            // A middle rung, so the village is a block you can drive round rather than two dead ends with
            // a bar across the back. It leaves the first lane a third of the way along and meets the
            // second at the same fraction, so the two halves of the block are roughly equal.
            Vector3 midFirst = PointAlong(firstStart, firstEnd, 0.45f);
            Vector3 midSecond = PointAlong(secondStart, secondEnd, 0.45f);

            RoadCourse middle = ConnectingLane(midFirst, midSecond);
            if (middle != null)
            {
                lanes.Add(middle);
            }

            return lanes;
        }

        private static RoadCourse StraightLane(Vector3 start, float heading, float length, float grade)
        {
            var builder = new RoadCourseBuilder(start, heading);
            builder.Straight(length, grade);
            return builder.Build();
        }

        /// <summary>
        /// A lane between two known points, taking its grade from the actual height difference so both
        /// ends land flush on whatever they join.
        /// </summary>
        private static RoadCourse ConnectingLane(Vector3 from, Vector3 to)
        {
            Vector3 span = to - from;
            span.y = 0f;

            float length = span.magnitude;
            if (length < 1f)
            {
                return null;
            }

            float gradePercent = (to.y - from.y) / length * 100f;

            var builder = new RoadCourseBuilder(from, HeadingOf(span));
            builder.Straight(length, gradePercent);
            return builder.Build();
        }

        private static Vector3 LastPoint(RoadCourse course)
        {
            return course.ControlPoints[course.ControlPoints.Count - 1];
        }

        private static Vector3 PointAlong(Vector3 from, Vector3 to, float t)
        {
            return Vector3.Lerp(from, to, t);
        }

        /// <summary>
        /// The points the ground under the village has to be levelled to — a coarse grid over the whole
        /// footprint, handed to <see cref="MountainField"/> as level samples.
        ///
        /// The lanes alone are not enough, and it is worth being exact about why, because it looked like
        /// they would be. A road levels a ribbon <see cref="MountainField.Verge"/> = 24 m wide either
        /// side. Two lanes 140 m apart therefore level two 48 m strips and leave 92 m of untouched Perlin
        /// noise between them — measured, that came out at 22 m of relief and a 44 % maximum grade, which
        /// is a hillside, not a village. The fix is not more lanes nobody would drive down: it is to say
        /// plainly which area is meant to be flat.
        ///
        /// Pitch has to stay under twice the verge or the shelves do not merge and the floor comes out
        /// corrugated. Heights are taken from the main road at the matching distance, so the floor follows
        /// the road's 1.5 % climb instead of sitting level in a valley that is not.
        /// </summary>
        public static List<Vector3> BuildLevelSamples(IRoadPath main, in VillageShape shape)
        {
            var samples = new List<Vector3>(256);
            if (main == null)
            {
                return samples;
            }

            float side = Mathf.Sign(shape.LaneSide == 0f ? -1f : shape.LaneSide);

            // 30 m across against a 24 m verge leaves the shelves overlapping by 18 m; 8 m along is close
            // enough that the inverse-distance weighting never notices the gaps.
            const float acrossPitch = 30f;
            const float alongPitch = 8f;

            // Out to the far side of the lanes plus a margin, and a little onto the other side of the main
            // road so the frontage there is buildable too.
            float outer = shape.LaneLength + 25f;
            float inner = -35f;

            for (float along = shape.AlongStart - 20f; along <= shape.AlongEnd + 20f; along += alongPitch)
            {
                float clamped = Mathf.Clamp(along, 0f, main.Length);
                Vector3 centre = main.GetPositionAtDistance(clamped);
                Vector3 right = main.GetRightAtDistance(clamped);

                for (float across = inner; across <= outer; across += acrossPitch)
                {
                    Vector3 point = centre + right * (across * side);
                    samples.Add(new Vector3(point.x, centre.y, point.z));
                }
            }

            return samples;
        }

        /// <summary>
        /// Adds a path's own centreline to a level-sample list.
        ///
        /// The apron grid takes its heights from the *main* road at the matching distance, which is right
        /// for open ground and wrong under a lane: a lane runs its own grade, so 100 m out it sat almost a
        /// metre above the shelf the apron had laid, and the ribbon stood on a plinth with daylight under
        /// its edge. Sampling the lane itself makes the ground follow it exactly.
        /// </summary>
        public static void AddPathSamples(IRoadPath path, float spacing, List<Vector3> into)
        {
            if (path == null || into == null)
            {
                return;
            }

            int steps = Mathf.Max(2, Mathf.CeilToInt(path.Length / Mathf.Max(1f, spacing)) + 1);
            for (int i = 0; i < steps; i++)
            {
                into.Add(path.GetPositionAtDistance(path.Length * i / (steps - 1)));
            }
        }

        /// <summary>
        /// Where a lane's ribbon begins: out past the main road's shoulder, so the two carriageways do
        /// not overlap.
        ///
        /// Nothing in the project builds a junction — two ribbons simply interpenetrate — so the seam is
        /// pushed onto the flat shelf beside the road where it reads as a join in the surface rather than
        /// as a fold through the middle of the carriageway.
        /// </summary>
        private static Vector3 LaneStart(
            IRoadPath main,
            in VillageShape shape,
            float distance,
            float side,
            out float headingDegrees)
        {
            float clamped = Mathf.Clamp(distance, 0f, main.Length);

            Vector3 centre = main.GetPositionAtDistance(clamped);
            Vector3 forward = main.GetDirectionAtDistance(clamped);
            Vector3 right = main.GetRightAtDistance(clamped);

            headingDegrees = HeadingOf(forward);

            float across = (RoadShape.Default.OuterHalfWidth + shape.JunctionGap) * side;
            return centre + right * across;
        }

        /// <summary>
        /// Works out where every plot, lamp and parked car goes.
        ///
        /// Run once, after the terrain exists — plots are seated with
        /// <see cref="TerrainTileBuilder.SampleSurface"/> rather than with the raw height field, because
        /// the mesh is a 12 m linear interpolation of the field and a house placed against the field
        /// itself sinks a corner into the ground on any slope. The same trap the plants were in.
        /// </summary>
        public static VillagePlan Plan(
            IRoadPath main,
            IReadOnlyList<IRoadPath> lanes,
            MountainField field,
            in TerrainShape terrainShape,
            in VillageShape shape)
        {
            var plots = new List<VillagePlan.Plot>(64);
            var lamps = new List<Vector3>(16);
            var lampYaws = new List<float>(16);

            float side = Mathf.Sign(shape.LaneSide == 0f ? -1f : shape.LaneSide);

            // Frontage on the main road. Both sides: the level samples reach 35 m to the uphill side, so
            // there is flat ground for one row there too, and a village with houses on only one side of
            // its high street reads as a film set.
            AddFrontage(plots, main, field, terrainShape, shape, shape.AlongStart, shape.AlongEnd,
                1, true, true);

            for (int i = 0; lanes != null && i < lanes.Count; i++)
            {
                // Lanes start at the junction, so the first plot is pushed clear of the main road.
                AddFrontage(plots, lanes[i], field, terrainShape, shape,
                    shape.PlotSetback, lanes[i].Length - 6f, 2 + i, false, false);
            }

            // Lamps down the high street, on the village side only.
            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += shape.LampSpacing)
            {
                float clamped = Mathf.Clamp(along, 0f, main.Length);
                Vector3 centre = main.GetPositionAtDistance(clamped);
                Vector3 right = main.GetRightAtDistance(clamped);

                Vector3 at = centre + right * (RoadShape.Default.OuterHalfWidth + 1.6f) * side;
                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 point, out Vector3 _);

                lamps.Add(point);
                lampYaws.Add(HeadingOf(-right * side));
            }

            // Each frontage was laid out against its own street and knows nothing about the others, so a
            // plot fronting the high street can sit squarely in a lane that crosses behind it. Cheaper to
            // drop those here than to make every frontage aware of every street.
            ClearStreets(plots, main, lanes, shape.PlotSetback * 0.6f);

            var footprint = new Bounds(
                plots.Count > 0 ? plots[0].Position : Vector3.zero, Vector3.one);
            for (int i = 0; i < plots.Count; i++)
            {
                footprint.Encapsulate(new Bounds(plots[i].Position, Vector3.one * (plots[i].Radius * 2f)));
            }

            return new VillagePlan(plots, lamps, lampYaws, footprint);
        }

        /// <summary>
        /// Drops any plot standing too close to a street. A plot is always about
        /// <see cref="VillageShape.PlotSetback"/> from the street it faces, so a threshold well under that
        /// only catches the ones sitting on a *different* street.
        /// </summary>
        private static void ClearStreets(
            List<VillagePlan.Plot> plots,
            IRoadPath main,
            IReadOnlyList<IRoadPath> lanes,
            float minDistance)
        {
            for (int i = plots.Count - 1; i >= 0; i--)
            {
                Vector3 at = plots[i].Position;

                bool blocked = PlanDistance(main, at) < minDistance;
                for (int j = 0; !blocked && lanes != null && j < lanes.Count; j++)
                {
                    blocked = PlanDistance(lanes[j], at) < minDistance;
                }

                if (blocked)
                {
                    plots.RemoveAt(i);
                }
            }
        }

        /// <summary>Plan distance from a point to the nearest place on a path.</summary>
        private static float PlanDistance(IRoadPath path, Vector3 point)
        {
            const float step = 5f;
            float best = float.MaxValue;

            for (float along = 0f; along <= path.Length; along += step)
            {
                Vector3 at = path.GetPositionAtDistance(Mathf.Min(along, path.Length));

                float dx = at.x - point.x;
                float dz = at.z - point.z;
                best = Mathf.Min(best, dx * dx + dz * dz);
            }

            return Mathf.Sqrt(best);
        }

        /// <summary>Lines plots up along one street, on one or both sides.</summary>
        private static void AddFrontage(
            List<VillagePlan.Plot> plots,
            IRoadPath street,
            MountainField field,
            in TerrainShape terrainShape,
            in VillageShape shape,
            float from,
            float to,
            int streetId,
            bool allowMill,
            bool isMainStreet)
        {
            if (street == null || to <= from)
            {
                return;
            }

            float villageSide = Mathf.Sign(shape.LaneSide == 0f ? -1f : shape.LaneSide);
            int index = 0;

            for (float along = from; along <= to; along += shape.PlotSpacing, index++)
            {
                float clamped = Mathf.Clamp(along, 0f, street.Length);
                Vector3 centre = street.GetPositionAtDistance(clamped);
                Vector3 right = street.GetRightAtDistance(clamped);

                for (int s = 0; s < 2; s++)
                {
                    float sign = s == 0 ? -1f : 1f;

                    // The uphill side of the high street gets a shallower row, because the level samples
                    // only reach 35 m that way before the mountain takes over.
                    bool uphill = isMainStreet && Mathf.Approximately(sign, -villageSide);
                    float setback = uphill ? shape.PlotSetback * 0.85f : shape.PlotSetback;

                    // Decided before the vacancy roll, not after. The first version tested for the church
                    // further down and a plot could be left empty before it ever got there — the village
                    // came out with no church at all, and nothing said so.
                    // The mill is decided before the vacancy roll, not after. Testing for it further down
                    // meant a plot could be left empty before the test ever ran, and the village came out
                    // with no landmark at all and nothing saying so.
                    bool mill = allowMill
                                && Mathf.Abs(along - shape.MillAt) < shape.PlotSpacing * 0.5f
                                && Mathf.Approximately(sign, villageSide);

                    var random = new PlantRandom(Hash(streetId, index, s));
                    if (!mill && (random.Chance(shape.PlotVacancy) || (uphill && random.Chance(0.35f))))
                    {
                        continue;
                    }

                    var kind = VillagePlotKind.House;
                    if (mill)
                    {
                        kind = VillagePlotKind.Windmill;
                    }
                    else if (random.Chance(shape.WorkingBuildingChance))
                    {
                        // A working building rather than another house. Scattered through the village
                        // instead of pushed to one edge, because a barn between two cottages is what a
                        // village actually looks like.
                        kind = random.Chance(0.6f) ? VillagePlotKind.Barn : VillagePlotKind.Sawmill;
                    }

                    bool landmark = kind != VillagePlotKind.House;

                    Vector3 at = centre + right * (setback * sign)
                                 + street.GetDirectionAtDistance(clamped) * (landmark ? 0f : random.Range(-3f, 3f));

                    TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                        out Vector3 point, out Vector3 _);

                    float halfWidth = mill ? 10f : shape.PlotHalfWidth;
                    float halfDepth = mill ? 10f : shape.PlotDepth * 0.5f;

                    plots.Add(new VillagePlan.Plot(
                        point,
                        HeadingOf(-right * sign),
                        halfWidth,
                        halfDepth,
                        kind,
                        kind == VillagePlotKind.House && random.Chance(shape.ParkedCarChance),
                        kind == VillagePlotKind.House && random.Chance(0.55f),
                        random.NextSeed()));
                }
            }
        }

        /// <summary>
        /// Everything standing on one terrain tile, as a single mesh. Null when the tile holds no plots,
        /// which is almost all of them.
        /// </summary>
        public static Mesh BuildTile(
            TerrainTileKey key,
            in TerrainShape terrainShape,
            in VillageShape shape,
            VillagePlan plan,
            string meshName,
            out VillageStats stats)
        {
            stats = new VillageStats();
            if (plan == null)
            {
                return null;
            }

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            var buffer = new VegetationMeshBuffer(BuildingMeshes.SubmeshCount);

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                VillagePlan.Plot plot = plan.Plots[i];
                if (!Owns(plot.Position, originX, originZ, tileSize))
                {
                    continue;
                }

                var place = new PlantPlacement(plot.Position, Vector3.up,
                    plot.Yaw * Mathf.Deg2Rad, 1f, plot.Seed);
                var random = new PlantRandom(plot.Seed);

                switch (plot.Kind)
                {
                    case VillagePlotKind.Windmill:
                        MillMeshes.AddWindmill(buffer, place);
                        stats.Windmills++;
                        continue;

                    case VillagePlotKind.Barn:
                        MillMeshes.AddBarn(buffer, place);
                        stats.Barns++;
                        continue;

                    case VillagePlotKind.Sawmill:
                        MillMeshes.AddSawmill(buffer, place);
                        stats.Sawmills++;
                        continue;
                }

                BuildingMeshes.AddHouse(buffer, place);
                stats.Houses++;

                AddGarden(buffer, place, plot, shape, ref random, stats);

                if (plot.HasCar)
                {
                    var carPlace = new PlantPlacement(plot.Position, Vector3.up,
                        (plot.Yaw + random.Range(-10f, 10f)) * Mathf.Deg2Rad, 1f, random.NextSeed());

                    // Parked on the street side of the house, clear of the front door.
                    Vector3 offset = carPlace.Right * (plot.HalfWidth * 0.62f) + carPlace.Forward * 3.5f;
                    var parked = new PlantPlacement(plot.Position + offset, Vector3.up,
                        (plot.Yaw + 90f) * Mathf.Deg2Rad, 1f, random.NextSeed());

                    BuildingMeshes.AddParkedCar(buffer, parked);
                    stats.Cars++;
                }
            }

            for (int i = 0; i < plan.Lamps.Count; i++)
            {
                Vector3 at = plan.Lamps[i];
                if (!Owns(at, originX, originZ, tileSize))
                {
                    continue;
                }

                var place = new PlantPlacement(at, Vector3.up, plan.LampYaws[i] * Mathf.Deg2Rad, 1f,
                    Hash(99, i, 0));
                BuildingMeshes.AddStreetLamp(buffer, place);
                stats.Lamps++;
            }

            stats.Triangles = buffer.TriangleCount;
            stats.Flips = buffer.FlipCount;
            return buffer.ToMesh(meshName, stats.Submeshes);
        }

        /// <summary>A hedge or a fence around the front of a plot, and a couple of garden bushes.</summary>
        private static void AddGarden(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            in VillagePlan.Plot plot,
            in VillageShape shape,
            ref PlantRandom random,
            VillageStats stats)
        {
            float frontZ = plot.HalfDepth;
            float halfWidth = plot.HalfWidth * 0.92f;

            if (plot.Fenced)
            {
                BuildingMeshes.AddFence(buffer, place, 0f, frontZ, halfWidth, 0f, ref random);
                stats.Fences++;
            }
            else
            {
                BuildingMeshes.AddHedge(buffer, place, 0f, frontZ, halfWidth, 0f, ref random);
            }

            // Side boundaries, one or both, so plots read as separate gardens rather than one long lawn.
            if (random.Chance(0.75f))
            {
                BuildingMeshes.AddHedge(buffer, place, halfWidth, frontZ * 0.35f, 0f,
                    plot.HalfDepth * 0.6f, ref random);
            }

            if (random.Chance(0.75f))
            {
                BuildingMeshes.AddHedge(buffer, place, -halfWidth, frontZ * 0.35f, 0f,
                    plot.HalfDepth * 0.6f, ref random);
            }
        }

        /// <summary>Which tile a plot belongs to, decided by where it stands.</summary>
        private static bool Owns(Vector3 position, float originX, float originZ, float tileSize)
        {
            return position.x >= originX && position.x < originX + tileSize
                   && position.z >= originZ && position.z < originZ + tileSize;
        }

        /// <summary>FNV-1a with an avalanche, so a plot depends on nothing but which plot it is.</summary>
        private static uint Hash(int a, int b, int c)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)a) * 16777619u;
                hash = (hash ^ (uint)b) * 16777619u;
                hash = (hash ^ (uint)c) * 16777619u;

                hash ^= hash >> 15;
                hash *= 2246822519u;
                hash ^= hash >> 13;
                hash *= 3266489917u;
                hash ^= hash >> 16;
                return hash;
            }
        }

        /// <summary>Heading in the builder's convention: 0 faces +Z, increasing turns towards +X.</summary>
        private static float HeadingOf(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        private static Vector3 HeadingVector(float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }
    }
}
