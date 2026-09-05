using UnityEngine;

namespace Horizon.Net
{
    /// <summary>
    /// A transport that talks to nobody: it holds everything this device sends for a moment and hands
    /// it straight back as though a guest had sent it.
    ///
    /// <para><b>This is the most valuable thing in the multiplayer work and it is thirty lines.</b>
    /// Every other part of the feature — the snapshot buffer, the interpolation, the wheels turning,
    /// the brake lamps, the name tag, the mark on the minimap, a car appearing and being timed out —
    /// can be built and, far more to the point, <i>looked at</i> in the editor on one machine with no
    /// build, no second device and no network. You drive along behind a ghost of yourself a third of a
    /// second in the past. Everything that is wrong with the presentation is wrong in that picture
    /// too.</para>
    ///
    /// <para>It knows nothing about the protocol above it, which is the test that the seam is real: if
    /// this class ever needs to understand a message to be useful, the session has grown a dependency
    /// on its transport that a relay would not satisfy either. The only reason it works with no
    /// synthesised <c>Hello</c> is that <c>NetSession</c> admits a channel that speaks rather than one
    /// that introduces itself — which is the right rule for a real guest whose first packet went
    /// missing, and this makes it a rule that gets exercised every time anybody tests.</para>
    /// </summary>
    public sealed class LoopbackTransport : INetTransport
    {
        /// <summary>
        /// How far behind the ghost runs.
        ///
        /// <para>Deliberately far longer than a real network — a third of a second rather than the
        /// twenty milliseconds a home LAN costs — because the point of the picture is to see the
        /// interpolation working, and a ghost that sits inside your own car answers nothing.</para>
        /// </summary>
        public float Delay = 0.35f;

        /// <summary>Enough that a burst never overruns at fifteen hertz against a third of a second.</summary>
        private const int QueueSlots = 64;

        private readonly byte[][] pending = new byte[QueueSlots][];
        private readonly int[] lengths = new int[QueueSlots];
        private readonly float[] dueAt = new float[QueueSlots];
        private int head;
        private int tail;
        private float clock;

        public LoopbackTransport()
        {
            for (int i = 0; i < QueueSlots; i++)
            {
                pending[i] = new byte[NetProtocol.MaxDatagramBytes];
            }
        }

        public NetRole Role { get; private set; } = NetRole.Offline;

        public NetStatus Status { get; private set; } = NetStatus.Idle;

        public string LastError => string.Empty;

        /// <summary>One, always: the ghost.</summary>
        public int ChannelCount => Role == NetRole.Host ? 1 : 0;

        public void StartHost()
        {
            Role = NetRole.Host;
            Status = NetStatus.Connected;
            head = 0;
            tail = 0;
            clock = 0f;
        }

        /// <summary>There is nothing to join. Hosting is the only thing this can do.</summary>
        public void StartGuest(string address) => StartHost();

        public void Stop()
        {
            Role = NetRole.Offline;
            Status = NetStatus.Idle;
            head = 0;
            tail = 0;
        }

        public void Tick(float deltaTime) => clock += deltaTime;

        public void Send(byte[] buffer, int length, int channel)
        {
            if (Role == NetRole.Offline || length <= 0 || length > NetProtocol.MaxDatagramBytes)
            {
                return;
            }

            int next = (tail + 1) % QueueSlots;

            if (next == head)
            {
                // Full. Dropping the newest rather than the oldest keeps the ghost's delay honest;
                // dropping the oldest would silently shorten it under load.
                return;
            }

            System.Array.Copy(buffer, pending[tail], length);
            lengths[tail] = length;
            dueAt[tail] = clock + Mathf.Max(0f, Delay);
            tail = next;
        }

        public int Receive(byte[] buffer, out int channel)
        {
            channel = 0;

            if (head == tail || clock < dueAt[head])
            {
                return 0;
            }

            int length = lengths[head];
            System.Array.Copy(pending[head], buffer, length);
            head = (head + 1) % QueueSlots;
            return length;
        }

        public void Dispose() => Stop();
    }
}
