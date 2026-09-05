namespace Horizon.Game
{
    /// <summary>
    /// Every page the menu can show, in the order <see cref="MenuPanels"/> holds them.
    ///
    /// <para><b>This enum is the contract between the builder and the runtime.</b> The page a button
    /// opens is baked into its saved UnityEvent as a plain integer — that is how <c>StartAt(int)</c> has
    /// always worked — so the builder and the component have to agree on which integer means which
    /// panel. Before this there were three panels and the agreement was "opening one hides the other
    /// two", written out pairwise in two different files. At nine pages that is eighty-one clauses and
    /// nobody would get them all right.</para>
    ///
    /// <para>Adding a page means adding a value here and building it in the same position;
    /// <c>MenuUiSetup</c> asserts the two have stayed in step as it registers each one, so a page
    /// inserted in the middle fails loudly at build time rather than sending the player to the
    /// wrong screen.</para>
    /// </summary>
    public enum MenuPage
    {
        /// <summary>The start screen's front page: the Drive button and the way into everything else.</summary>
        Start = 0,

        /// <summary>Pick a body.</summary>
        Garage = 1,

        /// <summary>Pick a paint.</summary>
        Paint = 2,

        /// <summary>Pick where to begin.</summary>
        Place = 3,

        /// <summary>Hour of the day and how thick the air is.</summary>
        Conditions = 4,

        /// <summary>Steering, pedals, sensitivity, tilt calibration.</summary>
        Controls = 5,

        /// <summary>How much world to draw.</summary>
        Quality = 6,

        /// <summary>The in-drive pause menu. Reached only from the pause button.</summary>
        Paused = 7,

        /// <summary>Which version is running, and the newer one if GitHub has published it.</summary>
        Update = 8,

        /// <summary>
        /// The whole world in plan. Reached from the minimap, and from the pause menu.
        ///
        /// <para>Appended rather than filed anywhere tidier, and that is the rule this enum's remarks
        /// are about: the page a button opens is a bare integer in a saved event, so a value inserted in
        /// the middle moves every page after it under buttons that still name the old numbers.</para>
        /// </summary>
        Map = 9,

        /// <summary>
        /// Hosting a game on this network, or joining one somebody else is hosting.
        ///
        /// <para>Appended, for the reason this enum's remarks give twice already: the page a button
        /// opens is a bare integer in a saved UnityEvent, and a value inserted in the middle moves
        /// every page after it under buttons that still name the old numbers.</para>
        /// </summary>
        Multiplayer = 10,

        /// <summary>
        /// Who is in the room, and the way out of it.
        ///
        /// <para><b>A second page rather than a second state of the first one, and the reason is
        /// measured rather than aesthetic.</b> <c>ValidatePageHeights</c> allows about a thousand units
        /// and a page holding both states comes to thirteen hundred — its top and bottom rows off the
        /// screen, on a menu with nothing to scroll with. Two pages that each fit is the answer that
        /// page-height check exists to push people towards.</para>
        /// </summary>
        Room = 11,
    }
}
