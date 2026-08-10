using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>What stands on a plot.</summary>
    public enum TownPlotKind
    {
        House = 0,

        /// <summary>The town's landmark, and the only building visible from the pass road above.</summary>
        Windmill = 1,

        Barn = 2,
        Sawmill = 3,
    }

    /// <summary>
    /// Where everything in the town goes, worked out once and then read by several passes: the mesh
    /// builder puts geometry on it, the vegetation scatter keeps out of it, and the setup tool hangs a
    /// collider on each plot.
    ///
    /// One plan rather than each pass deciding for itself, because the three would drift apart and the
    /// result would be a hedge growing through a wall.
    /// </summary>
    public sealed class TownPlan
    {
        public readonly struct Plot
        {
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly float HalfWidth;
            public readonly float HalfDepth;
            public readonly TownPlotKind Kind;
            public readonly bool HasCar;
            public readonly bool Fenced;
            public readonly uint Seed;

            public Plot(Vector3 position, float yaw, float halfWidth, float halfDepth,
                TownPlotKind kind, bool hasCar, bool fenced, uint seed)
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
            /// The distinction is what lets anything grow in the town. Blocking planting over the whole
            /// plot radius meant a 14.9 m circle per plot, and with plots 26 m apart those circles
            /// overlapped into one continuous bare strip down every street. A wall needs 8 m of clearance;
            /// a lawn needs none.
            /// </summary>
            public float BuildingRadius => Kind == TownPlotKind.Windmill ? 11f
                : Kind == TownPlotKind.Barn ? 9f
                : 7.5f;
        }

        private readonly List<Plot> plots;
        private readonly List<Vector3> lamps;
        private readonly List<float> lampYaws;

        internal TownPlan(List<Plot> plots, List<Vector3> lamps, List<float> lampYaws, Bounds footprint)
        {
            this.plots = plots;
            this.lamps = lamps;
            this.lampYaws = lampYaws;
            Footprint = footprint;
        }

        public IReadOnlyList<Plot> Plots => plots;

        public IReadOnlyList<Vector3> Lamps => lamps;

        public IReadOnlyList<float> LampYaws => lampYaws;

        /// <summary>Plan bounds of everything in the town, for a cheap early-out.</summary>
        public Bounds Footprint { get; }

        public int HouseCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < plots.Count; i++)
                {
                    if (plots[i].Kind == TownPlotKind.House)
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

    /// <summary>Counts from a town pass, for the build log.</summary>
    public sealed class TownStats
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
        /// and read, and it is here because the town once shipped with every wall inside out.
        /// </summary>
        public int Flips;

        public readonly List<int> Submeshes = new List<int>(BuildingMeshes.SubmeshCount);

        public void Add(TownStats other)
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
    /// Works out what stands where in the town: a plot for every frontage along every street.
    ///
    /// <para>Runs after <see cref="MountainField"/> and after the street network, because a plot needs
    /// both — the streets to face and the finished terrain to stand on. The streets themselves come from
    /// <see cref="StreetNetwork"/> now; this used to lay out two lanes of its own, and the difference
    /// between two lanes and a graph is most of the difference between a village and a town.</para>
    ///
    /// <para><b>Transitional.</b> Plots are still ranged along each street independently at a fixed
    /// spacing, which is why the corner keep-outs are crude and why nothing here knows what a block is.
    /// Block-based parcelling replaces the lot.</para>
    /// </summary>
    public static class TownPlanner
    {
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
        /// Works out where every plot, lamp and parked car goes.
        ///
        /// Run once, after the terrain exists — plots are seated with
        /// <see cref="TerrainTileBuilder.SampleSurface"/> rather than with the raw height field, because
        /// the mesh is a 12 m linear interpolation of the field and a house placed against the field
        /// itself sinks a corner into the ground on any slope. The same trap the plants were in.
        /// </summary>
        public static TownPlan Plan(
            StreetNetwork network,
            StreetIndex index,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape shape,
            IRoadPath trunk)
        {
            var plots = new List<TownPlan.Plot>(256);
            var lamps = new List<Vector3>(32);
            var lampYaws = new List<float>(32);

            float side = shape.Side;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                // From one junction pad to the other, with a corner keep-out at each end so nothing
                // stands in a sight line — and so two streets' frontages cannot collide at a corner.
                float keepOut = edge.HalfOuter + 4f;

                AddFrontage(
                    plots, edge.Path, field, terrainShape, shape,
                    edge.TrimStart + keepOut, edge.Length - edge.TrimEnd - keepOut,
                    i, allowMill: edge.Quarter == TownQuarter.Industry, setback: SetbackFor(edge, shape));
            }

            // Lamps down the trunk road through the town, on the town side only. The streets get their
            // own when there is a night to see them in.
            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += shape.LampSpacing)
            {
                float clamped = Mathf.Clamp(along, 0f, trunk.Length);
                Vector3 centre = trunk.GetPositionAtDistance(clamped);
                Vector3 right = trunk.GetRightAtDistance(clamped);

                Vector3 at = centre + right * (RoadShape.Default.OuterHalfWidth + 1.6f) * side;
                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 point, out Vector3 _);

                lamps.Add(point);
                lampYaws.Add(HeadingOf(-right * side));
            }

            // Each frontage was laid out against its own street and knows nothing about the others, so a
            // plot fronting one street can sit squarely in another that crosses behind it. Cheaper to
            // drop those here than to make every frontage aware of every street — and with the spatial
            // index it is one cell lookup per plot rather than a walk down every centreline.
            ClearStreets(plots, index, trunk, shape);

            var footprint = new Bounds(
                plots.Count > 0 ? plots[0].Position : Vector3.zero, Vector3.one);
            for (int i = 0; i < plots.Count; i++)
            {
                footprint.Encapsulate(new Bounds(plots[i].Position, Vector3.one * (plots[i].Radius * 2f)));
            }

            return new TownPlan(plots, lamps, lampYaws, footprint);
        }

        /// <summary>
        /// How far back from a street's centreline its houses stand.
        ///
        /// Scaled off the street's own width, so a house on an alley is not marooned in the middle of a
        /// field while one on the high street is in the gutter. The margin past the kerb is what has to
        /// stay constant, not the distance from the centreline — that was the mistake the village made
        /// with a single number for every lane.
        /// </summary>
        private static float SetbackFor(StreetEdge edge, in TownShape shape)
        {
            return edge.HalfOuter + shape.PlotSetback;
        }

        /// <summary>
        /// Drops any plot standing in a street it does not face.
        ///
        /// <para>A plot is always at least its street's setback from the street it fronts, so a threshold
        /// well under that only catches the ones sitting on a <i>different</i> street — which happens all
        /// the time when frontages are laid out one street at a time and forty of them cross.</para>
        ///
        /// <para>Through <see cref="StreetIndex"/> rather than by walking every centreline. The old
        /// version was plots times streets times samples-per-street: at four hundred plots and forty
        /// streets that is a million arc-length lookups, and it was comfortably the most expensive thing
        /// in the build.</para>
        /// </summary>
        private static void ClearStreets(
            List<TownPlan.Plot> plots, StreetIndex index, IRoadPath trunk, in TownShape shape)
        {
            float trunkClearance = RoadShape.Default.OuterHalfWidth + 6f;

            for (int i = plots.Count - 1; i >= 0; i--)
            {
                Vector3 at = plots[i].Position;

                // Its own street is further away than this, so anything inside it is a street the plot
                // does not face.
                float minimum = shape.PlotSetback * 0.7f;

                bool blocked = index.IsWithin(at.x, at.z, minimum)
                               || PlanDistance(trunk, at) < trunkClearance;

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

        /// <summary>Lines plots up along one street, on both sides.</summary>
        private static void AddFrontage(
            List<TownPlan.Plot> plots,
            IRoadPath street,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape shape,
            float from,
            float to,
            int streetId,
            bool allowMill,
            float setback)
        {
            if (street == null || to <= from)
            {
                return;
            }

            int index = 0;

            for (float along = from; along <= to; along += shape.PlotSpacing, index++)
            {
                float clamped = Mathf.Clamp(along, 0f, street.Length);
                Vector3 centre = street.GetPositionAtDistance(clamped);
                Vector3 right = street.GetRightAtDistance(clamped);

                for (int s = 0; s < 2; s++)
                {
                    float sign = s == 0 ? -1f : 1f;

                    // The mill is decided before the vacancy roll, not after. Testing for it further down
                    // meant a plot could be left empty before the test ever ran, and the town came out
                    // with no landmark at all and nothing saying so.
                    bool mill = allowMill
                                && Mathf.Abs(along - (from + to) * 0.5f) < shape.PlotSpacing * 0.5f
                                && s == 0;

                    var random = new PlantRandom(Hash(streetId, index, s));
                    if (!mill && random.Chance(shape.PlotVacancy))
                    {
                        continue;
                    }

                    var kind = TownPlotKind.House;
                    if (mill)
                    {
                        kind = TownPlotKind.Windmill;
                    }
                    else if (random.Chance(shape.WorkingBuildingChance))
                    {
                        // A working building rather than another house. Scattered through the town
                        // instead of pushed to one edge, because a barn between two cottages is what a
                        // town actually looks like.
                        kind = random.Chance(0.6f) ? TownPlotKind.Barn : TownPlotKind.Sawmill;
                    }

                    bool landmark = kind != TownPlotKind.House;

                    Vector3 at = centre + right * (setback * sign)
                                 + street.GetDirectionAtDistance(clamped) * (landmark ? 0f : random.Range(-3f, 3f));

                    TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                        out Vector3 point, out Vector3 _);

                    float halfWidth = mill ? 10f : shape.PlotHalfWidth;
                    float halfDepth = mill ? 10f : shape.PlotDepth * 0.5f;

                    plots.Add(new TownPlan.Plot(
                        point,
                        HeadingOf(-right * sign),
                        halfWidth,
                        halfDepth,
                        kind,
                        kind == TownPlotKind.House && random.Chance(shape.ParkedCarChance),
                        kind == TownPlotKind.House && random.Chance(0.55f),
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
            in TownShape shape,
            TownPlan plan,
            string meshName,
            out TownStats stats)
        {
            stats = new TownStats();
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
                TownPlan.Plot plot = plan.Plots[i];
                if (!Owns(plot.Position, originX, originZ, tileSize))
                {
                    continue;
                }

                var place = new PlantPlacement(plot.Position, Vector3.up,
                    plot.Yaw * Mathf.Deg2Rad, 1f, plot.Seed);
                var random = new PlantRandom(plot.Seed);

                switch (plot.Kind)
                {
                    case TownPlotKind.Windmill:
                        MillMeshes.AddWindmill(buffer, place);
                        stats.Windmills++;
                        continue;

                    case TownPlotKind.Barn:
                        MillMeshes.AddBarn(buffer, place);
                        stats.Barns++;
                        continue;

                    case TownPlotKind.Sawmill:
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
            in TownPlan.Plot plot,
            in TownShape shape,
            ref PlantRandom random,
            TownStats stats)
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
    }
}
