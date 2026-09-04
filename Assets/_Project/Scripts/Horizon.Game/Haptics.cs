using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Something that can shake the phone.
    ///
    /// <para>An interface with a no-op behind it, and that shape is forced by how this can be tested.
    /// Nothing here is observable in the editor: the only way to see a vibration is a twenty-minute
    /// IL2CPP build onto a device, which is the cost structure <c>CLAUDE.md</c> already records against
    /// <c>REQUEST_INSTALL_PACKAGES</c>. So the decisions — when to fire, how hard, how long — are made
    /// against numbers the debug overlay prints in the editor, and only the last step needs a phone.
    /// </para>
    /// </summary>
    public interface IHaptics
    {
        /// <summary>Whether this can actually do anything. False in the editor and on a phone with no motor.</summary>
        bool Available { get; }

        /// <summary>One pulse. Milliseconds, and amplitude 0..1.</summary>
        void Pulse(int milliseconds, float amplitude);
    }

    /// <summary>The editor's, and any device that turns out to have no vibrator. Records, never fires.</summary>
    public sealed class NullHaptics : IHaptics
    {
        public bool Available => false;

        public void Pulse(int milliseconds, float amplitude)
        {
        }
    }

    /// <summary>
    /// Android's vibrator, through <c>VibrationEffect</c>.
    ///
    /// <para><b>Not <see cref="Handheld.Vibrate"/>.</b> That is a fixed buzz of about half a second with
    /// no amplitude control at all — long enough to still be running when the next corner arrives, and
    /// identical for a kerb and for hitting a bridge parapet at speed. The whole value of haptics here is
    /// that a graze and a crash feel different, and that needs a duration and an amplitude.</para>
    ///
    /// <para>Every JNI handle is resolved once and kept. <c>AndroidJavaObject</c> lookups allocate and
    /// cross into the VM, and this is called from driving code.</para>
    /// </summary>
    public sealed class AndroidHaptics : IHaptics
    {
        private readonly AndroidJavaObject vibrator;
        private readonly AndroidJavaClass effectClass;

        public bool Available => vibrator != null;

        public AndroidHaptics()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");

                // VIBRATOR_MANAGER_SERVICE from API 31, the plain one before it. Asking for the new one
                // on an older phone throws rather than returning null, so the version is tested rather
                // than the result.
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                int sdk = version.GetStatic<int>("SDK_INT");

                if (sdk >= 31)
                {
                    using AndroidJavaObject manager =
                        activity.Call<AndroidJavaObject>("getSystemService", "vibrator_manager");
                    vibrator = manager?.Call<AndroidJavaObject>("getDefaultVibrator");
                }
                else
                {
                    vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }

                if (vibrator != null && !vibrator.Call<bool>("hasVibrator"))
                {
                    vibrator = null;
                }

                if (vibrator != null)
                {
                    effectClass = new AndroidJavaClass("android.os.VibrationEffect");
                }
            }
            catch (System.Exception error)
            {
                // A phone that will not hand over its vibrator is not a reason to fail to start. The
                // game is entirely playable without this and the log line is the only symptom worth
                // having.
                Debug.LogWarning($"[Horizon] Haptics unavailable: {error.Message}");
                vibrator = null;
            }
#endif
        }

        public void Pulse(int milliseconds, float amplitude)
        {
            if (vibrator == null || effectClass == null)
            {
                return;
            }

            // Android's scale is 1..255 and zero means "use the default", which is full — so a pulse
            // rounded down to nothing would come out as the hardest one available.
            int strength = Mathf.Clamp(Mathf.RoundToInt(amplitude * 255f), 1, 255);

            using AndroidJavaObject effect = effectClass.CallStatic<AndroidJavaObject>(
                "createOneShot", (long)milliseconds, strength);

            vibrator.Call("vibrate", effect);
        }
    }
}
