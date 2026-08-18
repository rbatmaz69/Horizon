using Horizon.Atmosphere;
using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Makes the world react to how fast the player is going: the fog closes in, and the air in front
    /// of the car fills with grit that the car then flies past.
    ///
    /// <para><b>Why the world and not just the camera.</b> Everything done so far — field of view, rig
    /// height, the tremor — moves the frame, and a frame can only say so much before the player reads
    /// it as a camera rather than as speed. The world saying it is different in kind: at 235 km/h the
    /// sight line is genuinely five seconds long, and a hairpin arriving out of the haze with five
    /// seconds' notice is frightening because it *is* frightening, not because it was dressed up.</para>
    ///
    /// <para><b>Why both effects live in one component.</b> They answer the same question from the same
    /// two inputs, and splitting them would mean two lookups of the vehicle, two speed curves and two
    /// places to disagree about what counts as fast.</para>
    ///
    /// <para>Lives in Horizon.Game because it is the only runtime assembly that can see the vehicle and
    /// the atmosphere at once. Horizon.Atmosphere must not learn about cars, so the speed is pushed into
    /// <see cref="TimeOfDayController.SpeedHaze"/> rather than pulled — the same shape as
    /// <c>DriveInput.Current</c>.</para>
    /// </summary>
    public sealed class SpeedAtmosphere : MonoBehaviour
    {
        [SerializeField] private TimeOfDayController atmosphere;

        [Tooltip("The air-rush emitter. Left empty there is simply no grit, and the fog still closes in.")]
        [SerializeField] private ParticleSystem rush;

        [Tooltip("Found at runtime if left empty — the player's car is spawned by the bootstrap, not "
               + "baked into the scene alongside this.")]
        [SerializeField] private VehicleController vehicle;

        [Header("Fog")]
        [Tooltip("Scales the whole speed-fog effect. Zero leaves the atmosphere alone entirely.")]
        [Range(0f, 1f)]
        [SerializeField] private float hazeAmount = 1f;

        [Header("Air rush")]
        [Tooltip("Fraction of top speed below which no grit is emitted at all.\n\n"
               + "There has to be one. A pass at 60 km/h is meant to be calm, and specks streaming past "
               + "a car pottering along would read as dirt on the lens.")]
        [Range(0f, 1f)]
        [SerializeField] private float rushOnsetSpeed = 0.35f;

        [Tooltip("Particles per second at top speed.")]
        [SerializeField] private float maxRushRate = 90f;

        [Tooltip("Nearest and furthest the grit is placed ahead of the car, metres. It is put in front "
               + "and left standing still, so the car passing it is what produces the motion — which is "
               + "the honest way round, and why this reads as air rather than as speed lines.")]
        [SerializeField] private float spawnNear = 15f;

        [SerializeField] private float spawnFar = 75f;

        [Tooltip("Half-width of the band the grit is scattered across, metres.")]
        [SerializeField] private float spawnHalfWidth = 14f;

        [SerializeField] private float spawnLow = 0.4f;
        [SerializeField] private float spawnHigh = 7f;

        [Tooltip("Ceiling on grit emitted in one frame, so a long frame cannot spend the whole budget "
               + "in a single burst.")]
        [SerializeField] private int maxPerFrame = 8;

        /// <summary>
        /// Whether the grit is wanted at all. Set by the quality director.
        ///
        /// <para>Separate from stopping the particle system, because emitting by hand ignores whether
        /// the system is playing — without this the component would keep pushing particles into a
        /// system that had been told to stop, and they would appear anyway.</para>
        /// </summary>
        public bool RushEnabled { get; set; } = true;

        private ParticleSystem.MainModule rushMain;
        private bool hasRushMain;
        private float pending;
        private Rigidbody body;

        private void Awake()
        {
            if (atmosphere == null)
            {
                atmosphere = FindFirstObjectByType<TimeOfDayController>();
            }

            if (rush != null)
            {
                rushMain = rush.main;
                hasRushMain = true;
            }
        }

        private void OnDisable()
        {
            pending = 0f;

            // Hand the atmosphere back as it was found. Left set, a disabled component would pin the
            // fog at whatever speed the car happened to be doing when it went away.
            if (atmosphere != null)
            {
                atmosphere.SpeedHaze = 0f;
            }
        }

        private void Update()
        {
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
                if (vehicle == null)
                {
                    return;
                }

                body = vehicle.GetComponent<Rigidbody>();
            }

            // Shaped rather than linear, and matched to the camera's own curve on purpose: the two are
            // describing the same thing, and a world that thickened on a different schedule from the
            // frame would read as two effects instead of one.
            float speed01 = Mathf.Clamp01(vehicle.SpeedNormalized);
            float shaped = speed01 * Mathf.Sqrt(speed01);

            if (atmosphere != null)
            {
                atmosphere.SpeedHaze = shaped * hazeAmount;
            }

            UpdateRush(speed01, shaped);
        }

        private void UpdateRush(float speed01, float shaped)
        {
            if (rush == null || !RushEnabled || speed01 <= rushOnsetSpeed)
            {
                pending = 0f;
                return;
            }

            float amount = Mathf.Clamp01(
                (speed01 - rushOnsetSpeed) / Mathf.Max(0.01f, 1f - rushOnsetSpeed));

            pending += maxRushRate * amount * Time.deltaTime;

            int count = Mathf.Min((int)pending, maxPerFrame);
            if (count <= 0)
            {
                return;
            }

            pending -= count;

            // Tinted to the fog it is hanging in, every frame, so the grit belongs to the weather and
            // the hour rather than being a grey that only matches at noon.
            if (hasRushMain)
            {
                Color fog = RenderSettings.fogColor;
                fog.a = Mathf.Lerp(0.25f, 0.7f, shaped);
                rushMain.startColor = fog;
            }

            Transform car = vehicle.transform;

            // Scattered along the direction of travel rather than where the nose points, so a car
            // sideways in a drift still meets its own grit head on.
            Vector3 velocity = body != null ? body.linearVelocity : car.forward;
            Vector3 heading = new Vector3(velocity.x, 0f, velocity.z);
            heading = heading.sqrMagnitude > 0.01f ? heading.normalized : car.forward;

            Vector3 side = Vector3.Cross(Vector3.up, heading);
            Vector3 origin = car.position;

            for (int i = 0; i < count; i++)
            {
                Vector3 position = origin
                    + heading * Random.Range(spawnNear, spawnFar)
                    + side * Random.Range(-spawnHalfWidth, spawnHalfWidth)
                    + Vector3.up * Random.Range(spawnLow, spawnHigh);

                var parameters = new ParticleSystem.EmitParams
                {
                    position = position,
                    applyShapeToPosition = false,
                };

                rush.Emit(parameters, 1);
            }
        }
    }
}
