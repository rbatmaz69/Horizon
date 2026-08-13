using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// How thick the air is. The whole of the weather model, honestly labelled.
    ///
    /// <para><b>There is no rain in this project</b> — no particle system, no wet-road shader, no splash
    /// audio, nothing in the traffic or camera code that knows about it. What exists is
    /// <c>TimeOfDayController.Overcast</c>, which dims the sun by up to three quarters and thickens the
    /// fog by up to 2.6×. These three names describe exactly that and promise nothing else. A "Rain"
    /// button that only made the fog heavier would be a promise the game does not keep, and the fix for
    /// that is a rain system rather than a different word.</para>
    /// </summary>
    public enum WeatherPreset
    {
        Clear = 0,
        Hazy = 1,
        Overcast = 2,
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
                (int)WeatherPreset.Clear, (int)WeatherPreset.Overcast);

            Quality = (QualityPreset)Mathf.Clamp(
                PlayerPrefs.GetInt(QualityKey, (int)QualityPreset.Balanced),
                (int)QualityPreset.Low, (int)QualityPreset.High);
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
        /// </summary>
        public static float OvercastFor(WeatherPreset preset)
        {
            switch (preset)
            {
                case WeatherPreset.Hazy:
                    return 0.45f;
                case WeatherPreset.Overcast:
                    return 0.90f;
                default:
                    return 0f;
            }
        }

        /// <summary>What the weather buttons say. Index matches <see cref="WeatherPreset"/>.</summary>
        public static readonly string[] WeatherNames = { "Clear", "Hazy", "Overcast" };

        /// <summary>What the quality buttons say. Index matches <see cref="QualityPreset"/>.</summary>
        public static readonly string[] QualityNames = { "Low", "Balanced", "High" };
    }
}
