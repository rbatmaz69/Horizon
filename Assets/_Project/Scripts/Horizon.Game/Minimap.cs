using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The map in the top-left corner: the world under the car, turned so the way ahead is up.
    ///
    /// <para><b>Heading-up rather than north-up.</b> The map is read at a glance taken off the road, and
    /// the only question being asked of it is which way the next corner goes. North-up answers that
    /// question backwards half the time. The car glyph therefore never moves — it sits in the middle
    /// pointing up, and the world turns underneath it — and the compass needle is what keeps north
    /// findable.</para>
    ///
    /// <para><b>It is the one instrument that is also a control.</b> The rev counter and the fuel gauge
    /// switch their raycasts off, because a readout that swallows taps is worse than one that cannot be
    /// touched. This one is tapped, so it carries a <c>Button</c> and its own graphic stays in the
    /// raycast — see <c>PauseMenu.OpenMap</c> for what the tap does.</para>
    /// </summary>
    public sealed class Minimap : MonoBehaviour
    {
        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when the scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [SerializeField] private MapGraphic graphic;

        [Tooltip("Turns to keep pointing at world +Z. Rotated by the heading itself, because a rect's "
               + "own up vector is (-sin, cos) of its z rotation — which is exactly where north lands "
               + "once the map has been turned by the same angle.")]
        [SerializeField] private RectTransform northNeedle;

        /// <summary>
        /// How much world the widget spans, metres.
        ///
        /// <para><b>Four hundred and forty, and it was 340.</b> The old number was argued from the far
        /// plane — wider than 600 m and the map would be telling the player about ground they have no
        /// other way of knowing, which is the full-screen map's job. That is still true, and 340 turned
        /// out to be the wrong side of a different limit: the widget is 300 units across and its clip
        /// takes the inner 80 %, so a car in the middle of it could see <b>136 m</b> of road ahead.
        /// At a hundred kilometres an hour that is five seconds, which is not enough to read a corner
        /// off a map — the complaint was that a road coming towards you cannot be guessed at.</para>
        ///
        /// <para>Zoom alone would have paid for that by shrinking everything, so most of it is bought
        /// with <see cref="ForwardBias"/> instead. Together they give about 260 m ahead, still inside
        /// the far plane.</para>
        /// </summary>
        [SerializeField] private float metresAcross = 440f;

        /// <summary>
        /// How much world the widget spans, metres. Read by the HUD preview, which used to carry its
        /// own copy of the number and therefore photographed the old zoom for as long as it took anybody
        /// to notice.
        /// </summary>
        public float MetresAcross => metresAcross;

        /// <summary>
        /// How far down the widget the car sits, as a fraction of its half-height.
        ///
        /// <para><b>A heading-up map with the car in the middle spends half of itself on where the
        /// driver has just been.</b> That half is worth very little: the road behind has been driven and
        /// the mirror is not the instrument for it. Sliding the car down the disc and pushing the view
        /// the same distance forward buys fifty per cent more road ahead at no zoom cost at all, which
        /// is what the widget is actually asked for.</para>
        ///
        /// <para>Public and a constant because <b>two things have to agree about it</b>: this component
        /// shifts the view, and <c>TouchUiSetup</c> places the car sprite. Written twice they would
        /// agree until the first time one of them was retuned, and the symptom would be a marker that
        /// no longer sits on the road it is meant to be on.</para>
        /// </summary>
        public const float ForwardBias = 0.4f;

        /// <summary>
        /// How far the car has to move, and how far it has to turn, before the mesh is rebuilt.
        ///
        /// <para>Half a metre is about half a canvas unit at this zoom — under the eye's threshold for
        /// a step, and enough that a stopped car costs nothing at all rather than rebuilding sixty
        /// times a second to draw the same picture.</para>
        /// </summary>
        private const float MoveThreshold = 0.5f;

        private const float TurnThreshold = 0.3f;

        private Vector2 shown;
        private float shownHeading;
        private float shownScale;
        private bool everShown;

        private void Update()
        {
            if (graphic == null)
            {
                return;
            }

            if (vehicle == null)
            {
                // Retried rather than resolved once: this component is in Bootstrap and the car is in
                // the world scene, which is still loading for the first frames.
                vehicle = FindFirstObjectByType<VehicleController>();
                if (vehicle == null)
                {
                    return;
                }
            }

            Vector3 position = vehicle.transform.position;
            Vector3 forward = vehicle.transform.forward;

            var centre = new Vector2(position.x, position.z);
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            // The zoom is worked out before the gate rather than after it. A stretched rect reports no
            // width until the first layout pass, so the very first sample can come out at a zoom of the
            // whole world — and a gate that watched only the car would hold that picture until the
            // player moved.
            var rect = (RectTransform)graphic.transform;
            float scale = metresAcross / Mathf.Max(1f, rect.rect.width);

            if (everShown
                && Mathf.Approximately(shownScale, scale)
                && (centre - shown).sqrMagnitude < MoveThreshold * MoveThreshold
                && Mathf.Abs(Mathf.DeltaAngle(shownHeading, heading)) < TurnThreshold)
            {
                return;
            }

            shown = centre;
            shownHeading = heading;
            shownScale = scale;
            everShown = true;

            // The view runs ahead of the car by exactly as far as the sprite sits behind the middle,
            // so the marker still lands on the road under it. See ForwardBias.
            var ahead = new Vector2(forward.x, forward.z);
            ahead = ahead.sqrMagnitude > 0.0001f ? ahead.normalized : Vector2.up;

            graphic.SetView(
                centre + ahead * (rect.rect.height * 0.5f * ForwardBias * scale), scale, heading);

            if (northNeedle != null)
            {
                northNeedle.localRotation = Quaternion.Euler(0f, 0f, heading);
            }
        }
    }
}
