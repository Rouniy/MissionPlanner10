#!/usr/bin/env bash
set -euo pipefail

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root_dir"

fail() {
  echo "WinForms retirement check failed: $*" >&2
  exit 1
}

manifest=docs/migration/WINFORMS_RETIREMENT.tsv
expected_header=$'retired_path\tbaseline_role\tdecision\tnative_evidence\tnotes'
[[ "$(head -n 1 "$manifest")" == "$expected_header" ]] || fail "unexpected manifest header"

while IFS=$'\t' read -r retired_path baseline_role decision native_evidence notes; do
  [[ "$decision" == replace || "$decision" == retire ]] ||
    fail "unknown decision '$decision' for $retired_path"
  [[ -n "$baseline_role" && -n "$notes" ]] || fail "incomplete row for $retired_path"
  [[ ! -e "$retired_path" ]] || fail "retired path still exists: $retired_path"
  [[ -z "$(git ls-files "$retired_path")" ]] || fail "retired path is still tracked: $retired_path"
  IFS=';' read -r -a evidence_paths <<< "$native_evidence"
  for evidence_path in "${evidence_paths[@]}"; do
    [[ -e "$evidence_path" ]] || fail "missing evidence $evidence_path for $retired_path"
  done
done < <(tail -n +2 "$manifest")

forms_projects=$(rg -l '<UseWindowsForms>[[:space:]]*true</UseWindowsForms>|System\.Windows\.Forms|Xamarin\.Forms\.Platform\.WinForms' \
  --glob '*.csproj' --glob '*.props' --glob '*.targets' . || true)
[[ -z "$forms_projects" ]] || fail "WinForms project metadata remains: $forms_projects"

forms_code=$(rg -l '^[[:space:]]*(using[[:space:]]+(static[[:space:]]+)?|namespace[[:space:]]+|global::)System\.Windows\.Forms|System\.Windows\.Forms\.(Form|Control|UserControl|MessageBox|ToolStrip|Application)' \
  --glob '*.cs' --glob '!docs/migration/Reference/**' . || true)
[[ -z "$forms_code" ]] || fail "WinForms code remains: $forms_code"

[[ ! -e .gitmodules ]] || fail ".gitmodules remains even though the repository has no submodule"
[[ -z "$(git ls-files -s | awk '$1 == "160000" { print $4 }')" ]] ||
  fail "a Git submodule entry remains"

echo "WinForms retirement is complete: no retired path, project metadata, code dependency or submodule remains."
