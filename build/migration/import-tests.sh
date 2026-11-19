#!/usr/bin/env bash
set -euo pipefail

readonly expected_source_commit="8ed19081c972a80a8b6996ed817581bc59cbcb4b"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
source_port="${1:-$repo_root/../MissionPlanner-Avalonia}"

if [[ "$(git -C "$source_port" rev-parse HEAD)" != "$expected_source_commit" ]]; then
  echo "The Avalonia test source is not at $expected_source_commit." >&2
  exit 1
fi
if ! git -C "$source_port" diff --quiet || ! git -C "$source_port" diff --cached --quiet; then
  echo "The Avalonia test source worktree is dirty; refusing an ambiguous import." >&2
  exit 1
fi
if [[ "$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)" != "port/avalonia-in-place" ]]; then
  echo "Run the import only on port/avalonia-in-place." >&2
  exit 1
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT
record="$temporary_dir/IMPORTED_TESTS.tsv"
printf 'source_path\tnative_path\tsource_blob\tnamespace_migration\n' > "$record"
imported=0
skipped=0

while IFS= read -r source_path; do
  [[ "$source_path" == tests/* ]] || continue
  [[ "$source_path" != *.csproj ]] || continue

  remainder="${source_path#tests/}"
  suite="${remainder%%/*}"
  relative="${remainder#*/}"
  native_suite="${suite/MissionPlannerAvalonia/MissionPlanner}"
  target_path="MissionPlannerTests/Avalonia/$native_suite/$relative"
  destination="$repo_root/$target_path"
  source_blob="$(git -C "$source_port" rev-parse "$expected_source_commit:$source_path")"

  original="$temporary_dir/original"
  candidate="$temporary_dir/candidate"
  git -C "$source_port" show "$expected_source_commit:$source_path" > "$original"
  sed 's/MissionPlannerAvalonia/MissionPlanner/g' "$original" > "$candidate"

  if [[ -e "$destination" ]]; then
    if cmp -s "$candidate" "$destination"; then
      skipped=$((skipped + 1))
    else
      echo "Refusing to overwrite an unexpected test target: $target_path" >&2
      exit 1
    fi
  else
    install -D -m 0644 "$candidate" "$destination"
    imported=$((imported + 1))
  fi

  printf '%s\t%s\t%s\t%s\n' "$source_path" "$target_path" "$source_blob" \
    'MissionPlannerAvalonia->MissionPlanner' >> "$record"
done < <(git -C "$source_port" ls-files 'tests/**')

mv "$record" "$repo_root/docs/migration/IMPORTED_TESTS.tsv"
echo "Imported $imported test files; $skipped already matched."
