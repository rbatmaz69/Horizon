using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Horizon.Updates
{
    /// <summary>What the last check found. <see cref="ReleaseFeed"/> is in exactly one of these.</summary>
    public enum ReleaseFeedState
    {
        /// <summary>Nothing has been asked yet.</summary>
        Idle = 0,

        /// <summary>A request is in flight.</summary>
        Checking = 1,

        /// <summary>The newest release is the one already running.</summary>
        UpToDate = 2,

        /// <summary>There is a newer release with an APK attached.</summary>
        UpdateAvailable = 3,

        /// <summary>GitHub could not be reached, or answered with something unusable.</summary>
        Unavailable = 4,
    }

    /// <summary>
    /// Asks GitHub what the newest Horizon release is.
    ///
    /// <para><b>It only ever offers.</b> The download URL goes to <c>Application.OpenURL</c> and from
    /// there to the browser, which downloads the APK and hands it to Android's own installer. Doing the
    /// download in-process instead would mean <c>REQUEST_INSTALL_PACKAGES</c>, a FileProvider in a
    /// <c>.androidlib</c> manifest and an install intent built through <c>AndroidJavaObject</c> — a pile
    /// of moving parts whose failures are only observable after a twenty-minute IL2CPP build.</para>
    ///
    /// <para>The repository is public, so the API answers unauthenticated. That is rate limited to 60
    /// requests an hour per IP address; this asks once per app start, which is not close.</para>
    ///
    /// <para>Not a <c>MonoBehaviour</c>: it is a coroutine and four properties, and the component that
    /// runs it lives in <c>Horizon.Game</c> next to the labels it fills in. That keeps this assembly
    /// below <c>Horizon.Game</c> in the dependency order rather than beside it.</para>
    /// </summary>
    public sealed class ReleaseFeed
    {
        /// <summary>
        /// The <c>latest</c> endpoint rather than the list plus a filter: GitHub already excludes drafts
        /// and prereleases from it, which is precisely the definition of "the release a player should be
        /// offered".
        /// </summary>
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/rbatmaz69/Horizon/releases/latest";

        /// <summary>
        /// Long enough for a slow mobile connection, short enough that the start screen is not sitting on
        /// "Checking for updates..." while somebody is trying to read it.
        /// </summary>
        private const int TimeoutSeconds = 10;

        public ReleaseFeedState State { get; private set; } = ReleaseFeedState.Idle;

        /// <summary>The newest release's version, without the tag's <c>v</c>. Empty until known.</summary>
        public string Version { get; private set; } = string.Empty;

        /// <summary>The release notes, verbatim and possibly long. Empty unless there is an update.</summary>
        public string Notes { get; private set; } = string.Empty;

        /// <summary>Where the APK is. Empty unless there is an update.</summary>
        public string DownloadUrl { get; private set; } = string.Empty;

        /// <summary>How big that APK is, for a label that warns before a 140 MB download starts.</summary>
        public long DownloadBytes { get; private set; }

        /// <summary>
        /// Runs one check. Yields on the request, so the caller drives it with <c>StartCoroutine</c>.
        ///
        /// <para>Safe while the game is paused: <c>UnityWebRequest</c> progresses on real frames and does
        /// not care that <c>Time.timeScale</c> is zero — which it is for the whole time the start screen
        /// is up.</para>
        /// </summary>
        public IEnumerator Fetch(string runningVersion)
        {
            State = ReleaseFeedState.Checking;

            using (UnityWebRequest request = UnityWebRequest.Get(LatestReleaseUrl))
            {
                request.timeout = TimeoutSeconds;
                request.SetRequestHeader("Accept", "application/vnd.github+json");

                // GitHub rejects API requests that arrive without a User-Agent. Unity sends one of its
                // own, but what it contains is a platform detail rather than a promise, and a header
                // this request depends on is worth setting where it can be seen.
                request.SetRequestHeader("User-Agent", $"Horizon/{runningVersion}");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    // Log, not LogWarning. A phone in a tunnel is not a defect, and this runs on every
                    // single start.
                    Fail($"{request.result} ({request.responseCode}): {request.error}");
                    yield break;
                }

                Apply(request.downloadHandler.text, runningVersion);
            }
        }

        private void Apply(string json, string runningVersion)
        {
            ReleasePayload payload;

            try
            {
                payload = JsonUtility.FromJson<ReleasePayload>(json);
            }
            catch (Exception exception)
            {
                Fail($"the response did not parse: {exception.Message}");
                return;
            }

            if (!ReleaseVersion.TryParse(payload.tag_name, out int latest))
            {
                Fail($"tag '{payload.tag_name}' is not MAJOR.MINOR.PATCH.");
                return;
            }

            // An unparseable running version means a hand-edited bundleVersion — every release comes out
            // of release.sh, which validates the shape before it builds anything. Treating it as 0 offers
            // the newest release, which for a version nobody can place is the more useful of the two
            // wrong answers.
            if (!ReleaseVersion.TryParse(runningVersion, out int running))
            {
                running = 0;
            }

            Version = ReleaseVersion.WithoutPrefix(payload.tag_name);

            if (latest <= running)
            {
                Notes = string.Empty;
                DownloadUrl = string.Empty;
                DownloadBytes = 0L;
                State = ReleaseFeedState.UpToDate;
                return;
            }

            if (!TryFindApk(payload.assets, out string url, out long bytes))
            {
                // Not UpdateAvailable-without-a-link: a release with nothing installable attached is a
                // release the player can do nothing with, and a button that opens the browser at nothing
                // is worse than no button.
                Fail($"release {Version} has no .apk asset.");
                return;
            }

            Notes = payload.body ?? string.Empty;
            DownloadUrl = url;
            DownloadBytes = bytes;
            State = ReleaseFeedState.UpdateAvailable;
        }

        private static bool TryFindApk(ReleaseAsset[] assets, out string url, out long bytes)
        {
            url = string.Empty;
            bytes = 0L;

            if (assets == null)
            {
                return false;
            }

            for (int i = 0; i < assets.Length; i++)
            {
                string name = assets[i].name;
                if (string.IsNullOrEmpty(name)
                    || !name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(assets[i].browser_download_url))
                {
                    continue;
                }

                url = assets[i].browser_download_url;
                bytes = assets[i].size;
                return true;
            }

            return false;
        }

        private void Fail(string reason)
        {
            State = ReleaseFeedState.Unavailable;
            Version = string.Empty;
            Notes = string.Empty;
            DownloadUrl = string.Empty;
            DownloadBytes = 0L;

            Debug.Log($"[Horizon] Update check did not complete — {reason}");
        }

        // --- The wire format.
        //
        // The field names are GitHub's, so they are snake_case in the middle of C# code and there is no
        // way around that: JsonUtility matches on the name and has no attribute for renaming. The types
        // are structs with public fields for the same reason — that is the only shape JsonUtility reads.
        // Everything the response contains and this does not declare is ignored, which is most of it.

        [Serializable]
        private struct ReleasePayload
        {
            public string tag_name;
            public string body;
            public ReleaseAsset[] assets;
        }

        [Serializable]
        private struct ReleaseAsset
        {
            public string name;
            public string browser_download_url;
            public long size;
        }
    }
}
