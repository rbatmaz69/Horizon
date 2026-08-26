using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Android;
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
        /// Where a <b>signed release</b> APK lands. Outside <c>Assets/</c> so it never becomes an
        /// imported asset, and already covered by the <c>*.apk</c> rule in <c>.gitignore</c>.
        /// </summary>
        private const string OutputName = "Horizon.apk";

        /// <summary>
        /// Where an unsigned one lands, and <b>it is a different file on purpose.</b>
        ///
        /// <para>Both build paths used to write <c>Horizon.apk</c>. The menu items do not set a
        /// keystore, so Unity signs what they produce with its own debug key — which is correct for
        /// something going straight onto a phone over USB, and catastrophic if it is the file that gets
        /// uploaded. That is what happened to 0.8.0: it shipped signed <c>CN=Android Debug</c>, so it
        /// would not install over 0.7.0, and 0.8.1 signed properly again would not install over
        /// <i>it</i>. Two releases, two forced uninstalls, and the APK is not something the build log
        /// can tell you about after the fact.</para>
        ///
        /// <para>Different names mean the wrong file cannot be picked up by accident. The check in
        /// <see cref="VerifyRelease"/> is what catches it if one ever is.</para>
        /// </summary>
        private const string DebugOutputName = "Horizon-debug.apk";

        /// <summary>
        /// SHA-256 of the certificate every release since 0.1.0 but 0.8.0 has been signed with.
        ///
        /// <para>Not a secret — a signing certificate is public and travels inside every APK; what is
        /// secret is the private key in the keystore. It is pinned here because Android decides whether
        /// an update may install by comparing exactly this, so it is the one value that has to be the
        /// same across every release forever. Changing the key means every player uninstalls, so
        /// changing this constant is a deliberate act, not a fix for a failing build.</para>
        /// </summary>
        private const string ReleaseCertificateSha256 =
            "9bae1b688a6029afc7287acf558662f98a3563d27f8d05db104291f87862846b";

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
            Build(false, DebugOutputName);
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
            Build(true, DebugOutputName);
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

                return Build(false, OutputName)
                       && VerifyRelease(Path.Combine(
                           Directory.GetParent(Application.dataPath).FullName, OutputName));
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
        /// Reads the certificate back out of the APK that was just built and refuses the release
        /// unless it is the one every other release carries.
        ///
        /// <para><b>Why this cannot be left to the build.</b> Unity falls back to its debug keystore
        /// whenever the release one cannot be opened, and reports a perfectly successful build when it
        /// does. The APK installs, runs and looks right; the only symptom is months later, when the
        /// next release refuses to install over it. The keystore-exists check above was written against
        /// exactly that risk and does not cover it, because it tests the input rather than the output —
        /// and 0.8.0 went out debug-signed anyway, by a route neither of them was watching.</para>
        ///
        /// <para>A failure to <i>run the check</i> fails the release too. An absent apksigner and a
        /// correctly signed APK look identical from here, and this project's rule about that is already
        /// written down against the driveable-corridor canary: no answer is not a clean answer.</para>
        /// </summary>
        private static bool VerifyRelease(string apkPath)
        {
            string apksigner = FindApksigner();

            if (apksigner == null)
            {
                Debug.LogError("[Horizon] No apksigner under the Android SDK, so the signature of the "
                               + "APK just built cannot be read. That is not a signed release, it is no "
                               + "answer — see the remarks on this method.");
                return false;
            }

            if (!TryRun(apksigner, $"verify --print-certs \"{apkPath}\"", out string output))
            {
                Debug.LogError($"[Horizon] apksigner could not verify {apkPath}:\n{output}");
                return false;
            }

            var match = Regex.Match(
                output, @"Signer #1 certificate SHA-256 digest:\s*([0-9a-fA-F]{64})");

            if (!match.Success)
            {
                Debug.LogError($"[Horizon] apksigner printed no certificate digest for {apkPath}:\n"
                               + output);
                return false;
            }

            string digest = match.Groups[1].Value;

            if (!string.Equals(digest, ReleaseCertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(
                    $"[Horizon] The APK is signed with certificate {digest}, not the release "
                    + $"certificate {ReleaseCertificateSha256}. Android will refuse to install this "
                    + "over any previous release and every player will have to uninstall the game "
                    + "first. Do not upload it.\n" + output);

                return false;
            }

            Debug.Log($"[Horizon] Signature verified: {digest}. This will install over the previous "
                      + "release.");

            return true;
        }

        /// <summary>
        /// The newest <c>apksigner</c> under the Android SDK Unity is configured to use.
        ///
        /// <para>Found rather than configured, and the SDK root comes from Unity's own setting rather
        /// than from a guess at the install layout, which differs on every platform.</para>
        /// </summary>
        private static string FindApksigner()
        {
            string sdk = AndroidExternalToolsSettings.sdkRootPath;

            if (string.IsNullOrEmpty(sdk))
            {
                return null;
            }

            string buildTools = Path.Combine(sdk, "build-tools");

            if (!Directory.Exists(buildTools))
            {
                return null;
            }

            string name = Application.platform == RuntimePlatform.WindowsEditor
                ? "apksigner.bat"
                : "apksigner";

            string[] versions = Directory.GetDirectories(buildTools);
            Array.Sort(versions, StringComparer.Ordinal);

            for (int i = versions.Length - 1; i >= 0; i--)
            {
                string candidate = Path.Combine(versions[i], name);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Runs a tool and collects everything it said. Both streams, because apksigner writes its
        /// warnings to stderr and a warning about a missing signature scheme is part of the answer.
        /// </summary>
        private static bool TryRun(string executable, string arguments, out string output)
        {
            var info = new System.Diagnostics.ProcessStartInfo(executable, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // apksigner is a shell script that finds its own Java, and Unity's Android module ships a
            // JDK the editor already knows the path to. Left to the environment it picks up whatever
            // java is on PATH, which on a machine without one is a build that fails at the last step.
            string jdk = AndroidExternalToolsSettings.jdkRootPath;

            if (!string.IsNullOrEmpty(jdk))
            {
                info.EnvironmentVariables["JAVA_HOME"] = jdk;
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(info);

                if (process == null)
                {
                    output = "the process did not start";
                    return false;
                }

                string standard = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                output = standard + error;
                return process.ExitCode == 0;
            }
            catch (Exception exception)
            {
                output = exception.ToString();
                return false;
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

        private static bool Build(bool run, string outputName)
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
            string path = Path.Combine(directory, outputName);

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
