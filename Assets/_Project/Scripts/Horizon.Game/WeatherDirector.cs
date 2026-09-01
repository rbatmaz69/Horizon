using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The one place that knows it is raining, and the four things it tells.
    ///
    /// <list type="bullet">
    /// <item>the water falling past the camera</item>
    /// <item>the road, through <see cref="WetSurfaces"/></item>
    /// <item>the tyres, through <c>VehicleController.WeatherGrip</c></item>
    /// <item>the noise, through <c>RainAudio.Level</c></item>
    /// </list>
    ///
    /// <para><b>The sky is the one thing it does not tell</b>, and that is deliberate rather than an
    /// omission — see the note at the end of <see cref="Push"/>. <c>StartScreen</c> and
    /// <c>PauseMenu</c> have written <c>TimeOfDayController.Overcast</c> since long before there was
    /// rain, and they have to: both call <c>Apply()</c> immediately so the player watches the light
    /// change behind the open menu. A second writer here would fight them. This class therefore holds
    /// no reference to the atmosphere at all — it had one for a while, wired and asserted and never
    /// read, which is a reference that looks like a dependency and is a decoration.</para>
    ///
    /// <para><b>One owner and four consumers, which is the rule this project keeps coming back to.</b>
    /// The boost gauge's own note puts it best: the needle and the whistle are the same number, so a
    /// dial claiming the turbo is on song cannot disagree with an engine that sounds like it is not.
    /// Four separate reads of <c>PlayerChoices.Weather</c>, each with its own ramp, would be four things
    /// able to disagree about whether it is raining — and the one that would show is a road that dries
    /// while the sound is still falling.</para>
    ///
    /// <para><b>It polls the static rather than listening for an event.</b> <c>PlayerChoices</c> raises
    /// nothing, and the preset is changed from two places — the start screen and the pause menu. An
    /// event would be a third thing to keep in step for a comparison of two integers a frame. It is the
    /// same argument <c>InstrumentCluster</c> makes for re-reading <c>VehicleController.Config</c>.</para>
    ///
    /// <para><b>The ramp is why this is a component at all rather than a line in <c>SetWeather</c>.</b>
    /// A whole box of water appearing between one frame and the next reads as a graphics setting being
    /// toggled. It is deliberately short — under a second — because the sky it is arriving under snaps,
    /// and a long fade would have the rain still building well after the light had finished changing.
    /// The road is the other exception: asphalt darkens at a threshold rather than fading, because a
    /// material swap has no in-between and pretending otherwise would need a third set of materials for
    /// every road in the world.</para>
    /// </summary>
    public sealed class WeatherDirector : MonoBehaviour
    {
        [Tooltip("The falling water. Emission rate is driven from here; the system itself is authored "
               + "by the setup tool and parented to the camera.")]
        [SerializeField] private ParticleSystem rain;

        [Tooltip("The road materials. Found at run time if left empty.")]
        [SerializeField] private WetSurfaces surfaces;

        [Tooltip("The car's cover probe, so it stops raining under a roof. Found at run time.")]
        [SerializeField] private VehicleCover cover;

        [Tooltip("Drops per second at the heaviest rain. Whatever the quality setting allows is applied "
               + "on top of this — see QualityDirector.")]
        [SerializeField] private float maxDropsPerSecond = 1600f;

        [Tooltip("Grip left in the wet, as a fraction of dry.\n\n"
               + "0.82, and it is deliberately gentler than it sounds. Real wet asphalt takes more than "
               + "this, but the car is driven with a thumb on a phone: past about a fifth the pass "
               + "stops being a road and becomes a punishment, and the driver has no seat to feel the "
               + "back stepping out from. What this is tuned for is that a corner taken at the dry "
               + "speed runs a little wide, which is a thing the player can learn.")]
        [Range(0.5f, 1f)]
        [SerializeField] private float wetGrip = 0.82f;

        /// <summary>
        /// How fast rain arrives and leaves, per second.
        ///
        /// <para>About two thirds of a second. Long enough that the drops arrive rather than appear,
        /// short enough that they have finished by the time the eye has adjusted to the darker sky the
        /// menu changed in one frame.</para>
        /// </summary>
        private const float RainResponse = 1.5f;

        /// <summary>Above this the road is wet. Below it, dry. A material swap has no half.</summary>
        private const float WetRoadThreshold = 0.35f;

        /// <summary>How often the car and the rain audio are looked for while missing, seconds.</summary>
        private const float SearchInterval = 0.5f;

        private VehicleController vehicle;
        private RainAudio rainAudio;
        private float nextSearch;

        private ParticleSystem.EmissionModule emission;
        private bool hasEmission;

        /// <summary>How hard it is raining right now, 0 to 1, after the ramp.</summary>
        public float Rain01 { get; private set; }

        /// <summary>
        /// Drops per second at the heaviest rain, before quality and cover are applied.
        ///
        /// <para>Public for the editor's weather preview, which has to fill the emitter itself because
        /// nothing ticks outside Play mode. It reads this rather than carrying a number of its own, for
        /// the reason <c>TrunkForkBuilder.MouthHalfWidth</c> records: a second copy agrees until the
        /// first retune and then quietly photographs the wrong thing.</para>
        /// </summary>
        public float MaxDropsPerSecond => maxDropsPerSecond;

        /// <summary>
        /// Scales the drop count. Written by <see cref="QualityDirector"/>; 0 turns the visible rain off
        /// and leaves the sky, the sound and the grip exactly as they were.
        ///
        /// <para>That split is the point. Rain is a thing the car is driving through, not a decoration:
        /// a phone that cannot afford the particles must still get the slippery road and the noise, or
        /// Low quality would quietly be an easier game.</para>
        /// </summary>
        public float DropScale { get; set; } = 1f;

        private void Awake()
        {
            if (surfaces == null)
            {
                surfaces = FindFirstObjectByType<WetSurfaces>();
            }

            if (rain != null)
            {
                emission = rain.emission;
                hasEmission = true;

                // Off until it rains. The system is left playing so the first drop does not arrive with
                // a Play() and a one-frame burst of everything at the emitter at once.
                emission.rateOverTime = 0f;
            }

            // Snapped rather than ramped on the first frame: a run that begins in the rain begins in the
            // rain, and watching it fade up from nothing over the opening seconds would read as the
            // weather having been switched on by the menu closing.
            Rain01 = TargetRain();
            Push();
        }

        private void Update()
        {
            FindTheCar();

            Rain01 = Mathf.MoveTowards(Rain01, TargetRain(), RainResponse * Time.deltaTime);
            Push();
        }

        private static float TargetRain() => PlayerChoices.RainFor(PlayerChoices.Weather);

        private void Push()
        {
            if (hasEmission)
            {
                // <b>Under a roof it stops raining, and this is a bug that had to be reasoned about
                // rather than seen.</b> The emitter box hangs fourteen metres over the camera, which
                // inside a bore is fourteen metres of solid rock — so drops were being born in the
                // massif and falling through it into the tunnel. Nothing in the build would have said a
                // word, and it is the kind of thing only a picture finds.
                //
                // Answered by the probe that already answers it for the sound and for the engine's
                // reverb, rather than by a second test of its own. It is eased, so the rain fades back
                // in across a portal instead of switching.
                float open = cover != null ? 1f - cover.CoverAmount : 1f;

                emission.rateOverTime =
                    maxDropsPerSecond * Rain01 * Mathf.Clamp01(DropScale) * open;
            }

            if (surfaces != null)
            {
                surfaces.SetWet(Rain01 > WetRoadThreshold);
            }

            if (vehicle != null)
            {
                vehicle.WeatherGrip = Mathf.Lerp(1f, wetGrip, Rain01);
            }

            if (rainAudio != null)
            {
                rainAudio.Level = Rain01;
            }

            // The sky is deliberately *not* pushed from here. StartScreen.ApplyConditions and
            // PauseMenu.SetWeather already write Overcast the moment the preset changes, and they have
            // to: both call TimeOfDayController.Apply() straight afterwards so the player sees the light
            // change behind the open menu. A second writer ramping the same field would fight them, and
            // what would show is the sky snapping to the new weather and then sliding back.
        }

        private void FindTheCar()
        {
            if (vehicle != null && rainAudio != null && cover != null)
            {
                return;
            }

            if (Time.unscaledTime < nextSearch)
            {
                return;
            }

            nextSearch = Time.unscaledTime + SearchInterval;

            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
            }

            if (rainAudio == null)
            {
                rainAudio = FindFirstObjectByType<RainAudio>();
            }

            if (cover == null)
            {
                cover = FindFirstObjectByType<VehicleCover>();
            }
        }
    }
}
