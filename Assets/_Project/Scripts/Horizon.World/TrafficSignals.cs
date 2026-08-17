using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Lights the traffic signals' lenses to match the phase the traffic is already obeying.
    ///
    /// <para><b>This component decides nothing.</b> The phase is a pure function of the clock on
    /// <see cref="TrafficNetwork"/>, and both this and <c>TrafficDirector</c> read it — so a light
    /// cannot show green at a junction cars are stopping at. Putting the timer here and having the
    /// director ask this object for it was the obvious shape and the wrong one: it makes a renderer a
    /// dependency of the driving, and it gives the two a chance to disagree by a frame.</para>
    ///
    /// <para><b>Material swapping, not a property block</b> — the same argument <see cref="TownLights"/>
    /// makes and for the same reason: a property block takes the renderer out of the SRP Batcher, and
    /// writing colours onto a shared material would permanently edit the .mat asset while playing in
    /// the editor. Each lens has a submesh of its own per phase group, and a submesh gets either the
    /// dark material or its own lit one.</para>
    ///
    /// <para>The one place it differs from <see cref="TownLights"/> is that this changes four times a
    /// cycle rather than twice a day, so <c>sharedMaterials</c> — which allocates a fresh array on
    /// every read — is fetched once in <see cref="Awake"/> and kept.</para>
    /// </summary>
    public sealed class TrafficSignals : MonoBehaviour
    {
        [Tooltip("Where the phase timing comes from. The same asset the traffic drives on.")]
        [SerializeField] private TrafficNetwork network;

        [Tooltip("Renderers holding lens submeshes. Filled in by the setup tool.")]
        [SerializeField] private MeshRenderer[] renderers;

        [Tooltip("Prefix offsets into the two arrays below: renderer i owns slots[slotStart[i]] up to "
               + "slotStart[i + 1]. One entry longer than the renderer array.")]
        [SerializeField] private int[] slotStart;

        [Tooltip("Flattened material-slot indices, as they survived the mesh's submesh compaction.")]
        [SerializeField] private int[] slots;

        [Tooltip("Which group and colour each slot is, as group * 3 + TrafficSignalState. Parallel to "
               + "the array above.")]
        [SerializeField] private int[] slotLens;

        [Tooltip("The unlit lens. One material for every dark lens there is — two thirds of them at any "
               + "moment, and the SRP Batcher draws them back to back for nearly nothing.")]
        [SerializeField] private Material darkMaterial;

        [Tooltip("The lit lens per TrafficSignalState: red, amber, green.")]
        [SerializeField] private Material[] lensMaterials;

        /// <summary>What each group is showing, so a change can be spotted without re-applying.</summary>
        private TrafficSignalState[] showing;

        /// <summary>
        /// Each renderer's material array, fetched once.
        ///
        /// <c>sharedMaterials</c> allocates on every read. Four reads a second across a dozen renderers
        /// is exactly the per-frame garbage this project's conventions spend the most effort forbidding.
        /// </summary>
        private Material[][] materials;

        private bool applied;

        /// <summary>What one group is showing. For the debug overlay; the traffic reads the asset.</summary>
        public TrafficSignalState StateOf(int group)
        {
            return showing != null && group >= 0 && group < showing.Length
                ? showing[group]
                : TrafficSignalState.Green;
        }

        /// <summary>
        /// Evaluates and applies immediately, whatever the cached state says and whether or not
        /// <see cref="Awake"/> has run.
        ///
        /// <para>For the preview renderer, which moves the sun and captures in the same frame with no
        /// <c>Update</c> in between — and, unlike <see cref="TownLights"/>, has to work in edit mode
        /// where <c>Awake</c> never runs at all. Without it every preview shows a head with three dark
        /// lenses, which is indistinguishable from a signal that has no bulbs in it, and the material
        /// swap is precisely the thing a shot is being taken to check.</para>
        /// </summary>
        public void Refresh()
        {
            if (!Prepare())
            {
                return;
            }

            applied = false;
            Tick();
        }

        private void Awake()
        {
            if (!Prepare())
            {
                enabled = false;
            }
        }

        /// <summary>Sizes the caches. Idempotent, so Refresh may call it before Awake ever runs.</summary>
        private bool Prepare()
        {
            if (network == null || renderers == null || renderers.Length == 0)
            {
                return false;
            }

            int groups = Mathf.Max(1, network.SignalGroupCount);

            if (showing == null || showing.Length != groups)
            {
                showing = new TrafficSignalState[groups];
                applied = false;
            }

            if (materials == null || materials.Length != renderers.Length)
            {
                materials = new Material[renderers.Length][];
                applied = false;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                materials[i] ??= renderers[i] != null ? renderers[i].sharedMaterials : null;
            }

            return true;
        }

        private void OnEnable()
        {
            // Force the first apply even if the starting state happens to match, so the lenses are never
            // left showing whichever material the mesh was built with.
            applied = false;
        }

        private void Update()
        {
            Tick();
        }

        private void Tick()
        {
            if (showing == null || network == null)
            {
                return;
            }

            bool changed = !applied;
            float now = Time.time;

            for (int group = 0; group < showing.Length; group++)
            {
                TrafficSignalState want = network.SignalStateOf(group, now);
                if (want != showing[group])
                {
                    showing[group] = want;
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
            if (slots == null || slotStart == null || slotLens == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length && i + 1 < slotStart.Length; i++)
            {
                Material[] set = materials[i];
                if (renderers[i] == null || set == null)
                {
                    continue;
                }

                bool touched = false;

                for (int entry = slotStart[i]; entry < slotStart[i + 1] && entry < slots.Length; entry++)
                {
                    int group = slotLens[entry] / 3;
                    var state = (TrafficSignalState)(slotLens[entry] % 3);

                    bool lit = group < showing.Length && showing[group] == state;

                    Material material = lit && lensMaterials != null && (int)state < lensMaterials.Length
                        ? lensMaterials[(int)state]
                        : darkMaterial;

                    int slot = slots[entry];
                    if (material == null || slot < 0 || slot >= set.Length || set[slot] == material)
                    {
                        continue;
                    }

                    set[slot] = material;
                    touched = true;
                }

                // Assigning the array back is what makes the change stick, and it is the expensive half —
                // so it happens only for a renderer that actually holds a lens that just changed.
                if (touched)
                {
                    renderers[i].sharedMaterials = set;
                }
            }
        }
    }
}
