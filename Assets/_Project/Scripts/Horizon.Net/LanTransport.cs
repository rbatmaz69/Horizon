using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace Horizon.Net
{
    /// <summary>
    /// UDP on the local network: one host, up to seven guests, and a broadcast beacon so a guest can
    /// find the host without being told its address.
    ///
    /// <para><b>UDP and not TCP, which is the opposite of what the relay will be.</b> A snapshot is
    /// worth nothing the moment the next one arrives, so re-sending a lost one is worse than dropping
    /// it: TCP would hold the newer packet back until the older one had been retransmitted, and the
    /// car would stop and then jump. That is head-of-line blocking, and it is the reason this protocol
    /// carries no reliability layer at all — see <see cref="NetMessage.Room"/> for how joining,
    /// leaving and the weather are made resilient by being <i>state</i> that is resent every second
    /// rather than events that have to arrive.</para>
    ///
    /// <para><b>Nothing here allocates once it is running, and that took two tricks.</b> A guest
    /// <c>Connect</c>s its socket to the host, so it can use <c>Receive</c> and <c>Send</c> with no
    /// endpoint at all. A host cannot — it has several guests — so it uses
    /// <see cref="FastEndPoint"/>, which caches its own <c>SocketAddress</c> and returns itself from
    /// <c>Create</c>. The ordinary spelling allocates an <c>IPEndPoint</c> for every datagram received
    /// and a <c>SocketAddress</c> for every one sent, which at fifteen hertz across seven guests is a
    /// couple of kilobytes a second of garbage in a game whose budget forbids any.</para>
    ///
    /// <para><b>Two things about a local network will defeat this and neither is a bug.</b> Most
    /// routers and effectively every public network have client isolation on, and then two devices
    /// cannot exchange a packet at all. And Android drops broadcast frames unless something holds a
    /// multicast lock — see <see cref="AndroidMulticastLock"/>, which is why joining by typed address
    /// exists beside the discovered list rather than as a fallback nobody finds.</para>
    /// </summary>
    public sealed class LanTransport : INetTransport, INetDiscovery
    {
        /// <summary>Seven, because the host is the eighth.</summary>
        private const int MaxChannels = NetProtocol.MaxPeers - 1;

        /// <summary>A channel that has said nothing for this long is forgotten by the transport.</summary>
        private const float ChannelTimeout = NetProtocol.PeerTimeout * 2f;

        private Socket game;
        private Socket beacon;

        private readonly FastEndPoint[] channels = new FastEndPoint[MaxChannels];
        private readonly float[] channelHeardAt = new float[MaxChannels];
        private readonly FastEndPoint receiveFrom = new FastEndPoint();
        private readonly FastEndPoint beaconFrom = new FastEndPoint();

        private FastEndPoint hostEndPoint;
        private FastEndPoint broadcastEndPoint;
        private FastEndPoint subnetEndPoint;

        private float clock;
        private string localAddress = string.Empty;

        public NetRole Role { get; private set; } = NetRole.Offline;

        public NetStatus Status { get; private set; } = NetStatus.Idle;

        public string LastError { get; private set; } = string.Empty;

        public int ChannelCount => MaxChannels;

        public bool IsAdvertising { get; private set; }

        public bool IsBrowsing { get; private set; }

        public string LocalAddress => localAddress;

        // --- Opening and closing -------------------------------------------------------------

        public void StartHost()
        {
            Stop();

            try
            {
                game = OpenSocket(NetProtocol.GamePort);
                Role = NetRole.Host;

                // A host is connected the moment it is listening. There is nothing to wait for: guests
                // arrive or they do not, and a menu that said "connecting…" forever would be lying.
                Status = NetStatus.Connected;

                ResolveLocalAddresses();
            }
            catch (Exception error)
            {
                Fail($"Could not open port {NetProtocol.GamePort}: {error.Message}");
            }
        }

        public void StartGuest(string address)
        {
            Stop();

            try
            {
                if (!IPAddress.TryParse(address == null ? string.Empty : address.Trim(), out IPAddress parsed))
                {
                    Fail($"'{address}' is not an address on this network.");
                    return;
                }

                // Ephemeral local port: a guest does not need a well-known one, and binding to the
                // game port would stop two guests running on one machine — which is exactly how this
                // gets tested in the editor.
                game = OpenSocket(0);

                hostEndPoint = new FastEndPoint(parsed, NetProtocol.GamePort);

                // Connected, so Receive and Send need no endpoint and therefore allocate nothing. It
                // also filters: anything not from the host is dropped by the kernel rather than by us.
                game.Connect(hostEndPoint);

                Role = NetRole.Guest;
                Status = NetStatus.Connecting;

                ResolveLocalAddresses();
            }
            catch (Exception error)
            {
                Fail($"Could not reach {address}: {error.Message}");
            }
        }

        public void Stop()
        {
            StopAdvertising();
            StopBrowsing();

            Close(ref game);

            for (int i = 0; i < MaxChannels; i++)
            {
                channels[i] = null;
                channelHeardAt[i] = 0f;
            }

            hostEndPoint = null;
            Role = NetRole.Offline;
            Status = NetStatus.Idle;
            clock = 0f;
        }

        public void Dispose() => Stop();

        // --- The beat ------------------------------------------------------------------------

        public void Tick(float deltaTime)
        {
            clock += deltaTime;

            if (Role != NetRole.Host)
            {
                return;
            }

            for (int i = 0; i < MaxChannels; i++)
            {
                if (channels[i] != null && clock - channelHeardAt[i] > ChannelTimeout)
                {
                    channels[i] = null;
                }
            }
        }

        // --- Moving bytes --------------------------------------------------------------------

        public void Send(byte[] buffer, int length, int channel)
        {
            if (game == null || length <= 0)
            {
                return;
            }

            try
            {
                if (Role == NetRole.Guest)
                {
                    // Connected socket: no endpoint, no SocketAddress, no allocation.
                    game.Send(buffer, 0, length, SocketFlags.None);
                    return;
                }

                if (channel >= 0)
                {
                    if (channel < MaxChannels && channels[channel] != null)
                    {
                        game.SendTo(buffer, 0, length, SocketFlags.None, channels[channel]);
                    }

                    return;
                }

                for (int i = 0; i < MaxChannels; i++)
                {
                    if (channels[i] != null)
                    {
                        game.SendTo(buffer, 0, length, SocketFlags.None, channels[i]);
                    }
                }
            }
            catch (SocketException error)
            {
                // A single failed send is not worth tearing the session down over — a guest that has
                // walked out of range produces one of these per tick until the timeout removes it.
                LastError = error.SocketErrorCode.ToString();
            }
        }

        public int Receive(byte[] buffer, out int channel)
        {
            channel = 0;

            if (game == null)
            {
                return 0;
            }

            try
            {
                if (!game.Poll(0, SelectMode.SelectRead))
                {
                    return 0;
                }

                if (Role == NetRole.Guest)
                {
                    int received = game.Receive(buffer, 0, buffer.Length, SocketFlags.None);

                    if (received > 0)
                    {
                        Status = NetStatus.Connected;
                    }

                    return received;
                }

                EndPoint from = receiveFrom;
                int length = game.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);

                if (length <= 0)
                {
                    return 0;
                }

                channel = ChannelFor(receiveFrom);
                return channel >= 0 ? length : 0;
            }
            catch (SocketException error)
            {
                if (error.SocketErrorCode == SocketError.WouldBlock)
                {
                    return 0;
                }

                // Windows hands back ConnectionReset for an ICMP port-unreachable caused by an earlier
                // send. It says a guest has gone, not that this socket is broken.
                if (error.SocketErrorCode == SocketError.ConnectionReset)
                {
                    return 0;
                }

                LastError = error.SocketErrorCode.ToString();
                return 0;
            }
        }

        /// <summary>
        /// Which channel a datagram came from, taking a free one if this sender is new.
        ///
        /// <para>Returns −1 when the room is full, and the datagram is dropped rather than answered.
        /// The eighth guest sees a room that never admits them, which is the honest outcome; a
        /// rejection would need the host to reply to an address it has decided not to keep.</para>
        /// </summary>
        private int ChannelFor(FastEndPoint sender)
        {
            int free = -1;

            for (int i = 0; i < MaxChannels; i++)
            {
                if (channels[i] == null)
                {
                    if (free < 0)
                    {
                        free = i;
                    }

                    continue;
                }

                if (channels[i].RawAddress == sender.RawAddress && channels[i].RawPort == sender.RawPort)
                {
                    channelHeardAt[i] = clock;
                    return i;
                }
            }

            if (free < 0)
            {
                return -1;
            }

            // Allocates once, when somebody joins. Everything after that reuses it.
            channels[free] = new FastEndPoint(sender.ToIPAddress(), sender.RawPort);
            channelHeardAt[free] = clock;
            return free;
        }

        // --- Discovery -----------------------------------------------------------------------

        public void StartAdvertising()
        {
            if (IsAdvertising)
            {
                return;
            }

            try
            {
                beacon = OpenSocket(0);
                beacon.EnableBroadcast = true;
                broadcastEndPoint = new FastEndPoint(IPAddress.Broadcast, NetProtocol.DiscoveryPort);
                ResolveLocalAddresses();
                IsAdvertising = true;

                // Sending does not need the lock, but a host is also the device most likely to want to
                // see that its own beacon is going out.
                AndroidMulticastLock.Acquire();
            }
            catch (Exception error)
            {
                Fail($"Could not start advertising: {error.Message}");
            }
        }

        public void StopAdvertising()
        {
            if (!IsAdvertising)
            {
                return;
            }

            IsAdvertising = false;
            AndroidMulticastLock.Release();

            if (!IsBrowsing)
            {
                Close(ref beacon);
            }
        }

        public void StartBrowsing()
        {
            if (IsBrowsing)
            {
                return;
            }

            try
            {
                // Bound to the discovery port so beacons land here. ReuseAddress so a host browsing on
                // the same machine — which is how two editor instances get tested — does not fail.
                Close(ref beacon);
                beacon = OpenSocket(NetProtocol.DiscoveryPort);
                beacon.EnableBroadcast = true;
                IsBrowsing = true;

                // The one that actually matters. See AndroidMulticastLock.
                AndroidMulticastLock.Acquire();
            }
            catch (Exception error)
            {
                Fail($"Could not listen for hosts: {error.Message}");
            }
        }

        public void StopBrowsing()
        {
            if (!IsBrowsing)
            {
                return;
            }

            IsBrowsing = false;
            AndroidMulticastLock.Release();
            Close(ref beacon);
        }

        public void Advertise(byte[] buffer, int length)
        {
            if (beacon == null || !IsAdvertising || length <= 0)
            {
                return;
            }

            try
            {
                if (broadcastEndPoint != null)
                {
                    beacon.SendTo(buffer, 0, length, SocketFlags.None, broadcastEndPoint);
                }

                // 255.255.255.255 is refused or dropped on some Android builds and some routers, and
                // the directed broadcast for the subnet the device is actually on gets through where it
                // does not. Sending both is two datagrams twice a second.
                if (subnetEndPoint != null)
                {
                    beacon.SendTo(buffer, 0, length, SocketFlags.None, subnetEndPoint);
                }
            }
            catch (SocketException error)
            {
                LastError = error.SocketErrorCode.ToString();
            }
        }

        public int ReceiveBeacon(byte[] buffer, out string fromAddress)
        {
            fromAddress = string.Empty;

            if (beacon == null || !IsBrowsing)
            {
                return 0;
            }

            try
            {
                if (!beacon.Poll(0, SelectMode.SelectRead))
                {
                    return 0;
                }

                EndPoint from = beaconFrom;
                int length = beacon.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);

                if (length <= 0)
                {
                    return 0;
                }

                // Allocates a string. Twice a second per host while a menu is open, and the menu needs
                // something to put in the list and to hand back when it is tapped.
                fromAddress = beaconFrom.ToIPAddress().ToString();
                return length;
            }
            catch (SocketException error)
            {
                if (error.SocketErrorCode != SocketError.WouldBlock)
                {
                    LastError = error.SocketErrorCode.ToString();
                }

                return 0;
            }
        }

        // --- Plumbing ------------------------------------------------------------------------

        private static Socket OpenSocket(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false,
            };

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            try
            {
                // Stops Windows turning an ICMP port-unreachable into a ConnectionReset on the next
                // receive. Unsupported elsewhere, which is what the catch is for.
                socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // Not available on this platform. The receive path handles ConnectionReset anyway.
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }

        private static void Close(ref Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch (Exception)
            {
                // Closing a socket that has already failed is not news.
            }

            socket = null;
        }

        private void Fail(string message)
        {
            LastError = message;
            Status = NetStatus.Failed;
            Role = NetRole.Offline;
            Close(ref game);
            Debug.LogWarning($"[Horizon] {message}");
        }

        /// <summary>
        /// Finds this device's own address and the broadcast address of the network it is on.
        ///
        /// <para>Walks the interfaces rather than asking DNS for the host name, which on Android
        /// answers with the loopback and on a desktop with whichever adapter the resolver prefers —
        /// including, reliably, a virtual one belonging to a container runtime.</para>
        /// </summary>
        private void ResolveLocalAddresses()
        {
            localAddress = string.Empty;
            subnetEndPoint = null;

            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up
                        || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation entry in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (entry.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        byte[] address = entry.Address.GetAddressBytes();

                        if (address[0] == 127 || (address[0] == 169 && address[1] == 254))
                        {
                            continue;
                        }

                        localAddress = entry.Address.ToString();

                        byte[] mask = entry.IPv4Mask != null
                            ? entry.IPv4Mask.GetAddressBytes()
                            : new byte[] { 255, 255, 255, 0 };

                        var directed = new byte[4];

                        for (int i = 0; i < 4; i++)
                        {
                            directed[i] = (byte)(address[i] | (byte)~mask[i]);
                        }

                        subnetEndPoint = new FastEndPoint(
                            new IPAddress(directed), NetProtocol.DiscoveryPort);

                        return;
                    }
                }
            }
            catch (Exception error)
            {
                LastError = error.Message;
            }
        }

        /// <summary>
        /// An <see cref="IPEndPoint"/> that does not allocate when a socket serialises it or fills it
        /// in from a received datagram.
        ///
        /// <para><c>Socket.SendTo</c> calls <c>Serialize</c> on the endpoint it is given and
        /// <c>ReceiveFrom</c> calls <c>Create</c> on it, and the stock implementations build a fresh
        /// <c>SocketAddress</c> and a fresh <c>IPEndPoint</c> respectively — every datagram, in both
        /// directions. Both are virtual. Caching the one and returning <c>this</c> from the other is
        /// the whole trick, and it turns the socket layer from a steady source of garbage into
        /// nothing at all.</para>
        ///
        /// <para><b>An instance is used for receiving or for sending, never both.</b> A receiving one
        /// has its raw address rewritten under the cached <c>SocketAddress</c> the runtime just filled
        /// in, so serialising it afterwards would send to the last sender rather than to the intended
        /// destination.</para>
        /// </summary>
        private sealed class FastEndPoint : IPEndPoint
        {
            private readonly SocketAddress cached;

            /// <summary>Host byte order, for comparing one sender against another.</summary>
            public uint RawAddress;

            public int RawPort;

            public FastEndPoint()
                : base(IPAddress.Any, 0)
            {
                cached = base.Serialize();
            }

            public FastEndPoint(IPAddress address, int port)
                : base(address, port)
            {
                cached = base.Serialize();
                byte[] bytes = address.GetAddressBytes();
                RawAddress = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16)
                             | ((uint)bytes[2] << 8) | bytes[3];
                RawPort = port;
            }

            public override SocketAddress Serialize() => cached;

            public override EndPoint Create(SocketAddress socketAddress)
            {
                RawPort = (socketAddress[2] << 8) | socketAddress[3];
                RawAddress = ((uint)socketAddress[4] << 24) | ((uint)socketAddress[5] << 16)
                             | ((uint)socketAddress[6] << 8) | socketAddress[7];
                return this;
            }

            /// <summary>Allocates. Called once when a channel is opened, and when a beacon is listed.</summary>
            public IPAddress ToIPAddress()
            {
                return new IPAddress(new[]
                {
                    (byte)(RawAddress >> 24),
                    (byte)(RawAddress >> 16),
                    (byte)(RawAddress >> 8),
                    (byte)RawAddress,
                });
            }
        }
    }
}
