using Horizon.Net;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The best lap each player in the room has driven, on each circuit.
    ///
    /// <para><b><c>LapTiming</c> gains no networking, and that is the whole reason this class
    /// exists.</b> That component owns a start line, six gates that have to be passed in order, and a
    /// clock; giving it a peer id and a send rate as well would make it a class that does two things
    /// and can only be understood by reading both. This one watches it from outside — a lap it has not
    /// seen before is a new number on a public property — which is the same relationship
    /// <c>LapTimer</c> already has with it.</para>
    ///
    /// <para><b>A circuit is an index, and the index is the alphabetical order of the names.</b> The
    /// world is baked, so both devices have exactly the same two circuits with exactly the same names;
    /// what they do not have is the same <c>FindObjectsByType</c> order, which is unspecified. Sorting
    /// by name is the cheapest thing that is the same on both. Sending the name instead would be
    /// sixteen bytes per row to say "Weissjochring" over and over on a table rebroadcast every
    /// second.</para>
    ///
    /// <para><b>The host keeps the table; a guest sends a claim and is told the answer.</b> There is
    /// no checking of any kind — these are friends, and a lap board that argued with the person who
    /// drove the lap would be worse than one that can be lied to.</para>
    /// </summary>
    public sealed class NetLapBoard : MonoBehaviour
    {
        /// <summary>Two circuits today, and the array is sized from what the world actually has.</summary>
        private const int MaxCircuits = 4;

        private readonly LapEntry[] entries = new LapEntry[NetProtocol.MaxPeers * MaxCircuits];

        private LapTiming[] circuits;
        private readonly float[] lastSeen = new float[MaxCircuits];

        [Tooltip("Found in Bootstrap. Told about a lap this device drove.")]
        [SerializeField] private NetSession session;

        /// <summary>How many rows the table can hold. Rows that are not in use are skipped by readers.</summary>
        public int EntryCount => entries.Length;

        public LapEntry EntryAt(int index) =>
            index >= 0 && index < entries.Length ? entries[index] : default;

        /// <summary>The circuit names, in the order the wire indexes them.</summary>
        public string CircuitName(int circuit) =>
            circuits != null && circuit >= 0 && circuit < circuits.Length && circuits[circuit] != null
                ? circuits[circuit].CircuitName
                : string.Empty;

        public int CircuitCount => circuits != null ? circuits.Length : 0;

        /// <summary>The best time a peer has on a circuit, in milliseconds. Zero when there is none.</summary>
        public uint BestOf(byte peerId, int circuit)
        {
            int slot = SlotOf(peerId, circuit);
            return slot >= 0 ? entries[slot].TimeMilliseconds : 0u;
        }

        private void Awake()
        {
            if (session == null)
            {
                session = FindFirstObjectByType<NetSession>();
            }

            if (session != null)
            {
                session.LapBoard = this;
            }
        }

        private void Update()
        {
            if (circuits == null || circuits.Length == 0)
            {
                // Retried while empty, for the reason LapTimer gives: this lives in Bootstrap and the
                // circuits are in the world scene, which is still loading for the first frames.
                circuits = FindObjectsByType<LapTiming>(FindObjectsSortMode.None);

                if (circuits.Length == 0)
                {
                    return;
                }

                System.Array.Sort(circuits, CompareByName);

                for (int i = 0; i < lastSeen.Length; i++)
                {
                    lastSeen[i] = 0f;
                }
            }

            if (session == null || !session.InRoom)
            {
                return;
            }

            WatchForLaps();
        }

        /// <summary>
        /// Notices a lap this device has just driven and reports it.
        ///
        /// <para>Watched as a change in <c>LapTiming.Last</c> rather than through an event, because
        /// that property is the whole of what that class publishes about a finished lap and adding an
        /// event to it for one reader is the coupling this class exists to avoid.</para>
        /// </summary>
        private void WatchForLaps()
        {
            for (int i = 0; i < circuits.Length && i < MaxCircuits; i++)
            {
                LapTiming timing = circuits[i];

                if (timing == null || timing.Last <= 0f || Mathf.Approximately(timing.Last, lastSeen[i]))
                {
                    continue;
                }

                lastSeen[i] = timing.Last;

                var milliseconds = (uint)Mathf.RoundToInt(timing.Last * 1000f);

                if (session.Role == NetRole.Host)
                {
                    Accept(session.LocalPeerId, (byte)i, milliseconds);
                }
                else if (session.Admitted)
                {
                    // Recorded locally too, so the board shows it at once rather than a second later
                    // when the host's table comes back.
                    Accept(session.LocalPeerId, (byte)i, milliseconds);
                    session.ClaimLap((byte)i, milliseconds);
                }
            }
        }

        /// <summary>Takes a lap, keeping it only if it beats what that peer already has.</summary>
        public void Accept(byte peerId, byte circuit, uint milliseconds)
        {
            if (peerId >= NetProtocol.MaxPeers || circuit >= MaxCircuits || milliseconds == 0u)
            {
                return;
            }

            int slot = peerId * MaxCircuits + circuit;

            if (entries[slot].InUse && entries[slot].TimeMilliseconds <= milliseconds)
            {
                return;
            }

            entries[slot] = new LapEntry
            {
                PeerId = peerId,
                Circuit = circuit,
                TimeMilliseconds = milliseconds,
            };
        }

        /// <summary>
        /// A guest is about to take the host's whole table.
        ///
        /// <para>Replace rather than merge, because the host's table <i>is</i> the answer — that is
        /// what makes this state rather than a stream of events, and it is what lets a guest that
        /// missed a packet be correct one second later with no repair logic anywhere.</para>
        /// </summary>
        public void BeginReplace() => System.Array.Clear(entries, 0, entries.Length);

        public void EndReplace()
        {
        }

        /// <summary>Somebody has left. Their times go with them; the room is the scoreboard.</summary>
        public void Forget(byte peerId)
        {
            if (peerId >= NetProtocol.MaxPeers)
            {
                return;
            }

            for (int circuit = 0; circuit < MaxCircuits; circuit++)
            {
                entries[peerId * MaxCircuits + circuit] = default;
            }
        }

        private int SlotOf(byte peerId, int circuit)
        {
            if (peerId >= NetProtocol.MaxPeers || circuit < 0 || circuit >= MaxCircuits)
            {
                return -1;
            }

            return peerId * MaxCircuits + circuit;
        }

        private static int CompareByName(LapTiming a, LapTiming b)
        {
            string left = a != null ? a.CircuitName : string.Empty;
            string right = b != null ? b.CircuitName : string.Empty;
            return string.CompareOrdinal(left, right);
        }
    }
}
