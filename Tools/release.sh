#!/usr/bin/env bash
#
# Builds a signed Horizon APK and publishes it as a GitHub Release.
#
#   ./Tools/release.sh 0.2.0
#
# Runs locally rather than in CI on purpose. Most of Assets/ is generated geometry that .gitignore
# excludes, so a fresh checkout has scenes full of dangling mesh GUIDs and would have to regenerate
# the world before it could build anything — see CLAUDE.md on Rebuild Prototype Scene.
#
# Keystore passwords come from the environment, never from a file in the repo:
#
#   export HORIZON_KEYSTORE_PASS=...
#   export HORIZON_KEYALIAS_PASS=...      # optional, defaults to HORIZON_KEYSTORE_PASS
#   export HORIZON_KEYSTORE_PATH=...      # optional, defaults to ~/.horizon/horizon-release.keystore
#   export HORIZON_KEYALIAS_NAME=...      # optional, defaults to "horizon"
#
# When HORIZON_KEYSTORE_PASS is unset the password is read from ~/.horizon/keystore-password.txt
# instead, so a release can be cut without the password passing through a shell history, a process
# argument list, or whoever happens to be reading over your shoulder.

set -euo pipefail

readonly REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$REPO/ProjectSettings/ProjectVersion.txt")"
readonly UNITY="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity"
readonly LOG="$REPO/Logs/release-build.log"

die() { echo "release: $*" >&2; exit 1; }

# --- Arguments -------------------------------------------------------------

[ $# -eq 1 ] || die "usage: $(basename "$0") MAJOR.MINOR.PATCH"

readonly VERSION="$1"
readonly TAG="v$VERSION"

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || die "'$VERSION' is not MAJOR.MINOR.PATCH."

# --- Preconditions, all of them before the build ---------------------------
#
# A twenty-minute IL2CPP build that then fails on a missing password is twenty minutes wasted, so
# everything that can be known up front is checked up front.

[ -x "$UNITY" ] || die "no Unity $UNITY_VERSION at $UNITY (install it via Unity Hub)."

command -v gh >/dev/null 2>&1 || die "the gh CLI is not installed."
gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"

readonly KEYSTORE="${HORIZON_KEYSTORE_PATH:-$HOME/.horizon/horizon-release.keystore}"
[ -f "$KEYSTORE" ] || die "no keystore at $KEYSTORE. See Tools/release.sh for how to generate one."

readonly PASS_FILE="${HORIZON_KEYSTORE_PASS_FILE:-$HOME/.horizon/keystore-password.txt}"
if [ -z "${HORIZON_KEYSTORE_PASS:-}" ] && [ -f "$PASS_FILE" ]; then
  # Only the first line, stripped: a password file written with a text editor almost always ends in
  # a newline, and a trailing newline in a keystore password fails as "wrong password" with no hint
  # that the password itself was right.
  HORIZON_KEYSTORE_PASS="$(head -n 1 "$PASS_FILE" | tr -d '[:space:]')"
  export HORIZON_KEYSTORE_PASS
fi

[ -n "${HORIZON_KEYSTORE_PASS:-}" ] \
  || die "no password: HORIZON_KEYSTORE_PASS is unset and $PASS_FILE does not exist."

cd "$REPO"

[ -z "$(git status --porcelain)" ] || die "the working tree is dirty. Commit or stash first."

readonly BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[ "$BRANCH" = "main" ] || die "on branch '$BRANCH'. Releases are cut from main."

git rev-parse -q --verify "refs/tags/$TAG" >/dev/null \
  && die "tag $TAG already exists."

gh release view "$TAG" >/dev/null 2>&1 \
  && die "a GitHub release $TAG already exists."

# Batch mode cannot open a project the editor is holding, and the error it gives for that is a wall
# of stack trace that says nothing about the real cause.
[ ! -f "$REPO/Temp/UnityLockfile" ] \
  || die "the Unity editor has this project open. Close it and try again."

# --- Build -----------------------------------------------------------------

echo "release: building $VERSION with Unity $UNITY_VERSION (this takes a while; tailing $LOG)"

mkdir -p "$REPO/Logs"
rm -f "$REPO/Horizon.apk"

# No -nographics: shader compilation wants a graphics device, and this project has been bitten by
# headless Unity producing blank output before.
HORIZON_VERSION="$VERSION" "$UNITY" \
  -batchmode \
  -quit \
  -projectPath "$REPO" \
  -executeMethod Horizon.EditorTools.AndroidBuild.BuildRelease \
  -logFile "$LOG" \
  || die "the Unity build failed. Last lines of $LOG:
$(tail -n 40 "$LOG")"

[ -f "$REPO/Horizon.apk" ] || die "Unity exited 0 but produced no APK. See $LOG."

# The editor should have cleared these again, but a committed password is not the kind of mistake
# that gets a second chance once it is pushed.
if git diff --quiet -- ProjectSettings/ProjectSettings.asset; then
  :
elif git diff -- ProjectSettings/ProjectSettings.asset | grep -qE '^\+\s*(AndroidKeystoreName|AndroidKeyaliasName):\s*\S'; then
  die "the build left keystore details in ProjectSettings.asset. Revert that file before releasing."
fi

readonly ASSET="$REPO/Horizon-$VERSION.apk"
cp "$REPO/Horizon.apk" "$ASSET"

echo "release: built $(du -h "$ASSET" | cut -f1) at $ASSET"

# --- Publish ---------------------------------------------------------------

# The version bump that Unity wrote into ProjectSettings.asset belongs in the tagged commit, so the
# repo says what each release shipped.
if ! git diff --quiet -- ProjectSettings/ProjectSettings.asset; then
  git add ProjectSettings/ProjectSettings.asset
  git commit -m "Set version to $VERSION"
fi

git tag -a "$TAG" -m "Horizon $VERSION"
git push origin main --follow-tags

gh release create "$TAG" "$ASSET" \
  --title "Horizon $VERSION" \
  --notes "Android APK, ARM64 only. Sideload it: download on the phone, then allow your browser to install unknown apps when prompted.

Requires Android 7.1 (API 25) or newer."

echo "release: published $(gh release view "$TAG" --json url --jq .url)"
