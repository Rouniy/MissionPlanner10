#!/usr/bin/env bash
set -euo pipefail

readonly expected_source_commit="8ed19081c972a80a8b6996ed817581bc59cbcb4b"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
source_port="${1:-$repo_root/../MissionPlanner-Avalonia}"
source_manifest="$repo_root/docs/migration/PORT_SOURCE_IMPORT.tsv"

if [[ "$(git -C "$source_port" rev-parse HEAD)" != "$expected_source_commit" ]]; then
  echo "The Avalonia reference source is not at $expected_source_commit." >&2
  exit 1
fi
if ! git -C "$source_port" diff --quiet || ! git -C "$source_port" diff --cached --quiet; then
  echo "The Avalonia reference source is dirty; refusing an ambiguous import." >&2
  exit 1
fi
if [[ "$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)" != "port/avalonia-in-place" ]]; then
  echo "Run the import only on port/avalonia-in-place." >&2
  exit 1
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT
record="$temporary_dir/IMPORTED_REFERENCE.tsv"
printf 'source_path\tnative_path\tsource_blob\n' > "$record"
imported=0
skipped=0

while IFS=$'\t' read -r source_path target_path action notes; do
  [[ "$source_path" == "source_path" ]] && continue
  [[ "$source_path" == docs/* ]] || continue
  [[ "$target_path" == docs/migration/Reference/* ]] || continue
  [[ "$action" == "import" ]] || continue

  if [[ "$target_path" == /* || "$target_path" == *".."* ]]; then
    echo "Unsafe reference target mapping for $source_path: $target_path" >&2
    exit 1
  fi

  source_blob="$(git -C "$source_port" rev-parse "$expected_source_commit:$source_path")"
  candidate="$temporary_dir/candidate"
  git -C "$source_port" show "$expected_source_commit:$source_path" > "$candidate"
  destination="$repo_root/$target_path"

  if [[ -e "$destination" ]]; then
    if cmp -s "$candidate" "$destination"; then
      skipped=$((skipped + 1))
    else
      echo "Refusing to overwrite an unexpected reference target: $target_path" >&2
      exit 1
    fi
  else
    install -D -m 0644 "$candidate" "$destination"
    imported=$((imported + 1))
  fi

  printf '%s\t%s\t%s\n' "$source_path" "$target_path" "$source_blob" >> "$record"
done < "$source_manifest"

mv "$record" "$repo_root/docs/migration/IMPORTED_REFERENCE.tsv"
echo "Imported $imported reference documents; $skipped already matched."
