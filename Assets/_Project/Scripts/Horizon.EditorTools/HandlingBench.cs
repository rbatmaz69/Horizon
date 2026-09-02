using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Drives all ten cars through the same eight tests and prints a table.
    ///
    /// <para><b>Why this exists at all, when everything else here is a photograph.</b> Twenty-three
    /// tools in this project either rebuild the scene or render a preview of it, because what goes
    /// wrong in a world is visible and silent. Handling is the opposite of that: a car that has lost a
    /// tenth of a g on the front axle, or that now takes four metres longer to stop, looks exactly like
    /// one that has not, in every frame this project can take, day or night. It is the same argument
    /// <c>ValidateSurfaces</c> already makes one system along — <i>a picture cannot check this, so it
    /// gets a measurement instead</i> — and it applies with more force here, because the subject is the
    /// one thing in the game the player is doing every second.</para>
    ///
    /// <para><b>Why it enters Play mode, which nothing else here does.</b> The obvious edit-time route
    /// is <c>Physics.simulationMode = Script</c> and a tight <c>Physics.Simulate</c> loop, which is how
    /// a deterministic bench would normally be written. It does not work: <c>Physics.Simulate</c> steps
    /// the solver and does <b>not</b> call <c>FixedUpdate</c>, and every force this car makes is applied
    /// in <c>VehicleController.FixedUpdate</c>. A bench built that way would measure a rigid body
    /// falling over, at full speed, with a completely clean report. So the run happens in Play mode,
    /// where <c>Awake</c> has run and the fixed step is real, and the clock is simply turned up.</para>
    ///
    /// <para><b>It runs on a bare plane, not on the world.</b> Nothing here is measuring the roads —
    /// camber, surface kind, traffic and terrain are all things that would make two runs differ for
    /// reasons that have nothing to do with the car. Untagged ground drives as asphalt, which is the
    /// documented safe direction for <c>GroundSurface</c> and exactly what is wanted for a reference
    /// figure.</para>
    ///
    /// <para><b>What it cannot tell you.</b> Whether the car is any fun. Every number below can be
    /// correct and the car still be miserable to drive, which is the distinction this project already
    /// records about the ambient audio it measured four ways and then deleted. The bench is what stops
    /// a retune from quietly breaking a car nobody happened to drive that week; the pass road is what
    /// says whether the retune was worth doing.</para>
    /// </summary>
    public static class HandlingBench
    {
        private const string PendingKey = "Horizon.HandlingBench.Pending";
        private const string ReturnSceneKey = "Horizon.HandlingBench.ReturnScene";

        [MenuItem("Tools/Horizon/Measure Handling", priority = 45)]
        public static void Measure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Horizon] Leave Play mode before running the handling bench.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Begin();
        }

        /// <summary>
        /// The same run, for <c>-executeMethod</c>.
        ///
        /// <para><b>Deliberately without <c>-quit</c>.</b> Play mode is entered by the editor loop on a
        /// later tick, so a batch run that asked to quit would exit before the first car had moved. The
        /// runner calls <see cref="EditorApplication.Exit"/> itself when it is done, which is what ends
        /// the process — and if a test hangs, the process hangs, which is the honest failure for a
        /// bench and better than a clean exit with no table.</para>
        ///
        /// <para>It does not prompt about unsaved scenes, because there is nobody to ask. That is safe
        /// only because it is never reached from the menu.</para>
        /// </summary>
        public static void MeasureBatch()
        {
            Begin();
        }

        private static void Begin()
        {
            // Remembered so the run can put the editor back where it found it. Play mode restores
            // whatever scene it started from, and that is about to be the empty one this makes.
            Scene open = SceneManager.GetActiveScene();
            SessionState.SetString(ReturnSceneKey, open.path);

            // A bare scene rather than the world: see the class remarks. It also means Play mode starts
            // without GameBootstrap, so nothing loads the world additively behind the bench's back.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            SessionState.SetBool(PendingKey, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (SessionState.GetBool(PendingKey, false))
                    {
                        // Cleared before the runner starts, so a run that throws does not arm itself
                        // again on the next Play — which would be a menu item nobody could turn off.
                        SessionState.SetBool(PendingKey, false);
                        new GameObject("HandlingBench").AddComponent<HandlingBenchRunner>();
                    }

                    break;

                case PlayModeStateChange.EnteredEditMode:
                    string path = SessionState.GetString(ReturnSceneKey, string.Empty);
                    if (!string.IsNullOrEmpty(path))
                    {
                        SessionState.EraseString(ReturnSceneKey);
                        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    }

                    break;
            }
        }
    }
}
