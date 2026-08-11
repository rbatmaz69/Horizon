using Horizon.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Horizon.Game
{
    /// <summary>
    /// A vertical throttle slider: push up from rest to accelerate, pull down to brake.
    ///
    /// <para>Springs back to rest on release like the wheel does, and for the same reason — a control
    /// you have to remember to put back is one you will forget to put back at the worst moment. What it
    /// buys over the pedals is proportion: a slider can ask for a third of the throttle, which on a car
    /// that now spins its wheels in first gear is the difference between pulling away and sitting there
    /// making smoke.</para>
    /// </summary>
    public sealed class TouchThrottleSlider : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        [Tooltip("Optional knob moved to show the current position.")]
        [SerializeField] private RectTransform knob;

        [Tooltip("Fraction of the travel either side of centre that does nothing, so resting a thumb "
               + "on the middle does not creep the car forward.")]
        [Range(0f, 0.4f)] [SerializeField] private float deadzone = 0.12f;

        [SerializeField] private float returnRate = 4f;

        private RectTransform self;
        private Camera uiCamera;
        private float value;
        private bool dragging;

        private void Awake()
        {
            self = (RectTransform)transform;
        }

        private void OnEnable()
        {
            value = 0f;
            dragging = false;
            Publish();
        }

        private void OnDisable()
        {
            dragging = false;
            TouchControlState.Throttle = 0f;
            TouchControlState.Brake = 0f;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            uiCamera = eventData.pressEventCamera;
            dragging = true;
            Track(eventData.position);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            dragging = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            Track(eventData.position);
        }

        private void Update()
        {
            if (dragging)
            {
                return;
            }

            value = Mathf.MoveTowards(value, 0f, returnRate * Time.unscaledDeltaTime);
            Publish();
        }

        private void Track(Vector2 screenPoint)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    self, screenPoint, uiCamera, out Vector2 local))
            {
                return;
            }

            float half = Mathf.Max(1f, self.rect.height * 0.5f);
            value = Mathf.Clamp(local.y / half, -1f, 1f);
            Publish();
        }

        private void Publish()
        {
            float scaled = Mathf.Abs(value) <= deadzone
                ? 0f
                : Mathf.Sign(value) * (Mathf.Abs(value) - deadzone) / (1f - deadzone);

            TouchControlState.Throttle = Mathf.Max(0f, scaled);
            TouchControlState.Brake = Mathf.Max(0f, -scaled);

            if (knob != null)
            {
                knob.anchoredPosition = new Vector2(0f, value * self.rect.height * 0.5f);
            }
        }
    }
}
