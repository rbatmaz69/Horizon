using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>What a signal is showing. Ordered so the value doubles as an index into a material table.</summary>
    public enum TrafficSignalState : byte
    {
        Red = 0,
        Amber = 1,
        Green = 2,
    }

    /// <summary>
    /// Which junctions of a town are traffic-light controlled, and which phase each approach belongs to.
    ///
    /// <para><b>Two groups for the whole city, not two per junction.</b> A junction's approaches are
    /// split onto two opposing axes, and every junction in the town uses the same two group numbers —
    /// so a car anywhere in Hochstadt asks one of two questions, and the lens meshes need one submesh
    /// trio per group rather than one per junction. What makes that legitimate rather than a
    /// simplification is <see cref="HochstadtLayout"/>: the grid is squared up and the boulevard is the
    /// highest-ranked street at every node it passes through, so "axis 0" means the boulevard's
    /// direction at every boulevard junction by construction rather than by luck.</para>
    ///
    /// <para><b>And no phase offset, which is the part that looks like an omission and is not.</b> A
    /// green wave needs each junction to go green one travel-time after the one before it. Hochstadt's
    /// cross streets are a uniform 200 m apart and the boulevard is driven at 12.5 m/s, so that travel
    /// time is sixteen seconds — and with a sixteen-second cycle and no offset at all, a car leaving one
    /// junction on green arrives at the next exactly one cycle later, on green again. The wave falls out
    /// of the cycle length. An offset on top of it would move every junction off the beat, and it would
    /// double the number of groups to pay for the privilege.</para>
    ///
    /// <para>Edit-time only, and a plain class rather than a <c>ScriptableObject</c>: it is an
    /// intermediate that two bakers share, the same role <see cref="StreetIndex"/> plays. Nothing here
    /// is serialised — what survives into the game is the group number baked onto each lane by
    /// <see cref="TrafficNetworkBuilder"/>, and the head geometry built by
    /// <see cref="TrafficSignalMeshes"/>.</para>
    /// </summary>
    public sealed class TrafficSignalPlan
    {
        /// <summary>How many phase groups there are. Two: one per axis. See the class remarks.</summary>
        public const int Groups = 2;

        /// <summary>
        /// Within this many degrees of the reference street's line, an approach is on its axis.
        ///
        /// Forty-five, which is the only value that partitions rather than leaves a gap or an overlap:
        /// every bearing is nearer to one of the two axes than to the other.
        /// </summary>
        private const float AxisHalfAngle = 45f;

        /// <summary>Prefix offsets into the approach arrays: node i owns entries [start[i], start[i+1]).</summary>
        private readonly int[] approachStart;

        /// <summary>Flattened incident edge indices, grouped by node.</summary>
        private readonly int[] approachEdge;

        /// <summary>Which axis each approach is on. Parallel to <see cref="approachEdge"/>.</summary>
        private readonly byte[] approachGroup;

        /// <summary>How many junctions carry lights.</summary>
        public int JunctionCount { get; }

        /// <summary>How many signal heads the geometry will need — one per approach.</summary>
        public int ApproachCount => approachEdge.Length;

        public int NodeCount => approachStart.Length - 1;

        private TrafficSignalPlan(
            int[] starts, int[] edges, byte[] groups, int junctions)
        {
            approachStart = starts;
            approachEdge = edges;
            approachGroup = groups;
            JunctionCount = junctions;
        }

        /// <summary>
        /// An empty plan, for a settlement that has no lights.
        ///
        /// Talheim gets one of these rather than a null, so no caller has to know which town it is
        /// holding — <see cref="GroupOf"/> answers -1 everywhere and the mesh builder emits nothing.
        /// </summary>
        public static TrafficSignalPlan None(StreetNetwork network)
        {
            int nodes = network != null ? network.Nodes.Count : 0;
            return new TrafficSignalPlan(
                new int[nodes + 1], System.Array.Empty<int>(), System.Array.Empty<byte>(), 0);
        }

        /// <summary>
        /// Works out which junctions to signalise and splits each one's approaches onto two axes.
        ///
        /// <para><b>The rule is "two main roads meet here", not "this junction is busy".</b> A degree
        /// test alone signalises every avenue T-junction in the grid — twenty-five of the thirty-seven,
        /// which is a red light every hundred and twenty metres in every direction, and a game about
        /// the pleasure of driving does not want to be a game about waiting. Requiring either a
        /// boulevard or a second city street cuts that to the five boulevard crossings plus the eight
        /// places where the two main-street spines cross something of their own rank.</para>
        /// </summary>
        public static TrafficSignalPlan Build(StreetNetwork network)
        {
            if (network == null || network.Nodes.Count == 0)
            {
                return None(network);
            }

            var starts = new int[network.Nodes.Count + 1];
            var edges = new List<int>(64);
            var groups = new List<byte>(64);

            int junctions = 0;

            for (int i = 0; i < network.Nodes.Count; i++)
            {
                starts[i] = edges.Count;

                StreetNode node = network.Nodes[i];
                if (!ShouldSignalise(network, node))
                {
                    continue;
                }

                if (Split(network, node, edges, groups))
                {
                    junctions++;
                }
            }

            starts[network.Nodes.Count] = edges.Count;

            return new TrafficSignalPlan(starts, edges.ToArray(), groups.ToArray(), junctions);
        }

        /// <summary>
        /// The phase group of the lane that travels <i>along</i> <paramref name="edge"/> <i>towards</i>
        /// <paramref name="node"/>, or -1 where nothing controls it.
        ///
        /// <para>Keyed on the pair rather than on the edge alone, because a street between two signalised
        /// junctions is approaching one of them and leaving the other, and the two ends can be on
        /// different axes.</para>
        ///
        /// <para>A linear scan over the node's own approaches. That is at most five comparisons and it
        /// runs a few hundred times during a bake; an index keyed on the pair would be more machinery
        /// than the thing it looks up.</para>
        /// </summary>
        public int GroupOf(int node, int edge)
        {
            if (node < 0 || node + 1 >= approachStart.Length)
            {
                return -1;
            }

            for (int i = approachStart[node]; i < approachStart[node + 1]; i++)
            {
                if (approachEdge[i] == edge)
                {
                    return approachGroup[i];
                }
            }

            return -1;
        }

        /// <summary>True where this node carries lights at all.</summary>
        public bool IsSignalised(int node)
        {
            return node >= 0 && node + 1 < approachStart.Length
                   && approachStart[node + 1] > approachStart[node];
        }

        /// <summary>One approach, for the mesh bake. Walk <see cref="ApproachCount"/> of them.</summary>
        public void GetApproach(int index, out int node, out int edge, out int group)
        {
            edge = approachEdge[index];
            group = approachGroup[index];

            // Which node owns this entry, from the prefix offsets. A scan rather than a stored parallel
            // array: the caller walks the approaches in order, so this is a walk alongside it.
            node = 0;
            for (int i = 0; i + 1 < approachStart.Length; i++)
            {
                if (index >= approachStart[i] && index < approachStart[i + 1])
                {
                    node = i;
                    break;
                }
            }
        }

        /// <summary>
        /// Whether a junction is important enough to control.
        ///
        /// Trunk-road nodes are excluded outright: a bell-mouth onto a mountain pass is a village
        /// entrance, and a light there would stop seventy-kilometre-an-hour traffic for a lane with four
        /// houses on it. Dead ends and simple corners have nothing to arbitrate.
        /// </summary>
        private static bool ShouldSignalise(StreetNetwork network, StreetNode node)
        {
            if (node.OnTrunkRoad || node.Degree < 3)
            {
                return false;
            }

            int cityStreets = 0;

            for (int i = 0; i < node.Degree; i++)
            {
                switch (network.Edges[node.Edges[i]].Kind)
                {
                    case TownStreetKind.Boulevard:
                        return true;

                    case TownStreetKind.CityStreet:
                        cityStreets++;
                        break;
                }
            }

            return cityStreets >= 2;
        }

        /// <summary>
        /// Splits one junction's approaches onto two opposing axes.
        ///
        /// <para>The reference is the highest-ranking street at the node, so the axis numbering follows
        /// the road that matters rather than whichever arm happened to be listed first. Everything within
        /// forty-five degrees of its line — <i>either</i> way along it, which is what taking the cosine
        /// rather than the angle gets — is on axis 0; the rest is axis 1.</para>
        ///
        /// <para>Returns false, and emits nothing, where one axis would be empty. That is a junction
        /// whose arms all run the same way, and signalising it would leave it all-red for half of every
        /// cycle serving a phase nobody is on.</para>
        /// </summary>
        private static bool Split(
            StreetNetwork network, StreetNode node, List<int> edges, List<byte> groups)
        {
            int reference = ReferenceArm(network, node);
            float referenceBearing = node.Bearings[reference];

            int onAxis = 0;
            int offAxis = 0;

            int firstEntry = edges.Count;

            for (int i = 0; i < node.Degree; i++)
            {
                float delta = Mathf.Abs(Mathf.DeltaAngle(node.Bearings[i], referenceBearing));

                // Folded onto a half turn: an arm pointing back down the reference street is on the
                // reference street's axis, not across it.
                if (delta > 90f)
                {
                    delta = 180f - delta;
                }

                bool sameAxis = delta <= AxisHalfAngle;

                edges.Add(node.Edges[i]);
                groups.Add(sameAxis ? (byte)0 : (byte)1);

                if (sameAxis)
                {
                    onAxis++;
                }
                else
                {
                    offAxis++;
                }
            }

            if (onAxis > 0 && offAxis > 0)
            {
                return true;
            }

            edges.RemoveRange(firstEntry, edges.Count - firstEntry);
            groups.RemoveRange(firstEntry, groups.Count - firstEntry);
            return false;
        }

        /// <summary>
        /// Which arm the axes are measured from: the widest kind of street at the node, and among equals
        /// the one with the lowest bearing so the answer does not depend on the order the layout table
        /// happened to list them in.
        /// </summary>
        private static int ReferenceArm(StreetNetwork network, StreetNode node)
        {
            int best = 0;
            int bestRank = -1;

            for (int i = 0; i < node.Degree; i++)
            {
                int rank = Rank(network.Edges[node.Edges[i]].Kind);
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>How important a street is, for picking the reference arm. Higher is wider.</summary>
        private static int Rank(TownStreetKind kind)
        {
            switch (kind)
            {
                case TownStreetKind.Boulevard: return 5;
                case TownStreetKind.CityStreet: return 4;
                case TownStreetKind.HighStreet: return 3;
                case TownStreetKind.Avenue: return 2;
                case TownStreetKind.SquareEdge: return 1;
                default: return 0;
            }
        }
    }
}
