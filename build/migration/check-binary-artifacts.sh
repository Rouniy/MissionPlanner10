#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
audit="$repo_root/docs/migration/BINARY_ARTIFACT_AUDIT.tsv"

fail() {
  echo "binary artifact audit: $*" >&2
  exit 1
}

[[ -f "$audit" ]] || fail "missing ${audit#$repo_root/}"
[[ $(head -n 1 "$audit") == $'path\tdecision\tevidence\tnote' ]] || fail "invalid header"

declare -A seen=()
declare -A retained=()
row_count=0
while IFS=$'\t' read -r path decision evidence note; do
  [[ -n "$path" ]] || fail "empty path"
  [[ -z "${seen[$path]+x}" ]] || fail "duplicate path: $path"
  seen[$path]=1
  ((row_count += 1))
  [[ "$decision" == "retain" || "$decision" == "remove" ]] || fail "invalid decision for $path: $decision"
  [[ -n "$evidence" && -e "$repo_root/$evidence" ]] || fail "missing evidence for $path: $evidence"
  [[ -n "$note" ]] || fail "missing note for $path"

  if [[ "$decision" == "retain" ]]; then
    [[ -f "$repo_root/$path" ]] || fail "retained binary is missing: $path"
    git -C "$repo_root" ls-files --error-unmatch -- "$path" >/dev/null 2>&1 ||
      fail "retained binary is not tracked: $path"
    retained[$path]=1
  else
    [[ ! -e "$repo_root/$path" ]] || fail "removed binary still exists: $path"
    [[ -z $(git -C "$repo_root" ls-files -- "$path") ]] || fail "removed binary is still tracked: $path"
  fi
done < <(tail -n +2 "$audit")

((row_count > 0)) || fail "audit is empty"

while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  [[ -n "${retained[$path]+x}" ]] || fail "unaudited tracked binary: $path"
done < <(git -C "$repo_root" ls-files | grep -Ei '\.(dll|exe|pdb|so|dylib)$' | sort || true)

echo "binary artifact audit: PASS ($row_count explicit decisions, ${#retained[@]} retained binaries)"
