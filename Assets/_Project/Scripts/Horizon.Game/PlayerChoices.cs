using System.Collections.Generic;
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
        /// The viewpoints this player has stood at, one name per line.
        ///
        /// <para><b>Names and not a bitmask, and the deviation is deliberate.</b> A mask indexed by
        /// position in the baked viewpoint list is four bytes and breaks the first time anybody inserts
        /// a viewpoint into the middle of a course — every one after it shifts, and a player who had
        /// stood at twelve places would find a different twelve marked after an update, silently. That
        /// is the same hazard this file already records against <see cref="WeatherPreset"/>, where the
        /// answer was "appended, never inserted"; a discipline that works for four enum values does not
        /// survive twenty places spread over fourteen courses that are edited for other reasons.</para>
        ///
        /// <para>A name is the identity the player already sees — it is what the map prints and what
        /// the notice says — and it costs a few hundred bytes. Newline-separated because a viewpoint
        /// name cannot contain one, where it can and does contain commas, spaces and non-ASCII.</para>
        /// </summary>
        private const string VisitedKey = "Horizon.Visited";

        /// <summary>
        /// Prefix for a circuit's best lap, in seconds, one key per circuit.
        ///
        /// <para>Per circuit rather than one number, because there are two of them and a single best
        /// would mean the Bahçe Ring's five kilometres competing with the Weissjochring's fifteen. Keyed
        /// by the circuit's own name for the reason above: a circuit added later must not renumber the
        /// one that was there first.</para>
        /// </summary>
        private const string BestLapPrefix = "Horizon.Best.";

        /// <summary>
        /// Where a viewpoint's name is stored, and the one thing that has to survive a launch here.
        ///
        /// <para>A set rather than a list, because the only two questions asked of it are "has this one
        /// been stood at" and "add this one" — and a viewpoint is visited or it is not.</para>
        /// </summary>
        private static readonly HashSet<string> visited = new HashSet<string>();

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

        /// <summary>How many viewpoints this player has stood at. For the map's key and the log.</summary>
        public static int VisitedCount => visited.Count;

        /// <summary>Whether this viewpoint has been stood at. Called per marker while the map draws.</summary>
        public static bool HasVisited(string viewpoint)
        {
            return !string.IsNullOrEmpty(viewpoint) && visited.Contains(viewpoint);
        }

        /// <summary>
        /// Records a viewpoint as stood at, and writes it out immediately.
        ///
        /// <para>Written on the spot rather than at the next <see cref="Save"/>, because the whole of
        /// this is a thing the player did once and would have to do again if the app were killed before
        /// the next pause. It returns whether it was new, so a caller can put a line on the screen for a
        /// place that has just been reached and stay quiet about one being passed for the tenth
        /// time.</para>
        /// </summary>
        public static bool MarkVisited(string viewpoint)
        {
            if (string.IsNullOrEmpty(viewpoint) || !visited.Add(viewpoint))
            {
                return false;
            }

            PlayerPrefs.SetString(VisitedKey, string.Join("\n", visited));
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>
        /// Marks a viewpoint visited in memory and writes nothing.
        ///
        /// <para><b>For the preview tools, and for nothing else.</b> A viewpoint drawn filled is a
        /// state no picture this project takes could otherwise reach: the map preview runs at edit
        /// time, where <see cref="Load"/> has never been called, so every mark comes out hollow and the
        /// other half of the feature is never photographed — which is the failure the boost gauge's
        /// notes are about. <c>MapPreviewRenderer</c> seeds the set, takes a second frame and clears
        /// it.</para>
        ///
        /// <para>It does not persist, deliberately: a tool that wrote a developer's registry to take a
        /// picture would be the working-tree hazard this project documents against materials, moved
        /// somewhere git cannot see it. The running game always goes through
        /// <see cref="MarkVisited"/>.</para>
        /// </summary>
        public static void SeedVisited(string viewpoint)
        {
            if (!string.IsNullOrEmpty(viewpoint))
            {
                visited.Add(viewpoint);
            }
        }

        /// <summary>Empties the in-memory set. The other half of <see cref="SeedVisited"/>.</summary>
        public static void ClearVisited()
        {
            visited.Clear();
        }

        /// <summary>The best lap on this circuit, seconds, or zero where none has been driven.</summary>
        public static float BestLap(string circuit)
        {
            return string.IsNullOrEmpty(circuit)
                ? 0f
                : PlayerPrefs.GetFloat(BestLapPrefix + circuit, 0f);
        }

        /// <summary>
        /// Records a best lap, and only when it is one.
        ///
        /// <para>The comparison is here rather than at the caller so that there is one place that
        /// decides what "better" means — <c>LapTiming</c> already holds a session best and would
        /// otherwise be the second opinion.</para>
        /// </summary>
        public static void SetBestLap(string circuit, float seconds)
        {
            if (string.IsNullOrEmpty(circuit) || seconds <= 0f)
            {
                return;
            }

            float existing = BestLap(circuit);

            if (existing > 0f && existing <= seconds)
            {
                return;
            }

            PlayerPrefs.SetFloat(BestLapPrefix + circuit, seconds);
            PlayerPrefs.Save();
        }

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

            visited.Clear();
            string stored = PlayerPrefs.GetString(VisitedKey, string.Empty);

            if (!string.IsNullOrEmpty(stored))
            {
                string[] names = stored.Split('\n');

                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].Length > 0)
                    {
                        visited.Add(names[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Writes everything back. Called when the player drives off, and after each change made from
        /// the pause menu, so quitting from a paused game does not lose the last thing they did.
        ///
        /// <para>The visited set and the lap times are deliberately not here: both are written the
        /// moment they change, because both are things the player earned rather than chose, and a
        /// choice that is lost costs one tap where an earned thing lost costs the drive that earned
        /// it.</para>
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
