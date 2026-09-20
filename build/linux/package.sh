#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-all}"
case "$MODE" in
  all|tar|deb) ;;
  *)
    echo "Usage: $0 [all|tar|deb]" >&2
    exit 2
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
APP_PROJECT="$ROOT_DIR/MissionPlanner.csproj"
DOTNET="${DOTNET:-dotnet}"
CONFIGURATION="${CONFIGURATION:-Release}"
RID="${RID:-linux-x64}"
OUTPUT_DIR="${OUTPUT_DIR:-$ROOT_DIR/out/packages}"
PUBLISH_PARENT="${PUBLISH_PARENT:-$ROOT_DIR/out}"
source "$ROOT_DIR/build/version.sh"

if [[ "$RID" != "linux-x64" ]]; then
  echo "The Debian and tar packaging target currently supports RID=linux-x64 only." >&2
  exit 2
fi

if [[ ! "$MP_PACKAGE_VERSION" =~ ^[0-9A-Za-z.+~]+$ ]]; then
  echo "Package version contains characters unsupported by Debian: '$MP_PACKAGE_VERSION'" >&2
  exit 2
fi
PACKAGE_ARCH="amd64"
DIR_NAME="MissionPlanner10-$MP_ARTIFACT_VERSION-$RID"
PUBLISH_DIR="$PUBLISH_PARENT/$DIR_NAME"
TAR_PATH="$OUTPUT_DIR/$DIR_NAME.tar.gz"
DEB_PATH="$OUTPUT_DIR/missionplanner10_${MP_ARTIFACT_VERSION}_${PACKAGE_ARCH}.deb"
BUILD_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT_DIR" log -1 --format=%ct)}"
export SOURCE_DATE_EPOCH="$BUILD_EPOCH"

case "$PUBLISH_DIR" in
  /|""|"$PUBLISH_PARENT")
    echo "Refusing unsafe publish directory: '$PUBLISH_DIR'" >&2
    exit 2
    ;;
esac

WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/missionplanner-package.XXXXXXXX")"
cleanup() {
  rm -rf -- "$WORK_ROOT"
}
trap cleanup EXIT

PUBLISH_TEMP="$WORK_ROOT/publish"
mkdir -p "$PUBLISH_TEMP" "$OUTPUT_DIR" "$PUBLISH_PARENT"

echo "Publishing $DIR_NAME"
# Do not pass the reserved global MSBuild Version property: it would leak into native
# ProjectReferences. The executable project consumes these namespaced properties and stamps
# its own official/local-build/commit version contract.
env -u VERSION "$DOTNET" publish "$APP_PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -m:1 \
  -p:DebugType=none \
  -p:MissionPlannerUpstreamVersion="$MP_UPSTREAM_VERSION" \
  -p:MissionPlannerLocalBuildNumber="$MP_LOCAL_BUILD_NUMBER" \
  -p:MissionPlannerCommit="$MP_COMMIT$MP_DIRTY_SUFFIX" \
  -o "$PUBLISH_TEMP"

"$ROOT_DIR/build/rename-apphost.sh" "$PUBLISH_TEMP" "$RID"

test -x "$PUBLISH_TEMP/MissionPlanner10"
test -s "$PUBLISH_TEMP/airports.csv"
# a host without a working Rust toolchain would silently publish the
# managed-parser-only app; packages must ship the native log parser
test -s "$PUBLISH_TEMP/libdflog_ffi.so"
for unwanted in libusb-1.0.dll simpleble-c.dll simpleble.dll; do
  if [[ -e "$PUBLISH_TEMP/$unwanted" ]]; then
    echo "Linux publish contains a Windows-native library: $unwanted" >&2
    exit 1
  fi
done

# Microsoft.Data.Sqlite carries a prebuilt native SQLite library with a full ELF symbol table.
# Debian treats that as an unstripped release object. Remove only non-runtime symbols from the
# temporary Linux publish copy; the NuGet cache and Windows/macOS assets remain untouched.
if [[ -f "$PUBLISH_TEMP/libe_sqlite3.so" ]]; then
  if ! command -v strip >/dev/null 2>&1; then
    echo "binutils strip is required to package libe_sqlite3.so." >&2
    exit 1
  fi
  strip --strip-unneeded "$PUBLISH_TEMP/libe_sqlite3.so"
fi

# NuGet packages can carry host checkout permissions (including writable or executable
# managed assemblies). Normalize the release tree; only the ELF apphost is executed.
find "$PUBLISH_TEMP" -type d -exec chmod 0755 {} +
find "$PUBLISH_TEMP" -type f -exec chmod 0644 {} +
chmod 0755 "$PUBLISH_TEMP/MissionPlanner10"
if [[ -f "$PUBLISH_TEMP/createdump" ]]; then
  chmod 0755 "$PUBLISH_TEMP/createdump"
fi

mkdir -p "$PUBLISH_DIR"
find "$PUBLISH_DIR" -mindepth 1 -delete
cp -a "$PUBLISH_TEMP/." "$PUBLISH_DIR/"
chmod 0755 "$PUBLISH_DIR/MissionPlanner10"

