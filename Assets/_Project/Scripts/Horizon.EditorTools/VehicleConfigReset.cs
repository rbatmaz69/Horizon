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
        private const string AssetPath = "Assets/_Project/Settings/VehicleConfig_Prototype.asset";

        [MenuItem("Tools/Horizon/Reset Vehicle Config to code defaults")]
        public static void Reset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<VehicleConfig>(AssetPath);
            if (existing == null)
            {
                Debug.LogError($"[Horizon] No vehicle config at {AssetPath}.");
                return;
            }

            Overwrite(existing);
        }

        /// <summary>
        /// Rewrites <paramref name="config"/> from the code defaults if it is stamped below
        /// <see cref="VehicleConfig.CurrentVersion"/>, and does nothing at all otherwise.
        ///
        /// <para>Cheap enough to call on every load, and the early return is what keeps tuning: an asset
        /// already at the current version is never touched.</para>
        /// </summary>
        public static void ResetIfStale(VehicleConfig config)
        {
            if (config == null || config.Version >= VehicleConfig.CurrentVersion)
            {
                return;
            }

            int was = config.Version;
            Overwrite(config);

            Debug.Log($"[Horizon] Vehicle config was stamped version {was}, below "
                      + $"{VehicleConfig.CurrentVersion} — its numbers were chosen under meanings the code "
                      + "no longer uses. Rewritten from the code defaults.");
        }

        private static void Overwrite(VehicleConfig existing)
        {
            var defaults = ScriptableObject.CreateInstance<VehicleConfig>();
            defaults.Version = VehicleConfig.CurrentVersion;

            // CopySerialized rather than a new asset: it replaces the contents in place, so the GUID the
            // prefab and the scene point at is untouched.
            EditorUtility.CopySerialized(defaults, existing);
            Object.DestroyImmediate(defaults);

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);

            Debug.Log($"[Horizon] Vehicle config reset to code defaults: drive {existing.DrivenAxle}, "
                      + $"grip {existing.LateralGrip.Evaluate(0f):0.00} falling to "
                      + $"{existing.LateralGrip.Evaluate(1f):0.00}, lock {existing.MaxSteerAngle:0}° at "
                      + $"{existing.SteerRate:0}°/s, angular damping {existing.AngularDamping:0.00} with "
                      + $"roll/pitch at {existing.RollPitchDamping:0.0}, turn-in assist "
                      + $"{existing.TurnInAssist:0.0}.");
        }

        /// <summary>
        /// Checks the asset on every domain reload, so a pull that changes what a field means brings the
        /// asset along with it without anyone having to know that it should.
        ///
        /// <para>Through <c>delayCall</c> rather than inline: the asset database is not reliably
        /// queryable while <c>InitializeOnLoad</c> is still running, and loading an asset from there
        /// returns null or a half-imported object depending on the day.</para>
        /// </summary>
        [InitializeOnLoadMethod]
        private static void HealOnLoad()
        {
            EditorApplication.delayCall += () =>
                ResetIfStale(AssetDatabase.LoadAssetAtPath<VehicleConfig>(AssetPath));
        }
    }
}
