#!/usr/bin/env bash
# Usage: testnet/export.sh <git-ref> <dir>
# Exports a ref as a clean build context. The UI build runs git rev-parse, so it gets a one-commit repo.
set -euo pipefail

ref=$1 dir=$2
sha=$(git rev-parse --verify "$ref^{commit}" 2>/dev/null || git rev-parse --verify "origin/$ref^{commit}")
rm -rf "$dir" && mkdir -p "$dir"
git -C "$(git rev-parse --show-toplevel)" archive "$sha" | tar -x -C "$dir"
git -C "$dir" init -q
git -C "$dir" -c user.name=testnet -c user.email=testnet@localhost commit -q --allow-empty -m "$sha"
echo "$sha"
