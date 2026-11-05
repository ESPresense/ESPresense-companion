#!/usr/bin/env bash
# Cut a beta prerelease or promote a beta to stable, from Release Drafter's draft.
#
#   release.sh beta   [ref]   tag vX.Y.Z-beta.N at ref (default origin/main) as a prerelease
#   release.sh stable [ref]   publish the draft as vX.Y.Z at ref (default: the newest beta's commit)
#
# X.Y.Z is the draft's version. Notes are the draft's lines for PRs that are in
# ref; a beta lists only PRs that are new since the previous beta of X.Y.Z.
# DRY_RUN=1 prints what would happen and changes nothing.
# Writes tag=, sha=, prerelease= to $GITHUB_OUTPUT when set.
set -euo pipefail

CHANNEL=${1:?usage: release.sh beta|stable [ref]}
REF=${2:-}
REPO=${GITHUB_REPOSITORY:-ESPresense/ESPresense-companion}
DRY_RUN=${DRY_RUN:-}

die() { echo "::error::$*" >&2; exit 1; }

git fetch --quiet --tags --force origin

DRAFT=$(gh api "repos/$REPO/releases" --paginate --jq '.[] | select(.draft)' | jq -s '.[0] // empty')
[[ -n "$DRAFT" ]] || die "No Release Drafter draft found. Merge something to main first."
DRAFT_ID=$(jq -r .id <<<"$DRAFT")
DRAFT_TAG=$(jq -r .tag_name <<<"$DRAFT")
VERSION=${DRAFT_TAG#v}
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Draft tag $DRAFT_TAG is not vX.Y.Z"

# Betas of this version, oldest first.
BETAS=$(git tag -l "v$VERSION-beta.*" | sort -t. -k4,4n)
LAST_BETA=$(tail -n1 <<<"$BETAS")

case "$CHANNEL" in
  beta)
    SHA=$(git rev-parse --verify "${REF:-origin/main}^{commit}")
    if [[ -n "$LAST_BETA" ]]; then
      LAST_SHA=$(git rev-parse "$LAST_BETA^{commit}")
      [[ "$LAST_SHA" != "$SHA" ]] || die "$LAST_BETA is already at ${SHA::7}"
      git merge-base --is-ancestor "$LAST_SHA" "$SHA" || die "${SHA::7} does not contain $LAST_BETA"
      N=$(( ${LAST_BETA##*.} + 1 ))
    else
      N=1
    fi
    TAG="v$VERSION-beta.$N"
    SINCE=${LAST_BETA:+$LAST_SHA}
    PRERELEASE=true
    ;;
  stable)
    if [[ -n "$REF" ]]; then
      SHA=$(git rev-parse --verify "$REF^{commit}")
    else
      [[ -n "$LAST_BETA" ]] || die "No v$VERSION-beta.* to promote. Cut a beta first, or pass a ref."
      SHA=$(git rev-parse "$LAST_BETA^{commit}")
    fi
    git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null && die "v$VERSION already exists"
    TAG="v$VERSION"
    SINCE=
    PRERELEASE=false
    ;;
  *) die "channel must be beta or stable, not $CHANNEL" ;;
esac

git merge-base --is-ancestor "$SHA" origin/main || die "${SHA::7} is not on main"

# Keep the draft's PR lines whose merge commit is in SHA (and not in SINCE).
NOTES=$(mktemp)
while IFS= read -r line; do
  if [[ "$line" =~ \(#([0-9]+)\)[[:space:]]*$ ]]; then
    PR=${BASH_REMATCH[1]}
    M=$(gh api "repos/$REPO/pulls/$PR" --jq '.merge_commit_sha // empty' 2>/dev/null || true)
    if [[ -z "$M" ]] || ! git merge-base --is-ancestor "$M" "$SHA" 2>/dev/null; then
      echo "  not in ${SHA::7}: #$PR" >&2; continue
    fi
    if [[ -n "$SINCE" ]] && git merge-base --is-ancestor "$M" "$SINCE"; then
      echo "  already in $LAST_BETA: #$PR" >&2; continue
    fi
  fi
  printf '%s\n' "$line"
done < <(jq -r .body <<<"$DRAFT" | tr -d '\r') > "$NOTES"

if [[ "$CHANNEL" == beta ]]; then
  {
    echo
    if [[ -n "$SINCE" ]]; then echo "Changes since $LAST_BETA."; fi
    echo "Pre-release: install the **ESPresense Companion - BETA** add-on, or run \`espresense/espresense-companion:beta\`."
  } >> "$NOTES"
fi

echo "== $TAG at $(git log -1 --format='%h %s' "$SHA")"
cat "$NOTES"

if [[ -n "$DRY_RUN" ]]; then
  echo "(dry run: nothing published)"
elif [[ "$CHANNEL" == beta ]]; then
  gh release create "$TAG" --repo "$REPO" --target "$SHA" --prerelease --latest=false \
    --title "$TAG" --notes-file "$NOTES"
else
  jq -n --arg tag "$TAG" --arg sha "$SHA" --rawfile body "$NOTES" \
    '{tag_name: $tag, name: $tag, target_commitish: $sha, body: $body,
      draft: false, prerelease: false, make_latest: "true"}' |
    gh api -X PATCH "repos/$REPO/releases/$DRAFT_ID" --input - --jq .html_url
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  { echo "tag=$TAG"; echo "sha=$SHA"; echo "prerelease=$PRERELEASE"; } >> "$GITHUB_OUTPUT"
fi
