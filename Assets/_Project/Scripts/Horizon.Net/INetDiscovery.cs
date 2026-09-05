namespace Horizon.Net
{
    /// <summary>
    /// Finding a game to join, for the transports where that is a question at all.
    ///
    /// <para><b>Separate from <see cref="INetTransport"/> because a relay has no discovery.</b> There,
    /// joining is typing a room code and the server does the finding; on a local network there is no
    /// server, so a host has to shout and a guest has to listen. Folding both into the transport
    /// interface would give the relay four members it could only implement by doing nothing, which is
    /// the shape of an interface that has stopped describing anything. The menu asks
    /// <c>transport as INetDiscovery</c> and shows a host list only when it gets one.</para>
    ///
    /// <para><b>The bytes are the session's, not the transport's.</b> A beacon carries a room name, a
    /// head count and a build string — all of which are protocol, and none of which a pipe should be
    /// composing. This moves somebody else's bytes to the broadcast address and hands back whatever
    /// arrives, exactly as the transport does for everything else.</para>
    /// </summary>
    public interface INetDiscovery
    {
        /// <summary>Whether this device is currently shouting, listening, or neither.</summary>
        bool IsAdvertising { get; }

        bool IsBrowsing { get; }

        /// <summary>The address a guest would have to type in by hand. Empty when it cannot be found.</summary>
        string LocalAddress { get; }

        void StartAdvertising();

        void StopAdvertising();

        void StartBrowsing();

        void StopBrowsing();

        /// <summary>Push one beacon out. Called on the host's own beat.</summary>
        void Advertise(byte[] buffer, int length);

        /// <summary>
        /// Take one beacon if there is one, with the address it came from so the menu can join it.
        /// Returns its length, or zero.
        /// </summary>
        int ReceiveBeacon(byte[] buffer, out string fromAddress);
    }
}
