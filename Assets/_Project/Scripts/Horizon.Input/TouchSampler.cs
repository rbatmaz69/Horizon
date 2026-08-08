using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Horizon.Input
{
    /// <summary>
    /// Reads touches as normalized viewport points, and falls back to the mouse in the Editor so
    /// the touch schemes can be tested on desktop without a device. Allocation-free: it walks
    /// <see cref="Touchscreen.touches"/> directly rather than using EnhancedTouch's managed lists.
    /// </summary>
    public static class TouchSampler
    {
        /// <summary>True if any active touch (or emulated mouse press) lies inside the region.</summary>
        public static bool IsPressed(Rect viewportRegion)
        {
            return TryGetPress(viewportRegion, out _, out _);
        }

        /// <summary>
        /// Finds the first active press inside the region. <paramref name="pressId"/> stays stable
        /// for the life of that finger, which is what drag-to-steer needs to track one touch.
        /// </summary>
        public static bool TryGetPress(Rect viewportRegion, out Vector2 viewportPosition, out int pressId)
        {
            Touchscreen screen = Touchscreen.current;
            if (screen != null)
            {
                var touches = screen.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    TouchControl touch = touches[i];
                    if (!IsActivePhase(touch.phase.ReadValue()))
                    {
                        continue;
                    }

                    Vector2 point = ToViewport(touch.position.ReadValue());
                    if (viewportRegion.Contains(point))
                    {
                        viewportPosition = point;
                        pressId = touch.touchId.ReadValue();
                        return true;
                    }
                }
            }

            // Mouse fallback. Keeps the touch schemes testable in the Editor; on device there is
            // no Mouse device, so this costs nothing.
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
            {
                Vector2 point = ToViewport(mouse.position.ReadValue());
                if (viewportRegion.Contains(point))
                {
                    viewportPosition = point;
                    pressId = MousePressId;
                    return true;
                }
            }

            viewportPosition = default;
            pressId = 0;
            return false;
        }

        /// <summary>Looks up a press by the id returned from <see cref="TryGetPress"/>.</summary>
        public static bool TryGetPressById(int pressId, out Vector2 viewportPosition)
        {
            if (pressId == MousePressId)
            {
                Mouse mouse = Mouse.current;
                if (mouse != null && mouse.leftButton.isPressed)
                {
                    viewportPosition = ToViewport(mouse.position.ReadValue());
                    return true;
                }

                viewportPosition = default;
                return false;
            }

            Touchscreen screen = Touchscreen.current;
            if (screen != null)
            {
                var touches = screen.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    TouchControl touch = touches[i];
                    if (touch.touchId.ReadValue() != pressId || !IsActivePhase(touch.phase.ReadValue()))
                    {
                        continue;
                    }

                    viewportPosition = ToViewport(touch.position.ReadValue());
                    return true;
                }
            }

            viewportPosition = default;
            return false;
        }

        private const int MousePressId = -999;

        private static bool IsActivePhase(UnityEngine.InputSystem.TouchPhase phase)
        {
            return phase == UnityEngine.InputSystem.TouchPhase.Began
                || phase == UnityEngine.InputSystem.TouchPhase.Moved
                || phase == UnityEngine.InputSystem.TouchPhase.Stationary;
        }

        private static Vector2 ToViewport(Vector2 screenPosition)
        {
            float width = Mathf.Max(1, Screen.width);
            float height = Mathf.Max(1, Screen.height);
            return new Vector2(screenPosition.x / width, screenPosition.y / height);
        }
    }
}
