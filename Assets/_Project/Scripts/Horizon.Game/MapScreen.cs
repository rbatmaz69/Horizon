using Horizon.Vehicle;
using Horizon.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The full-screen map: the whole world, north up, with the towns and the places named.
    ///
    /// <para><b>North-up here, and heading-up on the minimap, and the difference is the point.</b> The
    /// minimap answers "which way does the next corner go", which only heading-up answers. This one
    /// answers "where am I and what else is there", and a world that spins under the reader answers
    /// that badly. The car keeps an arrow of its own so its heading is still on the page.</para>
    ///
    /// <para><b>A menu page, so the world stops.</b> Everything else about it follows from that:
    /// <c>MenuPanels</c> makes it exclusive, <c>PauseMenu.SetPaused</c> has already frozen the car and
    /// cleared whatever a thumb was holding, and Back lands on the pause menu. A live map over a moving
    /// car would be a map read while rolling down a mountainside.</para>
    ///
    /// <para><b>Drag to pan, buttons to zoom.</b> Not pinch: a pinch means tracking a second pointer id
    /// through the drag stream for one gesture, and every other page in this menu is worked with one
    /// thumb.</para>
    /// </summary>
    public sealed class MapScreen : MonoBehaviour, IDragHandler
    {
        [SerializeField] private MapGraphic graphic;

        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when the scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("The arrow showing where the car is and which way it faces.")]
        [SerializeField] private RectTransform carMarker;

        [Tooltip("A fixed pool, filled from the top on every view change and hidden from wherever it "
               + "runs out. Text allocates when it is written, so these are written only when the view "
               + "actually moves — never per frame.")]
        [SerializeField] private Text[] labels = new Text[0];

        /// <summary>
        /// Past this many metres to the unit, only the towns and the start places keep their names.
        ///
        /// <para>Forty of the markers are tunnels, viewpoints and pumps. Named all at once on a view of
        /// the whole world they are not labels but a hedge.</para>
        /// </summary>
        [SerializeField] private float featureLabelLimit = 9f;

        /// <summary>Closest the map will zoom, metres to the unit. Below this a street is a corridor.</summary>
        [SerializeField] private float closestZoom = 1.2f;

        /// <summary>What one press of a zoom button does.</summary>
        [SerializeField] private float zoomStep = 1.45f;

        [SerializeField] private float labelOffset = 15f;

        private float scaleFactor = 1f;

        private void OnEnable()
        {
            Open();
        }

        /// <summary>
        /// Everything showing this page has to do: find the canvas, find the car, aim the view.
        ///
        /// <para>Split out of <see cref="OnEnable"/> so a tool can put the page in front of a camera.
        /// <c>OnEnable</c> does not run in the editor for a plain <c>MonoBehaviour</c>, so a preview that
        /// merely switched the panel on would photograph a map aimed at the world origin with no labels —
        /// a picture of nothing that looks like a picture of a fault.</para>
        /// </summary>
        public void Open()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            scaleFactor = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;

            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            // Opened on the car rather than on the whole world. Somebody who taps a minimap is asking
            // about where they are; the Fit button is one press away for the other question.
            CentreOnCar();
        }

        /// <summary>Wired to the map page's own buttons, so each is one baked call.</summary>
        public void ZoomIn()
        {
            Rescale(1f / zoomStep);
        }

        public void ZoomOut()
        {
            Rescale(zoomStep);
        }

        public void Fit()
        {
            if (graphic == null || graphic.Map == null)
            {
                return;
            }

            graphic.SetView(graphic.Map.PlanCentre, graphic.FitScale(), 0f);
            Refresh();
        }

        public void CentreOnCar()
        {
            if (graphic == null || graphic.Map == null)
            {
                return;
            }

            if (vehicle == null)
            {
                Fit();
                return;
            }

            Vector3 position = vehicle.transform.position;

            // Half the fitted scale: near enough to read the roads around the car, far enough that the
            // next town is usually on the page.
            float scale = Mathf.Clamp(graphic.FitScale() * 0.5f, closestZoom, graphic.FitScale());

            graphic.SetView(new Vector2(position.x, position.z), scale, 0f);
            Refresh();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (graphic == null || graphic.Map == null)
            {
                return;
            }

            // Screen pixels to canvas units to metres. The view is north-up, so there is no rotation to
            // undo between the finger and the world.
            Vector2 moved = eventData.delta / scaleFactor * graphic.MetresPerUnit;

            SetCentre(graphic.Centre - moved, graphic.MetresPerUnit);
        }

        private void Rescale(float factor)
        {
            if (graphic == null || graphic.Map == null)
            {
                return;
            }

            float scale = Mathf.Clamp(graphic.MetresPerUnit * factor, closestZoom, graphic.FitScale());
            SetCentre(graphic.Centre, scale);
        }

        /// <summary>
        /// Moves the view, keeping it over the world.
        ///
        /// <para>Clamped to the world's own bounds rather than left free: a map dragged off the edge is
        /// an empty rectangle with no way to tell which way is back.</para>
        /// </summary>
        private void SetCentre(Vector2 centre, float scale)
        {
            Vector2 min = graphic.Map.PlanMin;
            Vector2 max = graphic.Map.PlanMax;

            centre.x = Mathf.Clamp(centre.x, min.x, max.x);
            centre.y = Mathf.Clamp(centre.y, min.y, max.y);

            graphic.SetView(centre, scale, 0f);
            Refresh();
        }

        /// <summary>
        /// Places the car arrow and as many labels as there are slots for.
        ///
        /// <para>Called on a view change, never per frame. The world is stopped while this page is up,
        /// so there is nothing to follow.</para>
        /// </summary>
        private void Refresh()
        {
            WorldMap map = graphic.Map;
            int used = 0;

            // Towns first, so they never lose their slot to a viewpoint.
            for (int area = 0; area < map.AreaCount && used < labels.Length; area++)
            {
                if (map.AreaKindOf(area) != MapAreaKind.Town)
                {
                    continue;
                }

                string name = map.AreaNameOf(area);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                used = Place(used, name, Centroid(map, area));
            }

            bool features = graphic.MetresPerUnit <= featureLabelLimit;

            for (int i = 0; i < map.MarkerCount && used < labels.Length; i++)
            {
                if (!features && map.MarkerKindOf(i) != MapMarkerKind.Place)
                {
                    continue;
                }

                used = Place(used, map.MarkerNameOf(i), map.MarkerAt(i));
            }

            for (int i = used; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].gameObject.activeSelf)
                {
                    labels[i].gameObject.SetActive(false);
                }
            }

            PlaceCar();
        }

        /// <summary>Writes one label, or skips the slot if the point is off the page.</summary>
        private int Place(int slot, string name, Vector2 world)
        {
            Text label = labels[slot];
            if (label == null)
            {
                return slot + 1;
            }

            Vector2 at = graphic.LocalPointOf(world);
            Rect area = ((RectTransform)graphic.transform).rect;

            if (!area.Contains(at))
            {
                if (label.gameObject.activeSelf)
                {
                    label.gameObject.SetActive(false);
                }

                return slot;
            }

            if (!label.gameObject.activeSelf)
            {
                label.gameObject.SetActive(true);
            }

            if (label.text != name)
            {
                label.text = name;
            }

            label.rectTransform.anchoredPosition = at + new Vector2(0f, labelOffset);
            return slot + 1;
        }

        private void PlaceCar()
        {
            if (carMarker == null)
            {
                return;
            }

            if (vehicle == null)
            {
                carMarker.gameObject.SetActive(false);
                return;
            }

            if (!carMarker.gameObject.activeSelf)
            {
                carMarker.gameObject.SetActive(true);
            }

            Vector3 position = vehicle.transform.position;
            Vector3 forward = vehicle.transform.forward;

            carMarker.anchoredPosition = graphic.LocalPointOf(new Vector2(position.x, position.z));

            // The view is north-up, so the arrow carries the heading itself. A rect's own up vector is
            // (-sin, cos) of its z rotation, which lands on the car's forward at minus the heading.
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            carMarker.localRotation = Quaternion.Euler(0f, 0f, -heading);
        }

        private static Vector2 Centroid(WorldMap map, int area)
        {
            int from = map.AreaStartAt(area);
            int to = map.AreaEndAt(area);

            var sum = Vector2.zero;
            for (int p = from; p < to; p++)
            {
                sum += map.AreaPointAt(p);
            }

            return to > from ? sum / (to - from) : sum;
        }
    }
}
