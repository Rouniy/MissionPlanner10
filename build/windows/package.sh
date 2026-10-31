#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-all}"
case "$MODE" in
  all|zip|msi) ;;
  *)
    echo "Usage: $0 [all|zip|msi]" >&2
    exit 2
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
APP_PROJECT="$ROOT_DIR/MissionPlanner.csproj"
INSTALLER_PROJECT="$SCRIPT_DIR/msi/MissionPlanner.Installer.wixproj"
DOTNET="${DOTNET:-dotnet}"
CONFIGURATION="${CONFIGURATION:-Release}"
RID="${RID:-win-x64}"
OUTPUT_DIR="${OUTPUT_DIR:-$ROOT_DIR/out/packages}"
PUBLISH_PARENT="${PUBLISH_PARENT:-$ROOT_DIR/out}"
source "$ROOT_DIR/build/version.sh"

if [[ "$RID" != "win-x64" ]]; then
  echo "The Windows packaging target currently supports RID=win-x64 only." >&2
  exit 2
fi

DIR_NAME="MissionPlanner10-$MP_ARTIFACT_VERSION-$RID"
PUBLISH_DIR="$PUBLISH_PARENT/$DIR_NAME"
ZIP_PATH="$OUTPUT_DIR/$DIR_NAME.zip"
MSI_PATH="$OUTPUT_DIR/MissionPlanner10-$MP_ARTIFACT_VERSION-$RID.msi"

case "$PUBLISH_DIR" in
  /|""|"$PUBLISH_PARENT")
    echo "Refusing unsafe publish directory: '$PUBLISH_DIR'" >&2
    exit 2
    ;;
esac

WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/missionplanner-windows-package.XXXXXXXX")"
cleanup() {
  rm -rf -- "$WORK_ROOT"
}
trap cleanup EXIT

PUBLISH_TEMP="$WORK_ROOT/publish"
mkdir -p "$PUBLISH_TEMP" "$OUTPUT_DIR" "$PUBLISH_PARENT"

echo "Publishing $DIR_NAME"
env -u VERSION "$DOTNET" publish "$APP_PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -m:1 \
  -p:DebugType=none \
  -p:PlatformTarget=x64 \
  -p:MissionPlannerUpstreamVersion="$MP_UPSTREAM_VERSION" \
  -p:MissionPlannerLocalBuildNumber="$MP_LOCAL_BUILD_NUMBER" \
  -p:MissionPlannerCommit="$MP_COMMIT$MP_DIRTY_SUFFIX" \
  -o "$PUBLISH_TEMP"

"$ROOT_DIR/build/rename-apphost.sh" "$PUBLISH_TEMP" "$RID"

test -s "$PUBLISH_TEMP/MissionPlanner10.exe"
test -s "$PUBLISH_TEMP/airports.csv"
test -s "$PUBLISH_TEMP/simpleble-c.dll"
test -s "$PUBLISH_TEMP/simpleble.dll"
# a host without a working Rust toolchain would silently publish the
# managed-parser-only app; packages must ship the native log parser
test -s "$PUBLISH_TEMP/dflog_ffi.dll"

sign_windows_file() {
  local target="$1"
  if [[ -z "${WINDOWS_SIGN_PFX:-}" ]]; then
    return
  fi
  if [[ ! -s "$WINDOWS_SIGN_PFX" ]]; then
    echo "WINDOWS_SIGN_PFX does not point to a readable PFX file." >&2
    return 2
  fi
  local tool="${SIGNTOOL:-signtool.exe}"
  local timestamp="${WINDOWS_TIMESTAMP_URL:-http://timestamp.digicert.com}"
  local args=(sign /fd SHA256 /td SHA256 /tr "$timestamp" /f "$WINDOWS_SIGN_PFX")
  if [[ -n "${WINDOWS_SIGN_PASSWORD:-}" ]]; then
    args+=(/p "$WINDOWS_SIGN_PASSWORD")
  fi
  "$tool" "${args[@]}" "$target"
  "$tool" verify /pa "$target"
}

sign_windows_file "$PUBLISH_TEMP/MissionPlanner10.exe"

mkdir -p "$PUBLISH_DIR"
find "$PUBLISH_DIR" -mindepth 1 -delete
cp -a "$PUBLISH_TEMP/." "$PUBLISH_DIR/"

build_zip() {
  rm -f -- "$ZIP_PATH"
  python3 "$ROOT_DIR/build/make-update-bundle.py" \
    "$PUBLISH_TEMP" "$ZIP_PATH" --root-name "$DIR_NAME"
  echo "Created $ZIP_PATH"
}

build_msi() {
  local wix_out="$WORK_ROOT/wix-bin"
  local wix_obj="$WORK_ROOT/wix-obj"
  mkdir -p "$wix_out" "$wix_obj"
  "$DOTNET" build "$INSTALLER_PROJECT" \
    -c Release \
    -m:1 \
    --nologo \
    -p:PublishDir="$PUBLISH_TEMP" \
    -p:InstallerVersion="$MP_MSI_VERSION" \
    -p:FullVersion="$MP_INFORMATIONAL_VERSION" \
    -p:RepositoryRoot="$ROOT_DIR" \
    -p:OutputPath="$wix_out/" \
    -p:IntermediateOutputPath="$wix_obj/"

  local built_msi
  built_msi="$(find "$wix_out" -maxdepth 1 -type f -name '*.msi' -print -quit)"
  if [[ -z "$built_msi" || ! -s "$built_msi" ]]; then
    echo "WiX build did not produce an MSI." >&2
    return 1
  fi
  rm -f -- "$MSI_PATH"
  cp "$built_msi" "$MSI_PATH"
  sign_windows_file "$MSI_PATH"
  echo "Created $MSI_PATH (MSI ProductVersion $MP_MSI_VERSION; app $MP_INFORMATIONAL_VERSION)"
}

case "$MODE" in
  all)
    build_zip
    build_msi
    ;;
  zip) build_zip ;;
  msi) build_msi ;;
esac
