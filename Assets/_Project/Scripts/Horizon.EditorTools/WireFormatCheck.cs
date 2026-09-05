using Horizon.Net;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Writes a car onto the wire, reads it back, and prints the worst error it can find.
    ///
    /// <para><b>This is the one part of the multiplayer work a picture cannot check.</b> A quantisation
    /// that is a hair too coarse does not draw a broken car — it draws a perfectly good car a few
    /// centimetres off somebody else's road, or with a wheel angle that lags a corner by a degree, and
    /// there is no frame anywhere in this project where that is distinguishable from the network being
    /// slow. So it is measured, the way <c>ValidateSurfaceRelief</c> measures a road nobody can
    /// photograph.</para>
    ///
    /// <para>It runs against the real <c>NetWire</c> rather than against a description of it, and over
    /// the world's actual extent rather than over a tidy cube: the position error of a float32 grows
    /// with distance from the origin, and the whole argument for not quantising position is that
    /// thirteen kilometres out it is still under a millimetre. A check that sampled the first hundred
    /// metres would agree with that claim without testing it.</para>
    ///
    /// <para>Seconds to run, no scene, no device — which is what makes it the first thing to reach for
    /// when a byte layout changes, ahead of a twenty-minute build.</para>
    /// </summary>
    public static class WireFormatCheck
    {
        /// <summary>
        /// The world's plan extent, with room to spare.
        ///
        /// <para>Roughly x −3000..13200 and z −2500..8000 across the fourteen courses, and heights from
        /// sea level to the Weissjoch's 906 m col. Sampled past all of it, because a check that stops
        /// at the edge of today's world stops being a check the first time a road is added.</para>
        /// </summary>
        private static readonly Bounds WorldExtent = new Bounds(
            new Vector3(5100f, 500f, 2750f), new Vector3(20000f, 2000f, 14000f));

        /// <summary>How many random cars to push through. A second's work, and the tail is what matters.</summary>
        private const int Samples = 20000;

        [MenuItem("Tools/Horizon/Validate Wire Format", priority = 60)]
        public static void Validate()
        {
            var buffer = new byte[NetProtocol.MaxDatagramBytes];
            var random = new System.Random(12345);

            float worstPosition = 0f;
            float worstAngle = 0f;
            float worstVelocity = 0f;
            float worstSteer = 0f;
            float worstRevs = 0f;
            int failures = 0;

            for (int i = 0; i < Samples; i++)
            {
                CarSnapshot sent = Random(random);

                NetWriter writer = NetWire.BeginDatagram(
                    buffer, NetMessage.Snapshots, 0, 1, (ushort)i);
                NetWire.WriteSnapshot(ref writer, sent);

                if (!NetWire.BeginRead(
                        buffer, writer.Offset,
                        out NetMessage kind, out byte version, out byte _, out byte count,
                        out ushort tick, out NetReader reader)
                    || kind != NetMessage.Snapshots
                    || version != NetProtocol.Version
                    || count != 1
                    || tick != (ushort)i
                    || !NetWire.ReadSnapshot(ref reader, out CarSnapshot got))
                {
                    failures++;
                    continue;
                }

                if (got.PeerId != sent.PeerId || got.Flags != sent.Flags
                    || got.Body != sent.Body || got.Paint != sent.Paint)
                {
                    failures++;
                    continue;
                }

                worstPosition = Mathf.Max(worstPosition, (got.Position - sent.Position).magnitude);
                worstAngle = Mathf.Max(worstAngle, Quaternion.Angle(got.Rotation, sent.Rotation));
                worstVelocity = Mathf.Max(worstVelocity, (got.Velocity - sent.Velocity).magnitude);
                worstSteer = Mathf.Max(worstSteer, Mathf.Abs(got.SteerDegrees - sent.SteerDegrees));
                worstRevs = Mathf.Max(worstRevs, Mathf.Abs(got.Revs01 - sent.Revs01));
            }

            int perSecond = Mathf.RoundToInt(NetProtocol.SendRate);
            int hostBytes = NetProtocol.HeaderBytes + NetProtocol.MaxPeers * NetProtocol.SnapshotBytes;

            Debug.Log(
                $"[Horizon] Wire format v{NetProtocol.Version}: {Samples} round trips, "
                + $"{NetProtocol.SnapshotBytes} bytes a car. "
                + $"Worst error — position {worstPosition * 1000f:0.00} mm, "
                + $"rotation {worstAngle:0.000}°, velocity {worstVelocity * 100f:0.0} cm/s, "
                + $"steering {worstSteer:0.000}°, revs {worstRevs * 100f:0.0} %. "
                + $"A full room costs {hostBytes} bytes a tick, {hostBytes * perSecond} bytes a second "
                + "down to each guest.");

            if (failures > 0)
            {
                Debug.LogError(
                    $"[Horizon] {failures} of {Samples} snapshots did not survive the round trip. "
                    + "Something in the header or the byte layout disagrees with itself, and every car "
                    + "in every room would be drawn from it.");
                return;
            }

            // Thresholds, and each one is what would actually be visible rather than what looks tidy.
            if (worstPosition > 0.01f)
            {
                Debug.LogWarning(
                    $"[Horizon] Position survives to only {worstPosition * 1000f:0} mm at the far "
                    + "corner of the world. A centimetre is the point past which another car visibly "
                    + "does not sit on the road it is driving on.");
            }

            if (worstAngle > 0.05f)
            {
                Debug.LogWarning(
                    $"[Horizon] Rotation survives to only {worstAngle:0.000}°, which at fifteen metres "
                    + "of car length is a visible twitch rather than a rounding.");
            }

            if (worstSteer > 0.4f)
            {
                Debug.LogWarning(
                    $"[Horizon] Steering survives to only {worstSteer:0.00}°. The wheels of another "
                    + "car would step between values rather than turn.");
            }
        }

        private static CarSnapshot Random(System.Random random)
        {
            return new CarSnapshot
            {
                PeerId = (byte)random.Next(0, NetProtocol.MaxPeers),
                Flags = (CarFlags)random.Next(0, 128),
                Position = new Vector3(
                    Range(random, WorldExtent.min.x, WorldExtent.max.x),
                    Range(random, WorldExtent.min.y, WorldExtent.max.y),
                    Range(random, WorldExtent.min.z, WorldExtent.max.z)),
                Rotation = UnityEngine.Random.rotationUniform,

                // Faster than anything in the garage, and a vertical component a car falling off the
                // Steilufer would reach.
                Velocity = new Vector3(
                    Range(random, -90f, 90f), Range(random, -60f, 60f), Range(random, -90f, 90f)),

                // Past every config's lock, so the clamp is exercised rather than assumed.
                SteerDegrees = Range(random, -44f, 44f),
                Revs01 = Range(random, 0f, 1f),
                Body = (byte)random.Next(0, 10),
                Paint = (byte)random.Next(0, 8),
            };
        }

        private static float Range(System.Random random, float min, float max) =>
            min + (float)random.NextDouble() * (max - min);
    }
}
