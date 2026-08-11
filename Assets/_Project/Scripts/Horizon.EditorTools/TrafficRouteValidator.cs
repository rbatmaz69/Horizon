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
    /// <item>Every lane sample sits within its street's half-width of the centreline.</item>
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
                StreetNetwork streets = RebuildNetwork(out scratch);

                CheckLanesFollowTheirStreets(routes, streets);
                CheckConnectorsAreFlush(routes);
                CheckNothingIsStranded(routes);
                CheckConnectorsClearBuildings(routes);
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
        /// Every street lane sample is within its own street's half-width of a centreline.
        ///
        /// <para>Measured against the street graph rebuilt from the layout table rather than against the
        /// scene's <c>RoadPath</c> objects, for the reason the world preview rebuilds the course: both
        /// come from the same deterministic table, so they cannot disagree unless someone has hand-edited
        /// generated output — which this project's conventions say not to do.</para>
        ///
        /// <para>The check that matters is the <i>upper</i> bound. A lane inside the carriageway is fine;
        /// a lane outside it is a car on the footway.</para>
        /// </summary>
        private static void CheckLanesFollowTheirStreets(TrafficNetwork routes, StreetNetwork streets)
        {
            var index = new StreetIndex(streets, 2f, 16f);

            float worst = 0f;
            int worstLane = -1;
            int outside = 0;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                if (routes.NodeOf(lane) >= 0)
                {
                    continue;
                }

                for (int i = 0; i < routes.SampleCount(lane); i++)
                {
                    Vector3 at = routes.SampleAt(lane, i);
                    float distance = index.DistanceTo(at.x, at.z, out int edge);
                    if (edge < 0)
                    {
                        continue;
                    }

                    // Against the carriageway, not the paved width: a lane on the pavement is the thing
                    // being looked for, and a half-outer bound would let one through.
                    float allowed = streets.Edges[edge].HalfWidth;
                    float over = distance - allowed;

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
                             + $"{firstStranded}. Even a dead end should offer the U-turn back out — "
                             + "check that TrafficNetworkBuilder is still allowing it at degree one.");
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
        private static StreetNetwork RebuildNetwork(out GameObject scratch)
        {
            scratch = new GameObject("TrafficValidatorScratch") { hideFlags = HideFlags.HideAndDontSave };

            RoadCourse course = MountainPassCourse.Build();

            var pathObject = new GameObject("Path");
            pathObject.transform.SetParent(scratch.transform, false);

            RoadPath path = pathObject.AddComponent<RoadPath>();
            path.SetControlPoints(course.ControlPoints);

            StreetNetwork network = StreetNetwork.Build(
                path, TownShape.Default, TalheimLayout.Build(), scratch.transform,
                TerrainShape.Default.RoadShelfDrop);

            StreetJunctionBuilder.ResolveTrims(network, RoadShape.Default.OuterHalfWidth);
            return network;
        }
    }
}
