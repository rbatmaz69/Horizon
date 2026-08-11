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
        [Tooltip("Rotation from centre, in degrees, that means full lock.")]
        [SerializeField] private float lockDegrees = 110f;

        [Tooltip("Degrees per second the wheel returns to centre once released. A wheel that snaps "
               + "back instantly is a switch; one that never returns leaves the car turning.")]
        [SerializeField] private float returnRate = 420f;

        [SerializeField] private RectTransform wheel;

        private RectTransform self;
        private Camera uiCamera;
        private float angle;
        private float grabOffset;
        private bool dragging;

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
            Publish();
        }

        private void OnDisable()
        {
            dragging = false;
            TouchControlState.Steer = 0f;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            uiCamera = eventData.pressEventCamera;
            dragging = true;

            // Remember where on the rim the wheel was grabbed, so it does not jump to put the hub
            // under the finger the instant it is touched.
            if (TryAngleAt(eventData.position, out float pointer))
            {
                grabOffset = pointer - angle;
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            dragging = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || !TryAngleAt(eventData.position, out float pointer))
            {
                return;
            }

            angle = Mathf.Clamp(pointer - grabOffset, -lockDegrees, lockDegrees);
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
            TouchControlState.Steer = Mathf.Clamp(angle / Mathf.Max(1f, lockDegrees), -1f, 1f);

            if (wheel != null)
            {
                // Negated: turning the wheel clockwise on screen is a right turn, and screen-space
                // rotation runs the other way round.
                wheel.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }
        }

        private bool TryAngleAt(Vector2 screenPoint, out float degrees)
        {
            degrees = 0f;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    self, screenPoint, uiCamera, out Vector2 local))
            {
                return false;
            }

            // Too near the hub and the angle is meaningless — a millimetre of wobble would be a
            // hundred degrees of steering.
            if (local.sqrMagnitude < 100f)
            {
                return false;
            }

            degrees = -Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
            return true;
        }
    }
}
