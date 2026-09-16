using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The two sounds the menu makes.
    ///
    /// <para><b>There were none.</b> <c>Assets/_Project/Audio/</c> is empty and the only
    /// <c>AudioSource</c>s in this project are the eleven on the car, so every one of the sixty-odd
    /// buttons in this menu was silent — on a game played by touching glass, where a tap gives no
    /// travel, no click and no resistance. A control that answers nothing at all reads as one that did
    /// not register.</para>
    ///
    /// <para><b>Synthesised, like everything else audible here.</b> Three hundred lines of arithmetic
    /// against a few hundred kilobytes of APK, with no licence attached and nothing to ship. The rule
    /// about loop points does not apply: these are one-shots and a one-shot is over before it can meet
    /// its own tail. What does apply is that each has to <i>end</i> at silence, or
    /// <c>PlayOneShot</c> cuts it into a click of its own.</para>
    ///
    /// <para><b>Two sounds and not three, and that is an argument rather than a shortcut.</b> The
    /// obvious third is a confirmation on Drive and Resume — and those already have one, which is that
    /// the screen cuts. <see cref="ScreenFade"/> is the answer to "did that commit to something", so a
    /// sound saying the same thing is the second opinion this project keeps refusing. What a sound can
    /// say that the picture does not is <i>which way you moved</i>, so: forward, and back.</para>
    ///
    /// <para><b>They are told apart by register and by shape, never by level.</b> That is the rule this
    /// project has now paid for four times — the scrape against the rumble, the wind against the water,
    /// gravel against grass, and the throttle glyph against the brake. Two sounds separated only by
    /// volume are one sound at two distances. Forward is a short bright tick with a rising tail; back
    /// is lower, softer-edged and falls.</para>
    ///
    /// <para><b>On the canvas, not on the car.</b> <c>EngineAudio</c> re-synthesises its clips whenever
    /// the player changes car, because a diesel and a turbocharged six are different notes. A button
    /// does not care what is in the garage, so putting these there would mean rebuilding two clips that
    /// cannot change every time somebody opened it.</para>
    /// </summary>
    public sealed class UiAudio : MonoBehaviour
    {
        /// <summary>Matches the rest of the synthesis in this project.</summary>
        private const int SampleRate = 44100;

        [Tooltip("The source the taps go through. 2D, so it does not matter where the listener is.")]
        [SerializeField] private AudioSource source;

        [Tooltip("How loud a tap is against the engine. Low: this is punctuation, not an event.")]
        [SerializeField] private float level = 0.35f;

        private AudioClip forward;
        private AudioClip back;

        private void Awake()
        {
            if (source == null)
            {
                source = GetComponent<AudioSource>();
            }

            forward = BuildTickClip();
            back = BuildBackClip();
        }

        /// <summary>Anything that goes forward: a page, a choice, a button that does a thing.</summary>
        public void Tap()
        {
            Play(forward);
        }

        /// <summary>Anything that goes back. Wired from the one place that decides a button does.</summary>
        public void Back()
        {
            Play(back);
        }

        private void Play(AudioClip clip)
        {
            if (source == null || clip == null)
            {
                return;
            }

            source.PlayOneShot(clip, level);
        }

        /// <summary>
        /// Forward: a short wooden tick.
        ///
        /// <para>Two decaying partials a fifth apart rather than one, because a single sine with an
        /// envelope on it is a beep — what makes a tap sound like something being struck is that the
        /// upper partial dies several times faster than the lower one, which is the same trick the
        /// impact thud uses one register down. The noise burst in front of them is the contact itself;
        /// without it the tick starts rather than arrives.</para>
        ///
        /// <para>Twenty-five milliseconds. Long enough to have a pitch and short enough that a fast
        /// double-tap is two sounds rather than a warble.</para>
        /// </summary>
        private static AudioClip BuildTickClip()
        {
            const float seconds = 0.045f;
            int count = Mathf.RoundToInt(SampleRate * seconds);
            var samples = new float[count];

            uint state = 0x6C078965u;
            float peak = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                float time = i / (float)SampleRate;

                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;

                float white = (state / (float)uint.MaxValue) * 2f - 1f;

                float contact = white * Mathf.Exp(-t * 90f) * 0.45f;
                float low = Mathf.Sin(2f * Mathf.PI * 880f * time) * Mathf.Exp(-t * 26f);
                float high = Mathf.Sin(2f * Mathf.PI * 1320f * time) * Mathf.Exp(-t * 62f) * 0.5f;

                // Not a step. A true one is a click in its own right, which is the whole thing this is
                // trying not to sound like.
                float value = (contact + low + high) * Mathf.Min(1f, i / (SampleRate * 0.0008f));

                samples[i] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }

            return Finish(samples, peak, "UiTick");
        }

        /// <summary>
        /// Back: the same strike, lower and blunter, with the upper partial <i>under</i> the lower.
        ///
        /// <para>Inverting the interval is what makes it read as a retreat, and it is the only thing
        /// that does — dropping the pitch alone gives a tap on a bigger button. There is no noise burst
        /// either: a contact transient is what says something was struck, and going back is not a
        /// commitment to anything.</para>
        /// </summary>
        private static AudioClip BuildBackClip()
        {
            const float seconds = 0.07f;
            int count = Mathf.RoundToInt(SampleRate * seconds);
            var samples = new float[count];

            float peak = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                float time = i / (float)SampleRate;

                float low = Mathf.Sin(2f * Mathf.PI * 520f * time) * Mathf.Exp(-t * 16f);
                float under = Mathf.Sin(2f * Mathf.PI * 347f * time) * Mathf.Exp(-t * 30f) * 0.6f;

                float value = (low + under) * Mathf.Min(1f, i / (SampleRate * 0.004f));

                samples[i] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }

            return Finish(samples, peak, "UiBack");
        }

        /// <summary>
        /// Normalises and hands back a clip.
        ///
        /// <para>To 0.9 rather than to 1, which is what every other generator here does: a clip that
        /// touches full scale has nowhere to go when two of them overlap, and two of these overlap every
        /// time somebody taps twice quickly.</para>
        /// </summary>
        private static AudioClip Finish(float[] samples, float peak, string name)
        {
            if (peak > 0.0001f)
            {
                float scale = 0.9f / peak;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= scale;
                }
            }

            AudioClip clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
