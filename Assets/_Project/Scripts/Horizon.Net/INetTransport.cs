using System;

namespace Horizon.Net
{
    /// <summary>Which end of the wire this is.</summary>
    public enum NetRole : byte
    {
        Offline = 0,
        Host = 1,
        Guest = 2,
    }

    /// <summary>What the transport is doing, in words a menu can print.</summary>
    public enum NetStatus : byte
    {
        Idle = 0,
        Listening = 1,
        Connecting = 2,
        Connected = 3,
        Failed = 4,
    }

    /// <summary>
    /// A dumb pipe. It moves datagrams between this device and the others and knows nothing about
    /// cars, rooms or weather.
    ///
    /// <para><b>This interface is the only reason LAN is a first version rather than a dead end.</b>
    /// The decision was between a LAN game that needs no infrastructure and a relayed one that needs a
    /// server, and the honest answer was to build the LAN one behind a seam so the relay becomes a
    /// second class beside <c>LanTransport</c> rather than a rewrite. Everything above this line — the
    /// roster, the snapshots, the clock, the lap table — is written against these members and cannot
    /// tell which implementation is under it.</para>
    ///
    /// <para><b>Channels, not addresses.</b> A host addresses its guests by a small index; a guest has
    /// exactly one channel, the host, and it is zero. A UDP transport maps that onto endpoints and a
    /// relay would map it onto whatever the server hands out — neither of which the session above
    /// should ever have to see. The mapping from a channel to a peer id lives in <c>NetSession</c>,
    /// because a peer id is a protocol idea and a channel is a transport one.</para>
    ///
    /// <para><b>Nothing here blocks and nothing here allocates.</b> <see cref="Receive"/> fills a
    /// buffer the caller owns and returns a length, so a frame that receives nothing costs a socket
    /// poll. There is no background thread on purpose: a thread would mean either locking around
    /// every queue or touching Unity from the wrong one, and polling a non-blocking socket in
    /// <c>Update</c> costs a syscall.</para>
    /// </summary>
    public interface INetTransport : IDisposable
    {
        NetRole Role { get; }

        NetStatus Status { get; }

        /// <summary>The last thing that went wrong, ready to print. Empty when nothing has.</summary>
        string LastError { get; }

        /// <summary>How many guest channels are currently addressable. Zero on a guest.</summary>
        int ChannelCount { get; }

        /// <summary>Open as the host and start accepting guests.</summary>
        void StartHost();

        /// <summary>
        /// Open as a guest and start talking to <paramref name="address"/>.
        ///
        /// <para>What an address means belongs to the implementation: an IP for LAN, a room code for
        /// a relay. The menu hands through whatever the player chose or picked off a list.</para>
        /// </summary>
        void StartGuest(string address);

        /// <summary>Close everything. Safe to call when nothing is open.</summary>
        void Stop();

        /// <summary>
        /// Housekeeping the transport does on its own beat — beacons, endpoint expiry.
        /// Called once a frame before any <see cref="Receive"/>.
        /// </summary>
        void Tick(float deltaTime);

        /// <summary>Send to one channel, or to every channel when <paramref name="channel"/> is −1.</summary>
        void Send(byte[] buffer, int length, int channel);

        /// <summary>
        /// Take one datagram if there is one. Returns its length, or zero when there is nothing.
        ///
        /// <para>Call it in a loop until it returns zero. One call per frame would build a backlog at
        /// fifteen hertz against a sixty hertz frame the moment two peers are sending.</para>
        /// </summary>
        int Receive(byte[] buffer, out int channel);
    }
}
