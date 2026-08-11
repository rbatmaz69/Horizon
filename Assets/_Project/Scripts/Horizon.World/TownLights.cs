using UnityEngine;

namespace Horizon.World
{
    /// <summary>Kinds of thing that light up, each with its own pair of materials and its own dusk.</summary>
    public enum LitGroup
    {
        /// <summary>House and landmark glass — only the panes that drew into the lit submesh.</summary>
        Windows = 0,

        /// <summary>Lantern heads and the pools of light under them.</summary>
        Lamps = 1,

        /// <summary>Ambient traffic's headlights. Never <c>Light</c> components — see TrafficDirector.</summary>
        Headlights = 2,

        /// <summary>And their tail lamps, which are the pair the player actually drives behind.</summary>
        Taillights = 3,
    }

    /// <summary>
    /// Lights the town windows and lamp heads once the sun goes down.
    ///
    /// Reads the time of day from <see cref="RenderSettings.sun"/> rather than from
    /// <c>Horizon.Atmosphere</c>, for the same reason <c>VehicleLights</c> does: the sun's intensity is
    /// already the thing that decides whether it looks like night, and going through RenderSettings keeps
    /// this module out of a dependency that would point the wrong way up the assembly list.
    ///
    /// **Not a MaterialPropertyBlock, and not a write to the shared material.** The car uses a property
    /// block, which is right for one object and wrong for thirty: a block makes a renderer
    /// SRP-Batcher-incompatible, so a town of house meshes would be a town of broken batches.
    /// Writing colours onto the shared material instead would work at runtime and permanently edit the
    /// .mat asset while playing in the editor. So a lit submesh simply gets one of two ready-made
    /// materials swapped into its slot, and only on the frames the state actually changes — which is
    /// twice a day.
    ///
    /// <para><b>Grouped, because lamps and windows are not the same dusk.</b> Street lighting comes on
    /// while there is still light in the sky and it is brighter and whiter than a living room; house
    /// windows follow later. Two flat arrays of renderers and slots would have carried the two-window
    /// split perfectly well, so the generalisation is here rather than later for one reason only — doing
    /// it twice would mean doing it twice.</para>
    ///
    /// <para>The slot arrays are the counts-to-offsets shape used by <c>MountainField.BuildBuckets</c> and
    /// <c>StreetIndex</c>: <see cref="slotStart"/> has one entry per renderer plus a terminator, and
    /// <see cref="slots"/> and <see cref="slotGroup"/> are the flattened runs. A renderer can therefore
    /// own any number of lit slots — a town tile with houses and a lamp on it owns two — without a
    /// jagged array or a class per entry.</para>
    /// </summary>
    public sealed class TownLights : MonoBehaviour
    {
        [Tooltip("Renderers holding at least one lit submesh. Filled in by the setup tool.")]
        [SerializeField] private MeshRenderer[] renderers;

        [Tooltip("Prefix offsets into the two arrays below: renderer i owns slots[slotStart[i]] up to "
               + "slotStart[i + 1]. One entry longer than the renderer array.")]
        [SerializeField] private int[] slotStart;

        [Tooltip("Flattened material-slot indices.\n\n"
               + "Indices rather than a single fixed slot per group, because town meshes drop their empty "
               + "submeshes — a tile with only lamps on it has them in a different slot from a tile with "
               + "houses.")]
        [SerializeField] private int[] slots;

        [Tooltip("Which LitGroup each slot belongs to. Parallel to the array above.")]
        [SerializeField] private int[] slotGroup;

        [Tooltip("Day material per group, indexed by LitGroup.")]
        [SerializeField] private Material[] dayMaterials;

        [Tooltip("Night material per group, indexed by LitGroup.")]
        [SerializeField] private Material[] nightMaterials;

        [Tooltip("Sun intensity below which each group lights up, indexed by LitGroup. Lamps and "
               + "headlights come on earlier than windows: street lighting and dipped beams are both on "
               + "while there is still light in the sky.")]
        [SerializeField] private float[] nightSunIntensity = { 0.38f, 0.55f, 0.60f, 0.60f };

        [Tooltip("Sun intensity each group stays lit until. The gap is hysteresis — a single threshold "
               + "sits in the middle of dusk, where the sun barely moves, and the whole town flickers.")]
        [SerializeField] private float[] dawnSunIntensity = { 0.50f, 0.68f, 0.72f, 0.72f };

        private bool[] lit;
        private bool applied;

        /// <summary>True while the house windows are lit.</summary>
        public bool IsLit => IsGroupLit(LitGroup.Windows);

        /// <summary>Whether one group is currently showing its night material.</summary>
        public bool IsGroupLit(LitGroup group)
        {
            return lit != null && (int)group < lit.Length && lit[(int)group];
        }

        /// <summary>
        /// Re-evaluates and re-applies immediately, whatever the cached state says.
        ///
        /// For the night render path: that tool moves the sun and captures in the same frame, with no
        /// Update in between, so without this the town would be photographed in daylight materials under
        /// a night sky — which looks like a bug in the lighting rather than a missing call.
        /// </summary>
        public void Refresh()
        {
            applied = false;
            Tick();
        }

        private void OnEnable()
        {
            // Force the first Apply even if the starting state happens to match, so the town is never
            // left showing whichever material the mesh was built with.
            applied = false;
        }

        private void Update()
        {
            Tick();
        }

        private void Tick()
        {
            int groups = GroupCount;
            if (lit == null || lit.Length != groups)
            {
                lit = new bool[groups];
                applied = false;
            }

            bool changed = !applied;

            for (int group = 0; group < groups; group++)
            {
                bool want = IsDark(group);
                if (want != lit[group])
                {
                    lit[group] = want;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            applied = true;
            Apply();
        }

        private void Apply()
        {
            if (renderers == null || slots == null || slotStart == null || slotGroup == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length && i + 1 < slotStart.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Material[] materials = null;

                for (int entry = slotStart[i]; entry < slotStart[i + 1] && entry < slots.Length; entry++)
                {
                    int group = slotGroup[entry];
                    Material material = lit[group] ? nightMaterials[group] : dayMaterials[group];
                    if (material == null)
                    {
                        continue;
                    }

                    // Fetched once per renderer and only when something actually has to change:
                    // sharedMaterials allocates a fresh array on every read, and assigning it back is
                    // what makes the change stick.
                    materials ??= renderer.sharedMaterials;

                    int slot = slots[entry];
                    if (slot >= 0 && slot < materials.Length)
                    {
                        materials[slot] = material;
                    }
                }

                if (materials != null)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        private int GroupCount
        {
            get
            {
                int count = dayMaterials != null ? dayMaterials.Length : 0;
                if (nightMaterials != null)
                {
                    count = Mathf.Min(count, nightMaterials.Length);
                }

                return Mathf.Max(1, count);
            }
        }

        private bool IsDark(int group)
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
            {
                return false;
            }

            if (!sun.enabled)
            {
                return true;
            }

            float threshold = lit[group] ? Threshold(dawnSunIntensity, group, 0.5f)
                                         : Threshold(nightSunIntensity, group, 0.38f);
            return sun.intensity < threshold;
        }

        private static float Threshold(float[] values, int group, float fallback)
        {
            return values != null && group < values.Length ? values[group] : fallback;
        }
    }
}