build_tar() {
  local tar_root="$WORK_ROOT/tar"
  local tar_app="$tar_root/$DIR_NAME"
  mkdir -p "$tar_app"
  cp -a "$PUBLISH_TEMP/." "$tar_app/"
  cp "$SCRIPT_DIR/install.sh" "$tar_app/"
  cp "$SCRIPT_DIR/missionplanner.desktop" "$tar_app/"
  cp "$SCRIPT_DIR/missionplanner.png" "$tar_app/"
  cp "$ROOT_DIR/COPYING.txt" "$tar_app/LICENSE"
  cp "$ROOT_DIR/NOTICE.md" "$tar_app/"
  cp -a "$ROOT_DIR/LICENSES" "$tar_app/"
  chmod 0755 "$tar_app/MissionPlanner10" "$tar_app/install.sh"

  rm -f -- "$TAR_PATH"
  tar --sort=name \
      --mtime="@$BUILD_EPOCH" \
      --owner=0 --group=0 --numeric-owner \
      -czf "$TAR_PATH" -C "$tar_root" "$DIR_NAME"
  echo "Created $TAR_PATH"
}

build_deb() {
  local deb_root="$WORK_ROOT/deb"
  local app_dir="$deb_root/usr/lib/missionplanner10"
  local doc_dir="$deb_root/usr/share/doc/missionplanner10"
  local installed_size

  if ! command -v dpkg >/dev/null 2>&1 ||
      ! dpkg --validate-version "$MP_DEBIAN_VERSION" 2>/dev/null; then
    echo "Invalid Debian version or missing dpkg: '$MP_DEBIAN_VERSION'" >&2
    return 2
  fi

  mkdir -p \
    "$deb_root/DEBIAN" \
    "$app_dir" \
    "$deb_root/usr/bin" \
    "$deb_root/usr/share/applications" \
    "$deb_root/usr/share/icons/hicolor/256x256/apps" \
    "$deb_root/usr/share/lintian/overrides" \
    "$deb_root/usr/share/man/man1" \
    "$doc_dir"

  cp -a "$PUBLISH_TEMP/." "$app_dir/"
  : > "$app_dir/.package-managed"
  cp "$SCRIPT_DIR/debian/missionplanner" "$deb_root/usr/bin/missionplanner10"
  cp "$SCRIPT_DIR/debian/missionplanner.desktop" \
    "$deb_root/usr/share/applications/missionplanner10.desktop"
  cp "$SCRIPT_DIR/missionplanner.png" \
    "$deb_root/usr/share/icons/hicolor/256x256/apps/missionplanner10.png"
  cp "$SCRIPT_DIR/debian/copyright" "$doc_dir/copyright"
  cp "$ROOT_DIR/NOTICE.md" "$doc_dir/NOTICE.md"
  cp -a "$ROOT_DIR/LICENSES" "$doc_dir/"
  cp "$SCRIPT_DIR/debian/lintian-overrides" \
    "$deb_root/usr/share/lintian/overrides/missionplanner10"
  cp "$SCRIPT_DIR/debian/postinst" "$deb_root/DEBIAN/postinst"
  cp "$SCRIPT_DIR/debian/postrm" "$deb_root/DEBIAN/postrm"

  gzip -n -9 -c "$SCRIPT_DIR/debian/missionplanner.1" \
    > "$deb_root/usr/share/man/man1/missionplanner10.1.gz"
  {
    echo "missionplanner10 ($MP_DEBIAN_VERSION) unstable; urgency=medium"
    echo
    echo "  * Build the self-contained cross-platform Mission Planner 10 port."
    echo
    echo " -- Rouniy <Rouniy@users.noreply.github.com>  $(date -R -d "@$BUILD_EPOCH")"
  } | gzip -n -9 > "$doc_dir/changelog.gz"

  find "$deb_root" -type d -exec chmod 0755 {} +
  find "$deb_root" -type f -exec chmod 0644 {} +
  chmod 0755 \
    "$app_dir/MissionPlanner10" \
    "$deb_root/usr/bin/missionplanner10" \
    "$deb_root/DEBIAN/postinst" \
    "$deb_root/DEBIAN/postrm"
  if [[ -f "$app_dir/createdump" ]]; then
    chmod 0755 "$app_dir/createdump"
  fi

  (
    cd "$deb_root"
    find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums
  )
  installed_size="$(du -sk "$deb_root" | cut -f1)"

  sed \
    -e "s/@VERSION@/$MP_DEBIAN_VERSION/g" \
    -e "s/@ARCH@/$PACKAGE_ARCH/g" \
    -e "s/@INSTALLED_SIZE@/$installed_size/g" \
    "$SCRIPT_DIR/debian/control.in" > "$deb_root/DEBIAN/control"

  if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate \
      "$deb_root/usr/share/applications/missionplanner10.desktop"
  fi

  rm -f -- "$DEB_PATH"
  dpkg-deb --root-owner-group --build "$deb_root" "$DEB_PATH"
  echo "Created $DEB_PATH"
}

case "$MODE" in
  all)
    build_tar
    build_deb
    ;;
  tar) build_tar ;;
  deb) build_deb ;;
esac
