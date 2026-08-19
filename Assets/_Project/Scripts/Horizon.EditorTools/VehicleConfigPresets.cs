using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// How each of the ten bodies drives, as deltas from the fastback.
    ///
    /// <para><b>Why these cannot be field initialisers on <see cref="VehicleConfig"/>.</b> Those are the
    /// fastback's numbers, and <see cref="VehicleConfigReset"/> restores a stale asset by copying a
    /// freshly constructed instance over it. If the van's mass lived anywhere near the class it would be
    /// the mass of every car, and the first reset would turn the whole garage back into fastbacks.
    /// A table applied <i>after</i> construction is the only shape that survives that mechanism.</para>
    ///
    /// <para><b>The running gear is not written here.</b> <c>WheelRadius</c> and
    /// <c>SuspensionRestLength</c> are copied out of <c>CarMeshBuilder.ProfileByName</c> at the top of
    /// <see cref="Apply"/>, before the switch, and no case below may touch either. They belong to the
    /// profile because the mesh is lofted around them — the arch top is arithmetic off exactly those two
    /// numbers, and so is the ground plane every station table is quoted against. A car whose config
    /// disagreed with its own bodywork would sit with its wheels through its arches, and nothing would
    /// say so until somebody looked at it.</para>
    ///
    /// <para><b>Changing a suspension travel means changing AntiRollStiffness with it.</b> The bar
    /// works on <c>Compression01</c>, which is compression as a <i>fraction</i> of the travel — so the
    /// same lean through the same corner reads as a smaller number on a car with longer springs, and the
    /// bar quietly goes soft. <b>Multiply the bar by the same factor the travel moved by</b> and the roll
    /// stiffness is exactly where it was tuned. Every bar below carries the pre-scale number in its
    /// comment. Spring rate and ride frequency need no such correction: static sag is force over rate and
    /// does not know how long the spring is.</para>
    ///
    /// <para><b>Changing a radius means changing FinalDrive with it.</b> The radius enters the tractive
    /// force, the wheel-speed-to-rpm conversion and <c>TopSpeed</c> — and <c>TopSpeed</c> is the divisor
    /// for <c>SpeedNormalized</c>, which is in turn the lookup key for <c>SteeringBySpeed</c> and
    /// <c>LateralGrip</c>. So a bigger tyre does not just gear the car longer, it quietly moves every
    /// grip and steering number the car was tuned with. <b>Multiply FinalDrive by the same factor the
    /// radius moved by</b> and all of it stays exactly where it was: only the picture changes. Every
    /// FinalDrive below that is not 0.44 m's carries the pre-scale number in its comment, so the two can
    /// be checked against each other.</para>
    ///
    /// <para><b>What actually makes them feel different.</b> Three fields do nearly all of it.
    /// <c>DrivenAxle</c> is, by its own tooltip, the single largest number in the config for how a car
    /// feels — front drive spends the throttle's share of the friction circle at the tyres that are also
    /// steering, so the van and the hatchback push wide instead of stepping out.
    /// <c>CenterOfMass</c> height is what makes the van a van: at −0.10 against everyone else's −0.30 it
    /// leans and transfers weight, and it needs the 22 kN anti-roll bar not to fall over doing it. And
    /// <c>LateralGrip</c> separates the loose-tailed pickup from the hatchback, which has plenty of grip
    /// and almost no power to spend against it.</para>
    ///
    /// <para><b>And what they sound like, which is the same problem again.</b> Every case below ends
    /// with a <see cref="Voice"/> call and, on the four turbocharged cars, a <see cref="Turbo"/> one.
    /// Ten cars that drove differently and made one noise was a real state this file was in: three of
    /// them are straight sixes and were bit-for-bit the same engine. The notes on those two helpers say
    /// which numbers carry the difference. Keep the turbo's <c>spoolRevs</c> in step with the same
    /// case's <c>TorqueByRpm</c> — the ear and the seat should agree about when the boost arrives.</para>
    ///
    /// <para><b>The cylinder count and layout are not decoration.</b> They set where the firing pulses
    /// land, and the rumble falls out of that rather than being asked for — so an eight declared as an
    /// inline is not a slightly wrong label, it is a different engine. Match them to the car.</para>
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
            ("Coupe", SettingsFolder + "/VehicleConfig_Coupe.asset"),
            ("Liftback", SettingsFolder + "/VehicleConfig_Liftback.asset"),
            ("Saloon", SettingsFolder + "/VehicleConfig_Saloon.asset"),
            ("Notchback", SettingsFolder + "/VehicleConfig_Notchback.asset"),
            ("Offroader", SettingsFolder + "/VehicleConfig_Offroader.asset"),
        };

        /// <summary>The asset path for a profile name, or null if it is not one of the ten.</summary>
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

            // The running gear, from the shape rather than from here. Before the switch, so a case that
            // reached for either of these would be overwritten rather than quietly winning — and above
            // the fastback's early return, so the identity case gets them too.
            CarMeshBuilder.CarProfile shape = CarMeshBuilder.ProfileByName(profile);
            config.WheelRadius = shape.WheelRadius;
            config.SuspensionRestLength = shape.SuspensionRestLength;

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
                    config.AntiRollStiffness = 17000f;   // 15000 × 0.34 / 0.30
                    config.MaxTorqueNm = 470f;
                    config.RedlineRpm = 5400f;
                    config.UpshiftRpm = 5000f;
                    config.FinalDrive = 4.30f;
                    config.LateralGrip = Grip(1.62f, 1.45f, 1.28f);
                    config.MaxSteerAngle = 38f;
                    config.Downforce = 2.2f;
                    config.BrakeForce = 17000f;

                    // The fastback's engine in a heavier car with more bodywork around it: the same
                    // rumble, gone duller, and finished 400 rpm earlier.
                    Voice(config, 48f, 8, FiringLayout.CrossPlaneV8, pitchHz: 88f, clatterHz: 540f,
                        ring: 0.68f, rasp: 0.16f, noise: 0.46f, drive: 3.07f, boom: 0.85f,
                        exhaustLevel: 0.52f, idlePitch: 0.46f, redlinePitch: 1.40f);
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
                    config.AntiRollStiffness = 23400f;   // 22000 × 0.33 / 0.31
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

                    config.GearRatios = new[] { 4.35f, 3.05f, 2.20f, 1.62f, 1.18f, 0.86f };
                    config.PartThrottleUpshiftRpm = 2100f;
                    config.PartThrottleDownshiftRpm = 1050f;
                    // 4.90 at the old 0.44 m wheel, scaled by 0.46 / 0.44. Same top speed, same shift
                    // points, taller tyre.
                    config.FinalDrive = 5.12f;
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

                    // A diesel four: low, boomy, a hard throb at idle and no top end to speak of. Almost
                    // no half-order — a four is not a cross-plane V8 — but plenty of lope, which is the
                    // clatter.
                    Voice(config, 30f, 4, FiringLayout.Inline, pitchHz: 84f, clatterHz: 880f,
                        ring: 0.72f, rasp: 0.34f, noise: 0.80f, drive: 3.49f, boom: 1.05f,
                        exhaustLevel: 0.50f, idlePitch: 0.42f, redlinePitch: 1.24f);

                    // A small turbo that is on before you have finished pulling away, and no dump valve,
                    // because there is no throttle plate to shut. The whistle is barely there: it is a
                    // van, and the point of the layer here is that the van has one at all.
                    Turbo(config, whistle: 0.22f, spoolRevs: 0.14f, spoolRate: 1.5f,
                        notePitch: 0.70f, blowOff: 0f);
                    return;

                case "Pickup":
                    // Rear drive, a leaf-sprung back axle with nothing over it, and the least grip of the
                    // five. Willing to step out and slow to come back — the handbrake figures are low so
                    // it swings rather than stops.
                    config.Mass = 1750f;
                    config.CenterOfMass = new Vector3(0f, -0.18f, -0.05f);
                    config.RollDamping = 3.0f;
                    config.PitchDamping = 1.3f;
                    config.AntiRollStiffness = 18500f;   // 17000 × 0.36 / 0.33
                    config.MaxTorqueNm = 520f;
                    config.RedlineRpm = 4600f;
                    config.UpshiftRpm = 4300f;
                    config.DownshiftRpm = 1700f;
                    config.PartThrottleUpshiftRpm = 2300f;
                    config.PartThrottleDownshiftRpm = 1200f;

                    // Flat and low, the way a large lazy engine in a working vehicle is.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.72f),
                        new Keyframe(0.30f, 0.98f),
                        new Keyframe(0.65f, 1f),
                        new Keyframe(1f, 0.70f));

                    // 4.60 × 0.48 / 0.44.
                    config.FinalDrive = 5.02f;
                    config.LateralGrip = Grip(1.40f, 1.26f, 1.12f);
                    config.HandbrakeGrip = 0.35f;
                    config.MaxSteerAngle = 36f;
                    config.SteerRate = 260f;
                    config.BrakeForce = 17500f;
                    config.AeroDrag = 0.62f;
                    config.Downforce = 1.8f;
                    config.DriftYawDamping = 2.2f;

                    // A large lazy V8 in a working vehicle. Nearly the fastback's rumble, duller still,
                    // and the narrowest sweep of any petrol engine here.
                    Voice(config, 40f, 8, FiringLayout.CrossPlaneV8, pitchHz: 82f, clatterHz: 520f,
                        ring: 0.74f, rasp: 0.14f, noise: 0.52f, drive: 3.34f, boom: 0.95f,
                        exhaustLevel: 0.58f, idlePitch: 0.44f, redlinePitch: 1.42f);
                    return;

                case "Hatchback":
                    // The lightest and the least powerful. 260 Nm in 980 kg is momentum driving: you keep
                    // what speed you have, because getting it back takes a while. Plenty of grip and a lot
                    // of lock to spend it with.
                    config.Mass = 980f;
                    config.CenterOfMass = new Vector3(0f, -0.32f, 0.04f);
                    config.RollDamping = 2.2f;
                    config.PitchDamping = 0.9f;
                    config.AntiRollStiffness = 13700f;   // 12000 × 0.33 / 0.29
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
                    config.GearRatios = new[] { 3.75f, 2.62f, 1.92f, 1.45f, 1.14f, 0.94f };

                    // Scaled off the 6600 redline rather than left at the default: a threshold that is
                    // half of the fastback's redline is two thirds of a van's, and the point of the
                    // part-throttle figure is where it sits in the engine's own band.
                    config.PartThrottleUpshiftRpm = 3300f;
                    config.PartThrottleDownshiftRpm = 1700f;

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
                    // 6.40 × 0.40 / 0.44 — the one car that gets a shorter final drive out of this,
                    // because it is the one on a smaller wheel.
                    config.FinalDrive = 5.82f;
                    config.LateralGrip = Grip(1.66f, 1.54f, 1.40f);
                    config.MaxSteerAngle = 42f;
                    config.SteerRate = 320f;
                    config.BrakeForce = 13500f;
                    config.RollingResistanceN = 38f;
                    config.AeroDrag = 0.36f;
                    config.Downforce = 2.0f;

                    // A small four, and the widest pitch sweep in the game — 0.52 to 1.94. That is not
                    // decoration: this engine makes its power at the top and the car has to be revved to
                    // get anywhere, so the top of the band is where the player spends their time and it
                    // had better sound like somewhere worth being.
                    Voice(config, 40f, 4, FiringLayout.Inline, pitchHz: 150f, clatterHz: 1100f,
                        ring: 0.42f, rasp: 0.78f, noise: 0.40f, drive: 2.79f, boom: 0.55f,
                        exhaustLevel: 0.45f, idlePitch: 0.52f, redlinePitch: 1.94f);
                    return;

                case "Coupe":
                    // Four-wheel drive and the most grip here. It does not punish anything, and that is
                    // its character rather than a failure to give it one — after the pickup and the
                    // liftback there should be one car the player can lean on.
                    //
                    // The turbo lag is what stops that being boring. Flat until a third of the way up
                    // the band, then everything at once: a car that rewards being kept on the boil is a
                    // car where the gearbox matters, which nothing else in the garage can say.
                    config.Mass = 1560f;
                    config.CenterOfMass = new Vector3(0f, -0.32f, 0.02f);
                    config.RollDamping = 2.4f;
                    config.PitchDamping = 1.0f;
                    config.AntiRollStiffness = 16700f;   // 15000 × 0.30 / 0.27
                    config.DrivenAxle = DrivenAxle.All;
                    config.MaxTorqueNm = 520f;
                    config.IdleRpm = 800f;
                    config.RedlineRpm = 8000f;
                    config.UpshiftRpm = 7600f;
                    config.DownshiftRpm = 3200f;

                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.34f),
                        new Keyframe(0.32f, 0.52f),
                        new Keyframe(0.50f, 0.96f),
                        new Keyframe(0.78f, 1f),
                        new Keyframe(1f, 0.86f));

                    config.GearRatios = new[] { 3.83f, 2.36f, 1.69f, 1.31f, 1.13f, 1.00f };
                    config.PartThrottleUpshiftRpm = 4000f;
                    config.PartThrottleDownshiftRpm = 2000f;

                    // 5.05 rather than the 4.87 the real 3.545 scales to on 0.44 m wheels. Deliberately
                    // short: this is the car that gets out of a corner, and the liftback below is the
                    // one that arrives at the end of the straight first. Two cars with the same power
                    // and the same top speed are one car.
                    config.FinalDrive = 5.05f;
                    config.LateralGrip = Grip(1.75f, 1.60f, 1.45f);
                    config.MaxSteerAngle = 38f;
                    config.SteerRate = 310f;
                    config.BrakeForce = 19000f;
                    config.AeroDrag = 0.42f;
                    config.Downforce = 3.0f;
                    config.DriftYawDamping = 3.0f;

                    // A twin-turbo straight six that goes to 8000. No half-order whatsoever — an evenly
                    // firing six has none, and this is the single number that stops it sounding like the
                    // fastback played high. Rolloff 0.74 is the hardest, rawest voice here, and at 84 Hz
                    // its redline is the highest note any engine in the game reaches.
                    Voice(config, 45f, 6, FiringLayout.Inline, pitchHz: 132f, clatterHz: 920f,
                        ring: 0.36f, rasp: 0.88f, noise: 0.34f, drive: 3.30f, boom: 0.60f,
                        exhaustLevel: 0.52f, idlePitch: 0.42f, redlinePitch: 1.90f);

                    // Two small turbos: they come in late but arrive fast, and they sing high. spoolRevs
                    // 0.34 is deliberately the same place TorqueByRpm above stops being flat, so the
                    // whistle and the shove land together.
                    Turbo(config, whistle: 0.80f, spoolRevs: 0.34f, spoolRate: 3.4f,
                        notePitch: 1.20f, blowOff: 0.70f);
                    return;

                case "Liftback":
                    // The coupé's power through two wheels instead of four, and 10 kg heavier. The only
                    // car in the garage that cannot put down what it has.
                    //
                    // Its numbers are barely different from the coupé's — that is the point. Same era,
                    // same output, one drives out of a slide and the other has to be driven through it,
                    // and DrivenAxle is nearly the whole of the difference.
                    config.Mass = 1570f;
                    config.CenterOfMass = new Vector3(0f, -0.34f, 0f);
                    config.RollDamping = 2.3f;
                    config.PitchDamping = 1.0f;
                    config.AntiRollStiffness = 16050f;   // 14500 × 0.31 / 0.28
                    config.MaxTorqueNm = 560f;
                    config.IdleRpm = 780f;
                    config.RedlineRpm = 6800f;
                    config.UpshiftRpm = 6400f;
                    config.DownshiftRpm = 2700f;

                    // A big turbocharged six: on boost from a quarter of the way up and still pulling at
                    // the limiter. Less peaky than the coupé's, and far stronger low down.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.48f),
                        new Keyframe(0.26f, 0.88f),
                        new Keyframe(0.55f, 1f),
                        new Keyframe(1f, 0.90f));

                    config.GearRatios = new[] { 3.30f, 2.05f, 1.50f, 1.22f, 1.09f, 1.00f };
                    config.PartThrottleUpshiftRpm = 3200f;
                    config.PartThrottleDownshiftRpm = 1600f;
                    config.FinalDrive = 4.20f;
                    config.LateralGrip = Grip(1.68f, 1.52f, 1.34f);
                    config.MaxSteerAngle = 37f;
                    config.SteerRate = 300f;
                    config.BrakeForce = 18500f;
                    config.AeroDrag = 0.38f;
                    config.Downforce = 2.8f;

                    // Wide slip angle, low yaw damping and a lot of countersteer authority: it lets go
                    // early, holds the angle, and gives it back if the player asks properly.
                    config.DriftSlipAngle = 14f;
                    config.DriftYawDamping = 2.2f;
                    config.CountersteerAuthority = 0.80f;

                    // The same firing order as the coupé and a completely different engine. Bigger,
                    // longer-stroke, 1200 rpm less: rolloff 1.18 against 0.74 is full and round where
                    // that one is hard and raspy, and its redline note is 63 Hz against 84.
                    //
                    // This car, the coupé and the notchback are the three straight sixes, and they
                    // were once literally the same six numbers. They are now laid out deliberately
                    // apart — 84 / 71 / 63 Hz at the redline against rolloffs of 0.74 / 1.00 / 1.18 —
                    // so no two are close on either axis. Move one and check the other two.
                    Voice(config, 36f, 6, FiringLayout.Inline, pitchHz: 108f, clatterHz: 740f,
                        ring: 0.66f, rasp: 0.46f, noise: 0.30f, drive: 3.14f, boom: 0.80f,
                        exhaustLevel: 0.60f, idlePitch: 0.44f, redlinePitch: 1.76f);

                    // One large compressor against the coupé's two small ones, and every number says so:
                    // it starts earlier, takes half again as long to spool, whistles a fifth lower and
                    // louder, and has the biggest dump valve in the game. That valve is the single most
                    // recognisable noise this car makes and it is why blowOff is nearly at the ceiling.
                    Turbo(config, whistle: 1.00f, spoolRevs: 0.22f, spoolRate: 1.6f,
                        notePitch: 0.84f, blowOff: 0.95f);
                    return;

                case "Saloon":
                    // Light, modest and completely honest. Nothing in here is exaggerated, which is the
                    // whole brief: a garage of ten cars needs one that simply does what it is asked, or
                    // there is no baseline for the other nine to be interesting against.
                    config.Mass = 1120f;
                    config.CenterOfMass = new Vector3(0f, -0.28f, 0.03f);
                    config.RollDamping = 2.6f;
                    config.PitchDamping = 1.1f;
                    config.AntiRollStiffness = 14350f;   // 13000 × 0.32 / 0.29
                    config.MaxTorqueNm = 300f;
                    config.IdleRpm = 820f;
                    config.RedlineRpm = 6000f;
                    config.UpshiftRpm = 5600f;
                    config.DownshiftRpm = 2200f;

                    // A big naturally aspirated four: broad, unspectacular, nothing waiting at the top.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.62f),
                        new Keyframe(0.32f, 0.92f),
                        new Keyframe(0.60f, 1f),
                        new Keyframe(1f, 0.76f));

                    config.GearRatios = new[] { 3.91f, 2.32f, 1.60f, 1.25f, 1.00f };
                    config.PartThrottleUpshiftRpm = 2900f;
                    config.PartThrottleDownshiftRpm = 1450f;
                    // 4.64 × 0.42 / 0.44.
                    config.FinalDrive = 4.43f;
                    config.LateralGrip = Grip(1.58f, 1.44f, 1.28f);
                    config.MaxSteerAngle = 39f;
                    config.BrakeForce = 14500f;
                    config.RollingResistanceN = 42f;
                    config.AeroDrag = 0.48f;
                    config.Downforce = 2.1f;

                    // A big naturally aspirated four. A little half-order, because a four is not
                    // perfectly balanced the way a six is, and a middling rolloff: this is the plainest
                    // voice in the garage and that is the same brief the rest of its numbers have.
                    Voice(config, 36f, 4, FiringLayout.Inline, pitchHz: 120f, clatterHz: 900f,
                        ring: 0.55f, rasp: 0.50f, noise: 0.42f, drive: 2.79f, boom: 0.70f,
                        exhaustLevel: 0.48f, idlePitch: 0.48f, redlinePitch: 1.50f);
                    return;

                case "Notchback":
                    // The saloon 40 kg lighter, geared shorter, revving higher and with less grip to
                    // spend — a small six in a small car with a rear axle that gives up before the front
                    // does. Where the saloon understeers politely, this one rotates.
                    config.Mass = 1080f;
                    config.CenterOfMass = new Vector3(0f, -0.29f, -0.02f);
                    config.RollDamping = 2.3f;
                    config.PitchDamping = 1.0f;
                    config.AntiRollStiffness = 13800f;   // 12500 × 0.32 / 0.29
                    config.MaxTorqueNm = 330f;
                    config.IdleRpm = 800f;
                    config.RedlineRpm = 6500f;
                    config.UpshiftRpm = 6100f;
                    config.DownshiftRpm = 2500f;

                    // A smooth six that keeps pulling to the top, unlike the saloon's four.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.52f),
                        new Keyframe(0.35f, 0.86f),
                        new Keyframe(0.70f, 1f),
                        new Keyframe(1f, 0.88f));

                    config.GearRatios = new[] { 3.83f, 2.35f, 1.65f, 1.28f, 1.00f };
                    config.PartThrottleUpshiftRpm = 3100f;
                    config.PartThrottleDownshiftRpm = 1550f;
                    // 5.47 × 0.42 / 0.44.
                    config.FinalDrive = 5.22f;
                    config.LateralGrip = Grip(1.52f, 1.40f, 1.24f);

                    // Low, like the pickup's: the handbrake swings this car rather than stopping it,
                    // which on something this light is the point of having one.
                    config.HandbrakeGrip = 0.40f;
                    config.MaxSteerAngle = 41f;
                    config.SteerRate = 320f;
                    config.BrakeForce = 14000f;
                    config.RollingResistanceN = 40f;
                    config.AeroDrag = 0.46f;
                    config.Downforce = 2.0f;
                    config.DriftYawDamping = 2.0f;
                    config.CountersteerAuthority = 0.80f;

                    // The third straight six, and the only one without a turbocharger — which is most of
                    // what keeps it distinct from the other two. Smooth like them and brighter than
                    // either: a small naturally aspirated six is a thinner noise than a big boosted one,
                    // and with nothing whistling over it the engine is all there is to hear. Its
                    // redline note sits at 71 Hz, between the coupé's 84 and the liftback's 63.
                    Voice(config, 42f, 6, FiringLayout.Inline, pitchHz: 140f, clatterHz: 980f,
                        ring: 0.44f, rasp: 0.72f, noise: 0.32f, drive: 2.79f, boom: 0.62f,
                        exhaustLevel: 0.46f, idlePitch: 0.46f, redlinePitch: 1.78f);
                    return;

                case "Offroader":
                    // 2400 kg with its mass 1.9 m in the air, which makes it the one car here that can
                    // genuinely fall over. Everything below is arranged around not letting it.
                    //
                    // The centre of mass at -0.08 is two centimetres above the van's, and the van already
                    // needed a 22 kN bar. 26 kN is what this one needs, and it is not a tuning choice:
                    // CLAUDE.md says anti-roll bars are not optional because the car flips on the first
                    // hairpin without them, and that warning was written about lighter, lower vehicles
                    // than this. Do not raise the bar further if it still leans — a stiffer bar lifts the
                    // inside wheel and takes grip away. Lower the centre of mass instead.
                    config.Mass = 2400f;
                    config.CenterOfMass = new Vector3(0f, -0.08f, 0.04f);
                    config.RollDamping = 3.8f;
                    config.PitchDamping = 1.7f;
                    config.AntiRollStiffness = 26000f;
                    config.SuspensionStiffness = 58000f;
                    config.SuspensionDamping = 5200f;
                    config.DrivenAxle = DrivenAxle.All;
                    config.MaxTorqueNm = 620f;
                    config.IdleRpm = 650f;
                    config.RedlineRpm = 4800f;
                    config.UpshiftRpm = 4400f;
                    config.DownshiftRpm = 1600f;

                    // A large diesel: everything at the bottom and finished well before the limiter.
                    config.TorqueByRpm = new AnimationCurve(
                        new Keyframe(0f, 0.78f),
                        new Keyframe(0.24f, 1f),
                        new Keyframe(0.55f, 0.86f),
                        new Keyframe(1f, 0.48f));

                    // A crawler first gear, then ordinary spacing. It pulls away from anything at walking
                    // pace and runs out of enthusiasm by the third.
                    config.GearRatios = new[] { 4.75f, 3.10f, 2.10f, 1.55f, 1.20f, 1.00f };
                    config.PartThrottleUpshiftRpm = 2000f;
                    config.PartThrottleDownshiftRpm = 1000f;
                    // 4.89 × 0.48 / 0.44. The biggest correction in the file, and the one that matters
                    // most: 2400 kg geared 9% too long is a car that will not pull away from a junction.
                    config.FinalDrive = 5.33f;
                    config.LateralGrip = Grip(1.32f, 1.18f, 1.02f);
                    config.MaxSteerAngle = 32f;
                    config.SteerRate = 220f;
                    config.BrakeForce = 22000f;
                    config.RollingResistanceN = 62f;

                    // A brick, and the drag is most of why it will not reach its own gearing.
                    config.AeroDrag = 1.05f;
                    config.Downforce = 1.0f;
                    config.TurnInAssist = 2.2f;
                    config.DriftYawDamping = 3.6f;
                    config.CountersteerAuthority = 0.45f;

                    // The lowest and boomiest thing in the game: 28 Hz under a 1.72 rolloff, sweeping to
                    // only 1.12 because it is done at 4800. At idle that puts the quarter-order lope at
                    // under 3 Hz, which is the throb — a diesel V8 at rest is the one engine here you
                    // are meant to hear as separate beats rather than as a note.
                    Voice(config, 28f, 8, FiringLayout.CrossPlaneV8, pitchHz: 72f, clatterHz: 940f,
                        ring: 0.80f, rasp: 0.30f, noise: 0.76f, drive: 3.69f, boom: 1.15f,
                        exhaustLevel: 0.62f, idlePitch: 0.40f, redlinePitch: 1.12f);

                    // Turbocharged and, like the van, with no valve to dump through. Spools almost off
                    // idle, whistles lower than anything else, and stays well under the engine — this is
                    // a lorry's turbo, not a toy's.
                    Turbo(config, whistle: 0.32f, spoolRevs: 0.12f, spoolRate: 1.2f,
                        notePitch: 0.58f, blowOff: 0f);
                    return;
            }
        }

        /// <summary>
        /// One engine's voice: how it is put together, and what its pipe does with that.
        ///
        /// <para><b>The loop rule.</b> <c>firingHz / (cylinders / 2)</c> is the engine-cycle rate, and it
        /// must be a whole number that divides 44100 — so 12, 15, 18, 20, 7, 14 are fine and 11, 13, 16
        /// are not. Every call below is checked against it by hand and <c>VehicleConfig.LoopsCleanly</c>
        /// re-checks it on every reset, because the symptom is one tick a second and that is quiet
        /// enough to ship.</para>
        ///
        /// <para><b>What tells two engines apart now, in order.</b> The <i>layout</i> first, and it is no
        /// longer a number anyone chooses: a cross-plane V8's banks fire unevenly and beat, an inline six
        /// cannot, and the measured gap between them is a factor of five with nothing dialled in. Then
        /// the pipe — <c>pitchHz</c> is where it booms and <c>clatterHz</c> is where it rattles, and the
        /// distance between those two is most of what separates a diesel from a petrol engine. Then
        /// <c>ring</c>, which decides whether you hear a note or individual firings. The pitch span comes
        /// last and is what "revs" sounds like.</para>
        ///
        /// <para><b><paramref name="boom"/> is the one to reach for when a car sounds thin.</b> It weighs
        /// the resonance sitting on the firing order, which is the engine's fundamental and therefore
        /// most of what anybody means by an engine note. <paramref name="pitchHz"/> only colours what
        /// that produces.</para>
        /// </summary>
        private static void Voice(
            VehicleConfig config,
            float firingHz,
            int cylinders,
            FiringLayout layout,
            float pitchHz,
            float clatterHz,
            float ring,
            float rasp,
            float noise,
            float drive,
            float boom,
            float exhaustLevel,
            float idlePitch,
            float redlinePitch)
        {
            config.EngineFundamentalHz = firingHz;
            config.Cylinders = cylinders;
            config.Layout = layout;
            config.ExhaustPitchHz = pitchHz;
            config.ExhaustClatterHz = clatterHz;
            config.ExhaustRing = ring;
            config.ExhaustRasp = rasp;
            config.CombustionNoise = noise;
            config.ExhaustDrive = drive;
            config.ExhaustBoom = boom;
            config.ExhaustLevel = exhaustLevel;
            config.IdlePitch = idlePitch;
            config.RedlinePitch = redlinePitch;
        }

        /// <summary>
        /// One turbocharger. Left uncalled for a naturally aspirated car, which is what the zero default
        /// on <c>VehicleConfig.TurboWhistle</c> is for.
        ///
        /// <para><b>Keep <paramref name="spoolRevs"/> in step with the car's <c>TorqueByRpm</c>.</b> The
        /// curve is where the turbo lag actually lives — it is what the player feels — and this is only
        /// what they hear. A whistle arriving at a third of the band on an engine whose torque arrives
        /// at a quarter is a car that sounds like it is doing something other than what it is doing,
        /// and that reads as a bug in the physics rather than in the audio.</para>
        /// </summary>
        private static void Turbo(
            VehicleConfig config,
            float whistle,
            float spoolRevs,
            float spoolRate,
            float notePitch,
            float blowOff)
        {
            config.TurboWhistle = whistle;
            config.TurboSpoolRevs = spoolRevs;
            config.TurboSpoolRate = spoolRate;
            config.TurboNotePitch = notePitch;
            config.BlowOffLevel = blowOff;
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
