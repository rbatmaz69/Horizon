using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// The car's fuel: how much is left, and how fast the engine is drinking it.
    ///
    /// <para><b>In Horizon.Vehicle because everything it needs is here.</b> The burn is a function of
    /// <see cref="VehicleController.EngineTorqueNm"/>, <see cref="VehicleController.EngineRpm"/> and the
    /// config — no world, no HUD, no input. Putting it in <c>Horizon.Game</c> would have meant a
    /// cross-assembly poll every physics step and no clean way to cut the throttle inside the
    /// controller, which is the one thing an empty tank has to be able to do.</para>
    ///
    /// <para><b>Burn comes from work done, not from a speed table.</b> That is what makes speed, revs and
    /// driving style all fall out of one expression instead of three: power is torque times rpm, and the
    /// player asks for torque with the pedal and for rpm with the gear they are holding. A table indexed
    /// on km/h would say the same thing about a car cruising in sixth and one screaming in second at the
    /// same speed, and those are the two cases the whole feature exists to tell apart.</para>
    /// </summary>
    public sealed class FuelTank : MonoBehaviour
    {
        [Tooltip("The engine this tank feeds. Found on the same object if left empty.")]
        [SerializeField] private VehicleController vehicle;

        /// <summary>
        /// Below this fraction the gauge warns. A tenth of a tank, which on the fleet's smallest is
        /// about four and a half litres — far enough from empty to do something about it.
        /// </summary>
        public const float ReserveFraction = 0.12f;

        /// <summary>
        /// How much faster than life this car drinks, and the number that turns a simulation into a
        /// mechanic.
        ///
        /// <para><b>Why it cannot be 1.</b> The burn below is physically honest, and honestly a
        /// 55-litre tank at the fastback's ~7.4 l/100 km is 750 km of range. The whole world is about
        /// twenty-five kilometres of road. At 1 the tank would never once need filling and the stations
        /// would be scenery.</para>
        ///
        /// <para><b>Why not 60, which is what the sun runs at.</b> The world's clock was the obvious
        /// place to look — <c>TimeOfDayController.DayLengthMinutes</c> is 24, so a day passes in 24 real
        /// minutes. It does not work for fuel, and the reason is worth keeping: burn rises with the
        /// <i>cube</i> of speed through the drag term, so a compression that is merely brisk at a cruise
        /// is savage at the top end. At 60 a tank lasted ninety seconds flat out, which does not read as
        /// a thirsty car, it reads as a broken one.</para>
        ///
        /// <para><b>And why 20 was still wrong, which is the part to actually learn from.</b> 20 was
        /// chosen against a steady 100 km/h in sixth, where it gave a comfortable twenty-two minutes.
        /// But a car holding 100 km/h on a level road is asking for about 47 Nm out of 570 — eight per
        /// cent load — and nobody plays this game that way. Half throttle at 3000 rpm, which is simply
        /// what driving looks like, is 280 Nm and burns five times as much: at 20 that emptied a tank in
        /// <b>four minutes</b>. The calibration was not slightly out, it was measured against the wrong
        /// row of the table.</para>
        ///
        /// <para>At 5, on the fastback: about 17 minutes at half throttle, 8 flat out, 15 at a sustained
        /// 200 km/h down the motorway, an hour and a half if it is genuinely cruised, and over ten hours
        /// left idling. The spread between gentle and hard is left exactly as the physics has it,
        /// because that spread is the whole feature — only the scale moved.</para>
        ///
        /// <para>Note what is <i>not</i> compressed: distance. The car still covers real metres at real
        /// speed, so consumption expressed per 100 km would come out five times too large and read as
        /// nonsense. That is why the dial shows a level and nothing on screen ever prints a
        /// l/100 km figure.</para>
        /// </summary>
        [SerializeField] private float burnScale = 5f;

        /// <summary>
        /// Litres per hour at idle, per Nm of peak torque, plus a floor.
        ///
        /// <para>Derived from the engine's size rather than written per car for the same reason the burn
        /// is: a bigger engine idles thirstier, and <see cref="VehicleConfig.MaxTorqueNm"/> already says
        /// how big it is. The fastback's 570 Nm lands on about a litre an hour, which is what an idling
        /// petrol engine of that size actually uses.</para>
        /// </summary>
        private const float IdleLitresPerHourFloor = 0.4f;
        private const float IdleLitresPerHourPerNm = 1f / 900f;

        /// <summary>
        /// Litres burnt per kWh of work, at full load and at a trickle.
        ///
        /// <para>Brake-specific fuel consumption, and the reason cruising is cheap. A petrol engine is at
        /// its best near full load — around 0.30 l/kWh — and markedly worse when it is barely being
        /// asked for anything, because the losses that do not scale with output stay where they are.
        /// Interpolating between the two is what makes a gentle throttle pay for itself twice: less work
        /// done, and each unit of it cheaper.</para>
        /// </summary>
        private const float LitresPerKilowattHourAtLowLoad = 0.55f;
        private const float LitresPerKilowattHourAtFullLoad = 0.30f;

        private VehicleConfig face;
        private float litres;

        /// <summary>How much is in the tank, litres.</summary>
        public float Litres => litres;

        /// <summary>What the tank holds when full, litres.</summary>
        public float Capacity => vehicle != null && vehicle.Config != null
            ? Mathf.Max(1f, vehicle.Config.FuelCapacityLitres)
            : 55f;

        /// <summary>How full the tank is, 0 to 1. What the gauge reads.</summary>
        public float Fraction01 => Mathf.Clamp01(litres / Capacity);

        /// <summary>Nothing left. The engine stops.</summary>
        public bool IsDry => litres <= 0f;

        /// <summary>Low enough that the gauge should say so.</summary>
        public bool IsReserve => Fraction01 < ReserveFraction;

        /// <summary>
        /// What the engine is drinking right now, litres per hour of <i>world</i> time.
        ///
        /// <para>For the debug overlay. World time rather than real, because that is the figure that can
        /// be compared against what a real engine of this size would use — see
        /// <see cref="burnScale"/>.</para>
        /// </summary>
        public float LitresPerHour { get; private set; }

        /// <summary>Adds fuel, up to the brim. Negative amounts are ignored rather than draining it.</summary>
        public void Fill(float amount)
        {
            if (amount <= 0f)
            {
                return;
            }

            litres = Mathf.Min(Capacity, litres + amount);
        }

        /// <summary>Brims the tank.</summary>
        public void FillFully()
        {
            litres = Capacity;
        }

        /// <summary>
        /// Sets the level as a fraction of whatever this car holds.
        ///
        /// <para>A fraction rather than litres, because that is the form the level survives in: the
        /// saved value has to mean something after the player has changed to a car with a different
        /// tank, and "two thirds full" does while "38 litres" does not.</para>
        /// </summary>
        public void SetFraction(float fraction01)
        {
            litres = Mathf.Clamp01(fraction01) * Capacity;
        }

        private void Awake()
        {
            if (vehicle == null)
            {
                vehicle = GetComponent<VehicleController>();
            }

            face = vehicle != null ? vehicle.Config : null;
            FillFully();
        }

        private void FixedUpdate()
        {
            if (vehicle == null)
            {
                return;
            }

            VehicleConfig config = vehicle.Config;
            if (config == null)
            {
                return;
            }

            // The garage changes car while the game runs, and SetConfig raises no event — the same
            // reference-inequality check InstrumentCluster makes, for the same reason.
            //
            // The level carries over as a fraction, not as litres. Swapping a 45-litre hatchback for an
            // 80-litre offroader with the litres kept would leave the new car's needle somewhere the
            // player never put it; keeping the fraction means choosing a car stays a choice about cars.
            if (!ReferenceEquals(config, face))
            {
                float carried = face != null ? Fraction01 : 1f;
                face = config;
                SetFraction(carried);
            }

            if (litres <= 0f)
            {
                litres = 0f;
                LitresPerHour = 0f;
                return;
            }

            LitresPerHour = BurnFor(config);

            // Hours of world time elapsed this step: the step itself, scaled, over the 3600 seconds in
            // an hour. Divided rather than multiplied by a cached reciprocal because this is one
            // division per physics step and clarity is worth more than that here.
            float worldHours = Time.fixedDeltaTime * burnScale / 3600f;

            litres = Mathf.Max(0f, litres - LitresPerHour * worldHours);
        }

        /// <summary>
        /// Litres per hour at the engine's current output.
        ///
        /// <para>Idle draw plus the fuel the work itself costs. <c>EngineTorqueNm</c> is already the
        /// torque curve, the peak and the pedal folded together and is zero on every stroke the engine
        /// is not pulling — mid-shift, on the limiter, off the throttle — so coasting downhill costs the
        /// idle draw and no more, which is the right answer and one this gets for free.</para>
        /// </summary>
        private float BurnFor(VehicleConfig config)
        {
            float idle = IdleLitresPerHourFloor + config.MaxTorqueNm * IdleLitresPerHourPerNm;

            float torque = vehicle.EngineTorqueNm;
            if (torque <= 0f)
            {
                return idle;
            }

            // Power at the crank. Torque in newton-metres times angular velocity in radians per second
            // is watts; the rest is unit-keeping.
            float radiansPerSecond = vehicle.EngineRpm * (2f * Mathf.PI / 60f);
            float kilowatts = torque * radiansPerSecond / 1000f;

            float load01 = Mathf.Clamp01(torque / Mathf.Max(1f, config.MaxTorqueNm));
            float litresPerKilowattHour = Mathf.Lerp(
                LitresPerKilowattHourAtLowLoad, LitresPerKilowattHourAtFullLoad, load01);

            return idle + kilowatts * litresPerKilowattHour;
        }
    }
}
