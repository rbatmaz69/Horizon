using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Swaps the road surfaces between a dry material and a wet one.
    ///
    /// <para><b>Materials are swapped, never edited, and that distinction is the whole reason this class
    /// exists.</b> Darkening the asphalt by writing <c>_BaseColor</c> on the shared material would be
    /// one line — and Unity does not roll asset changes back when Play mode ends, so a player who tried
    /// the rain once would leave <c>M_RoadSurface.mat</c> modified in the working tree. That is the trap
    /// <c>QualityDirector</c> and <c>TownLights</c> both document. A <c>MaterialPropertyBlock</c> is the
    /// other obvious answer and it breaks the SRP batcher, which this world cannot afford across every
    /// carriageway in it. So: two finished assets, and the renderer is pointed at one or the other —
    /// exactly what <c>TownLights</c> does at dusk.</para>
    ///
    /// <para><b>The registry is built by asking what material a renderer already carries.</b> Roads are
    /// painted by a dozen different builders — the ribbons, the town streets, the forecourts, the fork
    /// throats, the motorway merges and termini, the bridge decks — and threading a "you are a road"
    /// flag through all of them would be a dozen places to forget one. The build instead sweeps the
    /// finished world once and records every renderer slot holding a known dry road material. That is
    /// not a checker forming its own opinion: the identity test is the exact asset the builder itself
    /// assigned, so a surface counts as a road if and only if a builder painted it like one.</para>
    ///
    /// <para><b>In Horizon.Game rather than beside TownLights in Horizon.World</b>, because the weather
    /// is decided here. <c>TownLights</c> can live in World only because it reads
    /// <c>RenderSettings.sun</c> — the engine itself carries the fact it needs. There is no
    /// <c>RenderSettings.wetness</c>, so this has to be told, and only the leaf assembly can tell it.
    /// The registry itself is nothing but renderers and materials and would sit happily in either.</para>
    /// </summary>
    public sealed class WetSurfaces : MonoBehaviour
    {
        /// <summary>
        /// One renderer and the slots on it that change.
        ///
        /// <para>Grouped per renderer rather than a flat list of slots, because
        /// <c>Renderer.sharedMaterials</c> is an array property: reading it allocates a copy and
        /// writing it replaces the lot. A road ribbon has two slots that both change, and a flat list
        /// would do that whole round trip twice for one object.</para>
        /// </summary>
        [System.Serializable]
        public sealed class Group
        {
            public Renderer Renderer;

            /// <summary>Which material slots on it are road. Same length as the two arrays below.</summary>
            public int[] Slots;

            public Material[] Dry;

            public Material[] Wet;
        }

        [SerializeField] private Group[] groups = new Group[0];

        /// <summary>How many renderers this is holding. Printed by the build so an empty sweep shows up.</summary>
        public int GroupCount => groups != null ? groups.Length : 0;

        /// <summary>Whether the roads are currently wet. Starts dry, which is how the scene is saved.</summary>
        public bool IsWet { get; private set; }

        public void SetGroups(Group[] value)
        {
            groups = value ?? new Group[0];
        }

        /// <summary>
        /// Points every registered slot at the dry set or the wet one.
        ///
        /// <para>Returns immediately when nothing changes, which is what makes it safe to call every
        /// frame from <see cref="WeatherDirector"/>. The work itself is a hundred-odd array round trips
        /// and belongs on the two frames a shower starts and stops — the same shape as
        /// <c>TownLights</c>, which does this twice a day and nothing in between.</para>
        /// </summary>
        public void SetWet(bool value)
        {
            if (value == IsWet || groups == null)
            {
                return;
            }

            IsWet = value;

            for (int i = 0; i < groups.Length; i++)
            {
                Group group = groups[i];
                if (group == null || group.Renderer == null || group.Slots == null)
                {
                    continue;
                }

                Material[] target = value ? group.Wet : group.Dry;
                if (target == null || target.Length != group.Slots.Length)
                {
                    continue;
                }

                Material[] current = group.Renderer.sharedMaterials;

                for (int slot = 0; slot < group.Slots.Length; slot++)
                {
                    int index = group.Slots[slot];
                    if (index >= 0 && index < current.Length && target[slot] != null)
                    {
                        current[index] = target[slot];
                    }
                }

                group.Renderer.sharedMaterials = current;
            }
        }
    }
}
