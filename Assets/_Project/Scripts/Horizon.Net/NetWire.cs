using System;
using System.Buffers.Binary;
using System.Text;
using UnityEngine;

namespace Horizon.Net
{
    /// <summary>
    /// Writes primitives into a caller-owned array.
    ///
    /// <para><b>A struct over a <see cref="Span{T}"/> rather than a <c>BinaryWriter</c>, and every
    /// write goes through <see cref="BinaryPrimitives"/>.</b> The obvious spelling —
    /// <c>BitConverter.GetBytes</c> — allocates a fresh array on every field, which at fifteen
    /// datagrams a second beside a physics step is exactly the garbage the budget forbids.
    /// <c>Array.AsSpan</c> costs nothing: a span is a stack value pointing at somebody else's
    /// memory.</para>
    ///
    /// <para>Little-endian throughout, stated rather than inherited. Every device this ships to is
    /// little-endian, but "whatever the machine does" is not a wire format.</para>
    /// </summary>
    public struct NetWriter
    {
        private readonly byte[] buffer;
        private int offset;

        public NetWriter(byte[] target, int start)
        {
            buffer = target;
            offset = start;
        }

        /// <summary>How many bytes have been written, counting from zero rather than from the start.</summary>
        public int Offset => offset;

        public void Byte(byte value) => buffer[offset++] = value;

        public void SByte(sbyte value) => buffer[offset++] = unchecked((byte)value);

