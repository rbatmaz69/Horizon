using UnityEngine;

namespace Horizon.Net
{
    /// <summary>
    /// The handful of booleans a car has to publish for somebody else's screen to be right.
    ///
    /// <para>One byte, and it is deliberately not eight separate fields on the wire: every one of
    /// these is read by exactly one thing on the far side — a material swap or a buffer decision —
    /// and none of them is worth a byte of its own.</para>
    /// </summary>
    [System.Flags]
    public enum CarFlags : byte
    {
        None = 0,

        /// <summary>Brake lamps. Not "decelerating" — see <c>RemoteCar</c> for why those differ.</summary>
        Braking = 1 << 0,

        Reversing = 1 << 1,

        Handbrake = 1 << 2,

        /// <summary>Headlamps lit. Decided by the sender's clock so it cannot disagree with its own sky.</summary>
        Headlights = 1 << 3,

        Drifting = 1 << 4,

        /// <summary>No wheel on the ground. Kept because it is what a landing looks like from outside.</summary>
        Airborne = 1 << 5,

        /// <summary>
        /// This car did not drive here.
        ///
        /// <para>Respawn, <c>PauseMenu.MoveTo</c> and every start place move a car by kilometres, and
        /// an interpolator handed two positions a valley apart slides the car through the landscape at
        /// two hundred metres a second. The receiver empties its buffer and sets hard when it sees
        /// this. Same idea as <c>VehicleController</c>'s own impact suppression after a
        /// <c>Teleport</c>: a placement is not a manoeuvre.</para>
        /// </summary>
        Teleported = 1 << 6,
    }

    /// <summary>
    /// One car at one instant, as it goes over the wire: thirty-two bytes.
    ///
    /// <para><b>Position is three plain floats and that is not laziness.</b> The world is roughly
    /// sixteen kilometres by ten, and a float32 carries about a millimetre at thirteen kilometres from
    /// the origin — far finer than anything anybody can see at the distance another car is ever
    /// resolved from. Quantising to centimetres would save nothing worth the second place for the
    /// range to be wrong; the twelve bytes are not the problem this protocol has.</para>
    ///
    /// <para><b>Rotation is the whole quaternion as four int16 and not a smallest-three.</b> That
    /// trick saves four bytes and costs a branch on decode plus a class of bug — the dropped component
    /// being reconstructed with the wrong sign — that shows as a car occasionally upside down. Four
    /// bytes at fifteen hertz is four hundred and eighty bytes a second across eight players.</para>
    ///
    /// <para><b>Velocity is carried so the receiver can extrapolate</b> through one missing packet
    /// rather than freezing. Decimetres a second is a tenth of a metre per second of resolution over a
    /// range no car in this game approaches.</para>
    ///
    /// <para><b>Body and paint ride along in every snapshot</b> rather than only in the roster. They
    /// cost two bytes and remove a whole failure: a car that appears before its roster row arrives
    /// would otherwise be drawn as a fastback in the first paint for up to a second, which reads as
    /// the garage being broken.</para>
    /// </summary>
    public struct CarSnapshot
    {
        public byte PeerId;
        public CarFlags Flags;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;

        /// <summary>Front wheel angle in degrees, so the wheels point where the driver is pointing them.</summary>
        public float SteerDegrees;

        /// <summary>Revs as a fraction of the redline. Enough for a pitch, which is all a distant car needs.</summary>
        public float Revs01;

        public byte Body;
        public byte Paint;

        /// <summary>
        /// When this arrived, on the receiver's clock.
        ///
        /// <para>Not on the wire — it is filled in on receipt. The sender's <c>Time.time</c> is
        /// meaningless here, and the only thing the interpolator needs is the spacing between the
        /// arrivals it has actually seen.</para>
        /// </summary>
        public float ReceivedAt;

        public bool Has(CarFlags flag) => (Flags & flag) != 0;
    }
}
