using Horizon.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Horizon.Game
{
    /// <summary>
    /// The steering wheel: drag it round, let go and it springs back.
    ///
    /// <para>Steering comes from the <i>angle</i> of the drag about the wheel's centre rather than from
    /// how far the finger has moved sideways. Horizontal displacement is the obvious implementation and
    /// it is wrong in a way you feel immediately: the further down the wheel your thumb is, the more it
    /// oversteers for the same movement, because the same sideways distance is a much larger rotation
    /// near the hub. Working in angle makes the wheel behave like a wheel wherever it is gripped.</para>
    /// </summary>
    public sealed class TouchSteeringWheel : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        /// <summary>
        /// Rotation from centre that means full lock, at zero sensitivity.
        ///
        /// <para>Not a serialized field, and that is deliberate. It was one, and the scene held 110 for
        /// weeks after the source said 75, because a serialized field is authored in two places and the
        /// scene always wins. Deriving it from the sensitivity setting leaves exactly one place the
        /// number can come from — and makes it something the player can answer for themselves, which is
        /// the right home for a question about the reach of a thumb.</para>
        /// </summary>
        private const float WidestLock = 110f;

        /// <summary>
        /// Full lock at full sensitivity. A thumb pivoting on the rim of a wheel in the corner of a
        /// phone covers about 45° comfortably without regripping.
        /// </summary>
        private const float TightestLock = 45f;

        [Tooltip("Degrees per second the wheel returns to centre once released. A wheel that snaps "
               + "back instantly is a switch; one that never returns leaves the car turning.")]
        [SerializeField] private float returnRate = 420f;

        [SerializeField] private RectTransform wheel;

        private RectTransform self;
        private Camera uiCamera;
        private float angle;
        private float lastPointer;
        private bool dragging;
        private bool hasPointer;
        private int pointerId;

        private static float LockDegrees =>
            Mathf.Lerp(WidestLock, TightestLock, Mathf.Clamp01(TouchControlState.SteerSensitivity01));

        private void Awake()
        {
            self = (RectTransform)transform;

            if (wheel == null)
            {
                wheel = self;
            }
        }

        private void OnEnable()
        {
            angle = 0f;
            dragging = false;
            hasPointer = false;
            Publish();
        }

        private void OnDisable()
        {
            dragging = false;
            TouchControlState.Steer = 0f;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            // First finger down owns the wheel until it lifts. Without this the throttle thumb
            // wandering across the rim on its way back takes the grip off the hand that is steering.
            if (dragging)
            {
                return;
            }

            uiCamera = eventData.pressEventCamera;
            dragging = true;
            pointerId = eventData.pointerId;

            // Where on the rim it was grabbed, so the wheel turns with the finger from here rather
            // than jumping to put the hub under it.
            hasPointer = TryAngleAt(eventData.position, out lastPointer);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (dragging && eventData.pointerId == pointerId)
            {
                dragging = false;
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || eventData.pointerId != pointerId)
            {
                return;
            }

            if (!TryAngleAt(eventData.position, out float pointer))
            {
                // Over the dead hub, where there is no meaningful angle. Drop the reference rather
                // than keeping it: a finger that crosses the middle and comes out the far side has
                // swept up to 180° that it never asked the wheel to turn, and measuring against a
                // stale sample on the way in would hand all of it over at once.
                hasPointer = false;
                return;
            }

            // A grab that landed on the hub, or one that has just crossed it, has no angle to move
            // from. Start from the first sample that does.
            if (!hasPointer)
            {
                lastPointer = pointer;
                hasPointer = true;
                return;
            }

            // Accumulated from the *change* in finger angle, never from its absolute value, and this
            // is what makes the wheel work at all. Atan2 returns (-180, 180], so a finger crossing
            // the bottom of the rim steps 360° in one sample — and the bottom of the rim is precisely
            // where a thumb sits when the wheel is in the corner of a phone. Read absolutely, that
            // step drove the wheel to the opposite stop; DeltaAngle folds it back to the small
            // rotation actually made.
            angle = Mathf.Clamp(angle + Mathf.DeltaAngle(lastPointer, pointer), -LockDegrees, LockDegrees);
            lastPointer = pointer;
            Publish();
        }

        private void Update()
        {
            if (dragging)
            {
                return;
            }

            // Unscaled: the wheel has to keep centring while the pause menu holds the time scale at
            // zero, or it is still turned when the game resumes.
            angle = Mathf.MoveTowards(angle, 0f, returnRate * Time.unscaledDeltaTime);
            Publish();
        }

        private void Publish()
        {
            TouchControlState.Steer = Mathf.Clamp(angle / Mathf.Max(1f, LockDegrees), -1f, 1f);

            if (wheel != null)
            {
                // Negated, and this one is correct: `angle` counts clockwise, a positive Z rotation in
                // Unity turns counter-clockwise, so the sign has to flip for the drawn wheel to follow
                // the finger. Both negations used to be here, which cancelled and left the rim turning
                // away from the hand holding it.
                wheel.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }
        }

        /// <summary>
        /// The finger's angle about the wheel's centre, measured in <b>screen</b> space.
        ///
        /// <para>Screen space and not the rect's local space, which was one of the two reasons the wheel
        /// did not work — see <see cref="OnDrag"/> for the other. This component rotates the same rect it
        /// was measuring against, so the local frame turned with the wheel and every reading came back
        /// relative to the rotation it had just been given. The update collapsed to</para>
        ///
        /// <code>angle_new = (finger - grab) - angle_old</code>
        ///
        /// <para>which is a fixed-point iteration: it converges on <i>half</i> the rotation asked for and
        /// alternates about it on the way, so the wheel reached half lock at best and shook while it did
        /// it. A rect's centre does not move when the rect spins, so measuring from it in screen space
        /// has no such feedback.</para>
        /// </summary>
        private bool TryAngleAt(Vector2 screenPoint, out float degrees)
        {
            degrees = 0f;

            Vector2 centre = RectTransformUtility.WorldToScreenPoint(uiCamera, self.position);
            Vector2 offset = screenPoint - centre;

            // Too near the hub and the angle is meaningless — a millimetre of wobble would be a
            // hundred degrees of steering. Taken from the wheel's own size so it holds at any density.
            float minimum = self.rect.width * 0.18f * Mathf.Abs(self.lossyScale.x);
            if (offset.sqrMagnitude < minimum * minimum)
            {
                return false;
            }

            // Atan2(x, y) — x first — is the angle measured *clockwise from straight up*, which is the
            // direction a steering wheel is turned for a right-hand corner. Reading it counter-
            // clockwise instead is the whole of the inverted-steering bug: a thumb sweeping left across
            // the top of the rim turned the car right, and spun the drawn wheel the other way from the
            // finger while it did it.
            degrees = Mathf.Atan2(offset.x, offset.y) * Mathf.Rad2Deg;
            return true;
        }
    }
}
