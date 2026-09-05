using Horizon.Net;
using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The seven cars other people might be driving, baked into the world scene and handed out as
    /// peers arrive.
    ///
    /// <para><b>A pool rather than instantiation, and that is not only about cost.</b> There is no
    /// runtime path to <c>Vehicle_Prototype.prefab</c> in this project at all — no <c>Resources</c>
    /// folder, no Addressables, no serialized reference outside the editor tool that builds it — so
    /// "instantiate a car" is a whole loading mechanism rather than one line. The traffic already
    /// proves the alternative works: ninety-six cars baked into the scene, parked below the world and
    /// moved into it when they are needed. Seven is nothing beside that.</para>
    ///
    /// <para><b>Cars outside the streamed world are hidden rather than drawn.</b> <c>WorldStreamer</c>
    /// turns renderers off past its unload radius, so a friend four kilometres away would be a car
    /// hanging in the sky over ground that does not exist yet. The radius is read off the streamer
    /// rather than written down again here, for the reason this project keeps restating: two copies of
    /// a number agree until the first time one of them is retuned.</para>
    /// </summary>
    public sealed class RemoteCarPool : MonoBehaviour
    {
        [SerializeField] private RemoteCar[] cars = new RemoteCar[0];

        [Tooltip("Where unused cars are parked. Far below the world, like the traffic pool's spares.")]
        [SerializeField] private Vector3 parkPosition = new Vector3(0f, -10000f, 0f);

        [Tooltip("Found in the world if left empty. Only its radii are read.")]
        [SerializeField] private WorldStreamer streamer;

        private readonly byte[] peerOf = new byte[NetProtocol.MaxPeers];

        /// <summary>How many cars this pool can hand out.</summary>
        public int SlotCount => cars != null ? cars.Length : 0;

        /// <summary>How many are currently bound to a peer.</summary>
        public int InUseCount { get; private set; }

        private void Awake()
        {
            if (streamer == null)
            {
                streamer = FindFirstObjectByType<WorldStreamer>();
            }

            ReleaseAll();
        }

        /// <summary>The car bound to a peer, or null.</summary>
        public RemoteCar Find(byte peerId)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (cars[i] != null && cars[i].InUse && cars[i].PeerId == peerId)
                {
                    return cars[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The car for a peer, taking a free slot if it does not have one yet.
        ///
        /// <para>Returns null when the pool is full. That can only happen if the protocol let more
        /// peers in than there are cars to draw them with, which is a mismatch worth being told about
        /// rather than a case worth handling.</para>
        /// </summary>
        public RemoteCar Acquire(byte peerId)
        {
            RemoteCar existing = Find(peerId);

            if (existing != null)
            {
                return existing;
            }

            for (int i = 0; i < SlotCount; i++)
            {
                if (cars[i] == null || cars[i].InUse)
                {
                    continue;
                }

                cars[i].Bind(peerId);
                cars[i].transform.position = parkPosition;
                InUseCount++;
                return cars[i];
            }

            Debug.LogWarning(
                $"[Horizon] No free remote car for peer {peerId}: the pool has {SlotCount} and the "
                + $"protocol allows {NetProtocol.MaxPeers - 1} guests. Re-run "
                + "Tools > Horizon > Rebuild Prototype Scene.");

            return null;
        }

        public void Release(byte peerId)
        {
            RemoteCar car = Find(peerId);

            if (car == null)
            {
                return;
            }

            car.Release();
            car.transform.position = parkPosition;
            InUseCount = Mathf.Max(0, InUseCount - 1);
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (cars[i] == null)
                {
                    continue;
                }

                cars[i].Release();
                cars[i].transform.position = parkPosition;
            }

            InUseCount = 0;
        }

        /// <summary>The car in slot <paramref name="index"/>, whether or not it is bound.</summary>
        public RemoteCar At(int index) =>
            index >= 0 && index < SlotCount ? cars[index] : null;

        /// <summary>
        /// Hides anything the streamer would not have built the ground under.
        ///
        /// <para>Called once a frame from <c>NetSession</c> rather than on its own beat, because a car
        /// popping in a frame after the terrain is not worth a second update loop.</para>
        /// </summary>
        public void CullAgainstStreaming(Vector3 viewer)
        {
            float radius = streamer != null ? streamer.UnloadRadius : 900f;
            float radiusSquared = radius * radius;

            for (int i = 0; i < SlotCount; i++)
            {
                RemoteCar car = cars[i];

                if (car == null || !car.InUse || !car.HasPose)
                {
                    continue;
                }

                car.SetCulled((car.DrawnPosition - viewer).sqrMagnitude > radiusSquared);
            }
        }

        /// <summary>Wired by the setup tool. Nothing else may call it.</summary>
        public void SetCars(RemoteCar[] built, WorldStreamer worldStreamer, Vector3 park)
        {
            cars = built;
            streamer = worldStreamer;
            parkPosition = park;
        }
    }
}
