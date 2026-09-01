using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Shows one menu page at a time, and remembers where you came from.
    ///
    /// <para><b>Why this is a component and not four lines in <see cref="PauseMenu"/>.</b> It used to be
    /// four lines: every open-a-panel method also turned off the other two by name. That works at three
    /// panels and stops working at eight, because the number of clauses is the square of the number of
    /// pages and every one is a place to forget one. Worse, forgetting is not a crash — it draws two
    /// translucent panels through each other with both sets of buttons live, which is exactly what this
    /// menu did once before, and the only symptom is that nothing can be read.</para>
    ///
    /// <para>All pages are siblings at the same depth on one canvas, so "show" genuinely means "hide
    /// everything else". There is no stacking and no z-order to get wrong.</para>
    ///
    /// <para><b>Back is one level deep on purpose.</b> Every page here is reached from one other page —
    /// the start screen, or the pause menu — so a full history stack would be a stack that never held
    /// more than one entry. One field says so honestly.</para>
    /// </summary>
    public sealed class MenuPanels : MonoBehaviour
    {
        /// <summary>
        /// The panels, indexed by <see cref="MenuPage"/>. Baked by <c>MenuUiSetup</c>, which asserts the
        /// order as it fills this in.
        /// </summary>
        [SerializeField] private GameObject[] panels = new GameObject[0];

        /// <summary>Which page is up, or −1 for none. Not serialized: the menu starts closed.</summary>
        public int Current { get; private set; } = -1;

        private int previous = -1;

        /// <summary>
        /// Where <see cref="Back"/> goes when there is nowhere to go back to, or −1 to close instead.
        ///
        /// <para><b>This exists because not having it froze the game.</b> Back used to close the menu
        /// outright whenever it was pressed on the first page of a session — which on a fresh install was
        /// the garage, the page the start screen opened on. Closing the menu does not unpause anything:
        /// <c>Time.timeScale</c> stayed at zero and the pause button is hidden while the start screen is
        /// up, so the player was left looking at a frozen world with no control on screen and no way
        /// back. Set by whoever owns the menu at the time — the start screen makes it the front page, the
        /// pause menu makes it PAUSED — so Back always has somewhere to land.</para>
        /// </summary>
        private int home = -1;

        /// <summary>Sets the page <see cref="Back"/> falls back to. −1 means close instead.</summary>
        public void SetHome(int page)
        {
            home = page;
        }

        /// <summary>Convenience for callers that hold the enum.</summary>
        public void SetHome(MenuPage page) => SetHome((int)page);

        /// <summary>True while any page is showing.</summary>
        public bool IsOpen => Current >= 0;

        private void Awake()
        {
            // Whatever the scene was saved with, the menu starts closed and the panels agree with
            // Current. A panel left active in the saved scene would otherwise be a page nothing can
            // close, because Back has never been told it is open.
            Hide();
        }

        /// <summary>Shows one page and hides the rest. Wired to buttons with the index baked in.</summary>
        public void Show(int page)
        {
            if (panels == null || page < 0 || page >= panels.Length)
            {
                return;
            }

            // Only remember a real page as the way back. Show(x) from closed should leave Back with
            // nothing to return to, or the first Back after opening the menu reopens whatever page
            // happened to be up last time.
            if (Current >= 0 && Current != page)
            {
                previous = Current;
            }

            Current = page;

            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null)
                {
                    panels[i].SetActive(i == page);
                }
            }
        }

        /// <summary>Convenience for callers that hold the enum rather than an int.</summary>
        public void Show(MenuPage page) => Show((int)page);

        /// <summary>
        /// Back to whichever page opened this one, or to <see cref="SetHome"/> if this page was the
        /// first of the session. Only closes when there is no home either — see the note on the field
        /// for what that used to cost.
        /// </summary>
        public void Back()
        {
            int target = previous >= 0 ? previous : home;

            if (target < 0)
            {
                Hide();
                return;
            }

            Show(target);

            // Cleared <b>after</b> the show, not before, and that ordering is the whole of it.
            // <see cref="Show"/> records the page it is leaving as the way back, so clearing first left
            // the page just departed sitting in <c>previous</c> — and Back from the page underneath
            // returned to the one it had come from. Two pages ping-ponging, with no way out but the
            // pause button.
            //
            // It could not happen while every page was reached from the start screen or the pause menu
            // and nothing was ever two deep. The paint page is, now that the garage opens it.
            previous = -1;
        }

        /// <summary>Closes the menu entirely.</summary>
        public void Hide()
        {
            Current = -1;
            previous = -1;

            if (panels == null)
            {
                return;
            }

            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null)
                {
                    panels[i].SetActive(false);
                }
            }
        }
    }
}
