using Horizon.Net;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The other players, drawn on a map.
    ///
    /// <para><b>Sprites rather than a new <c>MapMarkerKind</c>, and that is not a shortcut.</b> The
    /// marks <c>WorldMap</c> carries — filling stations, viewpoints, tunnels, start places — are
    /// <i>baked</i>: they are part of an asset built once by the setup tool, and a player who joined
    /// two minutes ago is not. The car marker already answers exactly this problem for the driver's own
    /// car, and this is that answer repeated. It also means <c>MapLine.KindCount</c>, the two emit
    /// lists in <c>MapGraphic.OnPopulateMesh</c> and the legend's line kinds are all untouched — the
    /// array-sizing mistake that once made the entire map draw nothing.</para>
    ///
    /// <para><b>The clip is this component's own.</b> <c>MapGraphic</c> clips the geometry it emits
    /// against the minimap's disc — deliberately, so no stencil pass is needed on a tile GPU — and a
    /// separate <c>Image</c> is not part of that geometry. Left to itself a friend two kilometres away
    /// would be drawn as a marker sitting on the rim, outside the map, over the rev counter.</para>
    /// </summary>
    public sealed class RemoteMapMarkers : MonoBehaviour
    {
        /// <summary>
        /// What another player is drawn in.
        ///
        /// <para><b>One constant, read by both the marker and the key beside the full-screen map.</b>
        /// The legend already takes every other swatch off <c>MapGraphic.ColourOf</c> for the reason
        /// this file's own remarks give: a key with its own copy of a colour agrees until the first
        /// retune and then quietly lies. Cool against the accent orange the driver's own marker wears,
        /// so which arrow is you is answerable without reading anything.</para>
        /// </summary>
        public static readonly Color MarkerTint = new Color(0.35f, 0.72f, 0.92f, 0.95f);

        [SerializeField] private MapGraphic graphic;

        [Tooltip("One per possible guest. Built active and hidden on the first frame, never the other "
               + "way round — anything left inactive is invisible in every picture this project takes.")]
        [SerializeField] private RectTransform[] markers = new RectTransform[0];

        [Tooltip("Radius, in canvas units, past which a marker is off the map. Zero clips against the "
               + "graphic's rectangle instead, which is what the full-screen map wants.")]
        [SerializeField] private float clipRadius;

        private RemoteCarPool pool;

        private void Update()
        {
            if (graphic == null || markers.Length == 0)
            {
                return;
            }

            if (pool == null)
            {
                // Retried while empty: this lives in Bootstrap and the pool is in the world scene,
                // which is still loading for the first frames.
                pool = FindFirstObjectByType<RemoteCarPool>();

                if (pool == null)
                {
                    HideFrom(0);
                    return;
                }
            }

            var rect = (RectTransform)graphic.transform;
            int shown = 0;

            for (int i = 0; i < pool.SlotCount && shown < markers.Length; i++)
            {
                RemoteCar car = pool.At(i);

                if (car == null || !car.InUse || !car.HasPose)
                {
                    continue;
                }

                Vector3 position = car.DrawnPosition;
                Vector2 point = graphic.LocalPointOf(new Vector2(position.x, position.z));

                if (!Inside(point, rect))
                {
                    continue;
                }

                RectTransform marker = markers[shown];

                if (marker == null)
                {
                    shown++;
                    continue;
                }

                if (!marker.gameObject.activeSelf)
                {
                    marker.gameObject.SetActive(true);
                }

                marker.anchoredPosition = point;

                // The map may be turned — the minimap is heading-up — so a marker's heading is its own
                // bearing measured against the view's, not against the world.
                Vector3 forward = car.DrawnRotation * Vector3.forward;
                float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg
                                - graphic.HeadingDegrees;

                marker.localRotation = Quaternion.Euler(0f, 0f, -heading);
                shown++;
            }

            HideFrom(shown);
        }

        private bool Inside(Vector2 point, RectTransform rect)
        {
            if (clipRadius > 0f)
            {
                return point.sqrMagnitude <= clipRadius * clipRadius;
            }

            Rect area = rect.rect;
            return point.x >= area.xMin && point.x <= area.xMax
                   && point.y >= area.yMin && point.y <= area.yMax;
        }

        private void HideFrom(int index)
        {
            for (int i = index; i < markers.Length; i++)
            {
                if (markers[i] != null && markers[i].gameObject.activeSelf)
                {
                    markers[i].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Wired by the setup tool. Nothing else may call it.</summary>
        public void SetParts(MapGraphic map, RectTransform[] built, float radius)
        {
            graphic = map;
            markers = built;
            clipRadius = radius;
        }

        /// <summary>How many markers a map needs: one for every guest the protocol admits.</summary>
        public const int MarkerCount = NetProtocol.MaxPeers - 1;
    }
}
