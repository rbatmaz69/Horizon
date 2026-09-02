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
    /// The Play-mode half of <see cref="HandlingBench"/>: builds the plane, fits each car in turn and
    /// drives it through the tests.
    ///
    /// <para><b>A MonoBehaviour in an Editor assembly, which is legal and deliberate.</b> Editor
    /// assemblies are loaded in the editor's own Play mode, so this can be added to a live object; it
    /// cannot exist in a player build, which is exactly right for a measuring instrument. The
    /// alternative was a runtime component in <c>Horizon.Game</c>, which would ship a test rig with
    /// the game.</para>
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
        }

        private readonly BenchInput input = new BenchInput();
        private readonly WaitForFixedUpdate step = new WaitForFixedUpdate();

        private VehicleController vehicle;
        private VehicleBodySet bodies;
        private FuelTank fuel;
        private Rigidbody body;
        private Vector3 previousVelocity;

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

                // A log line rather than a progress bar: EditorUtility's is modal, and a modal window
                // held open across a Play-mode run is a good way to stop the run it is reporting on.
                Debug.Log($"[Horizon] Measuring {results[i].Name} ({i + 1} of {bodies.BodyCount})");

                yield return Straight(results, i);
                yield return Braking(results, i);
                yield return CoastDown(results, i);
                yield return Skidpad(results, i);
                yield return Slalom(results, i);
            }

            Report(results);
            Finish(wasTimeScale, wasMaximum);
        }

        /// <summary>
        /// Puts the clock back and ends the run.
        ///
        /// <para>In an editor session that means leaving Play mode, and <see cref="HandlingBench"/>
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

            while (settle < 3f)
            {
                yield return step;
                settle += Time.fixedDeltaTime;
                HoldSpeed(CornerSpeed);

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

            while (time < 12f)
            {
                yield return step;
                time += Time.fixedDeltaTime;
                HoldSpeed(SlalomSpeed);

                // 0.5 Hz at 20 m/s is a change of direction every 20 m, which is a tight slalom and
                // the point: a lazy one is answered by every car in the fleet identically.
                input.Steer = Mathf.Sin(time * Mathf.PI);

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
