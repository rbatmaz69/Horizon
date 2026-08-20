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
    }
}
