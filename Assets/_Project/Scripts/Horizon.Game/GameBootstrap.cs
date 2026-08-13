using System.Collections;
using Horizon.Core;
using Horizon.Input;
using Horizon.Vehicle;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.Game
{
    /// <summary>
    /// Lives in Bootstrap.unity and owns everything that must survive a zone change: the input
    /// router, frame-rate policy, and the debug overlay. It then additively loads the world scene
    /// and wires the camera to whatever vehicle that scene contains.
    ///
    /// The prototype only has one zone, but the additive pattern is the point — adding the city or
    /// the highway later means loading another scene, not restructuring this one.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private string worldSceneName = "World_MountainPass";
        [SerializeField] private DriveInputRouter inputRouter;

        [Tooltip("Frame rate we ask the platform for. 60 is the target on mid-range Android. The quality "
               + "setting overrides this — Low asks for 30.")]
        [SerializeField] private int targetFrameRate = 60;

        [Tooltip("Owns the quality setting. Found among the children if left empty.")]
        [SerializeField] private QualityDirector qualityDirector;

        [Tooltip("The start screen, if the scene has one. Told when the world is ready.")]
        [SerializeField] private StartScreen startScreen;

        /// <summary>The vehicle the player is currently driving, once the world has loaded.</summary>
        public VehicleController PlayerVehicle { get; private set; }

        /// <summary>The active input router.</summary>
        public DriveInputRouter InputRouter => inputRouter;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            // vSync is meaningless on mobile; targetFrameRate is what the platform actually honours.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;

            if (inputRouter == null)
            {
                inputRouter = GetComponentInChildren<DriveInputRouter>();
            }

            // Before the additive load, because the streaming radii have to be right for the streamer's
            // very first pass — see WorldStreamingDriver.Start — and because the frame rate is a global
            // that wants setting once rather than being changed under a running game.
            PlayerChoices.Load();

            if (qualityDirector == null)
            {
                qualityDirector = GetComponentInChildren<QualityDirector>();
            }

            qualityDirector?.Apply(PlayerChoices.Quality);
        }

        private IEnumerator Start()
        {
            if (!string.IsNullOrEmpty(worldSceneName) && !SceneManager.GetSceneByName(worldSceneName).isLoaded)
            {
                yield return SceneManager.LoadSceneAsync(worldSceneName, LoadSceneMode.Additive);
            }

            WireUpWorld();
        }

        /// <summary>
        /// Connects the persistent systems to the freshly loaded world. Called after the additive
        /// load, and safe to call again if a zone is swapped.
        /// </summary>
        public void WireUpWorld()
        {
            PlayerVehicle = FindFirstObjectByType<VehicleController>();
            if (PlayerVehicle == null)
            {
                Debug.LogError(
                    $"[Horizon] No VehicleController found after loading '{worldSceneName}'. "
                    + "Run Tools > Horizon > Rebuild Prototype Scene.",
                    this);
                return;
            }

            ChaseCamera camera = FindFirstObjectByType<ChaseCamera>();
            if (camera != null)
            {
                camera.SetTarget(PlayerVehicle.transform, PlayerVehicle.GetComponent<Rigidbody>());
            }
            else
            {
                Debug.LogWarning("[Horizon] No ChaseCamera in the loaded world.", this);
            }

            // Last, so the start screen finds a bound camera when it puts the car at the chosen place.
            if (startScreen == null)
            {
                startScreen = FindFirstObjectByType<StartScreen>();
            }

            startScreen?.OnWorldReady(PlayerVehicle);
        }
    }
}
