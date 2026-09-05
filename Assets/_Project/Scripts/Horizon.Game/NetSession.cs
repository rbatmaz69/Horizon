using Horizon.Atmosphere;
using Horizon.Input;
using Horizon.Net;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Everything above the wire: who is in the room, where their cars are, what time it is and what
    /// the weather is doing.
    ///
    /// <para><b>One owner and four consumers</b>, which is the shape <c>WeatherDirector</c> already
    /// argues for one system along. This class reads the transport and pushes the answer to the car
    /// pool, the clock, the weather and the lap board. Four separate readers of the same datagrams,
    /// each with a rule of its own about when a peer has gone quiet, would be four things able to
    /// disagree about whether somebody is still in the room — and what would show is a car parked in
    /// the road with no name over it.</para>
    ///
    /// <para><b>There is no reliability layer, and that is the design rather than an omission.</b>
    /// Joining, leaving, changing car, moving the clock and changing the weather are all differences
    /// between two pictures of one state, so the host simply sends the whole picture once a second and
    /// a guest takes the newest one it has. Nothing has to be acknowledged, re-sent or numbered; a
    /// lost packet costs a second of staleness. The alternative — events with acknowledgements — is
    /// several hundred lines whose failure mode is a room that disagrees with itself and no way to
    /// tell which end is wrong.</para>
    ///
    /// <para><b>A channel that speaks is admitted, whether or not it introduced itself.</b> A guest
    /// whose <c>Hello</c> was dropped would otherwise send snapshots into a host that never draws
    /// them, forever, because the thing that would fix it is the packet that went missing. The car
    /// carries its own body and paint in every snapshot, so an anonymous peer is drawn correctly from
    /// its first frame and gains a name when the <c>Hello</c> gets through.</para>
    ///
    /// <para><b>Remote cars are silent, deliberately.</b> The note against <c>EngineAudio</c> settles
    /// this: this is a driving game, the car is the subject, and anything in the mix that is not the
    /// player's own car is competing with it. A second engine at a distance is exactly the "second
    /// thing to listen to" that got the ambient world audio removed. What conveys another car is that
    /// you can see it.</para>
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        /// <summary>One discovered host, as the menu lists it.</summary>
        public struct FoundHost
        {
            public string Address;
            public string Name;
            public int Players;
            public bool Compatible;
            public float SeenAt;
        }

        [Tooltip("Found in the loaded world. Holds the cars other players are drawn with.")]
        [SerializeField] private RemoteCarPool pool;

        [Tooltip("Found in the loaded world. The clock the host owns while a room is open.")]
        [SerializeField] private TimeOfDayController timeOfDay;

        [Tooltip("The pause menu, for the one thing it owns that the host may change: the weather.")]
        [SerializeField] private PauseMenu pauseMenu;

        [Tooltip("Talks to nobody and hands this device's own snapshots back a third of a second late. "
               + "The way every part of this is developed and looked at without a second device.")]
        [SerializeField] private bool useLoopback;

        private INetTransport transport;
        private INetDiscovery discovery;

        private VehicleController vehicle;
        private Rigidbody body;
        private VehicleLights lights;

        private readonly byte[] outgoing = new byte[NetProtocol.MaxDatagramBytes];
        private readonly byte[] incoming = new byte[NetProtocol.MaxDatagramBytes];
        private readonly byte[] beaconOut = new byte[NetProtocol.MaxDatagramBytes];
        private readonly byte[] beaconIn = new byte[NetProtocol.MaxDatagramBytes];
        private readonly byte[] nameScratch = new byte[NetProtocol.NameBytes];
        private readonly byte[] buildScratch = new byte[NetProtocol.BuildBytes];

        private readonly PeerInfo[] roster = new PeerInfo[NetProtocol.MaxPeers];
        private readonly byte[][] rosterNameBytes = new byte[NetProtocol.MaxPeers][];
        private readonly CarSnapshot[] latest = new CarSnapshot[NetProtocol.MaxPeers];
        private readonly bool[] hasLatest = new bool[NetProtocol.MaxPeers];

        /// <summary>Which peers a roster packet mentioned. A field, because it is used once a second.</summary>
        private readonly bool[] seenInRoom = new bool[NetProtocol.MaxPeers];

        private readonly FoundHost[] found = new FoundHost[16];
        private int foundCount;

        private float sendTimer;
        private float roomTimer;
        private float beaconTimer;
        private ushort tick;

        private uint token;
        private float joiningSince;
        private Vector3 lastSentPosition;
        private bool hasLastSent;

        private WeatherPreset restoreWeather;
        private float restoreHours;
        private bool hasRestore;

        /// <summary>Bytes in and out over the last second. Printed by the debug overlay.</summary>
        public int BytesInPerSecond { get; private set; }

        public int BytesOutPerSecond { get; private set; }

        private int bytesIn;
        private int bytesOut;
        private float rateTimer;

        // --- What the menus and the HUD read -------------------------------------------------

        public NetRole Role => transport != null ? transport.Role : NetRole.Offline;

        public NetStatus Status => transport != null ? transport.Status : NetStatus.Idle;

        public bool InRoom => Role != NetRole.Offline;

        /// <summary>True while this device is taking its world from somebody else.</summary>
        public bool IsGuest => Role == NetRole.Guest;

        /// <summary>Our own peer number, or <c>NetProtocol.NoPeerId</c> before the host has admitted us.</summary>
        public byte LocalPeerId { get; private set; } = NetProtocol.NoPeerId;

        /// <summary>Whether the host has this device in its roster yet.</summary>
        public bool Admitted => LocalPeerId != NetProtocol.NoPeerId;

        /// <summary>
        /// How long this device has been asking to be let in, seconds. Zero when it is not.
        ///
        /// <para><b>Nothing else can tell an unreachable host from a slow one.</b> A guest sends its
        /// Hello into a socket that is perfectly happy to accept it — UDP to an address with nothing
        /// listening produces no error on most platforms — so a wrong address, a router with client
        /// isolation on and a host that has just quit all look identical from here: silence. Without
        /// this the page says "Joining..." for ever and the player has no way to tell whether to wait
        /// or to check the number they typed.</para>
        /// </summary>
        public float JoiningFor =>
            Role == NetRole.Guest && !Admitted ? Time.unscaledTime - joiningSince : 0f;

        /// <summary>How long to keep asking before saying that nothing is answering.</summary>
        public const float JoinPatience = 8f;

        /// <summary>What a guest would have to type in to reach this device.</summary>
        public string LocalAddress => discovery != null ? discovery.LocalAddress : string.Empty;

        public string LastError => transport != null ? transport.LastError : string.Empty;

        /// <summary>Why the host would not admit us, if that is what happened.</summary>
        public NetReject RejectedBecause { get; private set; }

        public bool WasRejected { get; private set; }

        public int PeerCount
        {
            get
            {
                int total = 0;

                for (int i = 0; i < roster.Length; i++)
                {
                    if (roster[i].InUse)
                    {
                        total++;
                    }
                }

                return total;
            }
        }

        public PeerInfo PeerAt(int index) =>
            index >= 0 && index < roster.Length ? roster[index] : default;

        public int FoundHostCount => foundCount;

        public FoundHost FoundHostAt(int index) =>
            index >= 0 && index < foundCount ? found[index] : default;

        public bool IsBrowsing => discovery != null && discovery.IsBrowsing;

        /// <summary>The lap board, when there is one. Null when nothing has been driven.</summary>
        public NetLapBoard LapBoard { get; set; }

        // --- Lifecycle ------------------------------------------------------------------------

        private void Awake()
        {
            for (int i = 0; i < rosterNameBytes.Length; i++)
            {
                rosterNameBytes[i] = new byte[NetProtocol.NameBytes];
            }
        }

        /// <summary>
        /// Called by <c>GameBootstrap</c> once the world scene is up, with the car this device drives.
        /// </summary>
        public void OnWorldReady(VehicleController playerVehicle)
        {
            vehicle = playerVehicle;
            body = playerVehicle != null ? playerVehicle.GetComponent<Rigidbody>() : null;
            lights = playerVehicle != null ? playerVehicle.GetComponentInChildren<VehicleLights>() : null;

            if (pool == null)
            {
                pool = FindFirstObjectByType<RemoteCarPool>();
            }

            if (timeOfDay == null)
            {
                timeOfDay = FindFirstObjectByType<TimeOfDayController>();
            }

            if (pauseMenu == null)
            {
                pauseMenu = FindFirstObjectByType<PauseMenu>();
            }
        }

        private void OnDestroy() => Leave();

        // --- Opening and closing a room --------------------------------------------------------

        public void HostGame()
        {
            Leave();

            transport = useLoopback ? new LoopbackTransport() : (INetTransport)new LanTransport();
            discovery = transport as INetDiscovery;

            transport.StartHost();

            if (transport.Status == NetStatus.Failed)
            {
                return;
            }

            LocalPeerId = NetProtocol.HostPeerId;
            AddSelfToRoster();
            discovery?.StartAdvertising();
            RememberOwnConditions();
        }

        public void JoinGame(string address)
        {
            Leave();

            transport = new LanTransport();
            discovery = transport as INetDiscovery;

            // A fresh number every attempt. It is how a guest recognises its own row in a roster that
            // is one datagram sent to everybody — see NetProtocol.PeerRecordBytes.
            //
            // Never zero: that is what the host's own row carries, and a guest that drew it would
            // decide it was the host. One chance in four billion, and the fix is one line.
            do
            {
                token = (uint)Random.Range(int.MinValue, int.MaxValue);
            }
            while (token == 0u);

            transport.StartGuest(address);

            if (transport.Status == NetStatus.Failed)
            {
                return;
            }

            LocalPeerId = NetProtocol.NoPeerId;
            WasRejected = false;
            joiningSince = Time.unscaledTime;
            RememberOwnConditions();
        }

        /// <summary>
        /// Closes the room and puts this device's own weather and hour back.
        ///
        /// <para><b>The restore is not tidiness.</b> A guest's sky is written through
        /// <c>PauseMenu.SetWeather</c>, which is the one place that maps a preset onto
        /// <c>TimeOfDayController.Overcast</c> — and that method also calls <c>PlayerChoices.Save</c>.
        /// Without putting the old values back, an evening spent in somebody else's rainstorm would
        /// quietly become what this player had chosen, and they would find out the next time they
        /// launched the game alone.</para>
        /// </summary>
        public void Leave()
        {
            if (transport != null)
            {
                if (InRoom)
                {
                    SendBye();
                }

                transport.Dispose();
            }

            transport = null;
            discovery = null;
            LocalPeerId = NetProtocol.NoPeerId;
            foundCount = 0;
            sendTimer = 0f;
            roomTimer = 0f;
            beaconTimer = 0f;
            hasLastSent = false;

            for (int i = 0; i < roster.Length; i++)
            {
                roster[i] = default;
                hasLatest[i] = false;
            }

            pool?.ReleaseAll();
            RestoreOwnConditions();
        }

        public void StartBrowsing()
        {
            if (discovery == null)
            {
                transport = new LanTransport();
                discovery = transport as INetDiscovery;
            }

            foundCount = 0;
            discovery?.StartBrowsing();
        }

        public void StopBrowsing()
        {
            discovery?.StopBrowsing();

            if (transport != null && transport.Role == NetRole.Offline)
            {
                transport.Dispose();
                transport = null;
                discovery = null;
            }
        }

        // --- The beat --------------------------------------------------------------------------

        private void Update()
        {
            if (transport == null)
            {
                return;
            }

            if (pool == null)
            {
                // Retried while null rather than resolved once. It lives in the world scene, which is
                // still loading for the first frames — and a session with no pool draws nobody at all,
                // with nothing anywhere saying why.
                pool = FindFirstObjectByType<RemoteCarPool>();
            }

            float dt = Time.unscaledDeltaTime;
            transport.Tick(dt);

            Drain();
            DrainBeacons(dt);

            if (Role == NetRole.Host)
            {
                ExpirePeers();
            }

            SendOnBeat(dt);
            UpdateRates(dt);

            if (pool != null && vehicle != null)
            {
                pool.CullAgainstStreaming(vehicle.transform.position);
            }
        }

        private void UpdateRates(float dt)
        {
            rateTimer += dt;

            if (rateTimer < 1f)
            {
                return;
            }

            rateTimer -= 1f;
            BytesInPerSecond = bytesIn;
            BytesOutPerSecond = bytesOut;
            bytesIn = 0;
            bytesOut = 0;
        }

        // --- Receiving --------------------------------------------------------------------------

        private void Drain()
        {
            int length;

            while ((length = transport.Receive(incoming, out int channel)) > 0)
            {
                bytesIn += length;
                Handle(incoming, length, channel);
            }
        }

        private void Handle(byte[] buffer, int length, int channel)
        {
            if (!NetWire.BeginRead(
                    buffer, length,
                    out NetMessage kind, out byte version, out byte sender, out byte count,
                    out ushort _, out NetReader reader))
            {
                return;
            }

            if (Role == NetRole.Host)
            {
                HandleAsHost(kind, version, channel, count, ref reader);
                return;
            }

            HandleAsGuest(kind, version, sender, count, ref reader);
        }

        private void HandleAsHost(
            NetMessage kind, byte version, int channel, byte count, ref NetReader reader)
        {
            if (channel < 0 || channel >= NetProtocol.MaxPeers - 1)
            {
                return;
            }

            var peerId = (byte)(channel + 1);

            if (version != NetProtocol.Version)
            {
                SendReject(channel, 0u, NetReject.Protocol);
                return;
            }

            // Admitted for speaking, not for introducing itself. See the class remarks.
            if (!roster[peerId].InUse)
            {
                roster[peerId].InUse = true;
                roster[peerId].PeerId = peerId;
                roster[peerId].Name = DefaultNameFor(peerId);
                System.Array.Clear(rosterNameBytes[peerId], 0, NetProtocol.NameBytes);
                pool?.Acquire(peerId);
            }

            roster[peerId].LastHeardAt = Time.unscaledTime;

            switch (kind)
            {
                case NetMessage.Hello:
                    ReadHello(peerId, channel, ref reader);
                    break;

                case NetMessage.Snapshots:
                    // One, never the count it claims. A guest only ever has its own car to report, and
                    // reading further would let a malformed datagram overwrite the whole room with one
                    // peer — which is exactly what LoopbackTransport does when it hands the host's own
                    // bundle back, since every row in it would be stamped with the same channel.
                    ReadSnapshots(1, ref reader, peerId);
                    break;

                case NetMessage.LapClaim:
                    ReadLapClaim(peerId, ref reader);
                    break;

                case NetMessage.Bye:
                    DropPeer(peerId);
                    break;
            }
        }

        private void HandleAsGuest(
            NetMessage kind, byte version, byte sender, byte count, ref NetReader reader)
        {
            if (version != NetProtocol.Version)
            {
                Reject(NetReject.Protocol);
                return;
            }

            switch (kind)
            {
                case NetMessage.Room:
                    ReadRoom(count, ref reader);
                    break;

                case NetMessage.Snapshots:
                    ReadSnapshots(count, ref reader, NetProtocol.NoPeerId);
                    break;

                case NetMessage.Laps:
                    ReadLaps(count, ref reader);
                    break;

                case NetMessage.Reject:
                    ReadReject(ref reader);
                    break;

                case NetMessage.Bye:
                    Reject(NetReject.Unknown);
                    break;
            }
        }

        private void ReadHello(byte peerId, int channel, ref NetReader reader)
        {
            if (!reader.Has(4 + 2 + NetProtocol.NameBytes + NetProtocol.BuildBytes))
            {
                return;
            }

            uint peerToken = reader.UInt32();
            byte carBody = reader.Byte();
            byte carPaint = reader.Byte();
            reader.FixedStringBytes(nameScratch, NetProtocol.NameBytes);
            int buildUsed = reader.FixedStringBytes(buildScratch, NetProtocol.BuildBytes);

            // Same protocol, different world. The geometry is baked, so two builds agree about the
            // rules and disagree about where the mountains are — which is the worse of the two
            // failures, because everything looks like it is working.
            string build = System.Text.Encoding.UTF8.GetString(buildScratch, 0, buildUsed);

            if (build != Application.version)
            {
                SendReject(channel, peerToken, NetReject.Build);
                DropPeer(peerId);
                return;
            }

            roster[peerId].Token = peerToken;
            roster[peerId].Body = carBody;
            roster[peerId].Paint = carPaint;

            if (!SameBytes(rosterNameBytes[peerId], nameScratch))
            {
                System.Array.Copy(nameScratch, rosterNameBytes[peerId], NetProtocol.NameBytes);
                roster[peerId].Name = DecodeName(nameScratch);
            }
        }

        private void ReadSnapshots(byte count, ref NetReader reader, byte forcePeerId)
        {
            for (int i = 0; i < count; i++)
            {
                if (!NetWire.ReadSnapshot(ref reader, out CarSnapshot snapshot))
                {
                    return;
                }

                // A guest does not get to say which peer it is. On the host the channel decides, which
                // is the only thing a sender cannot forge by editing a byte.
                if (forcePeerId != NetProtocol.NoPeerId)
                {
                    snapshot.PeerId = forcePeerId;
                }

                if (snapshot.PeerId == LocalPeerId || snapshot.PeerId >= NetProtocol.MaxPeers)
                {
                    continue;
                }

                latest[snapshot.PeerId] = snapshot;
                hasLatest[snapshot.PeerId] = true;

                RemoteCar car = pool != null ? pool.Acquire(snapshot.PeerId) : null;
                car?.Push(snapshot);
            }
        }

        private void ReadRoom(byte count, ref NetReader reader)
        {
            if (!reader.Has(NetProtocol.RoomHeaderBytes))
            {
                return;
            }

            float hostHours = reader.Single();
            byte weather = reader.Byte();
            reader.Skip(NetProtocol.RoomHeaderBytes - 5);

            System.Array.Clear(seenInRoom, 0, seenInRoom.Length);

            for (int i = 0; i < count; i++)
            {
                if (!reader.Has(NetProtocol.PeerRecordBytes))
                {
                    break;
                }

                byte peerId = reader.Byte();
                byte peerBody = reader.Byte();
                byte peerPaint = reader.Byte();
                uint peerToken = reader.UInt32();
                reader.FixedStringBytes(nameScratch, NetProtocol.NameBytes);

                if (peerId >= NetProtocol.MaxPeers)
                {
                    continue;
                }

                seenInRoom[peerId] = true;

                if (peerToken == token && peerToken != 0u)
                {
                    LocalPeerId = peerId;
                }

                roster[peerId].InUse = true;
                roster[peerId].PeerId = peerId;
                roster[peerId].Body = peerBody;
                roster[peerId].Paint = peerPaint;
                roster[peerId].Token = peerToken;
                roster[peerId].LastHeardAt = Time.unscaledTime;

                // Only decoded when the bytes moved. A roster arrives once a second for every peer and
                // a name almost never changes; the string this would otherwise build is pure garbage.
                if (!SameBytes(rosterNameBytes[peerId], nameScratch))
                {
                    System.Array.Copy(nameScratch, rosterNameBytes[peerId], NetProtocol.NameBytes);
                    roster[peerId].Name = DecodeName(nameScratch);
                }
            }

            for (int i = 0; i < NetProtocol.MaxPeers; i++)
            {
                if (roster[i].InUse && !seenInRoom[i] && i != LocalPeerId)
                {
                    DropPeer((byte)i);
                }
            }

            ApplyHostConditions(hostHours, weather);
        }

        private void ReadLaps(byte count, ref NetReader reader)
        {
            if (LapBoard == null)
            {
                return;
            }

            LapBoard.BeginReplace();

            for (int i = 0; i < count; i++)
            {
                if (!reader.Has(NetProtocol.LapRecordBytes))
                {
                    break;
                }

                byte peerId = reader.Byte();
                byte circuit = reader.Byte();
                uint milliseconds = reader.UInt32();
                LapBoard.Accept(peerId, circuit, milliseconds);
            }

            LapBoard.EndReplace();
        }

        private void ReadLapClaim(byte peerId, ref NetReader reader)
        {
            if (LapBoard == null || !reader.Has(5))
            {
                return;
            }

            byte circuit = reader.Byte();
            uint milliseconds = reader.UInt32();
            LapBoard.Accept(peerId, circuit, milliseconds);
        }

        private void ReadReject(ref NetReader reader)
        {
            if (!reader.Has(5))
            {
                return;
            }

            uint forToken = reader.UInt32();
            var reason = (NetReject)reader.Byte();

            if (forToken == token)
            {
                Reject(reason);
            }
        }

        private void Reject(NetReject reason)
        {
            RejectedBecause = reason;
            WasRejected = true;
            Leave();
        }

        // --- Sending ----------------------------------------------------------------------------

        private void SendOnBeat(float dt)
        {
            sendTimer += dt;
            roomTimer += dt;
            beaconTimer += dt;

            if (sendTimer >= 1f / NetProtocol.SendRate)
            {
                sendTimer = 0f;
                tick++;

                if (Role == NetRole.Host)
                {
                    SendHostSnapshots();
                }
                else if (Admitted)
                {
                    SendGuestSnapshot();
                }
            }

            if (roomTimer >= 1f / NetProtocol.RoomRate)
            {
                roomTimer = 0f;

                if (Role == NetRole.Host)
                {
                    SendRoom();
                    SendLaps();
                }
                else if (!Admitted)
                {
                    // Repeated rather than acknowledged: this is the join, and it keeps going until the
                    // roster comes back with our own token in it.
                    SendHello();
                }
            }

            if (beaconTimer >= 1f / NetProtocol.BeaconRate)
            {
                beaconTimer = 0f;
                SendBeacon();
            }
        }

        private void SendHostSnapshots()
        {
            if (!TryCaptureLocal(out CarSnapshot mine))
            {
                return;
            }

            byte written = 0;
            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Snapshots, NetProtocol.HostPeerId, 0, tick);

            NetWire.WriteSnapshot(ref writer, mine);
            written++;

            for (int i = 1; i < NetProtocol.MaxPeers; i++)
            {
                if (roster[i].InUse && hasLatest[i])
                {
                    NetWire.WriteSnapshot(ref writer, latest[i]);
                    written++;
                }
            }

            // The count byte is in the header, which was written before the payload existed. Rewriting
            // it in place is cheaper than measuring first and then writing the whole thing twice.
            outgoing[5] = written;
            Broadcast(writer.Offset);
        }

        private void SendGuestSnapshot()
        {
            if (!TryCaptureLocal(out CarSnapshot mine))
            {
                return;
            }

            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Snapshots, LocalPeerId, 1, tick);
            NetWire.WriteSnapshot(ref writer, mine);
            Broadcast(writer.Offset);
        }

        private void SendHello()
        {
            NetWriter writer = NetWire.BeginDatagram(outgoing, NetMessage.Hello, 0, 1, tick);
            writer.UInt32(token);
            writer.Byte((byte)PlayerChoices.Car);
            writer.Byte((byte)PlayerChoices.Paint);
            writer.FixedString(PlayerChoices.DisplayName(), NetProtocol.NameBytes);
            writer.FixedString(Application.version, NetProtocol.BuildBytes);
            Broadcast(writer.Offset);
        }

        private void SendRoom()
        {
            byte written = 0;
            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Room, NetProtocol.HostPeerId, 0, tick);

            writer.Single(timeOfDay != null ? timeOfDay.TimeOfDayHours : PlayerChoices.Hours);
            writer.Byte((byte)PlayerChoices.Weather);
            writer.Byte(0);
            writer.Byte(0);
            writer.Byte(0);

            for (int i = 0; i < NetProtocol.MaxPeers; i++)
            {
                if (!roster[i].InUse)
                {
                    continue;
                }

                writer.Byte(roster[i].PeerId);
                writer.Byte(roster[i].Body);
                writer.Byte(roster[i].Paint);
                writer.UInt32(roster[i].Token);
                writer.FixedString(roster[i].Name, NetProtocol.NameBytes);
                written++;
            }

            outgoing[5] = written;
            Broadcast(writer.Offset);
        }

        private void SendLaps()
        {
            if (LapBoard == null)
            {
                return;
            }

            byte written = 0;
            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Laps, NetProtocol.HostPeerId, 0, tick);

            for (int i = 0; i < LapBoard.EntryCount; i++)
            {
                LapEntry entry = LapBoard.EntryAt(i);

                if (!entry.InUse)
                {
                    continue;
                }

                writer.Byte(entry.PeerId);
                writer.Byte(entry.Circuit);
                writer.UInt32(entry.TimeMilliseconds);
                written++;
            }

            outgoing[5] = written;
            Broadcast(writer.Offset);
        }

        /// <summary>A guest's own lap, on its way to the host that keeps the table.</summary>
        public void ClaimLap(byte circuit, uint milliseconds)
        {
            if (Role != NetRole.Guest || !Admitted)
            {
                return;
            }

            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.LapClaim, LocalPeerId, 1, tick);
            writer.Byte(circuit);
            writer.UInt32(milliseconds);
            Broadcast(writer.Offset);
        }

        private void SendBye()
        {
            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Bye, Admitted ? LocalPeerId : (byte)0, 0, tick);
            Broadcast(writer.Offset);
        }

        private void SendReject(int channel, uint forToken, NetReject reason)
        {
            NetWriter writer = NetWire.BeginDatagram(
                outgoing, NetMessage.Reject, NetProtocol.HostPeerId, 1, tick);
            writer.UInt32(forToken);
            writer.Byte((byte)reason);
            transport.Send(outgoing, writer.Offset, channel);
            bytesOut += writer.Offset;
        }

        private void SendBeacon()
        {
            if (discovery == null || !discovery.IsAdvertising)
            {
                return;
            }

            NetWriter writer = NetWire.BeginDatagram(
                beaconOut, NetMessage.Beacon, NetProtocol.HostPeerId, 1, tick);
            writer.FixedString(PlayerChoices.DisplayName(), NetProtocol.NameBytes);
            writer.Byte((byte)PeerCount);
            writer.Byte(NetProtocol.MaxPeers);
            writer.FixedString(Application.version, NetProtocol.BuildBytes);
            discovery.Advertise(beaconOut, writer.Offset);
        }

        private void Broadcast(int length)
        {
            transport.Send(outgoing, length, -1);
            bytesOut += length;
        }

        // --- Discovery --------------------------------------------------------------------------

        private void DrainBeacons(float dt)
        {
            for (int i = 0; i < foundCount; i++)
            {
                found[i].SeenAt += dt;
            }

            // A host that has stopped shouting drops off the list rather than staying there to be
            // tapped. Three beacons' worth of silence.
            for (int i = foundCount - 1; i >= 0; i--)
            {
                if (found[i].SeenAt > 3f / NetProtocol.BeaconRate)
                {
                    found[i] = found[foundCount - 1];
                    foundCount--;
                }
            }

            if (discovery == null || !discovery.IsBrowsing)
            {
                return;
            }

            int length;

            while ((length = discovery.ReceiveBeacon(beaconIn, out string address)) > 0)
            {
                if (!NetWire.BeginRead(
                        beaconIn, length,
                        out NetMessage kind, out byte version, out byte _, out byte _,
                        out ushort _, out NetReader reader)
                    || kind != NetMessage.Beacon)
                {
                    continue;
                }

                if (!reader.Has(NetProtocol.NameBytes + 2 + NetProtocol.BuildBytes))
                {
                    continue;
                }

                reader.FixedStringBytes(nameScratch, NetProtocol.NameBytes);
                byte players = reader.Byte();
                reader.Byte();
                int buildUsed = reader.FixedStringBytes(buildScratch, NetProtocol.BuildBytes);

                RecordHost(
                    address,
                    DecodeName(nameScratch),
                    players,
                    version == NetProtocol.Version
                    && System.Text.Encoding.UTF8.GetString(buildScratch, 0, buildUsed) == Application.version);
            }
        }

        private void RecordHost(string address, string name, int players, bool compatible)
        {
            for (int i = 0; i < foundCount; i++)
            {
                if (found[i].Address != address)
                {
                    continue;
                }

                found[i].Name = name;
                found[i].Players = players;
                found[i].Compatible = compatible;
                found[i].SeenAt = 0f;
                return;
            }

            if (foundCount >= found.Length)
            {
                return;
            }

            found[foundCount++] = new FoundHost
            {
                Address = address,
                Name = name,
                Players = players,
                Compatible = compatible,
                SeenAt = 0f,
            };
        }

        // --- Peers ------------------------------------------------------------------------------

        private void AddSelfToRoster()
        {
            roster[NetProtocol.HostPeerId] = new PeerInfo
            {
                PeerId = NetProtocol.HostPeerId,
                Body = (byte)PlayerChoices.Car,
                Paint = (byte)PlayerChoices.Paint,
                Token = 0u,
                Name = PlayerChoices.DisplayName(),
                LastHeardAt = Time.unscaledTime,
                InUse = true,
            };
        }

        private void ExpirePeers()
        {
            roster[NetProtocol.HostPeerId].Body = (byte)PlayerChoices.Car;
            roster[NetProtocol.HostPeerId].Paint = (byte)PlayerChoices.Paint;
            roster[NetProtocol.HostPeerId].Name = PlayerChoices.DisplayName();
            roster[NetProtocol.HostPeerId].LastHeardAt = Time.unscaledTime;

            for (int i = 1; i < roster.Length; i++)
            {
                if (roster[i].InUse
                    && Time.unscaledTime - roster[i].LastHeardAt > NetProtocol.PeerTimeout)
                {
                    DropPeer((byte)i);
                }
            }
        }

        private void DropPeer(byte peerId)
        {
            roster[peerId] = default;
            hasLatest[peerId] = false;
            System.Array.Clear(rosterNameBytes[peerId], 0, NetProtocol.NameBytes);
            pool?.Release(peerId);
            LapBoard?.Forget(peerId);
        }

        // --- The local car ------------------------------------------------------------------------

        /// <summary>
        /// Reads this device's car into a snapshot.
        ///
        /// <para><b>The teleport flag is worked out here rather than hooked into every placement.</b>
        /// <c>PauseMenu.MoveTo</c>, <c>Respawn</c>, the start places and the garage all call
        /// <c>VehicleController.Teleport</c>, and every one of them would have had to remember to say
        /// so. A car that has moved further since the last snapshot than any car can move in a
        /// sixteenth of a second was placed, not driven — which is a measurement rather than a list of
        /// call sites to keep in step.</para>
        /// </summary>
        private bool TryCaptureLocal(out CarSnapshot snapshot)
        {
            snapshot = default;

            if (vehicle == null)
            {
                return false;
            }

            Transform car = vehicle.transform;
            Vector3 position = car.position;

            snapshot.PeerId = Admitted ? LocalPeerId : NetProtocol.HostPeerId;
            snapshot.Position = position;
            snapshot.Rotation = car.rotation;
            snapshot.Velocity = body != null ? body.linearVelocity : Vector3.zero;
            snapshot.SteerDegrees = vehicle.SteerAngle;
            snapshot.Revs01 = vehicle.RpmNormalized;
            snapshot.Body = (byte)PlayerChoices.Car;
            snapshot.Paint = (byte)PlayerChoices.Paint;

            CarFlags flags = CarFlags.None;

            if (vehicle.BrakeInput > 0.05f)
            {
                flags |= CarFlags.Braking;
            }

            if (vehicle.IsReversing)
            {
                flags |= CarFlags.Reversing;
            }

            if (DriveInput.Current.Handbrake)
            {
                flags |= CarFlags.Handbrake;
            }

            if (lights != null && lights.HeadlightsOn)
            {
                flags |= CarFlags.Headlights;
            }

            if (vehicle.IsDrifting)
            {
                flags |= CarFlags.Drifting;
            }

            if (vehicle.GroundedWheelCount == 0)
            {
                flags |= CarFlags.Airborne;
            }

            if (hasLastSent && (position - lastSentPosition).sqrMagnitude > TeleportThresholdSquared)
            {
                flags |= CarFlags.Teleported;
            }

            lastSentPosition = position;
            hasLastSent = true;
            snapshot.Flags = flags;
            return true;
        }

        /// <summary>
        /// Twenty-five metres between two snapshots is three hundred and seventy-five metres a second.
        /// Nothing in the garage does a third of that.
        /// </summary>
        private const float TeleportThresholdSquared = 25f * 25f;

        // --- The host's clock and sky ---------------------------------------------------------------

        private void RememberOwnConditions()
        {
            if (hasRestore)
            {
                return;
            }

            restoreWeather = PlayerChoices.Weather;
            restoreHours = timeOfDay != null ? timeOfDay.TimeOfDayHours : PlayerChoices.Hours;
            hasRestore = true;
        }

        private void RestoreOwnConditions()
        {
            if (!hasRestore)
            {
                return;
            }

            hasRestore = false;

            if (pauseMenu != null && PlayerChoices.Weather != restoreWeather)
            {
                pauseMenu.SetWeather((int)restoreWeather);
            }

            if (timeOfDay != null)
            {
                timeOfDay.TimeOfDayHours = restoreHours;
            }

            PlayerChoices.Hours = restoreHours;
            PlayerChoices.Save();
        }

        /// <summary>
        /// Takes the host's hour and weather.
        ///
        /// <para><b>The weather goes through <c>PauseMenu.SetWeather</c> and never straight onto
        /// <c>TimeOfDayController.Overcast</c>.</b> That method is the one place a preset is turned
        /// into an overcast value, and the file this project keeps its arguments in is emphatic that a
        /// second writer ramping the same field shows as a sky that snaps to the new weather and then
        /// slides back off it. Everything downstream — the rain, the wet road, the tyre grip, the
        /// noise on the roof — follows on its own, because <c>WeatherDirector</c> polls
        /// <c>PlayerChoices.Weather</c> rather than being told.</para>
        ///
        /// <para><b>The clock is nudged, not set.</b> It advances at an hour a minute, so a snap once a
        /// second would be a visible step in the shadows; a difference small enough to be drift is
        /// closed by half each time, and only a real disagreement — somebody moving the slider — is
        /// taken whole.</para>
        /// </summary>
        private void ApplyHostConditions(float hostHours, byte weather)
        {
            if (Role != NetRole.Guest)
            {
                return;
            }

            if ((int)PlayerChoices.Weather != weather && pauseMenu != null)
            {
                pauseMenu.SetWeather(weather);
            }

            if (timeOfDay == null)
            {
                return;
            }

            float error = Mathf.DeltaAngle(timeOfDay.TimeOfDayHours * 15f, hostHours * 15f) / 15f;

            timeOfDay.TimeOfDayHours = Mathf.Abs(error) > ClockSnapHours
                ? Mathf.Repeat(hostHours, 24f)
                : Mathf.Repeat(timeOfDay.TimeOfDayHours + error * 0.5f, 24f);
        }

        /// <summary>Three minutes of world time — about three seconds of drift at an hour a minute.</summary>
        private const float ClockSnapHours = 0.05f;

        // --- Small things -------------------------------------------------------------------------

        private static bool SameBytes(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// What a peer is called before its <c>Hello</c> has arrived, or if it never sends one.
        ///
        /// <para>A prebuilt table rather than an interpolated string, because the name tag over a car
        /// reads this on <b>every frame</b> while driving — and building the string only to compare it
        /// against the one already shown is the allocation the rev counter's own number table exists to
        /// avoid. Eight strings, made once.</para>
        /// </summary>
        public static string DefaultNameFor(byte peerId) =>
            peerId < DefaultNames.Length ? DefaultNames[peerId] : "Driver";

        private static readonly string[] DefaultNames = BuildDefaultNames();

        private static string[] BuildDefaultNames()
        {
            var names = new string[NetProtocol.MaxPeers];

            for (int i = 0; i < names.Length; i++)
            {
                names[i] = $"Driver {i}";
            }

            return names;
        }

        private static string DecodeName(byte[] bytes)
        {
            int used = 0;

            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    used = i + 1;
                }
            }

            return used == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes, 0, used);
        }
    }
}
