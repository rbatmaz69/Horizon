using System.Collections.Generic;
using Horizon.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Checks the baked traffic routes against the world they were baked from.
    ///
    /// <para>Every one of these is here for the same reason the street network's validators are: a wrong
    /// route produces a picture that looks like traffic. A lane half a metre outside its carriageway is a
    /// car driving down the pavement, which from the driver's seat at thirty metres is a car; a connector
    /// that misses the lane it feeds is a car that jumps sideways once per junction, which reads as a
    /// dropped frame. None of it shows up in a screenshot and all of it shows up on the road.</para>
    ///
    /// <list type="bullet">
    /// <item>Every street lane sample sits within its street's half-width of the centreline.</item>
    /// <item>Every trunk lane sample sits within the trunk road's half-width of its centreline.</item>
    /// <item>Every connector is flush with the lanes it joins, to 5 cm.</item>
    /// <item>Every lane can be left — no route strands a car.</item>
    /// <item>No connector passes within a metre of a building.</item>
    /// </list>
    /// </summary>
    public static class TrafficRouteValidator
    {
        private const string WorldScenePath = "Assets/_Project/Scenes/World_MountainPass.unity";
        private const string RoutesPath = "Assets/_Project/Art/Models/Generated/TrafficNetwork.asset";

        /// <summary>How far a connector end may sit from the lane it joins, metres.</summary>
        private const float FlushTolerance = 0.05f;

        /// <summary>How close a connector may pass to a building, metres.</summary>
        private const float BuildingClearance = 1f;

        [MenuItem("Tools/Horizon/Validate Traffic Routes", priority = 44)]
        public static void Validate()
        {
            var routes = AssetDatabase.LoadAssetAtPath<TrafficNetwork>(RoutesPath);
            if (routes == null)
            {
                Debug.LogError($"[Horizon] No traffic routes at {RoutesPath}. Run Rebuild Prototype "
                               + "Scene first.");
                return;
            }

            Scene scene = SceneManager.GetSceneByPath(WorldScenePath);
            bool openedHere = !scene.isLoaded;

            if (openedHere)
            {
                scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Additive);
            }

            GameObject scratch = null;

            try
            {
                StreetNetwork[] streets = RebuildNetwork(
                    out scratch, out RoadPath trunk, out RoadPath[] trunkRoads);

                CheckLanesFollowTheirStreets(routes, streets);
                CheckLanesFollowTheTrunkRoad(routes, trunkRoads);
                CheckConnectorsAreFlush(routes);
                CheckNothingIsStranded(routes);
                CheckConnectorsClearBuildings(routes);
                CheckSignalsAreOnApproaches(routes);
            }
            finally
            {
                if (scratch != null)
                {
                    Object.DestroyImmediate(scratch);
                }

                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>
        /// The signals are on the lanes that stop at them, and every controlled junction has both phases.
        ///
        /// <para>Three faults, none of which throws and none of which is visible in a screenshot. A
        /// signal on a <b>connector</b> would stop a car in the middle of a junction rather than at the
        /// line in front of it — the director only consults the phase on a driven lane, so today it
        /// would be quietly ignored and would become a stopped car the day that condition is relaxed. A
        /// junction whose approaches are <b>all on one group</b> spends half of every cycle red for
        /// everybody. And a group number <b>past the end of the table</b> is a lane waiting on a phase
        /// that never comes round.</para>
        /// </summary>
        private static void CheckSignalsAreOnApproaches(TrafficNetwork routes)
        {
            int groups = routes.SignalGroupCount;
            if (groups <= 0)
            {
                Debug.Log("[Horizon] Traffic signals: none in this bake.");
                return;
            }

            int onConnectors = 0;
            int outOfRange = 0;
            int controlled = 0;

            // Which groups arrive at each junction, as a bitmask — two bits is the whole of what has to
            // be counted, so a mask beats a set.
            var arriving = new int[routes.NodeCount];

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                int group = routes.SignalOf(lane);
                if (group < 0)
                {
                    continue;
                }

                controlled++;

                if (routes.NodeOf(lane) >= 0)
                {
                    onConnectors++;
                    continue;
                }

                if (group >= groups)
                {
                    outOfRange++;
                    continue;
                }

                for (int i = 0; i < routes.ExitCount(lane); i++)
                {
                    int node = routes.NodeOf(routes.ExitAt(lane, i));
                    if (node >= 0 && node < arriving.Length)
                    {
                        arriving[node] |= 1 << group;
                    }
                }
            }

            int junctions = 0;
            int onePhase = 0;

            for (int node = 0; node < arriving.Length; node++)
            {
                if (arriving[node] == 0)
                {
                    continue;
                }

                junctions++;

                // A power of two is a single bit, which is a junction with only one phase arriving.
                if ((arriving[node] & (arriving[node] - 1)) == 0)
                {
                    onePhase++;
                }
            }

            if (onConnectors > 0)
            {
                Debug.LogError($"[Horizon] Traffic signals: {onConnectors} connector(s) carry a phase. A "
                               + "signal belongs on the lane that stops at it, never on the turn through "
                               + "the junction — see TrafficNetworkBuilder.AddStreetLanes.");
            }

            if (outOfRange > 0)
            {
                Debug.LogError($"[Horizon] Traffic signals: {outOfRange} lane(s) wait on a phase group "
                               + $"that does not exist. The network has {groups}.");
            }

            if (onePhase > 0)
            {
                Debug.LogError($"[Horizon] Traffic signals: {onePhase} junction(s) have only one phase "
                               + "arriving, so they are red for everybody for half of every cycle. See "
                               + "TrafficSignalPlan.Split, which is supposed to drop those.");
            }

            if (onConnectors == 0 && outOfRange == 0 && onePhase == 0)
            {
                Debug.Log($"[Horizon] Traffic signals: {junctions} junction(s), {controlled} controlled "
                          + $"lane(s), {groups} phase groups — all on approaches, all two-phase.");
            }
        }

        /// <summary>
        /// Every street lane sample is within its own street's half-width of a centreline.
        ///
        /// <para>Measured against the street graph rebuilt from the layout table rather than against the
        /// scene's <c>RoadPath</c> objects, for the reason the world preview rebuilds the course: both
        /// come from the same deterministic table, so they cannot disagree unless someone has hand-edited
        /// generated output — which this project's conventions say not to do.</para>
        ///
        /// <para>The check that matters is the <i>upper</i> bound. A lane inside the carriageway is fine;
        /// a lane outside it is a car on the footway.</para>
        ///
        /// <para><b>Against every settlement, and each sample against whichever is nearer.</b> This took
        /// one street graph while there was one town, and kept taking one after Hochstadt was added — so
        /// the city's hundred and twenty lanes were being measured against Talheim's streets, five
        /// kilometres away, and every one of them reported as a car in a field. Six and a half thousand
        /// samples of correct road, reported as faults, which is exactly how a check stops being
        /// read.</para>
        /// </summary>
        private static void CheckLanesFollowTheirStreets(
            TrafficNetwork routes, StreetNetwork[] towns)
        {
            var indices = new StreetIndex[towns.Length];
            for (int i = 0; i < towns.Length; i++)
            {
                indices[i] = new StreetIndex(towns[i], 2f, 16f);
            }

            float worst = 0f;
            int worstLane = -1;
            int outside = 0;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                // Street lanes only. A lane down the trunk road is legitimately hundreds of metres from
                // the nearest town street, and measured against this index every one of them would be
                // reported as a car on the pavement — which is how a check stops being read.
                if (routes.KindOf(lane) != TrafficLaneKind.Street)
                {
                    continue;
                }

                for (int i = 0; i < routes.SampleCount(lane); i++)
                {
                    Vector3 at = routes.SampleAt(lane, i);

                    // The nearest street in any town. A lane belongs to exactly one of them and is
                    // metres from it, so "nearest" and "its own" are the same answer — and finding it
                    // this way needs no assumption about which town's lanes were emitted first.
                    float over = float.MaxValue;

                    for (int town = 0; town < indices.Length; town++)
                    {
                        float distance = indices[town].DistanceTo(at.x, at.z, out int edge);
                        if (edge < 0)
                        {
                            continue;
                        }

                        // Against the carriageway, not the paved width: a lane on the pavement is the
                        // thing being looked for, and a half-outer bound would let one through.
                        over = Mathf.Min(over, distance - towns[town].Edges[edge].HalfWidth);
                    }

                    if (over == float.MaxValue)
                    {
                        continue;
                    }

                    if (over > 0f)
                    {
                        outside++;
                    }

                    if (over > worst)
                    {
                        worst = over;
                        worstLane = lane;
                    }
                }
            }

            if (outside == 0)
            {
                Debug.Log("[Horizon] Traffic routes: every lane sample is inside its own carriageway.");
                return;
            }

            Debug.LogWarning($"[Horizon] Traffic routes: {outside} lane sample(s) fall outside their "
                             + $"carriageway, worst {worst:0.00} m on lane {worstLane}. A lane is offset "
                             + "half a half-width from the centreline, so this means the street it was "
                             + "baked from is narrower than the one it is being measured against.");
        }

        /// <summary>
        /// Every trunk lane sample sits on the trunk road's carriageway.
        ///
        /// <para>The pass doubles back on itself twelve times, so "distance to the nearest point of the
        /// centreline" is the only question with a defensible answer — an inverse projection would have
        /// several and no way to choose. Asking it outright for every sample is five thousand walks of a
        /// seven-kilometre path, so the search is <b>windowed</b>: a lane runs along the road, so the
        /// answer for one sample is a few metres from the answer for the last one. A window that comes
        /// back with something implausible falls through to the full sweep rather than reporting the
        /// wrong stretch of road — which is the failure a windowed search has available to it, and it
        /// would land on a hairpin, where being wrong matters most.</para>
        ///
        /// <para>The bound is the carriageway's half-width, not the paved width including shoulders. A
        /// lane on the gravel is the thing being looked for.</para>
        /// </summary>
        private static void CheckLanesFollowTheTrunkRoad(
            TrafficNetwork routes, IReadOnlyList<RoadPath> trunks)
        {
            RoadShape shape = RoadShape.Default;

            float worst = 0f;
            int worstLane = -1;
            int outside = 0;
            int lanes = 0;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.KindOf(lane) != TrafficLaneKind.Trunk)
                {
                    continue;
                }

                lanes++;

                // One cached cursor per road, reset at the start of every lane. NearestApproach walks
                // forward from the last answer, and a cursor left where another lane finished sends the
                // first sample of this one off looking at the wrong end of the road.
                var near = new float[trunks.Count];
                for (int p = 0; p < near.Length; p++)
                {
                    near[p] = -1f;
                }

                for (int i = 0; i < routes.SampleCount(lane); i++)
                {
                    Vector3 at = routes.SampleAt(lane, i);

                    // The nearest carriageway, not a nominated one: a lane belongs to whichever road it
                    // was baked onto, and this file has no way of knowing which that was.
                    float over = float.MaxValue;
                    for (int p = 0; p < trunks.Count; p++)
                    {
                        over = Mathf.Min(over, NearestApproach(trunks[p], at, ref near[p]));
                    }

                    over -= shape.HalfWidth;

                    if (over > 0f)
                    {
                        outside++;
                    }

                    if (over > worst)
                    {
                        worst = over;
                        worstLane = lane;
                    }
                }
            }

            if (lanes == 0)
            {
                Debug.LogWarning("[Horizon] Traffic routes: not one lane on the trunk road. Ambient "
                                 + "traffic is confined to the town, which is a few hundred metres of a "
                                 + "world seven kilometres long.");
                return;
            }

            if (outside == 0)
            {
                Debug.Log($"[Horizon] Traffic routes: all {lanes} trunk lanes are inside the "
                          + "carriageway.");
                return;
            }

            Debug.LogWarning($"[Horizon] Traffic routes: {outside} trunk lane sample(s) fall outside the "
                             + $"carriageway, worst {worst:0.00} m on lane {worstLane}. A trunk lane is "
                             + "offset half a half-width from the centreline, so this means it was baked "
                             + "against a different RoadShape from the one the road was built with.");
        }

        /// <summary>
        /// Plan distance from a point to the nearest point of a path, and where along the path that was.
        ///
        /// <para><paramref name="near"/> carries the previous answer in and the new one out. Negative
        /// means "no idea", which sweeps the whole path; anything else searches a window around it first
        /// and only sweeps if what it finds there is too far off the road to believe.</para>
        /// </summary>
        private static float NearestApproach(RoadPath path, Vector3 point, ref float near)
        {
            const float window = 12f;
            const float fine = 0.5f;
            const float coarse = 4f;

            // Far enough off the carriageway that a windowed answer is more likely to be the wrong
            // stretch of road than a real fault.
            const float implausible = 25f;

            if (near >= 0f)
            {
                float windowed = Sweep(path, point, near - window, near + window, fine, out float at);
                if (windowed < implausible)
                {
                    near = at;
                    return windowed;
                }
            }

            float found = Sweep(path, point, 0f, path.Length, coarse, out float coarsely);
            found = Mathf.Min(found,
                Sweep(path, point, coarsely - coarse, coarsely + coarse, fine, out float finely));

            near = finely;
            return found;
        }

        /// <summary>The nearest point of a path within a span of it, in plan.</summary>
        private static float Sweep(
            RoadPath path, Vector3 point, float from, float to, float step, out float at)
        {
            float bestSqr = float.MaxValue;
            at = Mathf.Clamp(from, 0f, path.Length);

            for (float along = Mathf.Max(0f, from); along <= Mathf.Min(to, path.Length); along += step)
            {
                Vector3 on = path.GetPositionAtDistance(along);

                float dx = on.x - point.x;
                float dz = on.z - point.z;
                float distanceSqr = dx * dx + dz * dz;

                if (distanceSqr < bestSqr)
                {
                    bestSqr = distanceSqr;
                    at = along;
                }
            }

            return Mathf.Sqrt(bestSqr);
        }

        /// <summary>
        /// Every connector starts where its incoming lane ends and finishes where its outgoing lane
        /// starts.
        ///
        /// A quadratic Bézier passes through both endpoints exactly, so this should be zero to floating
        /// point — which is what makes it a good check. Anything above the tolerance is not drift, it is
        /// a connector wired to the wrong lane.
        /// </summary>
        private static void CheckConnectorsAreFlush(TrafficNetwork routes)
        {
            float worst = 0f;
            int worstLane = -1;
            int loose = 0;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.NodeOf(lane) >= 0 || routes.ExitCount(lane) == 0)
                {
                    continue;
                }

                Vector3 end = routes.SampleAt(lane, routes.SampleCount(lane) - 1);

                for (int e = 0; e < routes.ExitCount(lane); e++)
                {
                    int connector = routes.ExitAt(lane, e);

                    float entry = Vector3.Distance(end, routes.SampleAt(connector, 0));
                    float exit = 0f;

                    if (routes.ExitCount(connector) > 0)
                    {
                        int onward = routes.ExitAt(connector, 0);
                        exit = Vector3.Distance(
                            routes.SampleAt(connector, routes.SampleCount(connector) - 1),
                            routes.SampleAt(onward, 0));
                    }

                    float gap = Mathf.Max(entry, exit);
                    if (gap > FlushTolerance)
                    {
                        loose++;
                    }

                    if (gap > worst)
                    {
                        worst = gap;
                        worstLane = connector;
                    }
                }
            }

            if (loose == 0)
            {
                Debug.Log($"[Horizon] Traffic routes: every connector is flush with its lanes, worst gap "
                          + $"{worst * 1000f:0.0} mm.");
                return;
            }

            Debug.LogWarning($"[Horizon] Traffic routes: {loose} connector(s) do not meet their lanes, "
                             + $"worst {worst:0.00} m on lane {worstLane}. A car crossing one of those "
                             + "teleports sideways by that much, once per junction.");
        }

        /// <summary>
        /// No lane is a dead end, and every junction that has a way in has a way out.
        ///
        /// A car that reaches a lane with no exits stops on it forever, which reads as a broken-down car
        /// nobody tows — the one traffic failure that is permanent rather than momentary.
        /// </summary>
        private static void CheckNothingIsStranded(TrafficNetwork routes)
        {
            int stranded = 0;
            int firstStranded = -1;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.ExitCount(lane) > 0)
                {
                    continue;
                }

                stranded++;
                if (firstStranded < 0)
                {
                    firstStranded = lane;
                }
            }

            if (stranded == 0)
            {
                Debug.Log($"[Horizon] Traffic routes: all {routes.LaneCount} lanes lead somewhere.");
                return;
            }

            Debug.LogWarning($"[Horizon] Traffic routes: {stranded} lane(s) lead nowhere, first "
                             + $"{firstStranded}. Even a dead end should offer the U-turn back out, and "
                             + "so should both ends of the trunk road — check that TrafficNetworkBuilder "
                             + "is still falling back to the reverse lane when a junction offers nothing "
                             + "else.");
        }

        /// <summary>
        /// No connector passes close enough to a building for a car to clip its corner.
        ///
        /// <para>Against the plot colliders in the scene rather than against the plan, because the
        /// colliders are what a car would actually hit and they are the thing that has been through the
        /// collider table. Uses a sphere overlap per sample, which is a few thousand queries — slow
        /// enough to be a menu command and far too slow to be part of every rebuild.</para>
        /// </summary>
        private static void CheckConnectorsClearBuildings(TrafficNetwork routes)
        {
            var hits = new Collider[4];
            int fouled = 0;
            int firstFouled = -1;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.NodeOf(lane) < 0)
                {
                    continue;
                }

                for (int i = 0; i < routes.SampleCount(lane); i++)
                {
                    Vector3 at = routes.SampleAt(lane, i) + Vector3.up * 0.8f;

                    int found = Physics.OverlapSphereNonAlloc(at, BuildingClearance, hits);
                    bool building = false;

                    for (int h = 0; h < found; h++)
                    {
                        // Plot colliders are the box-per-building the town builds; everything else at
                        // this height is terrain or street mesh, which a connector is supposed to be on.
                        if (hits[h] != null && hits[h] is BoxCollider
                            && hits[h].name.StartsWith("Plot_"))
                        {
                            building = true;
                            break;
                        }
                    }

                    if (!building)
                    {
                        continue;
                    }

                    fouled++;
                    if (firstFouled < 0)
                    {
                        firstFouled = lane;
                    }

                    break;
                }
            }

            if (fouled == 0)
            {
                Debug.Log("[Horizon] Traffic routes: no connector passes within "
                          + $"{BuildingClearance:0.0} m of a building.");
                return;
            }

            Debug.LogWarning($"[Horizon] Traffic routes: {fouled} connector(s) pass within "
                             + $"{BuildingClearance:0.0} m of a building, first on lane {firstFouled}. "
                             + "A turn that clips a corner is a car driving through a wall — either the "
                             + "junction's trims are short for the angle, or a plot has been parcelled "
                             + "onto the mouth of a street.");
        }

        /// <summary>
        /// The street graph the routes were baked from, rebuilt under a throwaway object.
        ///
        /// <para>Rebuilt rather than read off the scene, for the reason the world preview rebuilds the
        /// course: both come from the same deterministic table, so they cannot disagree unless generated
        /// output has been hand-edited. Built once per run and destroyed in the caller's finally —
        /// caching it in a static would leave a hidden object holding forty <c>RoadPath</c> components
        /// alive between runs.</para>
        /// </summary>
        private static StreetNetwork[] RebuildNetwork(
            out GameObject scratch, out RoadPath trunk, out RoadPath[] trunkRoads)
        {
            scratch = new GameObject("TrafficValidatorScratch") { hideFlags = HideFlags.HideAndDontSave };

            RoadCourse course = MountainPassCourse.Build();

            var pathObject = new GameObject("Path");
            pathObject.transform.SetParent(scratch.transform, false);

            trunk = pathObject.AddComponent<RoadPath>();
            trunk.SetControlPoints(course.ControlPoints);

            // The city hangs off its own arterial rather than off the pass, and that line is never
            // paved — it is a coordinate axis. Rebuilt here the same way PrototypeSetup does it, because
            // a validator measuring against a graph the bake did not use is measuring nothing.
            var arterialObject = new GameObject("Arterial");
            arterialObject.transform.SetParent(scratch.transform, false);

            RoadPath arterial = arterialObject.AddComponent<RoadPath>();
            arterial.SetControlPoints(HochstadtCourse.Build().ControlPoints);

            // Every paved road a Trunk-kind lane can be baked onto, not just the pass.
            //
            // <b>Without this the check was dead.</b> It measured every trunk lane in the world against
            // the one road it had been handed, so the moment the country road was chained on it started
            // reporting thousands of samples "outside the carriageway" — by kilometres, on roads that
            // were perfectly correct. A check that always fails is a check nobody reads, and it can no
            // longer catch the fault it exists for: a lane baked against the wrong RoadShape.
            // A local, because a local function may not touch an `out` parameter.
            Transform under = scratch.transform;

            RoadPath Paved(string name, RoadCourse from)
            {
                var host = new GameObject(name);
                host.transform.SetParent(under, false);

                RoadPath built = host.AddComponent<RoadPath>();
                built.SetControlPoints(from.ControlPoints);
                return built;
            }

            trunkRoads = new[]
            {
                trunk,
                Paved("Ebental", EbentalCourse.Build()),
                Paved("Coast", CoastCourse.Build()),
                Paved("Kalkgrat", KalkgratCourse.Build()),
                Paved("Meerenge", MeerengeCourse.Build()),
            };

            return new[]
            {
                RebuildTown(TalheimLayout.Build(), trunk, TownShape.Default, scratch.transform),
                RebuildTown(HochstadtLayout.Build(), arterial, TownShape.Hochstadt, scratch.transform),
            };
        }

        /// <summary>One settlement, taken through the same two steps the bake takes it through.</summary>
        private static StreetNetwork RebuildTown(
            TownNetworkSpec layout, IRoadPath trunk, in TownShape preset, Transform scratch)
        {
            StreetNetwork network = StreetNetwork.Build(
                trunk,
                TownShape.CoverLayout(preset, layout, TerrainShape.Default.RoadShelfDrop),
                layout,
                scratch,
                TerrainShape.Default.RoadShelfDrop);

            StreetJunctionBuilder.ResolveTrims(network, RoadShape.Default.OuterHalfWidth);
            return network;
        }
    }
}
