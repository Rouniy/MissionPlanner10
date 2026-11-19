#!/usr/bin/env bash
set -euo pipefail

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root_dir"

readonly source_commit=8ed19081c972a80a8b6996ed817581bc59cbcb4b
readonly source_tree=a31f0d7fd49966907cb61680c049f4b2cc461e48
readonly source_count=708
readonly source_digest=4cccd6d0d5187a0cd7d9e73e8bfa2cd268e14e0483cd0b727385fb0e678c444b

fail() {
  echo "port-source resolution check failed: $*" >&2
  exit 1
}

require_header() {
  local file=$1
  local expected=$2
  local actual
  actual=$(head -n 1 "$file")
  [[ "$actual" == "$expected" ]] || fail "$file has an unexpected header"
}

require_header docs/migration/IMPORTED_APPLICATION.tsv $'source_path\tnative_path\tsource_blob\tnamespace_migration'
require_header docs/migration/IMPORTED_TESTS.tsv $'source_path\tnative_path\tsource_blob\tnamespace_migration'
require_header docs/migration/IMPORTED_REFERENCE.tsv $'source_path\tnative_path\tsource_blob'
require_header docs/migration/PORT_SOURCE_RESOLUTION.tsv $'source_path\tsource_blob\tresolution\tnative_evidence\tnotes'

canonical=$(
  {
    awk -F '\t' 'NR > 1 { print $1 "\t" $3 }' docs/migration/IMPORTED_APPLICATION.tsv
    awk -F '\t' 'NR > 1 { print $1 "\t" $3 }' docs/migration/IMPORTED_TESTS.tsv
    awk -F '\t' 'NR > 1 { print $1 "\t" $3 }' docs/migration/IMPORTED_REFERENCE.tsv
    awk -F '\t' 'NR > 1 { print $1 "\t" $2 }' docs/migration/PORT_SOURCE_RESOLUTION.tsv
  } | LC_ALL=C sort
)

actual_count=$(printf '%s\n' "$canonical" | awk 'NF { count++ } END { print count + 0 }')
[[ "$actual_count" == "$source_count" ]] ||
  fail "manifests cover $actual_count source paths; expected $source_count"

unique_count=$(printf '%s\n' "$canonical" | cut -f 1 | LC_ALL=C sort -u | wc -l)
[[ "$unique_count" == "$source_count" ]] || fail "source paths are missing or duplicated"

invalid_blobs=$(printf '%s\n' "$canonical" | awk -F '\t' '$2 !~ /^[0-9a-f]{40}$/ { print $1 }')
[[ -z "$invalid_blobs" ]] || fail "invalid source blob(s): $invalid_blobs"

actual_digest=$(printf '%s\n' "$canonical" | sha256sum | cut -d ' ' -f 1)
[[ "$actual_digest" == "$source_digest" ]] ||
  fail "pinned source path/blob digest changed: $actual_digest"

planned_paths=$(awk -F '\t' 'NR > 1 { print $1 }' docs/migration/PORT_SOURCE_IMPORT.tsv | LC_ALL=C sort)
covered_paths=$(printf '%s\n' "$canonical" | cut -f 1)
if [[ "$planned_paths" != "$covered_paths" ]]; then
  diff -u <(printf '%s\n' "$planned_paths") <(printf '%s\n' "$covered_paths") || true
  fail "the final manifests do not cover the complete import plan"
fi

while IFS=$'\t' read -r source_path native_path source_blob namespace_migration; do
  [[ -n "$source_path" && -n "$native_path" && "$source_blob" =~ ^[0-9a-f]{40}$ ]] ||
    fail "invalid imported application row for $source_path"
  [[ -f "$native_path" ]] || fail "missing imported application target: $native_path"
done < <(tail -n +2 docs/migration/IMPORTED_APPLICATION.tsv)

while IFS=$'\t' read -r source_path native_path source_blob namespace_migration; do
  [[ -n "$source_path" && -n "$native_path" && "$source_blob" =~ ^[0-9a-f]{40}$ ]] ||
    fail "invalid imported test row for $source_path"
  [[ -f "$native_path" ]] || fail "missing imported test target: $native_path"
done < <(tail -n +2 docs/migration/IMPORTED_TESTS.tsv)

while IFS=$'\t' read -r source_path native_path source_blob; do
  [[ -f "$native_path" ]] || fail "missing imported reference target: $native_path"
  actual_blob=$(git hash-object "$native_path")
  [[ "$actual_blob" == "$source_blob" ]] ||
    fail "reference target is no longer byte-identical: $native_path"
done < <(tail -n +2 docs/migration/IMPORTED_REFERENCE.tsv)

while IFS=$'\t' read -r source_path source_blob resolution native_evidence notes; do
  case "$resolution" in
    exact|merge|retire) ;;
    *) fail "unknown resolution '$resolution' for $source_path" ;;
  esac

  [[ -n "$notes" ]] || fail "missing resolution note for $source_path"
  IFS=';' read -r -a evidence_paths <<< "$native_evidence"
  [[ "${#evidence_paths[@]}" -gt 0 ]] || fail "missing native evidence for $source_path"
  for evidence_path in "${evidence_paths[@]}"; do
    [[ -e "$evidence_path" ]] || fail "missing native evidence $evidence_path for $source_path"
  done

  if [[ "$resolution" == exact ]]; then
    [[ "${#evidence_paths[@]}" == 1 && -f "${evidence_paths[0]}" ]] ||
      fail "exact resolution must have one file target for $source_path"
    actual_blob=$(git hash-object "${evidence_paths[0]}")
    [[ "$actual_blob" == "$source_blob" ]] ||
      fail "exact target ${evidence_paths[0]} changed for $source_path"
  fi
done < <(tail -n +2 docs/migration/PORT_SOURCE_RESOLUTION.tsv)

if [[ $# -gt 1 ]]; then
  fail "usage: $0 [path-to-MissionPlanner-Avalonia]"
fi

if [[ $# == 1 ]]; then
  source_dir=$(realpath "$1")
  [[ -d "$source_dir/.git" ]] || fail "$source_dir is not a Git worktree"
  actual_commit=$(git -C "$source_dir" rev-parse HEAD)
  [[ "$actual_commit" == "$source_commit" ]] ||
    fail "source worktree is $actual_commit; expected $source_commit"
  actual_tree=$(git -C "$source_dir" rev-parse HEAD^{tree})
  [[ "$actual_tree" == "$source_tree" ]] || fail "source tree changed: $actual_tree"
  source_status=$(git -C "$source_dir" status --short)
  [[ -z "$source_status" ]] || fail "source worktree is dirty"
  tree_digest=$(
    git -C "$source_dir" ls-tree -r "$source_commit" |
      awk '{ print $4 "\t" $3 }' | LC_ALL=C sort | sha256sum | cut -d ' ' -f 1
  )
  [[ "$tree_digest" == "$source_digest" ]] || fail "source tree path/blob digest changed"
fi

echo "Port source resolution is complete: 708/708 pinned paths, digest $source_digest."
