using System.Threading;
using Horizon.Net;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Opens a real host and two real guests on the loopback address and checks that the host can tell
    /// them apart.
    ///
    /// <para><b>There is one way this transport can fail that nothing else here could ever catch.</b>
    /// A host cannot use a connected socket — it has several guests — so it reads with
    /// <c>ReceiveFrom</c>, and the endpoint it passes in is a <c>FastEndPoint</c> that caches its own
    /// <c>SocketAddress</c> and returns <c>this</c> from <c>Create</c>. That removes an allocation per
    /// datagram, and it rests on the runtime actually calling those two virtual methods. If it ever
    /// stops, every sender's address reads as zero, <b>every guest in the room maps to channel zero</b>,
    /// and what shows is a room where the second person to join replaces the first — with no exception,
    /// no warning and nothing in any log. Reading the code cannot settle it; two sockets can.</para>
    ///
    /// <para>It is also the only part of the networking that can be exercised at all without a second
    /// device, and it takes about a second. Worth running before any release that people are going to
    /// try together, ahead of a twenty-minute Android build whose failure would look identical to a
    /// Wi-Fi problem.</para>
    /// </summary>
    public static class LanTransportCheck
    {
        private const string Loopback = "127.0.0.1";

        /// <summary>How long to wait for a datagram that has not got a network to cross.</summary>
        private const int WaitMilliseconds = 600;

        [MenuItem("Tools/Horizon/Validate LAN Transport", priority = 61)]
        public static void Validate()
        {
            var host = new LanTransport();
            var first = new LanTransport();
            var second = new LanTransport();

            var outgoing = new byte[NetProtocol.MaxDatagramBytes];
            var incoming = new byte[NetProtocol.MaxDatagramBytes];

            try
            {
                host.StartHost();

                if (host.Status == NetStatus.Failed)
                {
                    Debug.LogError(
                        $"[Horizon] The host could not open port {NetProtocol.GamePort}: "
                        + $"{host.LastError}. Something else on this machine is holding it.");
                    return;
                }

                first.StartGuest(Loopback);
                second.StartGuest(Loopback);

                if (first.Status == NetStatus.Failed || second.Status == NetStatus.Failed)
                {
                    Debug.LogError($"[Horizon] A guest could not open a socket: {first.LastError} "
                                   + $"{second.LastError}");
                    return;
                }

                // Two guests, two different bodies in their Hello, so the payloads are told apart by
                // content as well as by which channel they arrived on.
                SendHello(first, outgoing, 0xAAAA1111u, body: 3);
                SendHello(second, outgoing, 0xBBBB2222u, body: 7);

                int firstChannel = -1;
                int secondChannel = -1;
                uint firstToken = 0u;
                uint secondToken = 0u;
                int received = 0;

                for (int spin = 0; spin < WaitMilliseconds / 10 && received < 2; spin++)
                {
                    Thread.Sleep(10);
                    host.Tick(0.01f);

                    int length;

                    while ((length = host.Receive(incoming, out int channel)) > 0)
                    {
                        if (!NetWire.BeginRead(
                                incoming, length,
                                out NetMessage kind, out byte version, out byte _, out byte _,
                                out ushort _, out NetReader reader)
                            || kind != NetMessage.Hello
                            || version != NetProtocol.Version
                            || !reader.Has(4))
                        {
                            continue;
                        }

                        uint token = reader.UInt32();

                        if (token == 0xAAAA1111u)
                        {
                            firstChannel = channel;
                            firstToken = token;
                        }
                        else if (token == 0xBBBB2222u)
                        {
                            secondChannel = channel;
                            secondToken = token;
                        }

                        received++;
                    }
                }

                if (firstToken == 0u || secondToken == 0u)
                {
                    Debug.LogError(
                        $"[Horizon] LAN transport: only {received} of 2 datagrams arrived over the "
                        + "loopback. The host is not reading what the guests are sending, and no "
                        + "amount of Wi-Fi would fix that.");
                    return;
                }

                if (firstChannel == secondChannel)
                {
                    Debug.LogError(
                        $"[Horizon] LAN transport: both guests landed on channel {firstChannel}. "
                        + "FastEndPoint.Create is not being called by ReceiveFrom, so every sender's "
                        + "address reads as zero — in a real room the second person to join would "
                        + "replace the first, silently. See LanTransport.FastEndPoint.");
                    return;
                }

                // And back the other way, addressed to one channel only, which is what the host does
                // for a rejection.
                NetWriter writer = NetWire.BeginDatagram(
                    outgoing, NetMessage.Reject, NetProtocol.HostPeerId, 1, 1);
                writer.UInt32(0xAAAA1111u);
                writer.Byte((byte)NetReject.Full);
                host.Send(outgoing, writer.Offset, firstChannel);

                bool reachedFirst = false;
                bool reachedSecond = false;

                for (int spin = 0; spin < WaitMilliseconds / 10 && !reachedFirst; spin++)
                {
                    Thread.Sleep(10);
                    reachedFirst |= first.Receive(incoming, out int _) > 0;
                    reachedSecond |= second.Receive(incoming, out int _) > 0;
                }

                if (!reachedFirst)
                {
                    Debug.LogError(
                        "[Horizon] LAN transport: a datagram addressed to one channel never reached "
                        + "the guest on it. A guest would join and then hear nothing back.");
                    return;
                }

                if (reachedSecond)
                {
                    Debug.LogError(
                        "[Horizon] LAN transport: a datagram addressed to one channel reached the "
                        + "other guest as well. Every rejection and every private message would go to "
                        + "the wrong person.");
                    return;
                }

                Debug.Log(
                    $"[Horizon] LAN transport: two guests over the loopback landed on channels "
                    + $"{firstChannel} and {secondChannel}, and a reply addressed to one reached only "
                    + $"that one. Ports {NetProtocol.GamePort} game and {NetProtocol.DiscoveryPort} "
                    + $"discovery. Local address {host.LocalAddress}.");

                if (string.IsNullOrEmpty(host.LocalAddress))
                {
                    Debug.LogWarning(
                        "[Horizon] This machine reports no address of its own, so the multiplayer page "
                        + "would have nothing to show a guest to type in. On a phone that is what is "
                        + "left when broadcast discovery is blocked.");
                }
            }
            finally
            {
                host.Dispose();
                first.Dispose();
                second.Dispose();
            }
        }

        private static void SendHello(LanTransport guest, byte[] buffer, uint token, byte body)
        {
            NetWriter writer = NetWire.BeginDatagram(buffer, NetMessage.Hello, 0, 1, 1);
            writer.UInt32(token);
            writer.Byte(body);
            writer.Byte(0);
            writer.FixedString("check", NetProtocol.NameBytes);
            writer.FixedString(Application.version, NetProtocol.BuildBytes);
            guest.Send(buffer, writer.Offset, -1);
        }
    }
}
