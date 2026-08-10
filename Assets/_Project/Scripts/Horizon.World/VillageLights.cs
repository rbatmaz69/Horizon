using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Lights the village windows and lamp heads once the sun goes down.
    ///
    /// Reads the time of day from <see cref="RenderSettings.sun"/> rather than from
    /// <c>Horizon.Atmosphere</c>, for the same reason <c>VehicleLights</c> does: the sun's intensity is
    /// already the thing that decides whether it looks like night, and going through RenderSettings keeps
    /// this module out of a dependency that would point the wrong way up the assembly list.
    ///
    /// **Not a MaterialPropertyBlock, and not a write to the shared material.** The car uses a property
    /// block, which is right for one object and wrong for thirty: a block makes a renderer
    /// SRP-Batcher-incompatible, so a village of house meshes would be a village of broken batches.
    /// Writing colours onto the shared material instead would work at runtime and permanently edit the
    /// .mat asset while playing in the editor. So the window submesh simply gets one of two ready-made
    /// materials swapped into its slot, and only on the frames the state actually changes — which is
    /// twice a day.
    /// </summary>
    public sealed class VillageLights : MonoBehaviour
    {
        [Tooltip("Renderers holding a window submesh. Filled in by the setup tool.")]
        [SerializeField] private MeshRenderer[] renderers;

        [Tooltip("Which material slot is the windows, one entry per renderer.\n\n"
               + "Parallel to the array above rather than a single index, because village meshes drop "
               + "their empty submeshes — a tile with only lamps on it has the windows in a different "
               + "slot from a tile with houses.")]
        [SerializeField] private int[] windowSlots;

        [SerializeField] private Material dayMaterial;
        [SerializeField] private Material nightMaterial;

        [Tooltip("Sun intensity below which the windows light up.")]
        [SerializeField] private float nightSunIntensity = 0.38f;

        [Tooltip("Sun intensity they stay lit until. The gap is hysteresis — a single threshold sits in "
               + "the middle of dusk, where the sun barely moves, and the whole village flickers.")]
        [SerializeField] private float dawnSunIntensity = 0.5f;

        private bool lit;
        private bool applied;

        /// <summary>True while the windows are lit.</summary>
        public bool IsLit => lit;

        private void OnEnable()
        {
            // Force the first Apply even if the starting state happens to match, so the village is never
            // left showing whichever material the mesh was built with.
            applied = false;
        }

        private void Update()
        {
            bool wantLit = IsDark();
            if (applied && wantLit == lit)
            {
                return;
            }

            lit = wantLit;
            applied = true;
            Apply();
        }

        private void Apply()
        {
            Material material = lit ? nightMaterial : dayMaterial;
            if (material == null || renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || windowSlots == null || i >= windowSlots.Length)
                {
                    continue;
                }

                int slot = windowSlots[i];
                Material[] slots = renderer.sharedMaterials;
                if (slot < 0 || slot >= slots.Length || slots[slot] == material)
                {
                    continue;
                }

                slots[slot] = material;
                renderer.sharedMaterials = slots;
            }
        }

        private bool IsDark()
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

            float threshold = lit ? dawnSunIntensity : nightSunIntensity;
            return sun.intensity < threshold;
        }
    }
}
