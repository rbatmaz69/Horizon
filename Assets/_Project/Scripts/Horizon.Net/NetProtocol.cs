namespace Horizon.Net
{
    /// <summary>
    /// What kind of thing a datagram is.
    ///
    /// <para><b>Appended, never inserted</b> — the value goes on the wire as a bare byte, and a peer
    /// running yesterday's build reads whatever number it was given. The protocol version below is
    /// what makes that safe to be strict about, but only if the numbers themselves stay put.</para>
    /// </summary>
    public enum NetMessage : byte
    {
        /// <summary>Guest to host, repeated until the guest appears in a <see cref="Room"/>.</summary>
        Hello = 1,

        /// <summary>
        /// Host to everyone, once a second: the whole roster, the hour and the weather.
        ///
        /// <para>This is the reason there is no reliability layer in this protocol. Joining, leaving,
        /// changing car, changing weather and moving the clock are all differences between two
        /// snapshots of one state rather than events that have to arrive. A lost packet costs a
        /// second of staleness and nothing else.</para>
        /// </summary>
        Room = 2,

        /// <summary>Cars. A guest sends its own; the host sends everybody's in one datagram.</summary>
        Snapshots = 3,

        /// <summary>Host to everyone: the lap table, on the same once-a-second beat as <see cref="Room"/>.</summary>
        Laps = 4,

        /// <summary>Guest to host: I have just driven a lap.</summary>
        LapClaim = 5,

        /// <summary>Either way: I am going. Advisory — the timeout is what actually removes a peer.</summary>
        Bye = 6,

        /// <summary>Host to a guest it will not admit, with a reason it can print.</summary>
        Reject = 7,

        /// <summary>Host to the broadcast address: here is a room, on this port.</summary>
        Beacon = 8,
    }

    /// <summary>Why a host would not let somebody in. Printed by the guest, so each one has to read.</summary>
    public enum NetReject : byte
    {
        Unknown = 0,

        /// <summary>Different <see cref="NetProtocol.Version"/>. The two cannot understand each other.</summary>
        Protocol = 1,

        /// <summary>
        /// Different build. They understand each other perfectly and disagree about where the
        /// mountains are, which is worse.
        /// </summary>
        Build = 2,

        /// <summary>Already <see cref="NetProtocol.MaxPeers"/> in the room.</summary>
        Full = 3,
    }

    /// <summary>
    /// The numbers both ends have to agree about before a single byte means anything.
    ///
    /// <para><b>Why fixed-size binary and not <c>JsonUtility</c>.</b> The one piece of serialisation
    /// this project already has is <c>ReleaseFeed</c>, which parses JSON once per app start. This runs
    /// fifteen times a second beside a physics step, and <c>JsonUtility.FromJson</c> allocates a
    /// string and an object every call. The budget's rule about no per-frame allocation in driving
    /// code is the whole reason this file is full of byte offsets.</para>
    /// </summary>
    public static class NetProtocol
    {
        /// <summary>
        /// Bumped whenever any layout below changes.
        ///
        /// <para>A peer with a different number is refused rather than tolerated. The alternative is
        /// two builds that half-understand each other, which shows up as cars in the wrong paint or
        /// at the wrong place and reads as a bug in the game rather than as a version mismatch.</para>
        /// </summary>
        public const byte Version = 1;

        /// <summary>Two bytes of 'H' and 'Z' so a stray datagram on the port is discarded early.</summary>
        public const ushort Magic = 0x5A48;

        /// <summary>
        /// Eight, which is what the protocol carries. The presentation budget is tuned so four is the
        /// comfortable case — see <c>RemoteCarPool</c> for what the other four give up.
        /// </summary>
        public const int MaxPeers = 8;

        /// <summary>The host is always peer zero. Guests are handed 1..<see cref="MaxPeers"/>-1.</summary>
        public const byte HostPeerId = 0;

        /// <summary>Nobody. Used where a peer id has to mean "none" rather than "the host".</summary>
        public const byte NoPeerId = 255;

        /// <summary>Every datagram opens with magic, version, kind, sender, count and tick.</summary>
        public const int HeaderBytes = 8;

        /// <summary>One car. See <see cref="CarSnapshot"/> for the field-by-field argument.</summary>
        public const int SnapshotBytes = 32;

        /// <summary>
        /// How long a name may be, in bytes of UTF-8.
        ///
        /// <para>Fixed rather than length-prefixed-and-packed, because every record on this wire being
        /// a constant size is what lets the reader be a handful of offsets with no bounds arithmetic.
        /// Sixteen bytes is eight to sixteen characters depending on what somebody types, which is a
        /// name rather than a sentence.</para>
        /// </summary>
        public const int NameBytes = 16;

        /// <summary>How long a build version string may be. "0.8.2" and room to grow.</summary>
        public const int BuildBytes = 16;

        /// <summary>
        /// id, body, paint, the joining token, then the name. One roster row.
        ///
        /// <para><b>The token is what tells a guest which row is its own.</b> The roster is one
        /// datagram sent to everybody — that is most of why this protocol is cheap — so it cannot
        /// carry a different "you are peer 3" to each reader. A guest puts a random 32-bit number in
        /// its <see cref="NetMessage.Hello"/> and finds itself by looking for that number coming
        /// back. It also survives a relay, where the transport has no addresses of its own to reason
        /// from, which is the seam this protocol is being kept honest for.</para>
        /// </summary>
        public const int PeerRecordBytes = 3 + 4 + NameBytes;

        /// <summary>hours, weather, peer count, spare — then the rows.</summary>
        public const int RoomHeaderBytes = 8;

        /// <summary>peer, circuit, milliseconds.</summary>
        public const int LapRecordBytes = 6;

        /// <summary>
        /// The biggest datagram this protocol can produce, which is the host's own snapshot bundle.
        /// Buffers are allocated once at this size and never grown.
        /// </summary>
        public const int MaxDatagramBytes =
            HeaderBytes + MaxPeers * (PeerRecordBytes > SnapshotBytes ? PeerRecordBytes : SnapshotBytes)
            + RoomHeaderBytes + BuildBytes + NameBytes;

        /// <summary>
        /// Fifteen a second.
        ///
        /// <para>Chosen against the interpolation delay rather than against the network: two ticks of
        /// buffer is 133 ms, which is the shortest delay that survives one lost packet without the car
        /// stopping. Thirty would halve that and double the traffic to buy smoothing the interpolator
        /// already provides.</para>
        /// </summary>
        public const float SendRate = 15f;

        /// <summary>The roster, the clock and the lap table, once a second. See <see cref="NetMessage.Room"/>.</summary>
        public const float RoomRate = 1f;

        /// <summary>A host announces itself twice a second so a guest's list fills while they look at it.</summary>
        public const float BeaconRate = 2f;

        /// <summary>
        /// Three seconds of silence and a peer is gone.
        ///
        /// <para>Forty-five missed snapshots. Long enough that a phone changing cell or a moment of
        /// congestion does not empty the room, short enough that somebody who has actually quit stops
        /// being a car parked in the road.</para>
        /// </summary>
        public const float PeerTimeout = 3f;

        /// <summary>Where a host listens for its guests.</summary>
        public const int GamePort = 47801;

        /// <summary>Where a host shouts and a guest listens. Separate, so a guest need not be a host.</summary>
        public const int DiscoveryPort = 47800;
    }
}
