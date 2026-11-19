#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
audit="$repo_root/docs/migration/KEY_ARTIFACT_AUDIT.tsv"

fail() {
  echo "key artifact audit: $*" >&2
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
    [[ -f "$repo_root/$path" ]] || fail "retained key/certificate artifact is missing: $path"
    git -C "$repo_root" ls-files --error-unmatch -- "$path" >/dev/null 2>&1 ||
      fail "retained key/certificate artifact is not tracked: $path"
    retained[$path]=1
  else
    [[ ! -e "$repo_root/$path" ]] || fail "removed key artifact still exists: $path"
    [[ -z $(git -C "$repo_root" ls-files -- "$path") ]] || fail "removed key artifact is still tracked: $path"
  fi
done < <(tail -n +2 "$audit")

while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  [[ -n "${retained[$path]+x}" ]] || fail "unaudited tracked key/certificate artifact: $path"
done < <(git -C "$repo_root" ls-files | grep -Ei '\.(snk|pfx|p12|cer|key|pem|pub)$' | sort || true)

echo "key artifact audit: PASS ($row_count explicit decisions, ${#retained[@]} retained artifacts)"
