using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// What the weather is doing.
    ///
    /// <para><b>The first three are the whole of the sky and nothing else.</b> They set
    /// <c>TimeOfDayController.Overcast</c>, which dims the sun by up to three quarters and thickens the
    /// fog by up to 2.6×. Those three names describe exactly that and promise nothing more.</para>
    ///
    /// <para><b><see cref="Rain"/> was added last and it is a system rather than a word.</b> This
    /// enum's remarks used to say there was no rain here — no particles, no wet road, no audio, nothing
    /// the car knew about — and that a button claiming otherwise would be the menu lying about the
    /// world. So the button arrived with the four things that make it true: water falling past the
    /// camera, a noise on the roof that stops under a bridge, a darker sky, and tyres that let go
    /// earlier. <c>WeatherDirector</c> owns all four.</para>
    ///
    /// <para><b>Appended, never inserted.</b> The value is written to PlayerPrefs as a bare integer, so
    /// a preset added in the middle would silently change what every returning player had chosen. The
    /// clamp in <see cref="Load"/> has to move with it — that is the one line to check when the next
    /// one arrives.</para>
    /// </summary>
    public enum WeatherPreset
    {
        Clear = 0,
        Hazy = 1,
        Overcast = 2,
        Rain = 3,
    }

    /// <summary>How much world to draw. See <see cref="QualityDirector"/> for what each one moves.</summary>
    public enum QualityPreset
    {
        Low = 0,
        Balanced = 1,
        High = 2,
    }

    /// <summary>
    /// What the player chose on the start screen, and the only thing that remembers it between runs.
    ///
    /// <para><b>Static, in the shape of <c>Horizon.Input.TouchControlState</c>.</b> A MonoBehaviour would
    /// mean the start screen, the pause menu and <c>GameBootstrap</c> each doing a
    /// <c>FindFirstObjectByType</c> for it, and one of them getting there before it existed. There is
    /// exactly one player and exactly one set of choices, so a static bag is what this is.</para>
    ///
    /// <para><b>What it deliberately does not own: the controls.</b> Steering method, pedal method and
    /// <c>Horizon.SteerSensitivity</c> stay with <c>DriveInputRouter</c> and <c>TouchControlState</c>.
    /// The router reads its own preferences in its own <c>Awake</c>, before anything in
    /// <c>Horizon.Game</c> is alive, and <c>Horizon.Input</c> may not reference this assembly. The
    /// controls page just calls the methods <c>PauseMenu</c> already has.</para>
    ///
    /// <para><b>Everything is clamped on read, never on write.</b> These indices come off disk and the
    /// things they index are rebuilt from code — remove a body from <c>CarMeshBuilder.PlayerProfiles</c>
    /// and a saved 4 is suddenly out of range. Clamping at the point of use means that costs a returning
    /// player the wrong car for one launch instead of an exception on the first frame, which is the sort
    /// of bug only the people who have played longest ever see.</para>
    /// </summary>
    public static class PlayerChoices
    {
        private const string CarKey = "Horizon.Car";
        private const string PaintKey = "Horizon.Paint";
        private const string SpawnKey = "Horizon.Spawn";
        private const string HoursKey = "Horizon.TimeOfDay";
        private const string WeatherKey = "Horizon.Weather";
        private const string QualityKey = "Horizon.Quality";
        private const string NameKey = "Horizon.Name";

        /// <summary>
        /// The hour the world starts at when nothing is saved.
        ///
        /// <para>17.6 rather than a round number, because that is what <c>TimeOfDayController</c> has
        /// always been serialized at: low sun, long shadows, the light this game is at its best in. Two
        /// defaults that disagreed would mean the first launch looked different from every one after
        /// it.</para>
        /// </summary>
        public const float DefaultHours = 17.6f;

        public static int Car { get; set; }

        public static int Paint { get; set; }

        public static int Spawn { get; set; }

        public static float Hours { get; set; } = DefaultHours;

        public static WeatherPreset Weather { get; set; } = WeatherPreset.Clear;

        /// <summary>
        /// Balanced until a phone says otherwise. Guessing Low from
        /// <c>SystemInfo</c> would be a guess, and the player is one tap from the answer.
        /// </summary>
        public static QualityPreset Quality { get; set; } = QualityPreset.Balanced;

        /// <summary>
        /// What other players see over this car's roof.
        ///
        /// <para>Here rather than in <c>NetSession</c> for the reason this class already gives about
        /// the car and the paint: it is a thing the player chose that has to survive being closed, and
        /// there is exactly one player. It is also read before any transport exists — the multiplayer
        /// page shows it on the way in — so a session component would be a thing to find before it was
        /// alive.</para>
        ///
        /// <para><b>Trimmed and capped on read like every other value here.</b> The wire has sixteen
        /// bytes for it (<c>NetProtocol.NameBytes</c>) and truncates on a byte boundary, so a name that
        /// arrives too long comes back with its last character possibly missing. Capping here means
        /// what the player typed and what their friends read are the same string.</para>
        /// </summary>
        public static string Name { get; set; } = string.Empty;

        /// <summary>Sixteen bytes of UTF-8 is what the roster row holds, so this is where it is cut.</summary>
        public const int MaxNameLength = 16;

        /// <summary>
        /// The name, guaranteed to be something. Falls back to the device's own name rather than to
        /// "Player", because in a room of four the useful default is the one nobody has to explain.
        /// </summary>
        public static string DisplayName()
        {
            string chosen = Name != null ? Name.Trim() : string.Empty;

            if (chosen.Length > 0)
            {
                return chosen.Length > MaxNameLength ? chosen.Substring(0, MaxNameLength) : chosen;
            }

            string device = SystemInfo.deviceName;

            if (string.IsNullOrEmpty(device) || device == SystemInfo.unsupportedIdentifier)
            {
                return "Driver";
            }

            return device.Length > MaxNameLength ? device.Substring(0, MaxNameLength) : device;
        }

        /// <summary>
        /// Reads everything back. Called once, from <c>GameBootstrap.Awake</c>, before the world scene
        /// is asked to load.
        ///
        /// <para>There are no legacy keys to migrate yet. When the first one appears, the shape to copy
        /// is <c>DriveInputRouter.LoadPreferences</c>: try the current key, else read the old one,
        /// convert it, write the new one and delete the old — so a player who had already chosen
        /// something is not quietly put back to the default underneath them.</para>
        /// </summary>
        public static void Load()
        {
            Car = PlayerPrefs.GetInt(CarKey, 0);
            Paint = PlayerPrefs.GetInt(PaintKey, 0);
            Spawn = PlayerPrefs.GetInt(SpawnKey, 0);
            Hours = PlayerPrefs.GetFloat(HoursKey, DefaultHours);

            Weather = (WeatherPreset)Mathf.Clamp(
                PlayerPrefs.GetInt(WeatherKey, (int)WeatherPreset.Clear),
                (int)WeatherPreset.Clear, (int)WeatherPreset.Rain);

            Quality = (QualityPreset)Mathf.Clamp(
                PlayerPrefs.GetInt(QualityKey, (int)QualityPreset.Balanced),
                (int)QualityPreset.Low, (int)QualityPreset.High);

            Name = PlayerPrefs.GetString(NameKey, string.Empty);
        }

        /// <summary>
        /// Writes everything back. Called when the player drives off, and after each change made from
        /// the pause menu, so quitting from a paused game does not lose the last thing they did.
        /// </summary>
        public static void Save()
        {
            PlayerPrefs.SetInt(CarKey, Car);
            PlayerPrefs.SetInt(PaintKey, Paint);
            PlayerPrefs.SetInt(SpawnKey, Spawn);
            PlayerPrefs.SetFloat(HoursKey, Hours);
            PlayerPrefs.SetInt(WeatherKey, (int)Weather);
            PlayerPrefs.SetInt(QualityKey, (int)Quality);
            PlayerPrefs.SetString(NameKey, Name != null ? Name : string.Empty);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// The saved car index, made safe against a garage that has changed size since it was written.
        /// </summary>
        public static int CarIn(int count) => count > 0 ? Mathf.Clamp(Car, 0, count - 1) : 0;

        public static int PaintIn(int count) => count > 0 ? Mathf.Clamp(Paint, 0, count - 1) : 0;

        public static int SpawnIn(int count) => count > 0 ? Mathf.Clamp(Spawn, 0, count - 1) : 0;

        /// <summary>
        /// What each weather preset means to <c>TimeOfDayController.Overcast</c>.
        ///
        /// <para>0.9 rather than 1.0 at the top: at full overcast the sun contributes almost nothing and
        /// the scene is lit by ambient alone, which reads less like bad weather than like a bug.</para>
        ///
        /// <para><b>Rain sits just below Overcast rather than above it, and that is not a mistake.</b>
        /// It is the darker of the two to look at, because the rain itself takes light out of the frame
        /// on top of this — a rain preset that also asked for the heaviest sky came out as a grey wall
        /// with nothing readable in it. The sky is the setting; the rain is the weather.</para>
        /// </summary>
        public static float OvercastFor(WeatherPreset preset)
        {
            switch (preset)
            {
                case WeatherPreset.Hazy:
                    return 0.45f;
                case WeatherPreset.Overcast:
                    return 0.90f;
                case WeatherPreset.Rain:
                    return 0.80f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// How hard it is raining, 0 to 1. One number for all four consumers.
        ///
        /// <para>Falling water, the noise, the wet road and the grip are the same weather, so they read
        /// the same figure — the argument the boost gauge already makes about the needle and the
        /// whistle. Four constants would be four things able to disagree about whether it is raining.
        /// </para>
        /// </summary>
        public static float RainFor(WeatherPreset preset) => preset == WeatherPreset.Rain ? 1f : 0f;

        /// <summary>What the weather buttons say. Index matches <see cref="WeatherPreset"/>.</summary>
        public static readonly string[] WeatherNames = { "Clear", "Hazy", "Overcast", "Rain" };

        /// <summary>What the quality buttons say. Index matches <see cref="QualityPreset"/>.</summary>
        public static readonly string[] QualityNames = { "Low", "Balanced", "High" };
    }
}
