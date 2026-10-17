#!/usr/bin/env bash
# release-metadata.sh: decides whether this CI run publishes a catcom mod release, and under which
# version. Writes publish/version/modversion/tag/title/prerelease/channel to $GITHUB_OUTPUT.
#
#   push or manual run on main           -> release    "catcom 1.<run number>.0"       (channel main)
#   push or manual run on any other branch -> prerelease "catcom 1.<run number>.0-beta" (channel beta)
#   pull requests, tags, natives runs    -> build only (publish=false)
#
# A natives run is a manual run with publish_tag set ($PUBLISH_TAG): it publishes the natives to
# meow-sci/catcom-native and nothing here. The major is fixed at 1 and the patch at 0; the minor is
# GitHub's run number for this workflow, which only grows (reruns keep theirs).
set -euo pipefail

# Only branch pushes and manual branch runs may publish. PRs and tags build only.
if [[ "$GITHUB_EVENT_NAME" != push && "$GITHUB_EVENT_NAME" != workflow_dispatch ]] ||
   [[ "$GITHUB_REF" != refs/heads/* ]] || [[ -n "${PUBLISH_TAG:-}" ]]; then
  echo 'publish=false' >> "$GITHUB_OUTPUT"
  exit 0
fi
BRANCH="${GITHUB_REF#refs/heads/}"
PRERELEASE=true
CHANNEL=beta
if [[ "$BRANCH" == main ]]; then
  PRERELEASE=false
  CHANNEL=main
fi

if [[ ! "${GITHUB_RUN_NUMBER:-}" =~ ^[1-9][0-9]*$ ]]; then
  echo '::error::Invalid workflow run number' >&2
  exit 1
fi
MAJOR=1
NAME="$MAJOR.$GITHUB_RUN_NUMBER.0"
if [[ "$PRERELEASE" == true ]]; then
  NAME+=-beta
fi
{
  echo 'publish=true'
  echo "version=$NAME"
  echo "modversion=$NAME"
  echo "tag=v$NAME"
  echo "title=catcom $NAME"
  echo "prerelease=$PRERELEASE"
  echo "channel=$CHANNEL"
} >> "$GITHUB_OUTPUT"
