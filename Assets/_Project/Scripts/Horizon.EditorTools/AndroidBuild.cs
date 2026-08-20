using System;
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

            // The update check talks to the GitHub releases API, so the manifest needs INTERNET.
            //
            // Set rather than left on Unity's "Auto", which infers the permission from what it can see
            // surviving IL2CPP's code stripping. When that inference misses, the failure is a transport
            // error on the phone against a check that works perfectly in the editor — a whole release
            // cycle to notice and another to fix.
            PlayerSettings.Android.forceInternetPermission = true;

            // An APK to sideload or push over USB, not an App Store bundle.
            EditorUserBuildSettings.buildAppBundle = false;

            AssetDatabase.SaveAssets();

            Debug.Log($"[Horizon] Android player: {ApplicationId}, ARM64, IL2CPP, landscape, "
                      + $"min SDK {PlayerSettings.Android.minSdkVersion}, INTERNET. APK, not AAB.");
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

        /// <summary>
        /// Builds a signed, versioned APK and quits, for <c>Tools/release.sh</c> to upload.
        ///
        /// <para>Separate from <see cref="BuildApk"/> because those two want opposite things from a
        /// failure: the menu item leaves the editor open with the error in the console, this one has to
        /// hand a non-zero exit code back to the shell. Under <c>-quit -executeMethod</c> Unity exits 0
        /// unless somebody calls <see cref="EditorApplication.Exit"/>, so a build that failed would
        /// otherwise sail on into <c>git tag</c> and <c>gh release create</c>.</para>
        ///
        /// <para>Configured from the environment rather than from arguments, because two of the four
        /// values are keystore passwords and process arguments are readable by every other process on
        /// the machine.</para>
        /// </summary>
        public static void BuildRelease()
        {
            int exitCode = 1;

            try
            {
                exitCode = RunRelease() ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Horizon] Release build threw: {exception}");
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static bool RunRelease()
        {
            string version = Environment.GetEnvironmentVariable("HORIZON_VERSION");
            if (!TryVersionCode(version, out int versionCode))
            {
                Debug.LogError($"[Horizon] HORIZON_VERSION is '{version}'. Expected MAJOR.MINOR.PATCH "
                               + "with minor and patch below 100.");
                return false;
            }

            string keystore = Environment.GetEnvironmentVariable("HORIZON_KEYSTORE_PATH");
            if (string.IsNullOrEmpty(keystore))
            {
                keystore = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".horizon", "horizon-release.keystore");
            }

            string alias = Environment.GetEnvironmentVariable("HORIZON_KEYALIAS_NAME");
            if (string.IsNullOrEmpty(alias))
            {
                alias = "horizon";
            }

            string keystorePass = Environment.GetEnvironmentVariable("HORIZON_KEYSTORE_PASS");
            string aliasPass = Environment.GetEnvironmentVariable("HORIZON_KEYALIAS_PASS");
            if (string.IsNullOrEmpty(aliasPass))
            {
                aliasPass = keystorePass;
            }

            // Checked here rather than left to Unity, which falls back to the debug keystore when the
            // release one cannot be opened and still reports a successful build. That APK installs
            // perfectly well and only reveals itself months later, when the next release refuses to
            // install over it because the signature changed.
            if (!File.Exists(keystore))
            {
                Debug.LogError($"[Horizon] No keystore at {keystore}. Generate one with keytool, or "
                               + "point HORIZON_KEYSTORE_PATH at it.");
                return false;
            }

            if (string.IsNullOrEmpty(keystorePass))
            {
                Debug.LogError("[Horizon] HORIZON_KEYSTORE_PASS is empty.");
                return false;
            }

            // Before Configure(), whose SaveAssets() is what writes the version into
            // ProjectSettings.asset so the repo records what each tag shipped.
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = versionCode;

            Configure();

            // Captured so the finally below can put them back exactly as they were. Clearing them to
            // empty instead looks equivalent and is not: Unity normalises an empty keystore name to
            // the literal '{inproject}: ', which shows up as a modified ProjectSettings.asset after
            // every single release.
            bool previousUseCustomKeystore = PlayerSettings.Android.useCustomKeystore;
            string previousKeystoreName = PlayerSettings.Android.keystoreName;
            string previousKeyaliasName = PlayerSettings.Android.keyaliasName;

            try
            {
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = keystore;
                PlayerSettings.Android.keystorePass = keystorePass;
                PlayerSettings.Android.keyaliasName = alias;
                PlayerSettings.Android.keyaliasPass = aliasPass;

                Debug.Log($"[Horizon] Release {version} (versionCode {versionCode}), signed with "
                          + $"alias '{alias}' from {keystore}.");

                return Build(false);
            }
            finally
            {
                // Restored, and deliberately without a SaveAssets() afterwards: the keystore path
                // must not end up in ProjectSettings.asset, which is committed. The passwords are
                // held in memory only — Unity has no field for them in that file — but they are
                // cleared here anyway so they do not outlive the build inside the editor process.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
                PlayerSettings.Android.keystoreName = previousKeystoreName;
                PlayerSettings.Android.keyaliasName = previousKeyaliasName;
                PlayerSettings.Android.useCustomKeystore = previousUseCustomKeystore;
            }
        }

        /// <summary>
        /// Turns <c>0.2.0</c> into <c>200</c>.
        ///
        /// Derived from the version rather than counted up separately, because Android only compares
        /// the code when deciding whether an install is an upgrade — a counter that drifts away from
        /// the tag is a release that silently refuses to install over its predecessor.
        /// </summary>
        private static bool TryVersionCode(string version, out int versionCode)
        {
            versionCode = 0;

            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            string[] parts = version.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch))
            {
                return false;
            }

            if (major < 0 || minor < 0 || minor > 99 || patch < 0 || patch > 99)
            {
                return false;
            }

            versionCode = (major * 10000) + (minor * 100) + patch;

            return versionCode > 0;
        }

        private static bool Build(bool run)
        {
            Configure();

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Horizon] No scenes are enabled in Build Settings, so the APK would "
                               + "start on an empty screen. Bootstrap must be first — it owns the input "
                               + "router and loads the world additively.");
                return false;
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
                return false;
            }

            Debug.Log($"[Horizon] Android build succeeded: {summary.totalSize / (1024 * 1024)} MB in "
                      + $"{summary.totalTime.TotalMinutes:0.0} minutes, at {path}.");

            return true;
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