        public void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);
            offset += 2;
        }

        public void Int16(short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), value);
            offset += 2;
        }

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
            offset += 4;
        }

        public void Single(float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
            offset += 4;
        }

        /// <summary>
        /// A string in a fixed field, zero-padded.
        ///
        /// <para>Truncated at the capacity rather than refused, and truncated on a byte boundary that
        /// may land inside a multi-byte character — which is why the reader below trims trailing
        /// partial sequences rather than trusting what it is handed. A name is cosmetic; a decode
        /// that throws in the middle of a roster packet is not.</para>
        /// </summary>
        public void FixedString(string value, int capacity)
        {
            int written = 0;

            if (!string.IsNullOrEmpty(value))
            {
                // The overload that writes into an existing array. The one that returns a byte[]
                // allocates, and this runs once a second per peer.
                int characters = value.Length;

                while (characters > 0 && Encoding.UTF8.GetByteCount(value, 0, characters) > capacity)
                {
                    characters--;
                }

                if (characters > 0)
                {
                    written = Encoding.UTF8.GetBytes(value, 0, characters, buffer, offset);
                }
            }

            for (int i = written; i < capacity; i++)
            {
                buffer[offset + i] = 0;
            }

            offset += capacity;
        }
    }

    /// <summary>
    /// Reads back what <see cref="NetWriter"/> wrote, and refuses to read past what actually arrived.
    ///
    /// <para><b>Every read is bounds-checked against the datagram's real length, not against the
    /// buffer's.</b> Buffers here are allocated once at the largest size the protocol can produce and
    /// reused, so the bytes past a short datagram are whatever the last one left there. Trusting the
    /// array length would make a truncated packet read as a valid one full of stale cars.</para>
    /// </summary>
    public struct NetReader
    {
        private readonly byte[] buffer;
        private readonly int end;
        private int offset;

        public NetReader(byte[] source, int start, int length)
        {
            buffer = source;
            offset = start;
            end = start + length;
        }

        public int Offset => offset;

        public int Remaining => end - offset;

        public bool Has(int bytes) => Remaining >= bytes;

        public byte Byte() => buffer[offset++];

        public sbyte SByte() => unchecked((sbyte)buffer[offset++]);

        public ushort UInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
            offset += 2;
            return value;
        }

        public short Int16()
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset));
            offset += 2;
            return value;
        }

        public uint UInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
            offset += 4;
            return value;
        }

        public float Single()
        {
            float value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)));
            offset += 4;
            return value;
        }

        /// <summary>
        /// Copies a fixed string field out as raw bytes and reports how many were used.
        ///
        /// <para><b>Bytes rather than a string, and that is the whole point of this overload.</b>
        /// <c>Encoding.UTF8.GetString</c> allocates, and a roster of eight peers arriving once a
        /// second would be eight strings a second for names that almost never change. The caller
        /// compares these bytes against what it already holds and only decodes on a difference —
        /// which is <c>InstrumentCluster</c>'s rule about only assigning a label when the number
        /// behind it has moved, one system along.</para>
        /// </summary>
        public int FixedStringBytes(byte[] destination, int capacity)
        {
            int used = 0;

            for (int i = 0; i < capacity; i++)
            {
                byte value = buffer[offset + i];
                destination[i] = value;

                if (value != 0)
                {
                    used = i + 1;
                }
            }

            offset += capacity;
            return used;
        }

        public void Skip(int bytes) => offset += bytes;
    }

    /// <summary>
    /// Turns a car into thirty-two bytes and back, and stamps the header every datagram opens with.
    ///
    /// <para>All of it static and all of it writing into arrays the caller owns, so nothing here can
    /// allocate. <c>Tools &gt; Horizon &gt; Validate Wire Format</c> drives a round trip through these
    /// two methods and prints the worst error it can find in a position, an angle and a speed — the
    /// only way this file can be wrong that would otherwise show as cars a few centimetres off
    /// somebody else's road.</para>
    /// </summary>
    public static class NetWire
    {
        /// <summary>A quaternion component, times this, fits an int16 with a bit to spare.</summary>
        private const float RotationScale = 32767f;

        /// <summary>Velocity goes in decimetres a second: a tenth of a metre, over ±3276 m/s.</summary>
        private const float VelocityScale = 10f;

        /// <summary>Steering in 0.35° steps, which covers ±44.45° — every config's lock and then some.</summary>
        private const float SteerScale = 0.35f;

        /// <summary>
        /// Writes the eight-byte header and leaves the writer positioned at the payload.
        /// </summary>
        public static NetWriter BeginDatagram(
            byte[] buffer, NetMessage kind, byte senderPeerId, byte count, ushort tick)
        {
            var writer = new NetWriter(buffer, 0);
            writer.UInt16(NetProtocol.Magic);
            writer.Byte(NetProtocol.Version);
            writer.Byte((byte)kind);
            writer.Byte(senderPeerId);
            writer.Byte(count);
            writer.UInt16(tick);
            return writer;
        }

        /// <summary>
        /// Checks the header and hands back a reader on the payload.
        ///
        /// <para>Returns false for anything that is not ours — a stray broadcast on a shared port, a
        /// truncated datagram, the wrong magic. <b>The version is deliberately not checked here</b>:
        /// a peer on the wrong version has to be told so, and a reader that silently dropped the
        /// packet would leave them staring at a room that never admits them. See
        /// <c>NetSession</c>.</para>
        /// </summary>
        public static bool BeginRead(
            byte[] buffer, int length,
            out NetMessage kind, out byte version, out byte senderPeerId, out byte count,
            out ushort tick, out NetReader reader)
        {
            kind = default;
            version = 0;
            senderPeerId = NetProtocol.NoPeerId;
            count = 0;
            tick = 0;
            reader = default;

            if (length < NetProtocol.HeaderBytes)
            {
                return false;
            }

            var header = new NetReader(buffer, 0, length);

            if (header.UInt16() != NetProtocol.Magic)
            {
                return false;
            }

            version = header.Byte();
            kind = (NetMessage)header.Byte();
            senderPeerId = header.Byte();
            count = header.Byte();
            tick = header.UInt16();

            reader = new NetReader(buffer, NetProtocol.HeaderBytes, length - NetProtocol.HeaderBytes);
            return true;
        }

        public static void WriteSnapshot(ref NetWriter writer, in CarSnapshot snapshot)
        {
            writer.Byte(snapshot.PeerId);
            writer.Byte((byte)snapshot.Flags);

            writer.Single(snapshot.Position.x);
            writer.Single(snapshot.Position.y);
            writer.Single(snapshot.Position.z);

            writer.Int16(Quantise(snapshot.Rotation.x, RotationScale));
            writer.Int16(Quantise(snapshot.Rotation.y, RotationScale));
            writer.Int16(Quantise(snapshot.Rotation.z, RotationScale));
            writer.Int16(Quantise(snapshot.Rotation.w, RotationScale));

            writer.Int16(Quantise(snapshot.Velocity.x, VelocityScale));
            writer.Int16(Quantise(snapshot.Velocity.y, VelocityScale));
            writer.Int16(Quantise(snapshot.Velocity.z, VelocityScale));

            writer.SByte((sbyte)Mathf.Clamp(
                Mathf.RoundToInt(snapshot.SteerDegrees / SteerScale), -127, 127));

            writer.Byte((byte)Mathf.Clamp(Mathf.RoundToInt(snapshot.Revs01 * 255f), 0, 255));
            writer.Byte(snapshot.Body);
            writer.Byte(snapshot.Paint);
        }

        public static bool ReadSnapshot(ref NetReader reader, out CarSnapshot snapshot)
        {
            snapshot = default;

            if (!reader.Has(NetProtocol.SnapshotBytes))
            {
                return false;
            }

            snapshot.PeerId = reader.Byte();
            snapshot.Flags = (CarFlags)reader.Byte();

            snapshot.Position = new Vector3(reader.Single(), reader.Single(), reader.Single());

            var rotation = new Quaternion(
                reader.Int16() / RotationScale,
                reader.Int16() / RotationScale,
                reader.Int16() / RotationScale,
                reader.Int16() / RotationScale);

            // Renormalised rather than trusted. Four independently rounded components are not quite a
            // unit quaternion, and Unity's own Slerp on a drifting one walks the car's rotation.
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x + rotation.y * rotation.y
                + rotation.z * rotation.z + rotation.w * rotation.w);

            snapshot.Rotation = magnitude > 0.0001f
                ? new Quaternion(
                    rotation.x / magnitude, rotation.y / magnitude,
                    rotation.z / magnitude, rotation.w / magnitude)
                : Quaternion.identity;

            snapshot.Velocity = new Vector3(
                reader.Int16() / VelocityScale,
                reader.Int16() / VelocityScale,
                reader.Int16() / VelocityScale);

            snapshot.SteerDegrees = reader.SByte() * SteerScale;
            snapshot.Revs01 = reader.Byte() / 255f;
            snapshot.Body = reader.Byte();
            snapshot.Paint = reader.Byte();

            return true;
        }

        private static short Quantise(float value, float scale)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(value * scale), -32767, 32767);
        }
    }
}
