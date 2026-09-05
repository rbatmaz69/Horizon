using Horizon.Net;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// Whose car that is, written over its roof.
    ///
    /// <para><b>One screen-space canvas with seven labels, not a world-space canvas per car.</b> A
    /// world-space <c>Canvas</c> is a renderer, a material and at least one draw call each, and seven
    /// of them following cars around would be seven more things for the SRP batcher to break on. These
    /// are ordinary rows on the canvas that already exists, moved by
    /// <c>Camera.WorldToScreenPoint</c>.</para>
    ///
    /// <para><b>A label's text is assigned only when the name behind it changes.</b> That is the rev
    /// counter's rule and it applies for the same reason: assigning a string to a <c>Text</c> rebuilds
    /// its mesh, and a tag that says the same thing sixty times a second is sixty rebuilds for one
    /// picture. The position moves every frame, which is a transform and free.</para>
    ///
    /// <para><b>Behind the camera is not the same as far away.</b> <c>WorldToScreenPoint</c> happily
    /// returns a point on screen for something behind the viewer, mirrored through the origin — so a
    /// car you have just overtaken would have its name drawn on the opposite side of the road ahead.
    /// The z of the result is what says which, and it is the first thing tested.</para>
    /// </summary>
    public sealed class RemoteNameTags : MonoBehaviour
    {
        /// <summary>
        /// Past this, a tag is not drawn.
        ///
        /// <para>Inside the camera's 600 m far plane and inside the fog, so a name never floats over a
        /// car that has already been swallowed by the air. Also the distance past which the text would
        /// be a few pixels tall.</para>
        /// </summary>
        private const float MaxDistance = 260f;

        /// <summary>How far over the roof, in metres. A car is about 1.4 m tall.</summary>
        private const float Height = 2.1f;

        [Tooltip("The row each label sits in. This is what moves; the label inside it is stretched to "
               + "fill it, which is how every other caption in this menu is built.")]
        [SerializeField] private RectTransform[] rows = new RectTransform[0];

        [SerializeField] private Text[] labels = new Text[0];

        private RemoteCarPool pool;
        private Camera view;
        private readonly string[] shown = new string[NetProtocol.MaxPeers];
        private NetSession session;

        private void LateUpdate()
        {
            if (rows.Length == 0)
            {
                return;
            }

            if (pool == null)
            {
                pool = FindFirstObjectByType<RemoteCarPool>();
            }

            if (session == null)
            {
                session = FindFirstObjectByType<NetSession>();
            }

            if (view == null)
            {
                view = Camera.main;
            }

            if (pool == null || view == null || session == null)
            {
                HideFrom(0);
                return;
            }

            int used = 0;

            var holder = (RectTransform)transform;

            for (int i = 0; i < pool.SlotCount && used < rows.Length; i++)
            {
                RemoteCar car = pool.At(i);

                if (car == null || !car.InUse || !car.HasPose)
                {
                    continue;
                }

                Vector3 world = car.DrawnPosition + Vector3.up * Height;
                Vector3 screen = view.WorldToScreenPoint(world);

                // Behind the camera, or too far to read.
                if (screen.z <= 0f || screen.z > MaxDistance)
                {
                    continue;
                }

                RectTransform row = rows[used];
                Text label = used < labels.Length ? labels[used] : null;

                if (row == null || label == null)
                {
                    used++;
                    continue;
                }

                if (!row.gameObject.activeSelf)
                {
                    row.gameObject.SetActive(true);
                }

                // Against this component's own rect, which is stretched over the safe area — the row is
                // what moves, and asking it to convert a point relative to itself would be asking about
                // the thing being placed.
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        holder, screen, null, out Vector2 local))
                {
                    row.anchoredPosition = local;
                }

                // The fallback comes out of a table rather than being interpolated here. This runs on
                // every frame for every car in the room, and building a string only to find it equal to
                // the one already shown is the allocation the rev counter's number table exists to
                // avoid — in driving code, which is where the budget forbids it outright.
                PeerInfo info = session.PeerAt(car.PeerId);
                string name = info.InUse && !string.IsNullOrEmpty(info.Name)
                    ? info.Name
                    : NetSession.DefaultNameFor(car.PeerId);

                if (shown[car.PeerId] != name)
                {
                    shown[car.PeerId] = name;
                    label.text = name;
                }

                // Faded out with distance rather than cut at it, so a car pulling away does not have
                // its name blink off at a threshold.
                Color colour = label.color;
                colour.a = Mathf.Clamp01(1f - screen.z / MaxDistance) * 0.85f + 0.15f;
                label.color = colour;

                used++;
            }

            HideFrom(used);
        }

        private void HideFrom(int index)
        {
            for (int i = index; i < rows.Length; i++)
            {
                if (rows[i] != null && rows[i].gameObject.activeSelf)
                {
                    rows[i].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Wired by the setup tool. Nothing else may call it.</summary>
        public void SetLabels(RectTransform[] builtRows, Text[] built)
        {
            rows = builtRows;
            labels = built;
        }

        /// <summary>One per possible guest.</summary>
        public const int LabelCount = NetProtocol.MaxPeers - 1;
    }
}
