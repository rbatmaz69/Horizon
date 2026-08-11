using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Configures the Android player and builds an APK.
    ///
    /// <para>In code rather than left in the Inspector for the same reason everything else here is: a
    /// setting nobody wrote down is a setting nobody can review, and this project had three of them
    /// quietly disagreeing with its own conventions. <c>CLAUDE.md</c> says the target is ARM64 and
    /// IL2CPP; <c>ProjectSettings.asset</c> said ARMv7 and Mono, which is not merely off-convention —
    /// recent phones have dropped 32-bit support entirely, so that build would not have installed on
    /// them at all.</para>
    ///
    /// <para><b>This does not rebuild the world.</b> It builds the scenes as they are on disk, because
    /// <c>Tools > Horizon > Rebuild Prototype Scene</c> replaces the open scene and would throw away
    /// unsaved work if a build triggered it silently. Run that first when world code has changed; the
    /// log below says which scenes went in so a stale one is at least visible.</para>
    /// </summary>
    public static class AndroidBuild
    {
        /// <summary>
        /// Where the APK lands. Outside <c>Assets/</c> so it never becomes an imported asset, and
        /// already covered by the <c>*.apk</c> rule in <c>.gitignore</c>.
        /// </summary>
        private const string OutputName = "Horizon.apk";

        /// <summary>
        /// The application id.
        ///
        /// Not <c>com.DefaultCompany.Horizon</c>, which is what Unity leaves behind and which cannot be
        /// published and will collide with every other project that never changed it.
        /// </summary>
        private const string ApplicationId = "com.batmaz.horizon";

        [MenuItem("Tools/Horizon/Configure Android Player", priority = 60)]
        public static void Configure()
        {
            // ARM64 alone. Not ARM64 *and* ARMv7: a fat APK is nearly twice the size to ship the half
            // of it that modern hardware ignores, and Unity cannot build ARM64 on Mono anyway — the
            // architecture and the scripting backend are one decision, not two.
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);

            // Landscape only. The default lets the phone rotate into portrait, which for a game steered
            // by tilting the phone is not a preference — a portrait tilt axis is a different axis, and
            // the car would steer by being pitched rather than rolled.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            // An APK to sideload or push over USB, not an App Store bundle.
            EditorUserBuildSettings.buildAppBundle = false;

            AssetDatabase.SaveAssets();

            Debug.Log($"[Horizon] Android player: {ApplicationId}, ARM64, IL2CPP, landscape, "
                      + $"min SDK {PlayerSettings.Android.minSdkVersion}. APK, not AAB.");
        }

        [MenuItem("Tools/Horizon/Build Android APK", priority = 61)]
        public static void BuildApk()
        {
            Build(false);
        }

        /// <summary>
        /// Builds and installs onto whatever is plugged in.
        ///
        /// Unity uses the adb that ships with its own Android module, so nothing needs to be on PATH —
        /// the phone only needs USB debugging turned on and the connection authorised on the handset.
        /// </summary>
        [MenuItem("Tools/Horizon/Build Android APK and Run", priority = 62)]
        public static void BuildAndRun()
        {
            Build(true);
        }

        private static void Build(bool run)
        {
            Configure();

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Horizon] No scenes are enabled in Build Settings, so the APK would "
                               + "start on an empty screen. Bootstrap must be first — it owns the input "
                               + "router and loads the world additively.");
                return;
            }

            // Switching costs a re-import of every asset the first time and nothing afterwards. Doing it
            // here rather than asking the user to means the menu item works from a cold project.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("[Horizon] Switching the active build target to Android. The first switch "
                          + "re-imports the project's assets and takes a while.");

                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }

            string directory = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(directory, OutputName);

            Debug.Log($"[Horizon] Building {scenes.Length} scene(s) to {path}: {string.Join(", ", scenes)}");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = run ? BuildOptions.AutoRunPlayer : BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[Horizon] Android build {summary.result} with {summary.totalErrors} "
                               + "error(s). The first IL2CPP build is also the one most likely to fail "
                               + "on a missing NDK or JDK — Unity Hub installs both with the Android "
                               + "Build Support module.");
                return;
            }

            Debug.Log($"[Horizon] Android build succeeded: {summary.totalSize / (1024 * 1024)} MB in "
                      + $"{summary.totalTime.TotalMinutes:0.0} minutes, at {path}.");
        }

        /// <summary>The enabled scenes from Build Settings, in order. Bootstrap has to be first.</summary>
        private static string[] EnabledScenes()
        {
            EditorBuildSettingsScene[] all = EditorBuildSettings.scenes;
            var enabled = new System.Collections.Generic.List<string>(all.Length);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].enabled)
                {
                    enabled.Add(all[i].path);
                }
            }

            return enabled.ToArray();
        }
    }
}
