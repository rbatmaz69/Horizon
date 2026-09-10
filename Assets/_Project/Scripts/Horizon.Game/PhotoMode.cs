using System.Collections;
using System.IO;
using Horizon.Atmosphere;
using Horizon.Core;
using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// A free camera round the parked car, with the HUD gone and the clock in the player's hand.
    ///
    /// <para><b>CLAUDE.md has listed this under "later" since the concept was written, and the reason to
    /// build it now is that the world finally earns it.</b> A photo mode is a claim that the game is
    /// worth looking at from an angle the chase camera never offers — and until there was a horizon, a
    /// sky driven by the hour, signs on the roads and a line of poles going into the fog, the honest
    /// answer to that claim was no.</para>
    ///
    /// <para><b>It takes the rig over rather than adding a camera.</b> A second camera would need its
    /// own <c>UniversalAdditionalCameraData</c>, its own antialiasing mode, its own renderer index and
    /// its own far plane — four settings that would agree with the game's until somebody changed one,
    /// and the symptom would be a photograph of a world the player never sees. <c>ChaseCamera</c> is
    /// switched off and its transform is driven directly, so the picture goes through the same post
    /// stack, the same FXAA and the same backdrop as the frame it was taken from.</para>
    ///
    /// <para><b>Everything runs on unscaled time.</b> The whole of this happens at
    /// <c>timeScale</c> zero — that is what a pause is — so a drag integrated against
    /// <c>Time.deltaTime</c> would move the camera by exactly nothing. Same rule the input router and
    /// the menu widgets already follow.</para>
    ///
    /// <para><b>The shutter drops the canvas for one frame and puts it back.</b> Unity's own
    /// <c>ScreenCapture</c> photographs the composited frame, and this game's canvas is
    /// <c>ScreenSpaceOverlay</c> — which URP composites after the post stack — so the sliders would be
    /// in the picture. Toggling <c>Canvas.enabled</c> rather than the GameObject leaves every layout
    /// and every selection untouched, which is what makes it safe to do sixty times a session.</para>
    /// </summary>
    public sealed class PhotoMode : MonoBehaviour
    {
        [Tooltip("The canvas the controls live on, dropped for the one frame the shutter fires in.")]
        [SerializeField] private Canvas canvas;

        [SerializeField] private Slider distanceSlider;
        [SerializeField] private Slider heightSlider;
        [SerializeField] private Slider fieldOfViewSlider;
        [SerializeField] private Slider hourSlider;
        [SerializeField] private Text hourLabel;
        [SerializeField] private Text shotLabel;

        /// <summary>Degrees of orbit per unit of drag, at a 1080-unit reference height.</summary>
        [SerializeField] private float dragDegrees = 0.22f;

        [Tooltip("Degrees a second the showcase orbit turns at. Slow — the start screen is something "
               + "you look at while deciding, not a title sequence.")]
        [SerializeField] private float showcaseTurn = 5f;

        [Tooltip("Where the showcase stands: metres back, metres up, and the lens. Fixed rather than "
               + "read off the photo page's sliders, which belong to a page nobody has opened.")]
        [SerializeField] private Vector3 showcaseStand = new Vector3(10.5f, 1.35f, 44f);

        [Tooltip("How far the camera is pushed sideways, as a share of its distance, so the car does "
               + "not sit behind the menu panel. Positive puts the car left of centre.")]
        [SerializeField] private float showcaseBias = 0.42f;

        /// <summary>How far the pitch may be pushed either way. Short of the poles, where a look-at spins.</summary>
        [SerializeField] private float pitchLimit = 78f;

        /// <summary>
        /// The rig and the clock, found rather than wired.
        ///
        /// <para>Both live in the world scene and this component is on the Bootstrap canvas, so there is
        /// nothing for the setup tool to assign — the same bind <c>PauseMenu</c> is in, and it is
        /// resolved the same way. Looked up on the way in rather than in <c>Awake</c>, because the world
        /// is loaded additively and neither exists on the first frame.</para>
        /// </summary>
        private ChaseCamera rig;

        private TimeOfDayController clock;
        private Camera view;
        private Transform car;
        private float yaw;
        private float pitch = 12f;
        private float distance = 8f;
        private float height = 1.4f;
        private float fieldOfView = 55f;
        private float restoreFieldOfView = 60f;
        private bool active;
        private bool controls = true;
        private bool shooting;

        /// <summary>
        /// Where a picture goes.
        ///
        /// <para><b>Into <c>Application.persistentDataPath</c> and not into the gallery, and that is a
        /// limit rather than a choice.</b> Putting an image where Android's photo app can see it means
        /// <c>MediaStore</c> through <c>AndroidJavaObject</c>, a content resolver and a permission, and
        /// every failed attempt at that costs a twenty-minute IL2CPP build to observe — the cost
        /// structure this project already records against <c>REQUEST_INSTALL_PACKAGES</c>. The file name
        /// is printed on the page so the picture is findable rather than silently nowhere, and the
        /// gallery is its own change.</para>
        ///
        /// <para>There is deliberately no property holding the last path. It would be a value wired,
        /// asserted and never read — which is exactly what <c>WeatherDirector</c>'s atmosphere reference
        /// turned out to be, and it looks like a dependency while being a decoration.</para>
        /// </summary>

        /// <summary>Called by the page as it opens and closes. Wired to the panel's own events.</summary>
        /// <param name="value">Whether the camera is being flown by hand.</param>
        /// <param name="useControls">
        /// Whether the photo page's own sliders are driving it.
        ///
        /// <para><b>False is the start screen, and it is the whole reason this flag exists.</b> That
        /// screen wants exactly what this class already does — take the rig over, orbit the parked car,
        /// give it back — and nothing else here. Left true it would read four sliders that belong to a
        /// page nobody has opened, and one of them writes the clock: the hour the player chose would be
        /// dragged to the photo page's default the moment the game started. Two classes that both fly
        /// the camera would be the second opinion this project keeps refusing.</para>
        /// </param>
        public void SetActive(bool value, bool useControls = true)
        {
            if (value == active && useControls == controls)
            {
                return;
            }

            controls = useControls;
            active = value;

            if (active)
            {
                Enter();
            }
            else
            {
                Leave();
            }
        }

        /// <summary>Orbits. Called by the drag surface behind the controls.</summary>
        public void Drag(Vector2 delta)
        {
            if (!active)
            {
                return;
            }

            // Scaled by the canvas's own reference height rather than by pixels, so a drag across a
            // phone and a drag across a tablet turn the camera by the same amount.
            yaw += delta.x * dragDegrees;
            pitch = Mathf.Clamp(pitch - delta.y * dragDegrees, -pitchLimit, pitchLimit);
        }

        /// <summary>Takes the picture. Wired to the shutter.</summary>
        public void Shoot()
        {
            if (active && !shooting)
            {
                StartCoroutine(Capture());
            }
        }

        /// <summary>
        /// Puts a camera where the showcase orbit would have it, at a given yaw.
        ///
        /// <para><b>Public for one caller, and the argument is the one this project keeps making.</b>
        /// <c>HudPreviewRenderer</c> has to photograph the start screen over the world, and nothing here
        /// ticks outside Play mode — so the tool either asks this class where the camera goes or carries
        /// its own copy of the distance, the height, the lens and the sideways bias. A copy agrees until
        /// the first retune and then photographs a framing the game does not use, which is exactly what
        /// <c>FuelGauge.LayOutFace</c> and <c>VehicleCover.RoofedAt</c> are public for.</para>
        /// </summary>
        public void ShowcaseAt(Camera camera, Transform target, float atYaw)
        {
            view = camera;
            car = target;
            controls = false;
            yaw = atYaw;

            ApplyShowcaseStand();
            Place();
        }

        /// <summary>The showcase's fixed framing. One place, read by the orbit and by the preview.</summary>
        private void ApplyShowcaseStand()
        {
            distance = showcaseStand.x;
            height = showcaseStand.y;
            fieldOfView = showcaseStand.z;
        }

        private void Enter()
        {
            if (view == null)
            {
                view = Camera.main;
            }

            if (view == null)
            {
                return;
            }

            if (rig == null)
            {
                rig = FindFirstObjectByType<ChaseCamera>();
            }

            if (clock == null)
            {
                clock = FindFirstObjectByType<TimeOfDayController>();
            }

            car = Resolve();

            if (rig != null)
            {
                rig.enabled = false;
            }

            // Started from where the chase camera already is, so opening the page does not throw the
            // world round. The yaw is the rig's own, which is behind the car by construction.
            yaw = view.transform.eulerAngles.y;
            restoreFieldOfView = view.fieldOfView;

            if (controls)
            {
                Sync();
            }

            Place();
        }

        private void Leave()
        {
            if (rig != null)
            {
                rig.enabled = true;
                rig.SnapToTarget();
            }

            if (view != null)
            {
                view.fieldOfView = restoreFieldOfView;
            }

            if (canvas != null)
            {
                canvas.enabled = true;
            }
        }

        /// <summary>Reads the four sliders into the state the camera is placed from.</summary>
        private void Sync()
        {
            if (distanceSlider != null)
            {
                distanceSlider.SetValueWithoutNotify(distance);
            }

            if (heightSlider != null)
            {
                heightSlider.SetValueWithoutNotify(height);
            }

            if (fieldOfViewSlider != null)
            {
                fieldOfViewSlider.SetValueWithoutNotify(fieldOfView);
            }

            if (hourSlider != null && clock != null)
            {
                hourSlider.SetValueWithoutNotify(clock.TimeOfDayHours);
            }

            WriteHour();
        }

        private void LateUpdate()
        {
            if (!active)
            {
                return;
            }

            if (!controls)
            {
                // Unscaled, because the whole of the start screen happens at timeScale zero.
                yaw += showcaseTurn * Time.unscaledDeltaTime;

                ApplyShowcaseStand();
                Place();
                return;
            }

            if (distanceSlider != null)
            {
                distance = distanceSlider.value;
            }

            if (heightSlider != null)
            {
                height = heightSlider.value;
            }

            if (fieldOfViewSlider != null)
            {
                fieldOfView = fieldOfViewSlider.value;
            }

            if (hourSlider != null && clock != null
                && !Mathf.Approximately(hourSlider.value, clock.TimeOfDayHours))
            {
                clock.TimeOfDayHours = hourSlider.value;
                clock.Apply();
                WriteHour();
            }

            Place();
        }

        /// <summary>Puts the camera on its orbit and points it at the car.</summary>
        private void Place()
        {
            if (view == null)
            {
                return;
            }

            if (car == null)
            {
                car = Resolve();

                if (car == null)
                {
                    return;
                }
            }

            Vector3 focus = car.position + Vector3.up * height;
            Quaternion turn = Quaternion.Euler(pitch, yaw, 0f);

            // The camera is moved sideways while its aim is held, which is what puts the subject off
            // centre — swinging the aim instead would keep the car in the middle and merely point
            // somewhere else. Only the showcase does it: on the photo page the subject belongs where
            // the player put it.
            float sideways = controls ? 0f : showcaseBias * distance;

            view.transform.position = focus
                                      - (turn * Vector3.forward) * distance
                                      + (turn * Vector3.right) * sideways;

            view.transform.rotation = turn;
            view.fieldOfView = fieldOfView;
        }

        /// <summary>
        /// The player's own car.
        ///
        /// <para>By type, which is safe here for the reason the other seventeen of these are: a remote
        /// car in a room carries no <c>VehicleController</c> at all, so there is never more than one in
        /// a scene to choose between.</para>
        /// </summary>
        private Transform Resolve()
        {
            var vehicle = FindFirstObjectByType<VehicleController>();
            return vehicle != null ? vehicle.transform : null;
        }

        private void WriteHour()
        {
            if (hourLabel == null || clock == null)
            {
                return;
            }

            int hours = Mathf.FloorToInt(clock.TimeOfDayHours);
            int minutes = Mathf.FloorToInt((clock.TimeOfDayHours - hours) * 60f);

            hourLabel.text = $"{hours:00}:{minutes:00}";
        }

        /// <summary>
        /// Drops the canvas, waits for the frame to finish, photographs it and puts the canvas back.
        ///
        /// <para><c>WaitForEndOfFrame</c> rather than a frame count, because that is the only point at
        /// which the back buffer holds a finished picture — and it still fires at <c>timeScale</c> zero,
        /// which is where the whole of this runs.</para>
        /// </summary>
        private IEnumerator Capture()
        {
            shooting = true;

            if (canvas != null)
            {
                canvas.enabled = false;
            }

            yield return new WaitForEndOfFrame();

            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();

            if (canvas != null)
            {
                canvas.enabled = true;
            }

            if (shot != null)
            {
                byte[] png = shot.EncodeToPNG();
                Destroy(shot);

                string file = $"Horizon_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
                string path = Path.Combine(Application.persistentDataPath, file);

                File.WriteAllBytes(path, png);

                if (shotLabel != null)
                {
                    shotLabel.text = "Saved " + file;
                }
            }

            shooting = false;
        }
    }

    /// <summary>
    /// The full-screen surface behind the photo controls that turns a drag into an orbit.
    ///
    /// <para>A component of its own rather than an interface on <see cref="PhotoMode"/>, because uGUI
    /// delivers a drag to the graphic that was hit and the thing that was hit has to be a
    /// <c>Graphic</c> covering the screen — which the controls panel is not, and must not become. It
    /// sits as the first child of the page so every button drawn after it wins the raycast: uGUI walks
    /// front to back, and the buttons are in front.</para>
    /// </summary>
    public sealed class PhotoDragArea : MonoBehaviour, IDragHandler
    {
        [SerializeField] private PhotoMode photo;

        /// <summary>Cached rather than walked for. A drag is every frame a finger is down.</summary>
        private CanvasScaler scaler;

        /// <summary>Hands the surface its target. Called by the setup tool.</summary>
        public void SetPhotoMode(PhotoMode value)
        {
            photo = value;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (photo == null)
            {
                return;
            }

            if (scaler == null)
            {
                scaler = GetComponentInParent<CanvasScaler>();
            }

            // Scaled out of screen pixels into canvas units, so the same swipe turns the camera by the
            // same amount on a phone and on a tablet. The scaler's reference height is 1080.
            float scale = scaler != null && Screen.height > 0
                ? scaler.referenceResolution.y / Screen.height
                : 1f;

            photo.Drag(eventData.delta * scale);
        }
    }
}
