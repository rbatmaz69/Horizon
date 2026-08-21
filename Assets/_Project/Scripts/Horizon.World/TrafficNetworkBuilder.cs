using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Turns a finished <see cref="StreetNetwork"/> and the trunk road it hangs off into the lanes and
    /// turn connectors ambient traffic drives on.
    ///
    /// <para>Runs at edit time, after <see cref="StreetJunctionBuilder.ResolveTrims"/>, because a lane
    /// runs between the same trim points the ribbon does — the stretch between the trim point and the
    /// node belongs to the junction pad, and that is exactly the stretch a turn connector covers.</para>
    ///
    /// <para><b>The trunk road is in here too, and that is most of what this file is.</b> A town with
    /// traffic on its streets and none on the road through it is a town nobody drives to, and the pass is
    /// where the player spends nearly all of their time. It is cut into one lane pair per stretch between
    /// junctions, so a junction is the same object it is in the town — a place where lanes end and
    /// connectors take over — rather than a special case anything has to know about.</para>
    ///
    /// <para>In <c>Horizon.World</c> alongside the street graph it reads, not in the editor assembly:
    /// nothing here touches <c>UnityEditor</c>, and the lane maths is the sort of thing a route validator
    /// and a runtime director should both be able to see. Saving the result as an asset is the setup
    /// tool's business.</para>
    /// </summary>
    public static class TrafficNetworkBuilder
    {
        /// <summary>Target spacing between lane samples, metres.</summary>
        private const float SampleSpacing = 2.5f;

        /// <summary>
        /// How far from the centreline a lane runs, as a fraction of the carriageway's half-width.
        ///
        /// Half, which puts a car in the middle of its own side. Not further out: town streets here are
        /// 6.2 m wide kerb to kerb at their narrowest, and a lane at 0.75 would put a 2.3 m car's flank
        /// within a handspan of the kerb on every alley in the place. It is also the offset the player's
        /// own car is spawned at, so the two agree about where a lane is.
        /// </summary>
        private const float LaneOffset = 0.5f;

        /// <summary>
        /// A lane is lifted this far above the carriageway it follows, metres.
        ///
        /// The director adds the car's own ride height on top; this is only enough to keep a baked
        /// polyline off a surface that is itself interpolated, so a validator comparing the two is not
        /// measuring rounding.
        /// </summary>
        private const float SurfaceClearance = 0.02f;

        /// <summary>Speed limit on a town street, metres per second. Eleven is about 40 km/h.</summary>
        private const float StreetSpeed = 11f;

        /// <summary>
        /// And per kind of street, because one number for all of them was wrong in both directions.
        ///
        /// <para>Every town street was baked at <see cref="StreetSpeed"/>, which made a four-lane
        /// boulevard and a back alley behind a barn the same road as far as the traffic was concerned.
        /// Two things were wrong with that. The visible one is that a city's main avenue should move
        /// faster than a lane you can touch both walls of. The invisible one matters more: the turn a
        /// car takes at a junction is now weighted by where it leads, and with one speed everywhere that
        /// weighting had nothing to weigh — a boulevard and a dead-end stub scored identically.</para>
        ///
        /// <para>The spread is small on purpose. These are all 25–45 km/h; the point is the ordering,
        /// not the numbers, and a town where the traffic differs by more than that reads as a race
        /// track with pavements.</para>
        /// </summary>
        private static float StreetSpeedFor(TownStreetKind kind)
        {
            switch (kind)
            {
                case TownStreetKind.Boulevard: return 12.5f;
                case TownStreetKind.CityStreet: return 11.5f;
                case TownStreetKind.Avenue: return 10f;
                case TownStreetKind.HighStreet: return 9f;
                case TownStreetKind.Lane: return 9f;
                case TownStreetKind.SquareEdge: return 8f;
                case TownStreetKind.Alley: return 7f;
                default: return StreetSpeed;
            }
        }

        /// <summary>
        /// And on the trunk road. Nineteen is about 70 km/h — a country-road speed, quick enough that
        /// meeting a car on the pass is a moment rather than an obstacle, and slow enough that the player
        /// overtakes rather than being overtaken.
        /// </summary>
        private const float TrunkSpeed = 19f;

        /// <summary>What a car does to its speed to take a junction, as a fraction of the slower lane's.</summary>
        private const float ConnectorSpeedFactor = 0.7f;

        private const float MinimumConnectorSpeed = 6f;

        /// <summary>
        /// The least a stretch of trunk road between two junctions may be, metres.
        ///
        /// Two mouths closer together than the sum of their reaches would leave a lane of negative length
        /// between them, which the director would hand a car onto and back off in the same frame. The
        /// layout's junctions are sixty metres apart at their closest, so this is a guard rather than a
        /// thing that happens.
        /// </summary>
        private const float MinimumTrunkLane = 6f;

        /// <summary>
        /// Speed on the on-ramp, metres per second. Twenty-two is about 80 km/h.
        ///
        /// <para>Between the trunk road's 19 and the nearside motorway lane's, because that is what the
        /// lane is for: the loop is taken at country-road speed and the straight after it exists so a car
        /// arrives at the carriageway doing something close to what is already on it. A ramp at trunk
        /// speed puts a 70 km/h car into 110 km/h traffic, which the merge connector then has to
        /// absorb.</para>
        /// </summary>
        private const float RampSpeed = 22f;

        /// <summary>
        /// Builds the routes.
        ///
        /// <para>Two directed lanes per street and per stretch of trunk road, and one connector per legal
        /// turn at every junction. The one turn that is excluded is the U-turn — a car that arrives at a
        /// crossroads and reverses direction reads as a mistake even when it is legal — <b>unless there is
        /// nothing else to do</b>, which is the case at a dead end and at both ends of the trunk road,
        /// where the alternative is a car parked forever on the last lane it reached.</para>
        /// </summary>
        /// <param name="highway">
        /// The motorway's <b>median</b> line, or null. Never itself paved — the carriageways are offsets
        /// off it, and so are the lanes.
        /// </param>
        public static TrafficNetwork Build(
            IReadOnlyList<StreetNetwork> networks,
            IRoadPath trunk,
            in RoadShape trunkShape,
            IRoadPath highway = null,
            RoadShape highwayShape = default,
            float carriagewayOffset = 0f,
            int highwayEndTown = -1,
            int highwayEndNode = -1,
            IRoadPath link = null,
            RoadShape linkShape = default,
            float rampCapDistance = 0f,
            float rampMergeDistance = -1f,
            IReadOnlyList<TrafficSignalPlan> signalPlans = null,
            IRoadPath coast = null,
            RoadShape coastShape = default,
            int coastEndTown = -1,
            int coastEndNode = -1,
            IRoadPath country = null,
            RoadShape countryShape = default)
        {
            var lanes = new LaneBuffer();

            // Node indices run through every settlement in turn, so a street's own FromNode/ToNode has to
            // be shifted by however many nodes came before its town. Everything downstream — connectors,
            // give-way tokens, the exit table — sees one flat numbering and needs to know nothing about
            // which town a junction belongs to.
            var nodeOffset = new int[networks.Count];
            int nodeCount = 0;

            for (int i = 0; i < networks.Count; i++)
            {
                nodeOffset[i] = nodeCount;
                nodeCount += networks[i].Nodes.Count;
            }

            // Four synthetic junctions past the end of the street graphs: one for each end of the trunk
            // road, and two for the motorway. They carry no geometry — they exist so a road's open ends
            // are places where a lane ends and a connector begins, like every other junction here, rather
            // than a case the exit table has to special-case.
            int roadStartNode = nodeCount;
            int roadEndNode = nodeCount + 1;
            int highwayWestNode = nodeCount + 2;
            int highwayEastNode = nodeCount + 3;
            nodeCount += 4;

            // And one where the country road runs out. It needs a junction only at that end: the other
            // end is the trunk road's own far end, the same metre of tarmac and therefore the same node,
            // which is how the coast road joins the motorway and how the two become one drive rather
            // than two roads that happen to touch.
            bool continues = country != null && trunk != null;

            int countryEndNode = -1;
            if (continues)
            {
                countryEndNode = nodeCount;
                nodeCount += 1;
            }

            // And a fifth where the ramp meets the motorway, when there is one. It is a junction in every
            // sense the rest of this file means: lanes end there and connectors take over, which is what
            // turns two roads arriving at the same place into traffic that gives way to itself.
            bool merging = link != null && highway != null && rampMergeDistance >= 0f
                           && rampMergeDistance < highway.Length;

            int interchangeNode = -1;
            if (merging)
            {
                interchangeNode = nodeCount;
                nodeCount += 1;
            }

            // Unless the motorway ends somewhere real. Where it runs into a city, its far end and that
            // city's gateway are the same junction — not two junctions in the same place — and that
            // single fact is the whole of how the two networks join: AddConnectors works by node, so it
            // then builds the turns from all four nearside lanes into the boulevard by itself. With a
            // synthetic node there instead, the motorway's traffic has nothing to do at the end of the
            // road but turn round, which is what it was doing.
            bool joinsATown = highwayEndTown >= 0 && highwayEndTown < networks.Count
                              && highwayEndNode >= 0
                              && highwayEndNode < networks[highwayEndTown].Nodes.Count;

            if (joinsATown)
            {
                highwayEastNode = nodeOffset[highwayEndTown] + highwayEndNode;
            }

            // The coast road is a second road that runs out of one network and into another, and it needs
            // no synthetic junction at either end: it <i>starts</i> where the motorway starts — the same
            // metre of tarmac, so the same node — and it ends on the town's own gateway. Two real nodes,
            // and AddConnectors builds the turns at both from the lanes that meet there.
            bool coastJoinsATown = coast != null && highway != null
                                   && coastEndTown >= 0 && coastEndTown < networks.Count
                                   && coastEndNode >= 0
                                   && coastEndNode < networks[coastEndTown].Nodes.Count;

            var nodeAt = new Vector3[nodeCount];

            for (int i = 0; i < networks.Count; i++)
            {
                for (int n = 0; n < networks[i].Nodes.Count; n++)
                {
                    nodeAt[nodeOffset[i] + n] = networks[i].Nodes[n].Position;
                }
            }

            if (trunk != null)
            {
                nodeAt[roadStartNode] = trunk.GetPositionAtDistance(0f);
                nodeAt[roadEndNode] = trunk.GetPositionAtDistance(trunk.Length);
            }

            if (highway != null)
            {
                nodeAt[highwayWestNode] = highway.GetPositionAtDistance(0f);

                // Left alone when it is a town's own node: that position came from the street graph and
                // is where the junction actually is.
                if (!joinsATown)
                {
                    nodeAt[highwayEastNode] = highway.GetPositionAtDistance(highway.Length);
                }
            }

            if (continues)
            {
                nodeAt[countryEndNode] = country.GetPositionAtDistance(country.Length);
            }

            if (merging)
            {
                // On the nearside lane itself, not on the median: this is the point the ramp's lane and
                // the carriageway's two halves all end or begin at, and AddConnector shapes every turn
                // through it around this position.
                var nearside = new OffsetRoadPath(highway, -carriagewayOffset);
                float across = (-(carriagewayOffset + 1.5f * HighwayLaneWidth) + carriagewayOffset)
                               / Mathf.Max(0.001f, highwayShape.HalfWidth);

                nodeAt[interchangeNode] = LanePoint(
                    nearside, highwayShape, rampMergeDistance, across);
            }

            var entryNode = new List<int>(256);
            var exitNode = new List<int>(256);

            for (int i = 0; i < networks.Count; i++)
            {
                // The plan is queried with the town's own node indices, before nodeOffset is added to
                // anything — which is why re-numbering cannot corrupt it. The group it returns is 0 or 1
                // and carries no node identity at all.
                TrafficSignalPlan plan = signalPlans != null && i < signalPlans.Count
                    ? signalPlans[i]
                    : null;

                AddStreetLanes(networks[i], nodeOffset[i], lanes, entryNode, exitNode, plan);
            }

            AddTrunkLanes(networks[0], trunk, trunkShape, roadStartNode, roadEndNode,
                lanes, entryNode, exitNode);

            if (continues)
            {
                // No network, and that is not a shortcut. The cut list exists to break a trunk lane at
                // the junctions a town puts on it, and it reads each node's AlongTrunk — a distance
                // measured along the road that town sits on. Handing in a settlement that sits on a
                // different road would cut this one at distances that mean nothing here.
                AddTrunkLanes(null, country, countryShape, roadEndNode, countryEndNode,
                    lanes, entryNode, exitNode);
            }

            if (coastJoinsATown)
            {
                // Its town is handed in only so the cut list can look for junctions on it, and there are
                // none: Seeburg's axis is never paved, so nothing in that table is OnTrunkRoad. What this
                // builds is one lane each way over the whole road, ending at the two junctions above.
                AddTrunkLanes(networks[coastEndTown], coast, coastShape,
                    highwayWestNode, nodeOffset[coastEndTown] + coastEndNode,
                    lanes, entryNode, exitNode);
            }

            if (highway != null)
            {
                AddHighwayLanes(highway, highwayShape, carriagewayOffset,
                    highwayWestNode, highwayEastNode, lanes, entryNode, exitNode,
                    merging, rampMergeDistance, interchangeNode);
            }

            if (merging)
            {
                // Appended last, and as a pair, so the lane^1 arithmetic AddConnectors depends on keeps
                // landing on an even boundary. Everything before this is emitted two at a time.
                if ((lanes.Count & 1) != 0)
                {
                    Debug.LogError("[Horizon] Traffic lanes are meant to come in pairs and there is an "
                                   + "odd number of them before the interchange. AddConnectors reads a "
                                   + "lane's reverse as lane ^ 1, and from here on it would be reading "
                                   + "the wrong one.");
                }

                AddInterchangeLanes(highway, highwayShape, carriagewayOffset, link, linkShape,
                    rampCapDistance, rampMergeDistance, highwayEastNode, roadStartNode, interchangeNode,
                    lanes, entryNode, exitNode);
            }

            int drivenLanes = lanes.Count;
            AddConnectors(lanes, entryNode, exitNode, nodeAt, drivenLanes,
                out List<int>[] connectorsOf, out List<int> connectorTarget);

            int[] exitStart = BuildExitTable(
                drivenLanes, connectorsOf, connectorTarget, out int[] exits);

            if (merging)
            {
                ReportInterchange(lanes, exitStart, exits, connectorTarget, drivenLanes);
            }

            var asset = ScriptableObject.CreateInstance<TrafficNetwork>();
            asset.Fill(
                lanes.Points.ToArray(), lanes.Start.ToArray(), lanes.Length.ToArray(),
                lanes.Step.ToArray(), lanes.Node.ToArray(), lanes.Kind.ToArray(),
                lanes.Speed.ToArray(), exitStart, exits, nodeCount);

            asset.FillSignals(
                lanes.Signal.ToArray(), TrafficSignalPlan.Groups, SignalCycle, SignalGreen, SignalAmber);

            ReportSignals(lanes, signalPlans);

            return asset;
        }

        /// <summary>Seconds in a full round of every phase. See <see cref="TrafficSignalPlan"/>.</summary>
        private const float SignalCycle = 16f;

        private const float SignalGreen = 6f;

        private const float SignalAmber = 1f;

        /// <summary>
        /// Says how many lanes ended up stopping at a light, and checks that against how many approaches
        /// the plans asked for.
        ///
        /// <para>That single equality is the whole cross-check between the two bakes. The heads are built
        /// from the plan and the stopping is baked onto the lanes, and nothing else connects them — so a
        /// mismatch is a junction with lights nobody obeys, or cars stopping at nothing. Neither throws,
        /// and neither is visible in a screenshot.</para>
        /// </summary>
        private static void ReportSignals(LaneBuffer lanes, IReadOnlyList<TrafficSignalPlan> plans)
        {
            int signalled = 0;
            for (int i = 0; i < lanes.Signal.Count; i++)
            {
                if (lanes.Signal[i] != TrafficNetwork.NoSignal)
                {
                    signalled++;
                }
            }

            int junctions = 0;
            int approaches = 0;

            if (plans != null)
            {
                for (int i = 0; i < plans.Count; i++)
                {
                    if (plans[i] == null)
                    {
                        continue;
                    }

                    junctions += plans[i].JunctionCount;
                    approaches += plans[i].ApproachCount;
                }
            }

            if (signalled != approaches)
            {
                Debug.LogError(
                    $"[Horizon] Traffic signals: {approaches} approach(es) were planned but {signalled} "
                    + "lane(s) were baked to stop at one. The heads and the stopping come from the same "
                    + "plan and must agree — see TrafficSignalPlan.GroupOf and AddStreetLanes.");
                return;
            }

            if (junctions == 0)
            {
                return;
            }

            Debug.Log($"[Horizon] Traffic signals: {junctions} junction(s), {approaches} approach(es) on "
                      + $"{TrafficSignalPlan.Groups} phase groups, {SignalCycle:0}s cycle "
                      + $"({SignalGreen:0}s green, {SignalAmber:0}s amber, "
                      + $"{SignalCycle / TrafficSignalPlan.Groups - SignalGreen - SignalAmber:0.0}s "
                      + "all-red between phases).");
        }

        /// <summary>
        /// Two directed lanes down every town street.
        ///
        /// Lane 2i is edge i travelled forwards and 2i + 1 is the same edge travelled back, which is also
        /// what makes a pair each other's U-turn. The director never needs that fact; the connector rule
        /// and the validator do.
        /// </summary>
        private static void AddStreetLanes(
            StreetNetwork network, int nodeOffset, LaneBuffer lanes,
            List<int> entryNode, List<int> exitNode, TrafficSignalPlan plan)
        {
            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                // The signal a lane stops at is the one facing the junction it *ends* at, so the two
                // directions of one street read different ends of it — and a street between two
                // controlled junctions is on one axis at one end and possibly the other axis at the
                // other.
                AddStreetLane(edge, true, lanes, SignalAt(plan, edge.ToNode, i));
                entryNode.Add(nodeOffset + edge.FromNode);
                exitNode.Add(nodeOffset + edge.ToNode);

                AddStreetLane(edge, false, lanes, SignalAt(plan, edge.FromNode, i));
                entryNode.Add(nodeOffset + edge.ToNode);
                exitNode.Add(nodeOffset + edge.FromNode);
            }
        }

        private static byte SignalAt(TrafficSignalPlan plan, int node, int edge)
        {
            int group = plan != null ? plan.GroupOf(node, edge) : -1;
            return group < 0 ? TrafficNetwork.NoSignal : (byte)group;
        }

        /// <summary>
        /// One directed lane down one street, between its two trim points.
        ///
        /// Offset to the <i>direction of travel's</i> right, which for the backwards lane is the path's
        /// own left. Getting that sign from the side the town is on, or from which end of the edge the
        /// lane starts, is the class of mistake that puts half the traffic in the oncoming lane and looks
        /// almost right.
        /// </summary>
        private static void AddStreetLane(
            StreetEdge edge, bool forward, LaneBuffer lanes, byte signal)
        {
            float from = forward ? edge.TrimStart : edge.Length - edge.TrimEnd;
            float to = forward ? edge.Length - edge.TrimEnd : edge.TrimStart;

            float span = Mathf.Abs(to - from);
            int count = Mathf.Max(2, Mathf.CeilToInt(span / SampleSpacing) + 1);

            float across = edge.Shape.HalfWidth * LaneOffset * (forward ? 1f : -1f);
            float rise = edge.Shape.SurfaceLift + SurfaceClearance;

            for (int i = 0; i < count; i++)
            {
                float at = Mathf.Lerp(from, to, i / (float)(count - 1));
                lanes.Points.Add(TownStreetBuilder.PointAcross(edge.Path, edge.Shape, at, across, rise));
            }

            lanes.Close(span, count, -1, TrafficLaneKind.Street, StreetSpeedFor(edge.Kind), signal);
        }

        /// <summary>
        /// The trunk road, cut at every junction the town hangs on it and laid out as one lane pair per
        /// stretch between them.
        ///
        /// <para>Cut rather than run end to end, because a lane has to stop where a connector takes over —
        /// and a car that could drive straight past a junction on an unbroken lane would have no way to
        /// turn off it, which would leave the town's five entrances as decoration.</para>
        ///
        /// <para><paramref name="network"/> may be null, for a road with no settlement on it at all. The
        /// cuts come from each node's <c>AlongTrunk</c>, which is a distance measured along the road that
        /// town sits on — so the alternative to null is a road cut at distances belonging to a different
        /// road.</para>
        /// </summary>
        private static void AddTrunkLanes(
            StreetNetwork network,
            IRoadPath trunk,
            in RoadShape trunkShape,
            int roadStartNode,
            int roadEndNode,
            LaneBuffer lanes,
            List<int> entryNode,
            List<int> exitNode)
        {
            if (trunk == null || trunk.Length < MinimumTrunkLane)
            {
                return;
            }

            var cutNode = new List<int>(8) { roadStartNode };
            var cutAt = new List<float>(8) { 0f };
            var cutGap = new List<float>(8) { 0f };

            for (int i = 0; network != null && i < network.Nodes.Count; i++)
            {
                StreetNode node = network.Nodes[i];
                if (!node.OnTrunkRoad || node.Degree == 0)
                {
                    continue;
                }

                // Sorted in as it is found. Five junctions makes an insertion sort the honest choice, and
                // the layout table has no obligation to list them in order along the road.
                int slot = 1;
                while (slot < cutAt.Count && cutAt[slot] < node.AlongTrunk)
                {
                    slot++;
                }

                cutNode.Insert(slot, i);
                cutAt.Insert(slot, Mathf.Clamp(node.AlongTrunk, 0f, trunk.Length));
                cutGap.Insert(slot, MouthHalf(network, node));
            }

            cutNode.Add(roadEndNode);
            cutAt.Add(trunk.Length);
            cutGap.Add(0f);

            for (int s = 0; s < cutAt.Count - 1; s++)
            {
                float from = cutAt[s] + cutGap[s];
                float to = cutAt[s + 1] - cutGap[s + 1];

                if (to - from < MinimumTrunkLane)
                {
                    Debug.LogWarning(
                        $"[Horizon] Traffic: the trunk road between {cutAt[s]:0} m and {cutAt[s + 1]:0} m "
                        + "has less room between its two junction mouths than a lane needs. The mouths "
                        + "have been pulled back to make room, which means they now reach further than "
                        + "the geometry does — move the two entrances apart in the layout table.");

                    float middle = (cutAt[s] + cutAt[s + 1]) * 0.5f;
                    from = middle - MinimumTrunkLane * 0.5f;
                    to = middle + MinimumTrunkLane * 0.5f;
                }

                AddTrunkLane(trunk, trunkShape, from, to, true, lanes);
                entryNode.Add(cutNode[s]);
                exitNode.Add(cutNode[s + 1]);

                AddTrunkLane(trunk, trunkShape, to, from, false, lanes);
                entryNode.Add(cutNode[s + 1]);
                exitNode.Add(cutNode[s]);
            }
        }

        /// <summary>
        /// Speed limit of each motorway lane, nearside first, metres per second — 72, 90, 108 and
        /// 126 km/h.
        ///
        /// <para><b>This table is the entire traffic model on the motorway, and that is deliberate.</b>
        /// Every car in a lane drives at exactly that lane's limit, so nobody ever closes on anybody:
        /// there are no queues to form, no overtaking to arbitrate, and no lane changes to get wrong. The
        /// field still moves against itself, because the lanes move at different speeds — which is what
        /// weaving through it means from the driver's seat. The alternative, per-car speed variation,
        /// sounds livelier and requires lane changing to be built the same afternoon, because without it
        /// the first slow car turns its lane into a permanent stationary queue.</para>
        ///
        /// <para><b>Calibrated against the speed you travel at, not the one the car can reach.</b> These
        /// were 86 to 158 km/h, chosen so that a player at the top of fourth was passed by the offside
        /// lane — which read well on paper and was wrong in the car. The car tops out at 235 km/h, but
        /// nobody threading through traffic is doing that; the speed you are actually carrying is
        /// somewhere near 170, and against a 158 km/h lane that is a closing speed of twelve. Overtaking
        /// stopped being overtaking and became a slow drift past, which is the least interesting thing a
        /// motorway can offer.</para>
        ///
        /// <para>At 170 the offside lane now closes at 44 km/h and the nearside at 98, so every lane is
        /// something you go past and the four of them are visibly different speeds. The spread still
        /// matters more than the values: 54 km/h between the outside and inside lanes is what makes the
        /// field move against itself rather than drift along as a block.</para>
        /// </summary>
        private static readonly float[] HighwayLaneSpeeds = { 20f, 25f, 30f, 35f };

        /// <summary>Width of one motorway lane, metres. Four of these make RoadShape.Autobahn.</summary>
        private const float HighwayLaneWidth = 3.75f;

        /// <summary>
        /// Both carriageways of the motorway, four lanes each, run end to end.
        ///
        /// <para><b>Not cut at the interchange, and the link road carries no ambient traffic at all.</b>
        /// That is a stated limitation rather than an oversight. Joining a slip road into this graph
        /// properly means a diverge from the nearside lane and a merge back into it, on one carriageway
        /// only, with the other three lanes running through uninterrupted — and a lane graph whose only
        /// topology is "what may I do at the end of this lane" cannot express a merge without inventing
        /// one. The player uses the link; the traffic stays on the motorway. Adding it later is a matter
        /// of cutting the nearside lane at the junction and giving the link its own node.</para>
        ///
        /// <para>Emitted strictly as forward/backward pairs, because <see cref="AddConnectors"/> finds a
        /// lane's reverse with <c>lane ^ 1</c> and that arithmetic is load-bearing for the U-turn rule.
        /// At each end of the road the U-turn is the only thing to do, so traffic turns round there — the
        /// same behaviour the trunk road already has at its own two ends.</para>
        /// </summary>
        private static void AddHighwayLanes(
            IRoadPath centre,
            in RoadShape shape,
            float carriagewayOffset,
            int westNode,
            int eastNode,
            LaneBuffer lanes,
            List<int> entryNode,
            List<int> exitNode,
            bool merging,
            float mergeDistance,
            int interchangeNode)
        {
            if (centre == null || centre.Length < MinimumTrunkLane)
            {
                return;
            }

            for (int ordinal = 0; ordinal < HighwayLaneSpeeds.Length; ordinal++)
            {
                // Ordinal 0 is the nearside lane, furthest from the median; the offside lane is nearest
                // it. Measured out from the median line so both carriageways are described by one number
                // and the pair stays symmetric.
                float fromCentre = (1.5f - ordinal) * HighwayLaneWidth;
                float across = carriagewayOffset + fromCentre;
                float speed = HighwayLaneSpeeds[ordinal];

                AddHighwayLane(centre, shape, carriagewayOffset, across, true, speed, lanes);
                entryNode.Add(westNode);
                exitNode.Add(eastNode);

                // The nearside lane on the carriageway the ramp feeds is cut at the merge point, and what
                // is emitted here is only the half downstream of it. The upstream half is appended after
                // the loop, alongside the ramp — see AddInterchangeLanes for why the two go together.
                bool cut = merging && ordinal == 0;

                AddHighwayLane(centre, shape, -carriagewayOffset, -across, false, speed, lanes,
                    fromDistance: 0f, toDistance: cut ? mergeDistance : -1f);

                entryNode.Add(cut ? interchangeNode : eastNode);
                exitNode.Add(westNode);
            }
        }

        /// <summary>
        /// Says what the interchange actually came out as.
        ///
        /// <para>The merge is three lanes agreeing about one node, and every part of that is an index
        /// into a flat array. Nothing about a wrong one throws: the ramp simply leads nowhere, or the
        /// motorway's nearside lane stops feeding itself, and the first anybody knows is a queue of cars
        /// stopped on a slip road. Both counts below are two if it is wired right — the ramp reaching the
        /// carriageway, and the carriageway's upstream half reaching its downstream half.</para>
        /// </summary>
        private static void ReportInterchange(
            LaneBuffer lanes,
            int[] exitStart,
            int[] exits,
            List<int> connectorTarget,
            int drivenLanes)
        {
            // The pair appended last: the cut lane's upstream half, then the ramp.
            int upstream = drivenLanes - 2;
            int ramp = drivenLanes - 1;

            int RunsInto(int lane)
            {
                return lane >= 0 && lane + 1 < exitStart.Length
                    ? exitStart[lane + 1] - exitStart[lane]
                    : 0;
            }

            int fromRamp = RunsInto(ramp);
            int fromUpstream = RunsInto(upstream);

            if (fromRamp == 0 || fromUpstream == 0)
            {
                Debug.LogError(
                    $"[Horizon] The interchange is not wired: the ramp has {fromRamp} way(s) on and the "
                    + $"motorway's upstream nearside lane has {fromUpstream}. Both need at least one, and "
                    + "they need to be the same lane — see AddInterchangeLanes.");
                return;
            }

            Debug.Log($"[Horizon] Interchange: the ramp is {lanes.Length[ramp]:0} m of lane off the pass "
                      + $"with {fromRamp} way(s) onto the motorway, and the nearside carriageway lane is "
                      + $"cut into {lanes.Length[upstream]:0} m before the merge plus the rest after it, "
                      + $"with {fromUpstream} way(s) on. Traffic joins the motorway here rather than "
                      + "driving past the slip road as it did.");
        }

        /// <summary>
        /// The two lanes that meet at the interchange: the upstream half of the cut nearside lane, and
        /// the ramp coming down off the pass.
        ///
        /// <para><b>Emitted as one pair, and appended rather than woven in.</b> Both exit at the
        /// interchange node and the only lane entering it is the downstream half, so
        /// <see cref="AddConnectors"/> builds one connector from each — which is a merge, expressed
        /// entirely in the topology the graph already had. The file used to say this could not be done
        /// without inventing something; it could, because lanes already carry an entry and an exit node
        /// and connectors are already built from shared ones.</para>
        ///
        /// <para><b>Why the pair is these two and not the obvious ones.</b> <c>AddConnectors</c> reads a
        /// lane's reverse as <c>lane ^ 1</c> and uses it for one thing only: to hold the U-turn back
        /// unless there is nothing else to do. Pairing the upstream half with the ramp is harmless
        /// because neither of them enters a node the other leaves — the exclusion never fires, and both
        /// get their connector on the first pass. Splitting the eastbound nearside lane to keep a tidier
        /// pairing was the alternative, and it would have put a junction token on a motorway lane with no
        /// junction on it: one car through at a time, on a road where four go past abreast.</para>
        /// </summary>
        private static void AddInterchangeLanes(
            IRoadPath centre,
            in RoadShape shape,
            float carriagewayOffset,
            IRoadPath link,
            in RoadShape linkShape,
            float capDistance,
            float mergeDistance,
            int eastNode,
            int rampFromNode,
            int interchangeNode,
            LaneBuffer lanes,
            List<int> entryNode,
            List<int> exitNode)
        {
            float across = carriagewayOffset + 1.5f * HighwayLaneWidth;

            // Upstream half of the nearside lane: from the far end down to the merge point.
            AddHighwayLane(centre, shape, -carriagewayOffset, -across, false, HighwayLaneSpeeds[0],
                lanes, fromDistance: mergeDistance, toDistance: -1f);

            entryNode.Add(eastNode);
            exitNode.Add(interchangeNode);

            // The ramp. Its lane ends where the nearside lane's two halves meet, having already moved
            // across onto it, so the connector out of it is the last few metres rather than a swerve.
            var carriageway = new OffsetRoadPath(centre, -carriagewayOffset);
            float nearsideFraction = (-across + carriagewayOffset) / Mathf.Max(0.001f, shape.HalfWidth);

            AddRampLane(link, linkShape, carriageway, shape, capDistance, mergeDistance,
                nearsideFraction, lanes);

            entryNode.Add(rampFromNode);
            exitNode.Add(interchangeNode);
        }

        /// <summary>
        /// One motorway lane, offset from the <i>median</i> line rather than from a carriageway.
        ///
        /// <para>The offset is in metres and absolute, not a fraction of a half-width, because the
        /// centreline this is measured from is not paved: it is the middle of the median, and the
        /// carriageway it belongs to is itself an <see cref="OffsetRoadPath"/> off it. Converting to the
        /// fraction <see cref="LanePoint"/> wants is done here, once, against that carriageway.</para>
        /// </summary>
        private static void AddHighwayLane(
            IRoadPath centre,
            in RoadShape shape,
            float carriagewayOffset,
            float acrossFromMedian,
            bool forward,
            float speed,
            LaneBuffer lanes,
            float fromDistance = 0f,
            float toDistance = -1f)
        {
            // Rebased onto the carriageway this lane belongs to, so the camber and banking below are
            // measured across the asphalt the car is actually on rather than across the median.
            var carriageway = new OffsetRoadPath(centre, carriagewayOffset);
            float fraction = (acrossFromMedian - carriagewayOffset) / Mathf.Max(0.001f, shape.HalfWidth);

            float end = toDistance < 0f ? centre.Length : Mathf.Min(toDistance, centre.Length);
            float start = Mathf.Clamp(fromDistance, 0f, end);

            float length = end - start;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / SampleSpacing) + 1);

            for (int i = 0; i < count; i++)
            {
                // Backwards lanes are walked from the far end, so a lane's points always run in the
                // direction its cars travel.
                float t = i / (float)(count - 1);
                float at = forward ? Mathf.Lerp(start, end, t) : Mathf.Lerp(end, start, t);
                lanes.Points.Add(LanePoint(carriageway, shape, at, fraction));
            }

            lanes.Close(length, count, -1, TrafficLaneKind.Highway, speed);
        }

        /// <summary>
        /// The on-ramp, as one lane: down the link road from the pass and along the acceleration lane
        /// until it is on the motorway's nearside lane.
        ///
        /// <para><b>One lane, not a pair, and that is the honest shape of it.</b> Ambient traffic joins
        /// the motorway here and never leaves it — a diverge would need its own node at the ramp's mouth
        /// rather than at the merge point 315 m downstream, and hanging one off this node instead would
        /// send cars up the acceleration lane against the traffic joining from it. The player drives the
        /// ramp both ways regardless; the geometry is two-way and always was.</para>
        ///
        /// <para>The last stretch is a lateral lerp from the ramp's own lane onto the motorway's nearside
        /// one, sampled in the carriageway's frame. That is what makes the merge a line a car can follow
        /// rather than a step across two and a half metres — and it is the same curve the wedge under it
        /// is built along, so the cars stay on the asphalt that was laid for them.</para>
        /// </summary>
        private static void AddRampLane(
            IRoadPath link,
            in RoadShape linkShape,
            IRoadPath carriageway,
            in RoadShape highwayShape,
            float capDistance,
            float mergeDistance,
            float nearsideFraction,
            LaneBuffer lanes)
        {
            // Travelling down the link means walking its course backwards, so the driver's right hand is
            // the path's left: the lane sits on the negative side of the centreline.
            const float rampFraction = -0.5f;

            float linkLength = link.Length;
            int linkCount = Mathf.Max(2, Mathf.CeilToInt(linkLength / SampleSpacing) + 1);

            for (int i = 0; i < linkCount; i++)
            {
                float at = Mathf.Lerp(linkLength, 0f, i / (float)(linkCount - 1));
                lanes.Points.Add(LanePoint(link, linkShape, at, rampFraction));
            }

            // Then the merge itself. The first sample would repeat the cap, which is already the last
            // point above, so it starts one step in.
            float mergeSpan = Mathf.Abs(mergeDistance - capDistance);
            int mergeCount = Mathf.Max(2, Mathf.CeilToInt(mergeSpan / SampleSpacing) + 1);

            // Where the ramp's own lane sits, expressed in the carriageway's frame, so the two ends of
            // the lerp are measured the same way and the line between them cannot kink.
            Vector3 cap = LanePoint(link, linkShape, 0f, rampFraction);
            float capFraction = FractionOf(carriageway, highwayShape, capDistance, cap);

            for (int i = 1; i < mergeCount; i++)
            {
                float t = i / (float)(mergeCount - 1);
                float at = Mathf.Lerp(capDistance, mergeDistance, t);

                // Eased rather than linear: a straight lerp across four metres puts the whole lateral
                // move into a constant drift, and a car following it crabs the entire length of the
                // lane instead of merging at the end of it.
                float across = Mathf.Lerp(capFraction, nearsideFraction, Mathf.SmoothStep(0f, 1f, t));
                lanes.Points.Add(LanePoint(carriageway, highwayShape, at, across));
            }

            int count = linkCount + mergeCount - 1;
            lanes.Close(linkLength + mergeSpan, count, -1, TrafficLaneKind.Highway, RampSpeed);
        }

        /// <summary>
        /// Which fraction of a carriageway's half-width a world point sits at. Used to express the ramp's
        /// mouth in the motorway's own frame so the merge can be interpolated in one coordinate.
        /// </summary>
        private static float FractionOf(
            IRoadPath road, in RoadShape shape, float along, Vector3 point)
        {
            float at = Mathf.Clamp(along, 0f, road.Length);

            Vector3 centre = road.GetPositionAtDistance(at);
            Vector3 right = road.GetBankedRightAtDistance(
                at, shape.MaxBankDegrees, shape.FullBankRadius);

            return Vector3.Dot(point - centre, right) / Mathf.Max(0.001f, shape.HalfWidth);
        }

        /// <summary>How far a bell-mouth reaches along the trunk road either side of its junction.</summary>
        private static float MouthHalf(StreetNetwork network, StreetNode node)
        {
            float halfOuter = 0f;

            for (int i = 0; i < node.Degree; i++)
            {
                halfOuter = Mathf.Max(halfOuter, network.Edges[node.Edges[i]].HalfOuter);
            }

            return halfOuter + StreetJunctionBuilder.TrunkKerbReturn;
        }

        /// <summary>One directed lane down a stretch of trunk road.</summary>
        private static void AddTrunkLane(
            IRoadPath trunk, in RoadShape shape, float from, float to, bool forward, LaneBuffer lanes)
        {
            float span = Mathf.Abs(to - from);
            int count = Mathf.Max(2, Mathf.CeilToInt(span / SampleSpacing) + 1);

            for (int i = 0; i < count; i++)
            {
                float at = Mathf.Lerp(from, to, i / (float)(count - 1));
                lanes.Points.Add(TrunkLanePoint(trunk, shape, at, forward ? 1f : -1f));
            }

            lanes.Close(span, count, -1, TrafficLaneKind.Trunk, TrunkSpeed);
        }

        /// <summary>
        /// Where a lane sits on the trunk road's carriageway.
        ///
        /// <para>Built the way <c>RoadMeshBuilder.AppendRing</c> builds its own quarter-width vertex,
        /// camber and banking included, rather than by offsetting along the level right vector and hoping.
        /// The difference is the whole of the pass: at four degrees of bank a lane 2.6 m off the
        /// centreline is 18 cm from where the level construction puts it, so a car on a hairpin would be
        /// riding a fifth of a metre inside the road on the way up and the same distance above it on the
        /// way down.</para>
        /// </summary>
        private static Vector3 TrunkLanePoint(
            IRoadPath trunk, in RoadShape shape, float along, float side)
        {
            return LanePoint(trunk, shape, along, LaneOffset * side);
        }

        /// <summary>
        /// A point on a lane at an arbitrary fraction of the carriageway's half-width.
        ///
        /// <para><paramref name="acrossFraction"/> is signed and runs -1 to 1 across the asphalt, so the
        /// two-lane trunk road passes ±0.5 and the motorway's four lanes pass ±0.25 and ±0.75. It was a
        /// constant until there was a road with more than one lane each way; everything else about this
        /// calculation is unchanged, including the reason it exists.</para>
        /// </summary>
        private static Vector3 LanePoint(
            IRoadPath road, in RoadShape shape, float along, float acrossFraction)
        {
            float at = Mathf.Clamp(along, 0f, road.Length);

            Vector3 centre = road.GetPositionAtDistance(at);
            Vector3 right = road.GetBankedRightAtDistance(
                at, shape.MaxBankDegrees, shape.FullBankRadius);

            Vector3 up = Vector3.Cross(road.GetDirectionAtDistance(at), right).normalized;
            if (up.y < 0f)
            {
                up = -up;
            }

            // The camber is a parabola across the carriageway, so at half the half-width it has given up a
            // quarter of its rise. Written as the offset squared rather than as 0.75 so the two cannot
            // drift apart if the lane ever moves off the quarter point.
            float crown = shape.Crown * (1f - acrossFraction * acrossFraction);

            return centre
                   + up * (shape.SurfaceLift + crown + SurfaceClearance)
                   + right * (shape.HalfWidth * acrossFraction);
        }

        /// <summary>
        /// A connector for every legal turn, at every junction including the two synthetic ones at the
        /// ends of the trunk road.
        ///
        /// <para>The U-turn rule is stated once, here, and it is stated as "what else could this car do"
        /// rather than as a test on the junction's degree. Degree was the right question when every lane
        /// was a town street: a node with one street on it was a dead end. It is the wrong one now — a
        /// trunk junction has one street on it and three ways out.</para>
        /// </summary>
        private static void AddConnectors(
            LaneBuffer lanes,
            List<int> entryNode,
            List<int> exitNode,
            Vector3[] nodeAt,
            int drivenLanes,
            out List<int>[] connectorsOf,
            out List<int> connectorTarget)
        {
            connectorsOf = new List<int>[drivenLanes];
            connectorTarget = new List<int>(drivenLanes * 2);

            for (int lane = 0; lane < drivenLanes; lane++)
            {
                int node = exitNode[lane];
                if (node < 0)
                {
                    continue;
                }

                // Every driven lane is created as one of a pair covering the same stretch both ways, and
                // the street lanes come first in an even number of them — so a lane's reverse is its
                // sibling, and the U-turn out of a junction is the one turn that lands on it.
                int reverse = lane ^ 1;

                // Everything leaving this junction that is not the way we came in — and if that is
                // nothing at all, the way we came in after all.
                bool anyOnward = false;

                for (int pass = 0; pass < 2 && !anyOnward; pass++)
                {
                    bool allowReverse = pass == 1;

                    for (int other = 0; other < drivenLanes; other++)
                    {
                        if (entryNode[other] != node || (other == reverse) != allowReverse)
                        {
                            continue;
                        }

                        anyOnward = true;

                        int connector = lanes.Count;
                        AddConnector(lanes, nodeAt[node], node, lane, other);

                        (connectorsOf[lane] ??= new List<int>(3)).Add(connector);
                        connectorTarget.Add(other);
                    }
                }
            }
        }

        /// <summary>
        /// A turn through a junction: a quadratic from where one lane ends, bent about the node centre,
        /// to where the next one starts.
        ///
        /// <para>A quadratic Bézier passes through both its endpoints exactly, which is the whole reason
        /// for using one — a connector is flush with the lanes it joins by construction rather than to
        /// within a tolerance, and the validator checks that rather than assuming it. The node centre is
        /// the control point, not a point on the curve, so the turn cuts the corner the way a car does
        /// instead of driving to the middle of the junction and back out.</para>
        ///
        /// <para>It is also right for the two turns that are not turns at all — carrying straight on past
        /// a town entrance, and the U-turn at the end of the road. On a straight the control point sits
        /// on the line between the endpoints and the curve is that line; on the U-turn it sits on the
        /// centreline between the two lanes and the curve is the loop round it.</para>
        /// </summary>
        private static void AddConnector(
            LaneBuffer lanes, Vector3 nodePosition, int node, int fromLane, int toLane)
        {
            Vector3 start = lanes.Last(fromLane);
            Vector3 end = lanes.First(toLane);

            Vector3 control = nodePosition;
            control.y = (start.y + end.y) * 0.5f;

            // Length from the chord rather than by integrating the curve: a turn connector is a few tens
            // of metres of gentle arc, the two differ by a few per cent, and every consumer of this number
            // is either a speed or a look-ahead.
            float chord = Vector3.Distance(start, control) + Vector3.Distance(control, end);
            int count = Mathf.Max(3, Mathf.CeilToInt(chord / SampleSpacing) + 1);

            var previous = start;
            float measured = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float inverse = 1f - t;

                Vector3 on = inverse * inverse * start + 2f * inverse * t * control + t * t * end;
                lanes.Points.Add(on);

                if (i > 0)
                {
                    measured += Vector3.Distance(previous, on);
                }

                previous = on;
            }

            float speed = Mathf.Max(
                MinimumConnectorSpeed,
                Mathf.Min(lanes.Speed[fromLane], lanes.Speed[toLane]) * ConnectorSpeedFactor);

            lanes.Close(measured, count, node, TrafficLaneKind.Connector, speed);
        }

        /// <summary>
        /// The exit table. A driven lane's exits are its connectors; a connector's single exit is the lane
        /// it feeds. Same shape as everything else here — counts, offsets, items.
        /// </summary>
        private static int[] BuildExitTable(
            int drivenLanes, List<int>[] connectorsOf, List<int> connectorTarget, out int[] exits)
        {
            var start = new List<int>(drivenLanes + connectorTarget.Count + 1) { 0 };
            var flat = new List<int>(drivenLanes * 2 + connectorTarget.Count);

            for (int lane = 0; lane < drivenLanes; lane++)
            {
                List<int> found = connectorsOf[lane];
                if (found != null)
                {
                    flat.AddRange(found);
                }

                start.Add(flat.Count);
            }

            for (int i = 0; i < connectorTarget.Count; i++)
            {
                flat.Add(connectorTarget[i]);
                start.Add(flat.Count);
            }

            exits = flat.ToArray();
            return start.ToArray();
        }

        /// <summary>
        /// The lanes being built, as the flat arrays they will be baked into.
        ///
        /// <para>A type rather than nine lists threaded through every method's arguments. It exists only
        /// for the duration of a bake and hands its contents to <see cref="TrafficNetwork.Fill"/>; nothing
        /// here is allocated at run time.</para>
        /// </summary>
        private sealed class LaneBuffer
        {
            public readonly List<Vector3> Points = new List<Vector3>(8192);
            public readonly List<int> Start = new List<int>(512) { 0 };
            public readonly List<float> Length = new List<float>(512);
            public readonly List<float> Step = new List<float>(512);
            public readonly List<int> Node = new List<int>(512);
            public readonly List<byte> Kind = new List<byte>(512);
            public readonly List<float> Speed = new List<float>(512);

            /// <summary>
            /// The signal group at the end of each lane, or <see cref="TrafficNetwork.NoSignal"/>.
            ///
            /// Defaulted at every call site but one: only a town street can end at a light. A connector
            /// must never carry one, or a car would stop in the middle of a junction rather than at the
            /// line in front of it.
            /// </summary>
            public readonly List<byte> Signal = new List<byte>(512);

            public int Count => Length.Count;

            /// <summary>Closes the lane whose points have just been added, and returns its index.</summary>
            public int Close(
                float length, int sampleCount, int node, TrafficLaneKind kind, float speed,
                byte signal = TrafficNetwork.NoSignal)
            {
                Start.Add(Points.Count);
                Length.Add(length);
                Step.Add(length / Mathf.Max(1, sampleCount - 1));
                Node.Add(node);
                Kind.Add((byte)kind);
                Speed.Add(speed);
                Signal.Add(signal);

                return Length.Count - 1;
            }

            public Vector3 First(int lane)
            {
                return Points[Start[lane]];
            }

            public Vector3 Last(int lane)
            {
                return Points[Start[lane + 1] - 1];
            }
        }
    }
}
