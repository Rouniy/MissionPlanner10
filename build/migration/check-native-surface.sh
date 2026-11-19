#!/usr/bin/env bash
set -euo pipefail

readonly native_baseline_commit="67a3c4f22bd1b38ac499f9756902e04fa4ed8444"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
manifest="$repo_root/docs/migration/NATIVE_SURFACE.tsv"
require_closed=false

case "${1:-}" in
  "") ;;
  --require-closed) require_closed=true ;;
  *) echo "Usage: $0 [--require-closed]" >&2; exit 2 ;;
esac

if [[ ! -f "$manifest" ]]; then
  echo "Missing native surface manifest: $manifest" >&2
  exit 1
fi
if ! git -C "$repo_root" cat-file -e "$native_baseline_commit^{tree}"; then
  echo "Native migration baseline is unavailable: $native_baseline_commit" >&2
  exit 1
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT

baseline_paths="$temporary_dir/baseline-paths"
manifest_paths="$temporary_dir/manifest-paths"

LC_ALL=C git -C "$repo_root" ls-tree -r --name-only "$native_baseline_commit" |
  LC_ALL=C awk '($0 ~ /\.cs$/ || $0 ~ /\.resx$/) &&
      $0 !~ /^ExtLibs\// && $0 !~ /^MissionPlannerTests\//' |
  LC_ALL=C sort > "$baseline_paths"

LC_ALL=C awk -F '\t' '
  NR == 1 {
    expected = "native_path\tkind\tstatus\tported_candidates\tevidence_or_next_action"
    if ($0 != expected) {
      print "Unexpected manifest header: " $0 > "/dev/stderr"
      bad = 1
    }
    next
  }
  NF != 5 {
    print "Manifest row does not have five fields at line " NR > "/dev/stderr"
    bad = 1
  }
  $2 != "csharp" && $2 != "resx" {
    print "Invalid kind at line " NR ": " $2 > "/dev/stderr"
    bad = 1
  }
  $3 != "retain" && $3 != "replace" && $3 != "merge" &&
      $3 != "remove" && $3 != "unported-blocker" {
    print "Invalid status at line " NR ": " $3 > "/dev/stderr"
    bad = 1
  }
  seen[$1]++ {
    print "Duplicate native path at line " NR ": " $1 > "/dev/stderr"
    bad = 1
  }
  $5 == "" {
    print "Missing evidence at line " NR ": " $1 > "/dev/stderr"
    bad = 1
  }
  { print $1 }
  END { exit bad }
' "$manifest" | LC_ALL=C sort > "$manifest_paths"

if ! diff -u "$baseline_paths" "$manifest_paths"; then
  echo "NATIVE_SURFACE.tsv no longer represents the complete frozen native baseline." >&2
  exit 1
fi

blockers="$(awk -F '\t' 'NR > 1 && $3 == "unported-blocker" { count++ } END { print count + 0 }' "$manifest")"

missing_candidates=0
while IFS=$'\t' read -r native_path kind status candidates evidence; do
  [[ "$native_path" == "native_path" ]] && continue
  if [[ "$status" == "replace" || "$status" == "merge" || "$status" == "remove" ]]; then
    if [[ -z "$candidates" ]]; then
      echo "Mapped row has no replacement/evidence path: $native_path" >&2
      missing_candidates=$((missing_candidates + 1))
      continue
    fi
    IFS=';' read -r -a candidate_paths <<< "$candidates"
    for candidate in "${candidate_paths[@]}"; do
      if [[ ! -e "$repo_root/$candidate" ]]; then
        echo "Mapped candidate does not exist for $native_path: $candidate" >&2
        missing_candidates=$((missing_candidates + 1))
      fi
    done
  fi
done < "$manifest"
if (( missing_candidates != 0 )); then
  echo "Native surface has $missing_candidates missing mapped candidate(s)." >&2
  exit 1
fi

if [[ "$require_closed" == true ]]; then
  if (( blockers != 0 )); then
    echo "Native surface is not closed: $blockers unported blocker(s) remain." >&2
    exit 1
  fi

  remaining_legacy=0
  while IFS=$'\t' read -r native_path kind status candidates evidence; do
    [[ "$native_path" == "native_path" ]] && continue
    [[ "$kind" == "csharp" ]] || continue
    if [[ "$status" == "replace" || "$status" == "remove" ]]; then
      if [[ -e "$repo_root/$native_path" ]]; then
        echo "Mapped legacy C# file still exists: $native_path" >&2
        remaining_legacy=$((remaining_legacy + 1))
      fi
    fi
  done < "$manifest"
  if (( remaining_legacy != 0 )); then
    echo "Native surface is classified but $remaining_legacy replaced/removed C# file(s) remain." >&2
    exit 1
  fi
fi

rows="$(( $(wc -l < "$manifest") - 1 ))"
echo "Native surface manifest is structurally valid: $rows rows, $blockers blocker(s)."
