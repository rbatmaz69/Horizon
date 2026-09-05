using UnityEngine;

namespace Horizon.Net
{
    /// <summary>
    /// Holds Android's <c>WifiManager.MulticastLock</c> for as long as this device needs to hear
    /// broadcast packets.
    ///
    /// <para><b>Without it the host list is simply empty, and nothing anywhere says why.</b> Android's
    /// Wi-Fi power saving drops broadcast and multicast frames before they reach a socket unless
    /// something has taken this lock. There is no exception, no error and no log line — a guest browses,
    /// sees nothing, and every reasonable conclusion is wrong: that the host is not advertising, that
    /// the port is wrong, that the protocol is broken. It is the quietest failure in this feature,
    /// which is why it gets a class and a comment rather than three lines inside the transport.</para>
    ///
    /// <para>It also costs battery, so it is taken while browsing or advertising and released the
    /// moment either stops — never for the length of a session.</para>
    ///
    /// <para>The two permissions it needs, <c>ACCESS_WIFI_STATE</c> and
    /// <c>CHANGE_WIFI_MULTICAST_STATE</c>, are injected into the generated manifest by
    /// <c>AndroidBuild</c>, the same way <c>VIBRATE</c> already is. Dropping an
    /// <c>AndroidManifest.xml</c> into <c>Assets/Plugins/Android</c> would replace Unity's own rather
    /// than merge with it, and the app would install and refuse to launch — that trap is written out
    /// at length beside the vibrate permission.</para>
    /// </summary>
    public static class AndroidMulticastLock
    {
        private static AndroidJavaObject held;
        private static int depth;

        /// <summary>Whether the lock is currently held. Printed by the debug overlay.</summary>
        public static bool IsHeld => held != null;

        /// <summary>
        /// Take the lock, or add one to the count if it is already held.
        ///
        /// <para>Counted rather than boolean because advertising and browsing both want it and a host
        /// browsing for other rooms would otherwise release it out from under itself.</para>
        /// </summary>
        public static void Acquire()
        {
            depth++;

            if (held != null || Application.platform != RuntimePlatform.Android)
            {
                return;
            }

            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using AndroidJavaObject context = activity.Call<AndroidJavaObject>("getApplicationContext");
                using AndroidJavaObject wifi = context.Call<AndroidJavaObject>("getSystemService", "wifi");

                if (wifi == null)
                {
                    return;
                }

                held = wifi.Call<AndroidJavaObject>("createMulticastLock", "Horizon");
                held.Call("setReferenceCounted", false);
                held.Call("acquire");
            }
            catch (System.Exception error)
            {
                // Not fatal: on a device where this fails, broadcast discovery may still work and the
                // player can always type the host's address. Saying so is the point — a silent empty
                // list is what this class exists to prevent.
                Debug.LogWarning($"[Horizon] Could not take the Wi-Fi multicast lock: {error.Message}. "
                                 + "Host discovery may find nothing; joining by address still works.");
                held = null;
            }
        }

        /// <summary>Give one back, and release for real when the last holder does.</summary>
        public static void Release()
        {
            depth = Mathf.Max(0, depth - 1);

            if (depth > 0 || held == null)
            {
                return;
            }

            try
            {
                held.Call("release");
            }
            catch (System.Exception error)
            {
                Debug.LogWarning($"[Horizon] Could not release the Wi-Fi multicast lock: {error.Message}");
            }
            finally
            {
                held.Dispose();
                held = null;
            }
        }
    }
}
