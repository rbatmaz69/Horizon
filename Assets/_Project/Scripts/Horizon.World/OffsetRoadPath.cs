using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// A centreline shifted sideways: the same road, measured from a lane or a carriageway rather than
    /// from the middle.
    ///
    /// <para><b>Why this is a decorator and not a feature of the mesh builders.</b> A divided highway is
    /// two carriageways with a median between them, and the obvious implementation is an offset argument
    /// threaded through <see cref="RoadMeshBuilder"/>, <see cref="GuardRailBuilder"/>,
    /// <see cref="TunnelBuilder"/> and <see cref="MountainField"/> — four builders changed to say the
    /// same thing four times. Every one of them already takes an <see cref="IRoadPath"/> and asks it
    /// only where the road is and which way it points. Answering those two questions from one metre to
    /// the side is the whole of what a carriageway is, so it belongs here, once, and nothing downstream
    /// needs to know that a road can be divided at all.</para>
    ///
    /// <para><b>The one inaccuracy, stated rather than hidden.</b> Arc length is not preserved under a
    /// lateral offset: through a bend the outer carriageway is genuinely longer than the centreline by
    /// roughly <c>offset / radius</c>, and this class does not correct for it — a distance passed in is
    /// a distance along the <i>centreline</i>, and the point returned is beside it. That is deliberate,
    /// because it is what keeps the two carriageways in step: the same distance means the same place
    /// across the road, so a junction, a tunnel portal or a bridge span lands square on both sides. The
    /// cost is that anything spaced by distance stretches slightly on the outside of a bend — at
    /// <c>AutobahnCourse.CarriagewayOffset</c> and the 700 m radii a motorway is built to, under 2 %,
    /// which is a lane dash a few centimetres longer than its neighbour. Do not use this for a tight radius, where that ratio stops being small and
    /// an inner offset can fold through the centre of the arc entirely.</para>
    /// </summary>
    public sealed class OffsetRoadPath : IRoadPath
    {
        private readonly IRoadPath inner;
        private readonly float offset;

        /// <param name="inner">The centreline to measure from.</param>
        /// <param name="offset">
        /// Metres to the <b>right</b> of it, in the direction of travel. Negative is left.
        /// </param>
        public OffsetRoadPath(IRoadPath inner, float offset)
        {
            this.inner = inner;
            this.offset = offset;
        }

        /// <summary>The centreline it is measured from, for anything that needs the road as a whole.</summary>
        public IRoadPath Centre => inner;

        /// <summary>Metres to the right of the centreline.</summary>
        public float Offset => offset;

        /// <summary>
        /// The centreline's length, not this path's own. See the note on the class: distances are
        /// centreline distances by design, so the two carriageways stay in step.
        /// </summary>
        public float Length => inner.Length;

        public bool IsLoop => inner.IsLoop;

        public Vector3 GetPositionAtDistance(float distance)
        {
            // GetRightAtDistance and not the banked variant: this is where the carriageway *is*, and
            // camber is a roll applied to the ribbon about that line, not a displacement of it. Using
            // the banked right here would slide the whole carriageway sideways as the road leaned.
            return inner.GetPositionAtDistance(distance) + inner.GetRightAtDistance(distance) * offset;
        }

        public Vector3 GetDirectionAtDistance(float distance)
        {
            // Parallel curves share a tangent direction at matching parameter — the offset changes
            // where you are, never which way you face.
            return inner.GetDirectionAtDistance(distance);
        }
    }
}
