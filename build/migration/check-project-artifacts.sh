#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
audit="$repo_root/docs/migration/PROJECT_ARTIFACT_AUDIT.tsv"

fail() {
  echo "project artifact audit: $*" >&2
  exit 1
}

[[ -f "$audit" ]] || fail "missing ${audit#$repo_root/}"
[[ $(head -n 1 "$audit") == $'path\tdecision\tevidence\tnote' ]] || fail "invalid header"

declare -A seen=()
declare -A retained_metadata=()
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
    [[ -e "$repo_root/$path" ]] || fail "retained path is missing: $path"
    git -C "$repo_root" ls-files --error-unmatch -- "$path" >/dev/null 2>&1 ||
      fail "retained path is not tracked: $path"
    case "$path" in
      *.csproj|*.sln|*.slnx|*.vcxproj|*.vcxproj.filters) retained_metadata[$path]=1 ;;
    esac
  else
    [[ ! -e "$repo_root/$path" ]] || fail "removed path still exists: $path"
    [[ -z $(git -C "$repo_root" ls-files -- "$path") ]] || fail "removed path is still tracked: $path"
  fi
done < <(tail -n +2 "$audit")

((row_count > 0)) || fail "audit is empty"

declare -A active_metadata=([MissionPlanner.slnx]=1)
while IFS= read -r path; do
  [[ -n "$path" ]] && active_metadata[$path]=1
done < <(sed -n 's/.*<Project Path="\([^"]*\)".*/\1/p' "$repo_root/MissionPlanner.slnx")

while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  if [[ -z "${active_metadata[$path]+x}" && -z "${retained_metadata[$path]+x}" ]]; then
    fail "unaudited project/build metadata: $path"
  fi
done < <(git -C "$repo_root" ls-files '*.csproj' '*.sln' '*.slnx' '*.vcxproj' '*.vcxproj.filters' | sort)

for path in "${!active_metadata[@]}"; do
  [[ -e "$repo_root/$path" ]] || fail "active solution path is missing: $path"
done

echo "project artifact audit: PASS ($row_count explicit decisions, ${#active_metadata[@]} active metadata files)"
