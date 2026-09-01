using UnityEngine;

namespace Horizon.Core
{
    /// <summary>
    /// What a tyre is standing on. Three, and the list is short on purpose.
    ///
    /// <para>The world can only tell these apart at the granularity it is built at. Terrain tiles carry
    /// grass, rock, sand and snow as <i>vertex tints on one material in one mesh</i>, so no amount of
    /// asking the collider will separate a snowfield from a meadow — they are the same triangles. A road
    /// mesh, by contrast, genuinely does carry its shoulders as a submesh of their own
    /// (<c>RoadMeshBuilder.ShoulderSubmesh</c>), which is why that one distinction is available and the
    /// others are not. Adding <c>Gravel</c> or <c>Ice</c> here without first giving them geometry of
    /// their own would be a kind nothing could ever return.</para>
    /// </summary>
    public enum SurfaceKind
    {
        /// <summary>Carriageway, street, forecourt, bridge deck, footway. Everything paved.</summary>
        Asphalt = 0,

        /// <summary>The gravel verge either side of a carriageway. Loose, and it drags.</summary>
        Shoulder = 1,

        /// <summary>Terrain. Grass, dirt, rock — off the road entirely.</summary>
        Ground = 2,
    }

    /// <summary>
    /// Tags a piece of generated geometry with what it drives like, so a wheel can ask its own raycast
    /// hit instead of asking the world where the roads are.
    ///
    /// <para><b>In Horizon.Core because Horizon.Vehicle is the reader and may not see Horizon.World.</b>
    /// The alternative — a per-frame nearest-road query — is the search <c>RoadRespawn.TryNearest</c>
    /// does, and its own remarks say why that runs on a button press and not per frame: a lane can be a
    /// kilometre long and there are three hundred of them. The wheel raycasts already happen four times
    /// a physics step and already know exactly what they hit. This makes that answer readable.</para>
    ///
    /// <para><b>Asphalt is the default, and that is the safe direction to be wrong in.</b> Geometry
    /// nobody remembered to tag then drives like a road, which is invisible; the other way round, one
    /// forgotten call would put the car on grass in the middle of a carriageway and it would read as the
    /// handling model having broken. The build counts what it tagged so a forgotten call shows up as a
    /// number instead of as a mystery.</para>
    /// </summary>
    public sealed class GroundSurface : MonoBehaviour
    {
        [Tooltip("What the whole object is, unless the runs below say otherwise.")]
        [SerializeField] private SurfaceKind kind = SurfaceKind.Asphalt;

        /// <summary>
        /// First triangle of each run, ascending, or empty when the whole object is one kind.
        ///
        /// <para><see cref="RaycastHit.triangleIndex"/> counts triangles across the whole mesh in
        /// submesh order, so a submesh is a contiguous run and the boundaries are prefix sums of the
        /// submesh triangle counts. Stored rather than derived, because deriving it at run time means
        /// reading <c>Mesh.GetIndices</c>, and a generated mesh is not marked readable — the copy Unity
        /// would have to keep for that is the memory this project spends on terrain instead.</para>
        /// </summary>
        [SerializeField] private int[] runStart = new int[0];

        /// <summary>What each run in <see cref="runStart"/> is. Same length, same order.</summary>
        [SerializeField] private SurfaceKind[] runKind = new SurfaceKind[0];

        /// <summary>The whole object's kind, for anything that does not have a triangle to hand.</summary>
        public SurfaceKind Kind => kind;

        /// <summary>
        /// What is under one particular triangle.
        ///
        /// <para>A linear walk, and it stays one: the only object in this world with runs at all has
        /// two of them. A binary search over two entries is a branch and a subtraction spent to save a
        /// comparison.</para>
        ///
        /// <para>A negative index is what a <see cref="BoxCollider"/> reports — ambient traffic, which
        /// the wheels do raycast against because <c>groundMask</c> is everything. It answers with the
        /// object's own kind rather than refusing, so driving over another car is asphalt.</para>
        /// </summary>
        public SurfaceKind KindAt(int triangleIndex)
        {
            if (triangleIndex < 0 || runStart.Length == 0)
            {
                return kind;
            }

            SurfaceKind found = kind;
            for (int i = 0; i < runStart.Length; i++)
            {
                if (triangleIndex < runStart[i])
                {
                    break;
                }

                found = runKind[i];
            }

            return found;
        }

        /// <summary>The whole object is one thing.</summary>
        public void SetUniform(SurfaceKind value)
        {
            kind = value;
            runStart = new int[0];
            runKind = new SurfaceKind[0];
        }

        /// <summary>
        /// The object is several things, split by triangle. <paramref name="starts"/> must ascend and
        /// begin at zero; the two arrays must be the same length.
        /// </summary>
        public void SetRuns(int[] starts, SurfaceKind[] kinds)
        {
            if (starts == null || kinds == null || starts.Length != kinds.Length || starts.Length == 0)
            {
                return;
            }

            kind = kinds[0];
            runStart = starts;
            runKind = kinds;
        }

        /// <summary>
        /// The grip multiplier for a surface, 1 on tarmac.
        ///
        /// <para>Deliberately not a cliff. A verge that took half the grip away would make the shoulder
        /// a wall the car bounces off, which is worse than no surfaces at all — the shoulder is 1.5 m
        /// wide and a driver clips it on nearly every hairpin exit. What these numbers are tuned for is
        /// that dropping two wheels onto the verge is a moment the driver feels and corrects, not a
        /// moment the car is taken away from them.</para>
        /// </summary>
        public static float GripOf(SurfaceKind value)
        {
            switch (value)
            {
                case SurfaceKind.Shoulder:
                    return 0.78f;
                case SurfaceKind.Ground:
                    return 0.62f;
                default:
                    return 1f;
            }
        }

        /// <summary>
        /// How much noise a surface makes under a rolling tyre, 0 on tarmac.
        ///
        /// <para><b>The verge is the louder of the two now, and it used to be the quieter.</b> The
        /// original pair read 0.72 for the shoulder against 1 for open ground, on the reasoning that
        /// open ground is the rougher ride — which it is. But this number is read by exactly one thing,
        /// and that thing is the level of a sound. A gravel verge is loose stone thrown against the arch
        /// liners at whatever speed the car is doing; grass and earth are a duller and genuinely quieter
        /// thing to roll over. Written as a ride-quality figure and spent as a volume, it had the two
        /// backwards.</para>
        ///
        /// <para>Neither is near zero, because the pair have to stay tellable apart while both are being
        /// heard: dropping two wheels onto a verge is the common case and it must not sound like half a
        /// field.</para>
        /// </summary>
        public static float RoughnessOf(SurfaceKind value)
        {
            switch (value)
            {
                case SurfaceKind.Shoulder:
                    return 1f;
                case SurfaceKind.Ground:
                    return 0.82f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Which of the two off-tarmac surfaces this is: 1 for loose stone, 0 for soft ground, and
        /// meaningless on tarmac, where nothing is asked of it.
        ///
        /// <para><b>It exists because level was doing the work of character, which is the mistake this
        /// project has already recorded twice.</b> The scrape and the rumble are separated by register
        /// because they can play at once; the wind and the water were meant to be separated by shape for
        /// the same reason. Gravel and grass had nothing at all between them — one clip, played quieter
        /// for one of them — so a car on a verge and a car in a field made the same noise at different
        /// volumes, which reads as one surface at two distances rather than as two surfaces.</para>
        /// </summary>
        public static float GritOf(SurfaceKind value) => value == SurfaceKind.Shoulder ? 1f : 0f;
    }
}
