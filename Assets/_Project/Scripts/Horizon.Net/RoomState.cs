namespace Horizon.Net
{
    /// <summary>One row of the roster: who is in the room and what they are driving.</summary>
    public struct PeerInfo
    {
        public byte PeerId;
        public byte Body;
        public byte Paint;

        /// <summary>The random number this peer put in its Hello. See <c>NetProtocol.PeerRecordBytes</c>.</summary>
        public uint Token;

        public string Name;

        /// <summary>Filled in on receipt. Silence past <c>NetProtocol.PeerTimeout</c> removes the row.</summary>
        public float LastHeardAt;

        public bool InUse;
    }

    /// <summary>
    /// A lap somebody drove. Peer, circuit, time.
    ///
    /// <para>The circuit is an index rather than a name because there are two of them and a
    /// sixteen-byte string per lap on a table rebroadcast every second is a hundred and twenty-eight
    /// bytes to say "Weissjochring" over and over. <c>NetLapBoard</c> holds the two names.</para>
    /// </summary>
    public struct LapEntry
    {
        public byte PeerId;
        public byte Circuit;
        public uint TimeMilliseconds;

        public bool InUse => TimeMilliseconds > 0u;
    }

    /// <summary>
    /// What the host says the room is: everybody in it, the hour and the weather.
    ///
    /// <para><b>The host owns the clock and the sky and nothing else.</b> Each peer is authoritative
    /// for its own car and nobody corrects anybody else's, which is what makes ghost cars free — there
    /// is no state two devices can disagree about. Time and weather are the exception because they are
    /// the world rather than a car, and two friends in the same valley at different hours is the first
    /// thing anybody would photograph.</para>
    /// </summary>
    public struct RoomState
    {
        /// <summary>The host's hour, 0..24. The guest's own clock is nudged towards it.</summary>
        public float Hours;

        /// <summary>The host's weather preset, as the plain integer <c>PlayerChoices</c> stores.</summary>
        public byte Weather;

        public int PeerCount;
    }
}
