using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// How each of the five bodies drives, as deltas from the fastback.
    ///
    /// <para><b>Why these cannot be field initialisers on <see cref="VehicleConfig"/>.</b> Those are the
    /// fastback's numbers, and <see cref="VehicleConfigReset"/> restores a stale asset by copying a
    /// freshly constructed instance over it. If the van's mass lived anywhere near the class it would be
    /// the mass of every car, and the first reset would turn the whole garage back into fastbacks.
    /// A table applied <i>after</i> construction is the only shape that survives that mechanism.</para>
    ///
    /// <para><b>What is deliberately identical across all five.</b> <c>WheelRadius</c> stays 0.44 and
    /// <c>SuspensionRestLength</c> stays 0.30 on every car, and neither is negotiable: the wheel arches
    /// are carved at <c>ArchTopY = 0.16</c>, which is arithmetic off exactly those two numbers, and
    /// <c>FinalDrive</c> is coupled to the radius such that changing one silently retunes acceleration,
    /// top speed and every shift point. All five bodies are drawn around one set of running gear — see
    /// <c>CarMeshBuilder.CarProfile</c> — so this is a constraint of the art as much as of the physics.
    /// The gearing differences below are all in <c>FinalDrive</c> and the ratios, never the radius.</para>
    ///
    /// <para><b>What actually makes them feel different.</b> Three fields do nearly all of it.
    /// <c>DrivenAxle</c> is, by its own tooltip, the single largest number in the config for how a car
    /// feels — front drive spends the throttle's share of the friction circle at the tyres that are also
    /// steering, so the van and the hatchback push wide instead of stepping out.
    /// <c>CenterOfMass</c> height is what makes the van a van: at −0.10 against everyone else's −0.30 it
    /// leans and transfers weight, and it needs the 22 kN anti-roll bar not to fall over doing it. And
    /// <c>LateralGrip</c> separates the loose-tailed pickup from the hatchback, which has plenty of grip
    /// and almost no power to spend against it.</para>
    /// </summary>
    internal static class VehicleConfigPresets
    {
        internal const string SettingsFolder = "Assets/_Project/Settings";

        /// <summary>
        /// Which asset holds which body's handling.
        ///
        /// <para>The fastback keeps <c>VehicleConfig_Prototype.asset</c> under its original name. Renaming
        /// it to match the others would orphan the GUID the prefab and the scene point at, and throw away
        /// whatever Play-mode tuning has accumulated in it — a real cost for a tidier file listing.</para>
        ///
        /// <para>Order matters: it is the order the bodies appear in <c>CarMeshBuilder.PlayerProfiles</c>,
        /// and therefore the order of the garage menu and of the saved car index.</para>
        /// </summary>
        internal static readonly (string Profile, string AssetPath)[] All =
        {
            ("Fastback", SettingsFolder + "/VehicleConfig_Prototype.asset"),
            ("Estate", SettingsFolder + "/VehicleConfig_Estate.asset"),
            ("Van", SettingsFolder + "/VehicleConfig_Van.asset"),
            ("Pickup", SettingsFolder + "/VehicleConfig_Pickup.asset"),
            ("Hatchback", SettingsFolder + "/VehicleConfig_Hatchback.asset"),
        };

        /// <summary>The asset path for a profile name, or null if it is not one of the five.</summary>
        internal static string PathFor(string profile)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Profile == profile)
                {
                    return All[i].AssetPath;
                }
            }

            return null;
        }

        /// <summary>
        /// Applies one body's character to a config that already holds the code defaults.
        ///
        /// <para>The fastback is the identity case and writes nothing — the defaults <i>are</i> the
        /// fastback, and a preset that restated them would be a second copy to keep in step.</para>
        /// </summary>
        internal static void Apply(VehicleConfig config, string profile)
        {
            if (config == null)
            {
                return;
            }

            switch (profile)
            {
                case "Fastback":
                    return;

                case "Estate":
                    // The same car carrying a family and a roof. Heavier, softer, geared a little shorter
                    // to make up for it, and enough grip left that it still corners like the coupé it is
                    // underneath.
                    config.Mass = 1420f;
                    config.CenterOfMass = new Vector3(0f, -0.26f, 0.02f);
                    config.RollDamping = 2.7f;
                    config.PitchDamping = 1.1f;
                    config.AntiRollStiffness = 15000f;
                    config.MaxTorqueNm = 470f;
                    config.RedlineRpm = 5400f;
                    config.UpshiftRpm = 5000f;
                    config.FinalDrive = 4.30f;
                    config.LateralGrip = Grip(1.62f, 1.45f, 1.28f);
                    config.MaxSteerAngle = 38f;
                    config.Downforce = 2.2f;
                    config.BrakeForce = 17000f;
                    return;

                case "Van":
                    // Tall, heavy, front-driven and slow. The centre of mass is the whole character: at
                    // -0.10 it is 20 cm higher than every other car here, which is what makes it lean into
                    // a corner and lift on the way out. The anti-roll bar is not a tuning choice, it is
                    // what stops that ending on its roof.
                    config.Mass = 1950f;
                    config.CenterOfMass = new Vector3(0f, -0.10f, 0.10f);
                    config.RollDamping = 3.4f;
                    config.PitchDamping = 1.5f;
                    config.AntiRollStiffness = 22000f;
                    config.DrivenAxle = DrivenAxle.Front;
                    config.MaxTorqueNm = 420f;
                    config.IdleRpm = 700f;
                    config.RedlineRpm = 4200f;
                    config.UpshiftRpm = 3900f;
                    config.DownshiftRpm = 1500f;

                    // A diesel van's torque arrives early and is gone by the top of the range.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.70f),
                        new Keyframe(0.28f, 1f),
                        new Keyframe(0.60f, 0.88f),
                        new Keyframe(1f, 0.55f));

                    config.GearRatios = new[] { 3.90f, 2.31f, 1.52f, 1.12f, 0.86f };
                    config.FinalDrive = 4.90f;
                    config.LateralGrip = Grip(1.45f, 1.30f, 1.15f);
                    config.MaxSteerAngle = 34f;
                    config.SteerRate = 240f;
                    config.BrakeForce = 19000f;
                    config.AeroDrag = 0.78f;
                    config.Downforce = 1.5f;

                    // Below the 3 the others run, but only just: the assist is what makes a car feel
                    // direct under a thumb, and taking it away entirely reads as broken steering rather
                    // than as a heavy vehicle.
                    config.TurnInAssist = 2.4f;
                    config.DriftYawDamping = 3.2f;
                    config.CountersteerAuthority = 0.5f;
                    return;

                case "Pickup":
                    // Rear drive, a leaf-sprung back axle with nothing over it, and the least grip of the
                    // five. Willing to step out and slow to come back — the handbrake figures are low so
                    // it swings rather than stops.
                    config.Mass = 1750f;
                    config.CenterOfMass = new Vector3(0f, -0.18f, -0.05f);
                    config.RollDamping = 3.0f;
                    config.PitchDamping = 1.3f;
                    config.AntiRollStiffness = 17000f;
                    config.MaxTorqueNm = 520f;
                    config.RedlineRpm = 4600f;
                    config.UpshiftRpm = 4300f;
                    config.DownshiftRpm = 1700f;

                    // Flat and low, the way a large lazy engine in a working vehicle is.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.72f),
                        new Keyframe(0.30f, 0.98f),
                        new Keyframe(0.65f, 1f),
                        new Keyframe(1f, 0.70f));

                    config.FinalDrive = 4.60f;
                    config.LateralGrip = Grip(1.40f, 1.26f, 1.12f);
                    config.HandbrakeGrip = 0.35f;
                    config.MaxSteerAngle = 36f;
                    config.SteerRate = 260f;
                    config.BrakeForce = 17500f;
                    config.AeroDrag = 0.62f;
                    config.Downforce = 1.8f;
                    config.DriftYawDamping = 2.2f;
                    return;

                case "Hatchback":
                    // The lightest and the least powerful. 260 Nm in 980 kg is momentum driving: you keep
                    // what speed you have, because getting it back takes a while. Plenty of grip and a lot
                    // of lock to spend it with.
                    config.Mass = 980f;
                    config.CenterOfMass = new Vector3(0f, -0.32f, 0.04f);
                    config.RollDamping = 2.2f;
                    config.PitchDamping = 0.9f;
                    config.AntiRollStiffness = 12000f;
                    config.DrivenAxle = DrivenAxle.Front;
                    config.MaxTorqueNm = 260f;
                    config.IdleRpm = 850f;
                    config.RedlineRpm = 6600f;
                    config.UpshiftRpm = 6300f;
                    config.DownshiftRpm = 2600f;

                    // A small engine that does its work at the top, which is what makes it worth revving.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.42f),
                        new Keyframe(0.35f, 0.72f),
                        new Keyframe(0.65f, 1f),
                        new Keyframe(0.88f, 0.94f),
                        new Keyframe(1f, 0.80f));

                    // Close ratios, because there is no torque to cover a gap with.
                    config.GearRatios = new[] { 3.31f, 2.05f, 1.48f, 1.14f, 0.94f };

                    // 6.40, and it looks absurd until you remember every car here is on the fastback's
                    // 0.44 m wheels. A real hatchback rolls on about 0.30 m, and FinalDrive is coupled
                    // to the radius exactly — scale both and nothing changes — so a car wearing wheels
                    // 1.47× too big needs a final drive 1.47× taller to gear the same. 4.35 × 1.47 is
                    // this number.
                    //
                    // It is not cosmetic. At 4.20 the redline in top worked out at 283 km/h, higher than
                    // the muscle car's, which the hatchback could never have reached on 260 Nm — but
                    // VehicleConfig.TopSpeed is what SpeedNormalized divides by, and SteeringBySpeed and
                    // LateralGrip are both looked up on it. The car would have spent its whole life
                    // below 0.3 of its own scale, permanently at the grippiest, most steerable end of
                    // both curves, feeling nothing like the thing it is meant to be.
                    config.FinalDrive = 6.40f;
                    config.LateralGrip = Grip(1.66f, 1.54f, 1.40f);
                    config.MaxSteerAngle = 42f;
                    config.SteerRate = 320f;
                    config.BrakeForce = 13500f;
                    config.RollingResistanceN = 38f;
                    config.AeroDrag = 0.36f;
                    config.Downforce = 2.0f;
                    return;
            }
        }

        /// <summary>
        /// A grip curve in the shape the config documents: a coefficient falling with speed, standing in
        /// for aerodynamics and tyre heat at once. Three keys, like the default.
        /// </summary>
        private static AnimationCurve Grip(float atRest, float atHalf, float atTop)
        {
            return new AnimationCurve(
                new Keyframe(0f, atRest),
                new Keyframe(0.5f, atHalf),
                new Keyframe(1f, atTop));
        }
    }
}
