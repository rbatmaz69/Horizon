using System.Collections;
using Horizon.Updates;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// Tells the player which version they are running and whether there is a newer one.
    ///
    /// <para>Horizon is sideloaded, so nothing else is ever going to mention that a release happened.
    /// Without this the only way to find out is to open the Releases page in a browser and compare two
    /// numbers by hand, which is a thing nobody does.</para>
    ///
    /// <para><b>It reads as a row on the start screen and a page behind it.</b> The row is the whole
    /// feature for anyone who is up to date — one line, no interruption, no dialog over a game somebody
    /// opened to drive. The page exists for the case where there <i>is</i> something, and is where the
    /// release notes and the download button live.</para>
    ///
    /// <para>On the canvas object beside <c>StartScreen</c> and <c>PauseMenu</c>, so it is inside the
    /// bootstrap's <c>DontDestroyOnLoad</c> and survives the additive world load.</para>
    /// </summary>
    public sealed class UpdateScreen : MonoBehaviour
    {
        [Tooltip("The value on the start screen's Update row.")]
        [SerializeField] private Text summaryLabel;

        [Tooltip("The sentence at the top of the update page.")]
        [SerializeField] private Text statusLabel;

        [Tooltip("The release notes, on the update page.")]
        [SerializeField] private Text notesLabel;

        [Tooltip("Hidden unless there is something to download.")]
        [SerializeField] private Button downloadButton;

        [Tooltip("The download button's own caption, which carries the version and the size.")]
        [SerializeField] private Text downloadLabel;

        /// <summary>
        /// How much of the release notes the page shows.
        ///
        /// <para>The page is a stack with a content-size fitter, so its height is whatever its rows come
        /// to — an unbounded body would push Back off the bottom of a phone. Anyone who wants the full
        /// text is one tap from it on GitHub anyway.</para>
        /// </summary>
        private const int NotesLimit = 300;

        /// <summary>
        /// The accent the rest of the menu uses for the one thing on a screen that is the point — Drive
        /// on the start page, Download on this one. Duplicated as a literal because the palette lives in
        /// <c>TouchUiSetup</c>, which is Editor-only.
        /// </summary>
        private static readonly Color AvailableTint = new Color(0.98f, 0.62f, 0.35f, 1f);

        private readonly ReleaseFeed feed = new ReleaseFeed();

        private IEnumerator Start()
        {
            // Rendered once before the request so the row says something sensible while it is in flight,
            // and again after it whatever the outcome was.
            Render();

            yield return feed.Fetch(Application.version);

            Render();
        }

        /// <summary>
        /// Opens the APK in the browser, which downloads it and hands it to Android's installer.
        ///
        /// <para>Bound by name from <c>MenuUiSetup</c>, so renaming it breaks the scene build rather than
        /// the button.</para>
        /// </summary>
        public void Download()
        {
            if (feed.State != ReleaseFeedState.UpdateAvailable || string.IsNullOrEmpty(feed.DownloadUrl))
            {
                return;
            }

            Application.OpenURL(feed.DownloadUrl);
        }

        private void Render()
        {
            string running = Application.version;

            switch (feed.State)
            {
                case ReleaseFeedState.UpdateAvailable:
                    // Short, because this is a half-width button on the front page and the sentence that
                    // explains it is one tap away. The accent colour is what actually carries the news.
                    SetText(summaryLabel, $"Update: {feed.Version}");
                    Tint(summaryLabel, AvailableTint);
                    SetText(statusLabel,
                        $"Version {feed.Version} is available.\nYou are running {running}.");
                    SetNotes(Shorten(feed.Notes, NotesLimit));
                    SetText(downloadLabel, $"Download {feed.Version} ({Megabytes(feed.DownloadBytes)} MB)");
                    Show(downloadButton, true);
                    break;

                case ReleaseFeedState.UpToDate:
                    SetText(summaryLabel, $"Version {running}");
                    Tint(summaryLabel, Color.white);
                    SetText(statusLabel, $"Version {running} is the newest release.");
                    SetNotes(string.Empty);
                    Show(downloadButton, false);
                    break;

                case ReleaseFeedState.Unavailable:
                    SetText(summaryLabel, $"Version {running}");
                    Tint(summaryLabel, Color.white);
                    SetText(statusLabel, $"You are running {running}.\nGitHub could not be reached.");
                    SetNotes(string.Empty);
                    Show(downloadButton, false);
                    break;

                default:
                    SetText(summaryLabel, $"Version {running}");
                    Tint(summaryLabel, Color.white);
                    SetText(statusLabel, "Checking for updates...");
                    SetNotes(string.Empty);
                    Show(downloadButton, false);
                    break;
            }
        }

        /// <summary>
        /// Cuts the notes at a word boundary rather than mid-word, which is the difference between a
        /// summary and a text that looks corrupted.
        /// </summary>
        private static string Shorten(string text, int limit)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string flattened = text.Replace("\r\n", "\n").Trim();
            if (flattened.Length <= limit)
            {
                return flattened;
            }

            int cut = flattened.LastIndexOf(' ', limit);
            if (cut < limit / 2)
            {
                cut = limit;
            }

            return flattened.Substring(0, cut).TrimEnd() + "...";
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024f * 1024f)).ToString("0");
        }

        /// <summary>
        /// The notes row, switched off when there are none.
        ///
        /// <para>Emptying the text would leave its 160 units of reserved height behind as a hole in the
        /// middle of the page — the layout group sizes rows from their <c>LayoutElement</c>, not from
        /// what is written in them. Deactivating the object takes the row out of the layout entirely.</para>
        /// </summary>
        private void SetNotes(string value)
        {
            if (notesLabel == null)
            {
                return;
            }

            notesLabel.text = value;
            notesLabel.gameObject.SetActive(!string.IsNullOrEmpty(value));
        }

        private static void Tint(Text label, Color colour)
        {
            if (label != null)
            {
                label.color = colour;
            }
        }

        private static void SetText(Text label, string value)
        {
            if (label != null)
            {
                label.text = value;
            }
        }

        private static void Show(Button button, bool visible)
        {
            if (button != null)
            {
                button.gameObject.SetActive(visible);
            }
        }
    }
}
