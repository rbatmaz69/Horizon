using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The sheet that covers the screen while the car is being put somewhere else.
    ///
    /// <para><b>Nothing in this project faded anything.</b> Pressing Drive, choosing a different start
    /// place and pressing Respawn all moved the car by kilometres and snapped the camera after it in the
    /// same frame — three hard cuts, plus a fourth at launch, where <c>Bootstrap</c> has no camera of
    /// its own and the world arrives from an additive load. A cut is what a game does when it has not
    /// been finished, and it is the cheapest of all of them to fix.</para>
    ///
    /// <para><b>It covers rather than gates, and that is a deliberate limit on what this class may
    /// do.</b> The obvious build is a coroutine that fades out, moves the car, fades back in — and it
    /// cannot be had here without rewriting the callers: <c>PauseMenu.MoveTo</c> is synchronous and
    /// <c>StartScreen.ApplyPlace</c> calls <c>ChaseCamera.SnapToTarget</c> on the line after it, so a
    /// deferred teleport would snap the rig to a car that has not moved yet. So the move happens exactly
    /// when it always did and the sheet goes opaque in the same frame, which hides the identical
    /// thing. What the player sees is a cut to black and a reveal, which is what a cut is supposed to
    /// look like.</para>
    ///
    /// <para><b>Unscaled time throughout.</b> All of this happens with <c>timeScale</c> at zero — that
    /// is what a pause menu is — and a ramp integrated against <c>Time.deltaTime</c> there moves by
    /// exactly nothing. Same rule <c>PhotoMode</c> and <c>NetSession</c> already follow.</para>
    ///
    /// <para><b>Not black.</b> Every surface in this world is warm, and a neutral cut reads as the
    /// application having gone away rather than as the game moving you. The colour is the menu's own
    /// panel tint, set by the setup tool — this class does not know it, for the same reason nothing here
    /// keeps a second copy of a number.</para>
    ///
    /// <para><b>The sheet is switched off when it is clear.</b> A full-screen <c>Image</c> at alpha zero
    /// is still a full-screen transparent quad in the canvas mesh, and on a tile GPU that is a screen of
    /// overdraw for something nobody can see. It also must never take a raycast: an invisible sheet over
    /// the whole canvas that does is a menu where nothing can be pressed, which is the one way this
    /// could be worse than the hard cuts it replaces.</para>
    /// </summary>
    public sealed class ScreenFade : MonoBehaviour
    {
        [Tooltip("The full-screen sheet. Built and coloured by the setup tool.")]
        [SerializeField] private Image sheet;

        [Tooltip("How long the sheet takes to clear, seconds. Long enough to read as a reveal and short "
               + "enough that nobody waits for it.")]
        [SerializeField] private float revealSeconds = 0.34f;

        [Tooltip("How long it stays fully opaque before clearing. Covers the physics step the teleport "
               + "lands on, so the first thing revealed is a car that has already settled.")]
        [SerializeField] private float holdSeconds = 0.08f;

        /// <summary>True while <see cref="Cover"/> is holding the sheet down until told otherwise.</summary>
        private bool held;

        private float opacity;
        private float remaining;

        /// <summary>What the sheet is showing, for the debug overlay and the preview tool.</summary>
        public float Opacity => opacity;

        private void Awake()
        {
            // Covered from the first frame there is. GameBootstrap loads the world additively and
            // Bootstrap carries no camera, so until that finishes there is nothing to render at all —
            // the alternative to a sheet here is whatever the device last had in its buffer.
            Cover();
        }

        /// <summary>Covers the screen and holds it until <see cref="Reveal"/>.</summary>
        public void Cover()
        {
            held = true;
            opacity = 1f;
            remaining = 0f;
            Push();
        }

        /// <summary>Covers the screen and clears it again on its own. The transition.</summary>
        public void Cut()
        {
            held = false;
            opacity = 1f;
            remaining = holdSeconds;
            Push();
        }

        /// <summary>Clears whatever is there, from wherever it is.</summary>
        public void Reveal()
        {
            held = false;
            remaining = 0f;
        }

        private void Update()
        {
            if (held || opacity <= 0f)
            {
                return;
            }

            if (remaining > 0f)
            {
                remaining -= Time.unscaledDeltaTime;
                return;
            }

            opacity -= Time.unscaledDeltaTime / Mathf.Max(0.01f, revealSeconds);
            if (opacity < 0f)
            {
                opacity = 0f;
            }

            Push();
        }

        /// <summary>
        /// Writes the current opacity onto the sheet.
        ///
        /// <para>The colour is read back from the sheet and only its alpha replaced, so the tint stays
        /// whatever the setup tool painted. Writing a whole colour here would be this class holding an
        /// opinion about what a fade looks like, and then two files would have one.</para>
        /// </summary>
        private void Push()
        {
            if (sheet == null)
            {
                return;
            }

            Color colour = sheet.color;
            colour.a = opacity;
            sheet.color = colour;

            bool visible = opacity > 0f;
            if (sheet.enabled != visible)
            {
                sheet.enabled = visible;
            }
        }
    }
}
