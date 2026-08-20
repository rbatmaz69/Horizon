namespace Horizon.Updates
{
    /// <summary>
    /// Turns a release tag into the number Android compares.
    ///
    /// <para><b>Deliberately the same encoding as <c>AndroidBuild.TryVersionCode</c>.</b> That method is
    /// what stamps <c>bundleVersionCode</c> into every APK, and <c>bundleVersionCode</c> is the only
    /// thing the Android package installer looks at when it decides whether an install is an upgrade or
    /// a refusal. Deriving the update check from the same arithmetic means a release this class calls
    /// newer is a release the installer will actually accept over the running one — a version comparison
    /// that agreed with itself but not with Android would offer downloads that end at "app not
    /// installed".</para>
    ///
    /// <para>It is a copy rather than a reference because <c>AndroidBuild</c> lives in
    /// <c>Horizon.EditorTools</c>, which is Editor-only and cannot be linked into a player build. If one
    /// side changes, the other has to change with it.</para>
    /// </summary>
    public static class ReleaseVersion
    {
        /// <summary>
        /// Parses <c>0.2.0</c> or <c>v0.2.0</c> into <c>200</c>.
        ///
        /// <para>Minor and patch are capped at 99 for the same reason the build tool caps them: two
        /// decimal digits each is what the encoding has room for, and 1.0.100 colliding with 1.1.0 is
        /// the kind of bug that only shows up as a phone quietly refusing an update.</para>
        /// </summary>
        public static bool TryParse(string text, out int code)
        {
            code = 0;

            string trimmed = WithoutPrefix(text);
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            string[] parts = trimmed.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch))
            {
                return false;
            }

            if (major < 0 || minor < 0 || minor > 99 || patch < 0 || patch > 99)
            {
                return false;
            }

            code = (major * 10000) + (minor * 100) + patch;

            return code > 0;
        }

        /// <summary>
        /// Strips the <c>v</c> a git tag carries and the whitespace a hand-edited version field does.
        ///
        /// <para>Tags are <c>v0.2.0</c> because that is what <c>release.sh</c> writes; the version the
        /// player is shown is <c>0.2.0</c>, because a leading v in a sentence about which version you are
        /// running reads as a typo.</para>
        /// </summary>
        public static string WithoutPrefix(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();

            return trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V')
                ? trimmed.Substring(1)
                : trimmed;
        }
    }
}
