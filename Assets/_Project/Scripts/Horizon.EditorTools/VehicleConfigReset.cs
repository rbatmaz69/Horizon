using Horizon.Vehicle;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Writes the code defaults onto the existing <see cref="VehicleConfig"/> asset.
    ///
    /// <para><b>Why this needs to exist.</b> Tunables live in an asset so they can be edited during Play
    /// and kept, and <c>LoadOrCreate</c> deliberately never overwrites one that is already there. That is
    /// the right default and it has one sharp edge: when a field's <i>meaning</i> changes in code, the
    /// asset goes on holding a number that was correct under the old meaning and is nonsense under the
    /// new one. Nothing complains, because the value is present, in range, and of the right type.</para>
    ///
    /// <para>That is not hypothetical. <c>LateralGrip</c> used to be "fraction of the sideways slide
    /// cancelled", so its curve sat around 0.9. It became the radius of a friction circle — a coefficient
    /// where 1.6 is a grippy road tyre — and the asset kept 0.9, which is ice. The car was rebuilt,
    /// validated and driven for two rounds of handling complaints on tyres nobody had chosen, while the
    /// intended values sat in the source file being read by no one. <c>DrivenAxle</c> was stale in the
    /// same way, so the rear-drive character the model was written around never applied either.</para>
    ///
    /// <para>Deliberately a separate menu item rather than part of the rebuild: overwriting the asset on
    /// every build would throw away exactly the Play-mode tuning it exists to keep. This is the button
    /// you press when the source is ahead of the asset, and it works by copying a freshly constructed
    /// instance over the old one, so the GUID survives and every reference to it holds.</para>
    ///
    /// <para><b>And why a button was not enough.</b> All of the above was already true, this menu item
    /// already existed, and the asset was still holding ice-grade grip and all-wheel drive long
    /// afterwards, because nothing ever told anyone to press it. So whether the asset is stale is no
    /// longer a person's job to notice: <see cref="VehicleConfig.Version"/> is stamped on write and
    /// checked on every domain reload by <see cref="HealOnLoad"/>. Bump
    /// <see cref="VehicleConfig.CurrentVersion"/> in the same commit that changes what a field means and
    /// the asset catches up on its own. Play-mode tuning still survives, because the version only moves
    /// when a person moves it.</para>
    /// </summary>
    public static class VehicleConfigReset
    {
        [MenuItem("Tools/Horizon/Reset Vehicle Configs to code defaults")]
        public static void Reset()
        {
            for (int i = 0; i < VehicleConfigPresets.All.Length; i++)
            {
                (string profile, string path) = VehicleConfigPresets.All[i];

                var existing = AssetDatabase.LoadAssetAtPath<VehicleConfig>(path);
                if (existing == null)
                {
                    Debug.LogError($"[Horizon] No vehicle config at {path}. Run "
                                   + "Tools > Horizon > Rebuild Prototype Scene to create it.");
                    continue;
                }

                Overwrite(existing, profile, path);
            }
        }

        /// <summary>
        /// Rewrites <paramref name="config"/> from the code defaults plus its profile's preset if it is
        /// stamped below <see cref="VehicleConfig.CurrentVersion"/>, and does nothing at all otherwise.
        ///
        /// <para>Cheap enough to call on every load, and the early return is what keeps tuning: an asset
        /// already at the current version is never touched. That early return matters more now than it
        /// did — this runs over five assets on every domain reload rather than one.</para>
        ///
        /// <para><b>It is also what stamps a brand new asset.</b> <see cref="VehicleConfig.Version"/> has
        /// no initialiser on purpose, so a config that <c>LoadOrCreate</c> has just made reads 0, counts
        /// as stale, and gets its preset applied here on first load. That is why adding four bodies did
        /// not need <see cref="VehicleConfig.CurrentVersion"/> bumped: bumping it would additionally
        /// rewrite the fastback and discard its tuning, for nothing.</para>
        /// </summary>
        public static void ResetIfStale(VehicleConfig config, string profile, string path)
        {
            if (config == null || config.Version >= VehicleConfig.CurrentVersion)
            {
                return;
            }

            int was = config.Version;
            Overwrite(config, profile, path);

            Debug.Log($"[Horizon] Vehicle config '{profile}' was stamped version {was}, below "
                      + $"{VehicleConfig.CurrentVersion} — its numbers were chosen under meanings the code "
                      + "no longer uses. Rewritten from the code defaults.");
        }

        private static void Overwrite(VehicleConfig existing, string profile, string path)
        {
            var defaults = ScriptableObject.CreateInstance<VehicleConfig>();

            // Defaults first, then the body's own character on top. The defaults are the fastback, so for
            // that one the second step deliberately does nothing.
            VehicleConfigPresets.Apply(defaults, profile);
            defaults.Version = VehicleConfig.CurrentVersion;

            // CopySerialized rather than a new asset: it replaces the contents in place, so the GUID the
            // prefab and the scene point at is untouched.
            EditorUtility.CopySerialized(defaults, existing);
            Object.DestroyImmediate(defaults);

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            // The one thing in a preset that fails silently and audibly at the same time. A cycle rate
            // that is not a whole divisor of the sample rate makes the generated engine loop tick once a
            // second, which is easy to write, hard to hear over an engine, and impossible to find later.
            if (!existing.LoopsCleanly)
            {
                Debug.LogWarning(
                    $"[Horizon] Vehicle config '{profile}' will tick once a second: "
                    + $"{existing.EngineFundamentalHz:0.##} Hz over {existing.Cylinders} cylinders is "
                    + $"{existing.CycleHz:0.###} engine cycles per second, which is not a whole number "
                    + "that divides 44100. Pick a firing frequency where it is — see the note on "
                    + "VehicleConfigPresets.Voice.");
            }

            Debug.Log($"[Horizon] Vehicle config '{profile}' reset to code defaults: "
                      + $"{existing.Mass:0} kg, drive {existing.DrivenAxle}, "
                      + $"{existing.MaxTorqueNm:0} Nm to {existing.RedlineRpm:0} rpm, "
                      + $"top {existing.TopSpeed * 3.6f:0} km/h, "
                      + $"grip {existing.LateralGrip.Evaluate(0f):0.00} falling to "
                      + $"{existing.LateralGrip.Evaluate(1f):0.00}, lock {existing.MaxSteerAngle:0}° at "
                      + $"{existing.SteerRate:0}°/s, CoM y {existing.CenterOfMass.y:0.00}, anti-roll "
                      + $"{existing.AntiRollStiffness:0}, turn-in assist {existing.TurnInAssist:0.0}.");
        }

        /// <summary>
        /// Checks every config on each domain reload, so a pull that changes what a field means brings the
        /// assets along with it without anyone having to know that it should.
        ///
        /// <para>Through <c>delayCall</c> rather than inline: the asset database is not reliably
        /// queryable while <c>InitializeOnLoad</c> is still running, and loading an asset from there
        /// returns null or a half-imported object depending on the day.</para>
        ///
        /// <para>Missing assets are passed over in silence here, unlike in <see cref="Reset"/>. Before the
        /// first rebuild only the fastback exists, and a domain reload is not the moment to complain
        /// about four files nobody has asked for yet.</para>
        /// </summary>
        [InitializeOnLoadMethod]
        private static void HealOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                for (int i = 0; i < VehicleConfigPresets.All.Length; i++)
                {
                    (string profile, string path) = VehicleConfigPresets.All[i];
                    ResetIfStale(AssetDatabase.LoadAssetAtPath<VehicleConfig>(path), profile, path);
                }
            };
        }
    }
}
