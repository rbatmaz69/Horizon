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
        /// <para>Three hundred and forty, against a camera whose far plane is 600 m and a fog wall
        /// inside that. Wider and the map would be showing ground the player has no other way of
        /// knowing about, which is the full-screen map's job; narrower and a hairpin stack would not
        /// fit in it, which is the one thing a map of this world has to show.</para>
        /// </summary>
        [SerializeField] private float metresAcross = 340f;

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

            graphic.SetView(centre, scale, heading);

            if (northNeedle != null)
            {
                northNeedle.localRotation = Quaternion.Euler(0f, 0f, heading);
            }
        }
    }
}
