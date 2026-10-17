#!/usr/bin/env bash
# publish-release.sh: creates the catcom mod release for this run (see tools/release-metadata.sh).
# Env: ZIP (the packaged mod folder), TAG, TITLE, PRERELEASE, GITHUB_REPOSITORY, GITHUB_SHA, GH_TOKEN.
set -euo pipefail

if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  # Reruns keep their version and preserve an already-published release.
  echo "Preserving existing release $TAG"
  exit 0
fi

EXTRA=(--repo "$GITHUB_REPOSITORY")
[[ "$PRERELEASE" == "true" ]] && EXTRA+=(--prerelease --latest=false)
gh release create "$TAG" "$ZIP" \
  --target "$GITHUB_SHA" \
  --title "$TITLE" \
  --notes "**Installing over an existing install:** delete the old \`catcom/\` folder from your mods directory before unzipping — files removed from the mod are not removed by unzipping over it." \
  --generate-notes \
  "${EXTRA[@]}"
