using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Insets a rect to the device's safe area, so no control ends up under a notch, a punch-hole or
    /// the gesture bar.
    ///
    /// <para>It has to be done at run time: the safe area is a property of the handset and is simply
    /// not known while the scene is being built. It also changes — rotating between the two landscape
    /// orientations moves the notch from one end to the other, and a layout inset once at startup puts
    /// the handbrake under it for the rest of the session.</para>
    ///
    /// <para>Cheap enough to check every frame, but it only touches the transform when something has
    /// actually moved, because writing to a <c>RectTransform</c> dirties the whole canvas below it.</para>
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeAreaPanel : MonoBehaviour
    {
        private RectTransform self;
        private Rect applied;
        private Vector2Int appliedResolution;

        private void Awake()
        {
            self = (RectTransform)transform;
            Apply();
        }

        private void Update()
        {
            Apply();
        }

        private void Apply()
        {
            Rect safe = Screen.safeArea;
            var resolution = new Vector2Int(Screen.width, Screen.height);

            if (safe == applied && resolution == appliedResolution)
            {
                return;
            }

            applied = safe;
            appliedResolution = resolution;

            if (resolution.x <= 0 || resolution.y <= 0)
            {
                return;
            }

            // Safe area is in pixels; anchors are fractions of the screen.
            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;

            min.x /= resolution.x;
            min.y /= resolution.y;
            max.x /= resolution.x;
            max.y /= resolution.y;

            self.anchorMin = min;
            self.anchorMax = max;
            self.offsetMin = Vector2.zero;
            self.offsetMax = Vector2.zero;
        }
    }
}
