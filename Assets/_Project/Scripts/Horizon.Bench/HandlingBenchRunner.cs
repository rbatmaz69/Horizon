using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Horizon.Input;
using Horizon.Vehicle;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// The Play-mode half of <c>HandlingBench</c>: builds the plane, fits each car in turn and
    /// drives it through the tests.
    ///
    /// <para><b>A MonoBehaviour in <c>Horizon.Bench</c>, a runtime assembly compiled in the editor only
    /// — and it used to live in <c>Horizon.EditorTools</c>, where it could never run.</b> The reasoning
    /// then was that editor assemblies are loaded in the editor's own Play mode, so the component could
    /// be added to a live object. They are loaded, and Unity refuses all the same: <c>AddComponent</c> of
    /// a MonoBehaviour from an Editor-only assembly leaves an empty GameObject and logs "Can't add
    /// script behaviour … because it is an editor script". Every run, in the editor or in a batch run,
    /// entered Play mode, made the object, found nothing on it and waited for a car that was never
    /// placed, and no report was ever written. The half of the old reasoning that was right survives:
    /// <c>defineConstraints: ["UNITY_EDITOR"]</c> keeps this out of every player build, so the game still
    /// ships no test rig. See the module layout in CLAUDE.md.</para>
    ///
    /// <para><b>Every test samples once per physics step, not once per frame.</b> The clock is turned
    /// up twentyfold to keep the whole run near two minutes, which means a rendered frame covers a
    /// dozen or more fixed steps — and a peak lateral acceleration read once a frame would miss most of
    /// what it was looking for. <c>WaitForFixedUpdate</c> resumes once per step whatever the frame rate
    /// is doing, so the sampling is tied to the physics rather than to the machine the bench happens to
    /// be run on.</para>
    /// </summary>
    public sealed class HandlingBenchRunner : MonoBehaviour
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/Vehicles/Vehicle_Prototype.prefab";
        private const string ReportPath = "HandlingBench.txt";

        /// <summary>
        /// How much faster than real time the bench runs.
        ///
        /// <para>The fixed step is untouched, so every force is computed at the same 50 Hz the game
        /// uses and the numbers are the game's own. What changes is only how many of those steps fit
        /// into a second of the operator's life. <see cref="Time.maximumDeltaTime"/> has to move with
        /// it or Unity caps the catch-up at a third of a second of scaled time and the speed-up
        /// silently stops at about twelve.</para>
        /// </summary>
        private const float Clock = 20f;

        private const float KmhPerMs = 3.6f;
        private const float HundredKmh = 100f / KmhPerMs;
        private const float FiftyKmh = 50f / KmhPerMs;

        /// <summary>Speed the cornering tests are run at, m/s. 79 km/h: fast enough to matter, slow
        /// enough that every car in the fleet can hold it round a circle.</summary>
        private const float CornerSpeed = 22f;

        /// <summary>Speed the slalom is run at, m/s.</summary>
        private const float SlalomSpeed = 20f;

        private sealed class BenchInput : IDriveInput
        {
            public float Steer { get; set; }

            public float Throttle { get; set; }

            public float Brake { get; set; }

            public bool Handbrake { get; set; }

            public void Clear()
            {
                Steer = 0f;
                Throttle = 0f;
                Brake = 0f;
                Handbrake = false;
            }
        }

        private struct Result
        {
            public string Name;
            public float ZeroToHundred;
            public float TopSpeedKmh;
            public float BrakingDistance;
            public float CoastDecel;
            public float SkidpadG;
            public float SkidpadWobbleG;
            public float UnderRatio;
            public float LiftOffYawChange;
            public float SlalomRollDegrees;
            public float SlalomG;

            /// <summary>Seconds from reversing to moving forward again, throttle only.</summary>
            public float ReverseRecoverySeconds;

            /// <summary>
            /// How fast the car is still going backwards, km/h, with the throttle <i>and</i> a stuck
            /// brake both held. Zero is the pass.
            /// </summary>
            public float StuckPedalReverseKmh;

            /// <summary>The static tipping point, g — <c>VehicleConfig.StaticTippingPointG</c>.</summary>
            public float TipG;

            /// <summary>
            /// The least any wheel's spring was compressed on the settled skidpad circle, as a share of
            /// its compression parked. Zero means a wheel left the road.
            /// </summary>
            public float SkidInsidePercent;

            public int SkidWheelOffSteps;
            public int SlalomWheelOffSteps;

            public float HairpinRollDegrees;
            public float HairpinInsidePercent;
            public int HairpinWheelOffSteps;
            public bool HairpinTipped;
        }

        private readonly BenchInput input = new BenchInput();
        private readonly WaitForFixedUpdate step = new WaitForFixedUpdate();

        private VehicleController vehicle;
        private VehicleBodySet bodies;
        private FuelTank fuel;
        private Rigidbody body;
        private Vector3 previousVelocity;

        /// <summary>Each wheel's spring compression parked, taken at the end of every <see cref="Reset"/>.</summary>
        private readonly float[] parkedCompression = new float[4];

        private void Start()
        {
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            float wasTimeScale = Time.timeScale;
            float wasMaximum = Time.maximumDeltaTime;

            Time.timeScale = Clock;
            Time.maximumDeltaTime = 0.5f;

            BuildGround();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[Horizon] No vehicle prefab at {PrefabPath}. Run "
                               + "Tools > Horizon > Rebuild Prototype Scene first.");
                Finish(wasTimeScale, wasMaximum);
                yield break;
            }

            GameObject car = Instantiate(prefab, new Vector3(0f, 1.5f, 0f), Quaternion.identity);
            vehicle = car.GetComponent<VehicleController>();
            bodies = car.GetComponent<VehicleBodySet>();
            fuel = car.GetComponent<FuelTank>();
            body = car.GetComponent<Rigidbody>();
            vehicle.SetInput(input);

            StripCosmetics(car);

            var results = new Result[bodies.BodyCount];

            for (int i = 0; i < bodies.BodyCount; i++)
            {
                bodies.Select(i, 0);
                results[i].Name = bodies.NameOf(i);
                results[i].TipG = vehicle.Config != null ? vehicle.Config.StaticTippingPointG() : float.NaN;

                // A log line rather than a progress bar: EditorUtility's is modal, and a modal window
                // held open across a Play-mode run is a good way to stop the run it is reporting on.
                Debug.Log($"[Horizon] Measuring {results[i].Name} ({i + 1} of {bodies.BodyCount})");

                yield return Straight(results, i);
                yield return Braking(results, i);
                yield return CoastDown(results, i);
                yield return Skidpad(results, i);
                yield return Slalom(results, i);
                yield return Hairpin(results, i);
                yield return Reverse(results, i);
            }

            Report(results);
            Finish(wasTimeScale, wasMaximum);
        }

        /// <summary>
        /// Puts the clock back and ends the run.
        ///
        /// <para>In an editor session that means leaving Play mode, and <c>HandlingBench</c>
        /// reopens whatever scene was showing before. In a batch run there is no session to hand back
        /// to, so the process is ended outright — Unity's batch mode is deliberately started without
        /// <c>-quit</c> for this, because the play loop has to keep turning until the last car has
        /// stopped.</para>
        /// </summary>
        private void Finish(float timeScale, float maximumDeltaTime)
        {
            Time.timeScale = timeScale;
            Time.maximumDeltaTime = maximumDeltaTime;

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
                return;
            }

            EditorApplication.ExitPlaymode();
        }

        /// <summary>
        /// A single box, twenty kilometres on a side, with its top face at zero.
        ///
        /// <para>Untagged, so every wheel reads <c>SurfaceKind.Asphalt</c> — the documented default for
        /// geometry nobody marked. A box rather than Unity's plane primitive because a
        /// <c>BoxCollider</c> is exact and free where a mesh collider that size is neither, and because
        /// the top-speed run covers about four kilometres and has to land on ground the whole way.</para>
        /// </summary>
        private static void BuildGround()
        {
            var ground = new GameObject("Ground");
            ground.transform.position = new Vector3(0f, -1f, 0f);

            BoxCollider box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(20000f, 2f, 20000f);
        }

        /// <summary>
        /// Removes everything on the car that makes a noise, a puff of smoke or a beam of light.
        ///
        /// <para>Destroyed rather than disabled, because <c>VehicleBodySet.Select</c> null-checks its
        /// audio reference and rebuilds five synthesised clips per car when it finds one. Ten cars is
        /// ten rebuilds of something no one is listening to.</para>
        /// </summary>
        private static void StripCosmetics(GameObject car)
        {
            DestroyAll<EngineAudio>(car);
            DestroyAll<ContactAudio>(car);
            DestroyAll<RainAudio>(car);
            DestroyAll<TyreSmoke>(car);
            DestroyAll<ExhaustSmoke>(car);
            DestroyAll<VehicleLights>(car);
            DestroyAll<VehicleCover>(car);
            DestroyAll<AudioSource>(car);
        }

        private static void DestroyAll<T>(GameObject car) where T : Component
        {
            T[] found = car.GetComponentsInChildren<T>(true);
            for (int i = 0; i < found.Length; i++)
            {
                DestroyImmediate(found[i]);
            }
        }

        /// <summary>Puts the car back at the origin, full of fuel, and lets it settle on its springs.</summary>
        private IEnumerator Reset()
        {
            input.Clear();
            vehicle.Teleport(new Vector3(0f, 1.5f, 0f), Quaternion.identity);
            fuel.FillFully();
            previousVelocity = Vector3.zero;

            for (int i = 0; i < 90; i++)
            {
                yield return step;
            }

            previousVelocity = body.linearVelocity;

            for (int w = 0; w < parkedCompression.Length; w++)
            {
                parkedCompression[w] = vehicle.TryGetWheelCompression(w, out float compression) ? compression : 0f;
            }
        }

        /// <summary>
        /// One physics step's worth of the rollover instruments: whether a wheel is off the road, and the
        /// least any spring is compressed against its parked compression.
        ///
        /// <para>Compression is a proxy for load, not the load — the anti-roll bar moves load across an
        /// axle as well as the springs do — which is why a wheel actually leaving the road is counted
        /// separately, and why that count is the hard signal. The proxy is what shows a car getting
        /// close.</para>
        /// </summary>
        private void SampleRollover(ref float insideShare, ref int wheelOffSteps)
        {
            if (vehicle.GroundedWheelCount < parkedCompression.Length)
            {
                wheelOffSteps++;
            }

            for (int w = 0; w < parkedCompression.Length; w++)
            {
                float share = vehicle.TryGetWheelCompression(w, out float compression) && parkedCompression[w] > 0.001f
                    ? compression / parkedCompression[w]
                    : 0f;

                insideShare = Mathf.Min(insideShare, share);
            }
        }

        private float Speed => Vector3.Dot(body.linearVelocity, vehicle.transform.forward);

        /// <summary>Lateral acceleration in g, differentiated from the body's own velocity.</summary>
        private float LateralG(float deltaTime)
        {
            Vector3 velocity = body.linearVelocity;
            Vector3 acceleration = (velocity - previousVelocity) / deltaTime;
            previousVelocity = velocity;
            return Vector3.Dot(acceleration, vehicle.transform.right) / Physics.gravity.magnitude;
        }

        private float YawRate => Vector3.Dot(body.angularVelocity, vehicle.transform.up);

        /// <summary>Full throttle from rest: the time to 100 km/h and the speed it runs out of gearing at.</summary>
        private IEnumerator Straight(Result[] results, int index)
        {
            yield return Reset();

            input.Throttle = 1f;

            float elapsed = 0f;
            float reached = float.NaN;
            float top = 0f;

            while (elapsed < 70f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;

                float speed = Speed;
                if (speed > top)
                {
                    top = speed;
                }

                if (float.IsNaN(reached) && speed >= HundredKmh)
                {
                    reached = elapsed;
                }
            }

            results[index].ZeroToHundred = reached;
            results[index].TopSpeedKmh = top * KmhPerMs;
        }

        /// <summary>Distance from 100 km/h to a standstill, brake hard on.</summary>
        private IEnumerator Braking(Result[] results, int index)
        {
            yield return Reset();

            input.Throttle = 1f;

            float elapsed = 0f;
            while (Speed < HundredKmh + 1f && elapsed < 60f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
            }

            input.Throttle = 0f;
            input.Brake = 1f;

            float distance = 0f;
            elapsed = 0f;

            while (Speed > 0.3f && elapsed < 20f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
                distance += Mathf.Abs(Speed) * Time.fixedDeltaTime;
            }

            results[index].BrakingDistance = distance;
        }

        /// <summary>
        /// Getting out of reverse, and the one case in this bench that is about the controls rather
        /// than the tyres.
        ///
        /// <para><b>Two things are measured and the second is the regression.</b> This car has one
        /// pedal for braking and reversing — brake below 0.6 m/s means reverse — and every backwards
        /// speed there is sits below that threshold. So while the car is reversing, a brake input can
        /// only mean "reverse harder", and it used to beat the throttle outright: press forward and the
        /// car accelerated away from you with the pedal you were holding doing nothing.</para>
        ///
        /// <para>With two working buttons nobody reaches that. Without them it is easy: a finger on the
        /// brake when a notification arrives leaves it latched at 1 with no pointer-up ever coming, and
        /// the car then brakes itself to a stop, reverses on its own and refuses to come back. This
        /// holds both pedals at once, which is exactly that state and is otherwise unreachable, and
        /// asks whether the car still accelerates backwards.</para>
        /// </summary>
        private IEnumerator Reverse(Result[] results, int index)
        {
            yield return Reset();

            // Into reverse the way a player gets there: hold the one pedal and wait.
            input.Brake = 1f;

            float elapsed = 0f;
            while (Speed > -3f && elapsed < 20f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
            }

            // --- Both pedals, which is the stuck-brake case. The throttle is unambiguous and must win.
            input.Throttle = 1f;

            float worst = Speed;
            elapsed = 0f;

            while (elapsed < 3f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
                worst = Mathf.Min(worst, Speed);
            }

            results[index].StuckPedalReverseKmh = Mathf.Max(0f, -Speed) * KmhPerMs;

            // --- And the ordinary case: brake off, throttle on, how long back to going forwards.
            input.Brake = 0f;

            elapsed = 0f;
            while (Speed < 1f && elapsed < 20f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
            }

            results[index].ReverseRecoverySeconds = Speed >= 1f ? elapsed : float.NaN;

            input.Clear();
        }

        /// <summary>
        /// Deceleration coasting from 100 to 50 km/h with no pedal at all, m/s².
        ///
        /// <para>The one number that says whether lifting off does anything. Today it is rolling
        /// resistance and air and nothing else, which is about a fifth of a metre per second squared —
        /// so this is the reference the engine braking work is measured against.</para>
        /// </summary>
        private IEnumerator CoastDown(Result[] results, int index)
        {
            yield return Reset();

            input.Throttle = 1f;

            float elapsed = 0f;
            while (Speed < HundredKmh + 1f && elapsed < 60f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
            }

            input.Throttle = 0f;
            elapsed = 0f;

            while (Speed > HundredKmh && elapsed < 5f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
            }

            float coasted = 0f;
            while (Speed > FiftyKmh && coasted < 120f)
            {
                yield return step;
                coasted += Time.fixedDeltaTime;
            }

            results[index].CoastDecel = coasted > 0.001f
                ? (HundredKmh - FiftyKmh) / coasted
                : float.NaN;
        }

        /// <summary>
        /// Steady-state cornering: how much lateral grip there is, whether it is steady, whether the car
        /// understeers, and what happens when the driver lifts.
        ///
        /// <para><b>The wobble figure is the stability test for the whole tyre model.</b> A tyre whose
        /// force law is too stiff for a 50 Hz step does not fail loudly — it holds roughly the right
        /// average and rings around it, which reads on the road as a car that buzzes in long corners
        /// and in a log as nothing at all. The spread of lateral g over the last second of a settled
        /// circle is the one place that shows.</para>
        ///
        /// <para><b>Understeer is measured as a ratio of radii, not as a gradient.</b> The car is held
        /// at one steering angle and one speed, so the circle it actually drives is
        /// <c>speed / yawRate</c>, and the circle its front wheels are pointing at is
        /// <c>Wheelbase / tan(SteerAngle)</c>. Above one it is running wide. Both halves come off the
        /// controller rather than being recomputed here, for the reason those two properties give.</para>
        /// </summary>
        private IEnumerator Skidpad(Result[] results, int index)
        {
            yield return Reset();

            float elapsed = 0f;
            while (Speed < CornerSpeed && elapsed < 60f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
                HoldSpeed(CornerSpeed);
            }

            // Steering is ramped rather than stepped: a step is a transient, and this test is about
            // what the car settles at.
            float ramp = 0f;
            while (ramp < 3f)
            {
                yield return step;
                ramp += Time.fixedDeltaTime;
                HoldSpeed(CornerSpeed);
                input.Steer = Mathf.Clamp01(ramp / 3f);
                LateralG(Time.fixedDeltaTime);
            }

            float peak = 0f;
            float low = float.MaxValue;
            float high = float.MinValue;
            float steerAngle = 0f;
            float yawRate = 0f;
            float speed = 0f;
            float settle = 0f;
            float skidInside = float.MaxValue;
            int skidOff = 0;

            while (settle < 3f)
            {
                yield return step;
                settle += Time.fixedDeltaTime;
                HoldSpeed(CornerSpeed);

                SampleRollover(ref skidInside, ref skidOff);

                float lateral = Mathf.Abs(LateralG(Time.fixedDeltaTime));
                if (lateral > peak)
                {
                    peak = lateral;
                }

                // Only the last second counts toward the spread: the first two are the car still
                // settling onto the circle, and a transient is not a wobble.
                if (settle > 2f)
                {
                    low = Mathf.Min(low, lateral);
                    high = Mathf.Max(high, lateral);
                    steerAngle += Mathf.Abs(vehicle.SteerAngle);
                    yawRate += Mathf.Abs(YawRate);
                    speed += Mathf.Abs(Speed);
                }
            }

            int samples = Mathf.Max(1, Mathf.RoundToInt(1f / Time.fixedDeltaTime));
            steerAngle /= samples;
            yawRate /= samples;
            speed /= samples;

            results[index].SkidpadG = peak;
            results[index].SkidInsidePercent = skidInside * 100f;
            results[index].SkidWheelOffSteps = skidOff;
            results[index].SkidpadWobbleG = high > low ? high - low : 0f;

            float drivenRadius = yawRate > 0.001f ? speed / yawRate : float.NaN;
            float ackermannRadius = steerAngle > 0.05f
                ? vehicle.Wheelbase / Mathf.Tan(steerAngle * Mathf.Deg2Rad)
                : float.NaN;

            results[index].UnderRatio = drivenRadius / ackermannRadius;

            // And the lift, from the circle the car is already settled on.
            float before = Mathf.Abs(YawRate);
            input.Throttle = 0f;
            input.Brake = 0f;

            float lift = 0f;
            while (lift < 1.2f)
            {
                yield return step;
                lift += Time.fixedDeltaTime;
            }

            float after = Mathf.Abs(YawRate);
            results[index].LiftOffYawChange = before > 0.001f ? (after - before) / before : float.NaN;
        }

        /// <summary>
        /// A steering sinusoid at speed: how far the body leans and how much grip a transient finds.
        ///
        /// <para>Roll is the number to watch on the tall cars. The van and the offroader are the two
        /// this project has already recorded as able to fall over, and their anti-roll bars are sized
        /// against exactly this manoeuvre rather than against a steady circle.</para>
        /// </summary>
        private IEnumerator Slalom(Result[] results, int index)
        {
            yield return Reset();

            float elapsed = 0f;
            while (Speed < SlalomSpeed && elapsed < 60f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
                HoldSpeed(SlalomSpeed);
            }

            float roll = 0f;
            float lateral = 0f;
            float time = 0f;
            float slalomInside = float.MaxValue;
            int slalomOff = 0;

            while (time < 12f)
            {
                yield return step;
                time += Time.fixedDeltaTime;
                HoldSpeed(SlalomSpeed);

                // 0.5 Hz at 20 m/s is a change of direction every 20 m, which is a tight slalom and
                // the point: a lazy one is answered by every car in the fleet identically.
                input.Steer = Mathf.Sin(time * Mathf.PI);
                SampleRollover(ref slalomInside, ref slalomOff);

                float lean = Mathf.Abs(
                    Vector3.SignedAngle(Vector3.up, vehicle.transform.up, vehicle.transform.forward));
                if (lean > roll)
                {
                    roll = lean;
                }

                float g = Mathf.Abs(LateralG(Time.fixedDeltaTime));
                if (g > lateral)
                {
                    lateral = g;
                }
            }

            results[index].SlalomRollDegrees = roll;
            results[index].SlalomG = lateral;
            results[index].SlalomWheelOffSteps = slalomOff;
        }

        /// <summary>
        /// Full lock thrown at speed, all at once: the transient the other two cornering tests cannot see.
        ///
        /// <para><b>The skidpad ramps its steering over three seconds on purpose</b> — it is measuring what
        /// a car settles at — and the slalom is a smooth sine. Neither can catch a car that goes over on
        /// the overshoot of a sudden turn-in, which is how a narrow, tall car actually falls over on a
        /// hairpin. This asks for full lock in a tenth of a second at the cornering speed and holds it for
        /// three, with the pedal frozen where it was holding that speed; the car's own
        /// <c>SteerRate</c> decides how fast the wheels actually get there.</para>
        ///
        /// <para>Over is counted when the body passes sixty degrees of roll, which no car recovers from on
        /// its own.</para>
        /// </summary>
        private IEnumerator Hairpin(Result[] results, int index)
        {
            yield return Reset();

            float elapsed = 0f;
            while (Speed < CornerSpeed && elapsed < 60f)
            {
                yield return step;
                elapsed += Time.fixedDeltaTime;
                HoldSpeed(CornerSpeed);
            }

            float throttle = input.Throttle;
            input.Brake = 0f;

            float roll = 0f;
            float inside = float.MaxValue;
            int off = 0;
            bool tipped = false;
            float time = 0f;

            while (time < 3f)
            {
                yield return step;
                time += Time.fixedDeltaTime;

                input.Throttle = throttle;
                input.Steer = Mathf.Clamp01(time / 0.1f);

                SampleRollover(ref inside, ref off);

                float lean = Mathf.Abs(
                    Vector3.SignedAngle(Vector3.up, vehicle.transform.up, vehicle.transform.forward));
                roll = Mathf.Max(roll, lean);

                if (vehicle.transform.up.y < 0.5f)
                {
                    tipped = true;
                }
            }

            results[index].HairpinRollDegrees = roll;
            results[index].HairpinInsidePercent = inside * 100f;
            results[index].HairpinWheelOffSteps = off;
            results[index].HairpinTipped = tipped;
        }

        /// <summary>
        /// A proportional pedal that keeps the car at a speed while a cornering test does its work.
        ///
        /// <para>Deliberately crude. A tuned controller would hide the very thing some of these tests
        /// are looking for — a car that cannot hold its speed round a circle is telling you something,
        /// and a throttle that chased the number would report the same speed for every car.</para>
        /// </summary>
        private void HoldSpeed(float target)
        {
            float error = target - Speed;
            input.Throttle = Mathf.Clamp01(error * 0.5f);
            input.Brake = Mathf.Clamp01(-error * 0.5f);
        }

        private static void Report(Result[] results)
        {
            var text = new StringBuilder();
            CultureInfo culture = CultureInfo.InvariantCulture;

            text.AppendLine("Horizon handling bench");
            text.AppendLine("Flat asphalt, no wind, no camber, one paint. 50 Hz fixed step.");
            text.AppendLine();
            text.AppendLine(
                "Car          0-100s   Top km/h   100-0 m   Coast m/s2   Skid g   Wobble g   R/R0   Lift %   Roll deg   Slalom g");

            for (int i = 0; i < results.Length; i++)
            {
                Result r = results[i];
                text.AppendLine(string.Format(
                    culture,
                    "{0,-12}{1,7}{2,11}{3,10}{4,13}{5,9}{6,11}{7,7}{8,9}{9,11}{10,11}",
                    r.Name,
                    Number(r.ZeroToHundred, 2),
                    Number(r.TopSpeedKmh, 1),
                    Number(r.BrakingDistance, 1),
                    Number(r.CoastDecel, 2),
                    Number(r.SkidpadG, 2),
                    Number(r.SkidpadWobbleG, 3),
                    Number(r.UnderRatio, 2),
                    Number(r.LiftOffYawChange * 100f, 1),
                    Number(r.SlalomRollDegrees, 1),
                    Number(r.SlalomG, 2)));
            }

            text.AppendLine();
            text.AppendLine("Rollover");
            text.AppendLine(
                "Car          Tip g   Skid/Tip   Skid in %   Skid off   Slalom off   Hairpin deg   Hairpin in %   Hairpin off   Over");

            string over = null;
            string lifting = null;

            for (int i = 0; i < results.Length; i++)
            {
                Result r = results[i];
                text.AppendLine(string.Format(
                    culture,
                    "{0,-12}{1,6}{2,11}{3,12}{4,11}{5,13}{6,14}{7,15}{8,14}{9,7}",
                    r.Name,
                    Number(r.TipG, 2),
                    Number(r.TipG > 0f ? r.SkidpadG / r.TipG : float.NaN, 2),
                    Number(r.SkidInsidePercent, 0),
                    r.SkidWheelOffSteps,
                    r.SlalomWheelOffSteps,
                    Number(r.HairpinRollDegrees, 1),
                    Number(r.HairpinInsidePercent, 0),
                    r.HairpinWheelOffSteps,
                    r.HairpinTipped ? "OVER" : "-"));

                if (r.HairpinTipped)
                {
                    over = over == null ? r.Name : over + ", " + r.Name;
                }

                if (r.SkidWheelOffSteps > 0)
                {
                    lifting = lifting == null ? r.Name : lifting + ", " + r.Name;
                }
            }

            text.AppendLine();
            text.AppendLine("Tip g is the static tipping point, track / (2 x centre-of-mass height), and Skid/Tip");
            text.AppendLine("is the settled skidpad g over it. In % is the least any wheel's spring was compressed");
            text.AppendLine("against its compression parked -- a proxy for load, since the anti-roll bar moves load");
            text.AppendLine("too. Off counts physics steps with a wheel off the road, and is the hard signal. The");
            text.AppendLine("hairpin is full lock thrown in 0.1 s at 79 km/h and held for 3 s; Over is past 60 deg.");

            if (over != null)
            {
                Debug.LogError(
                    $"[Horizon] Rolled over in the hairpin step-steer: {over}. A car that goes over on a sudden "
                    + "turn-in at 79 km/h goes over on the first hairpin a player takes too fast. See the "
                    + "centre of mass, the track and the anti-roll bars, in that order.");
            }

            if (lifting != null)
            {
                Debug.LogWarning(
                    $"[Horizon] Lifted a wheel on the settled skidpad circle: {lifting}. Steady cornering "
                    + "should never do that — the car is being held past its own tipping margin.");
            }

            text.AppendLine();
            text.AppendLine("Getting out of reverse");
            text.AppendLine(
                "Car          Recover s   Stuck-pedal km/h back");

            var worstStuck = 0f;

            for (int i = 0; i < results.Length; i++)
            {
                Result r = results[i];
                worstStuck = Mathf.Max(worstStuck, r.StuckPedalReverseKmh);

                text.AppendLine(string.Format(
                    culture,
                    "{0,-12}{1,10}{2,24}",
                    r.Name,
                    Number(r.ReverseRecoverySeconds, 2),
                    Number(r.StuckPedalReverseKmh, 1)));
            }

            text.AppendLine();
            text.AppendLine("Recover s is throttle-only, from 3 m/s backwards to 1 m/s forwards.");
            text.AppendLine("Stuck-pedal is how fast the car is still going backwards with the throttle");
            text.AppendLine("and a latched brake both held -- the state a lost pointer-up leaves behind.");
            text.AppendLine("Anything but zero there is the car driving away from the pedal being asked.");

            if (worstStuck > 1f)
            {
                Debug.LogError(
                    $"[Horizon] A car still reverses at {worstStuck:0.0} km/h with the throttle held. "
                    + "The brake is beating the throttle into reverse, which is what a latched pedal "
                    + "looks like from the driver's seat: a car that accelerates on its own and will "
                    + "not come back. See VehicleController's reverse condition.");
            }

            text.AppendLine();
            text.AppendLine("R/R0 above 1 is understeer, below 1 oversteer. Lift % is the change in yaw");
            text.AppendLine("rate 1.2 s after closing the throttle on a settled circle: positive is the");
            text.AppendLine("car tucking in. Wobble g is the spread of lateral g over the last second of");
            text.AppendLine("that circle and is the tyre model's stability test -- anything above about");
            text.AppendLine("0.05 is the force law ringing against the fixed step rather than a car.");

            string path = Path.Combine(Application.dataPath, "..", ReportPath);
            File.WriteAllText(Path.GetFullPath(path), text.ToString());

            Debug.Log($"[Horizon] Handling bench:\n{text}");
            Debug.Log($"[Horizon] Written to {ReportPath}");
        }

        private static string Number(float value, int digits) =>
            float.IsNaN(value) || float.IsInfinity(value)
                ? "-"
                : value.ToString("F" + digits, CultureInfo.InvariantCulture);
    }
}
