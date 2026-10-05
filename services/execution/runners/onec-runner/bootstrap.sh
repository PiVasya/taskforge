#!/usr/bin/env bash
set -Eeuo pipefail

PLATFORM_ROOT="${ONEC_PLATFORM_ROOT:-/opt/1cv8}"
RUNTIME_ROOT="${ONEC_RUNTIME_ROOT:-/opt/taskforge/onec/runtime}"
CACHE_ROOT="${ONEC_INSTALL_CACHE_ROOT:-/installer-cache}"
VERSION="${ONEC_PLATFORM_VERSION:-}"
EXPECTED_SHA="${ONEC_INSTALL_SHA256:-}"
SOURCE="${ONEC_INSTALL_SOURCE:-auto}"
URL="${ONEC_INSTALL_URL:-}"
SOURCE_FILE="${ONEC_INSTALL_FILE:-/source/installer.run}"
COMPONENTS="${ONEC_INSTALL_COMPONENTS:-client_full,client_thin_fib,ru}"
MARKER="$PLATFORM_ROOT/.taskforge-platform"
STABLE_BIN="$PLATFORM_ROOT/taskforge/1cv8"

log(){ printf '[onec-bootstrap] %s\n' "$*"; }
die(){ log "ERROR: $*" >&2; exit "${2:-2}"; }

validate_version() {
  [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] \
    || die 'ONEC_PLATFORM_VERSION must use exact A.B.C.D numeric form' 19
}

find_exact_onec() {
  local arch candidate
  for arch in x86_64 i386; do
    candidate="$PLATFORM_ROOT/$arch/$VERSION/1cv8"
    if [ -x "$candidate" ]; then printf '%s\n' "$candidate"; return 0; fi
  done
  return 1
}

find_onec() {
  find_exact_onec 2>/dev/null && return 0
  find "$PLATFORM_ROOT" -type f -name 1cv8 -perm -111 -print 2>/dev/null \
    | grep -v '/taskforge/1cv8$' | sort -V | tail -n1
}

marker_matches() {
  [ -n "$VERSION" ] || return 1
  [ -s "$MARKER" ] || return 1
  grep -Fxq "version=$VERSION" "$MARKER" || return 1
  if [ -n "$EXPECTED_SHA" ]; then grep -Fxq "sha256=$EXPECTED_SHA" "$MARKER" || return 1; fi
  exact="$(find_exact_onec 2>/dev/null || true)"
  [ -n "$exact" ] || return 1
  [ -x "$STABLE_BIN" ] || return 1
  [ "$(readlink -f "$STABLE_BIN" 2>/dev/null || true)" = "$(readlink -f "$exact")" ] || return 1
}

install_platform() {
  [ -n "$VERSION" ] || die 'ONEC_PLATFORM_VERSION is required for first installation' 20
  [ -n "$EXPECTED_SHA" ] || die 'ONEC_INSTALL_SHA256 is required for first installation' 21
  mkdir -p "$CACHE_ROOT"
  installer="$CACHE_ROOT/setup-full-$VERSION.run"

  resolved="$SOURCE"
  if [ "$resolved" = auto ]; then
    if [ -s "$SOURCE_FILE" ]; then resolved=file
    elif [ -n "$URL" ]; then resolved=url
    else resolved=none
    fi
  fi

  case "$resolved" in
    file)
      [ -s "$SOURCE_FILE" ] || die "installer file missing: $SOURCE_FILE" 22
      if [ ! -s "$installer" ] || ! echo "$EXPECTED_SHA  $installer" | sha256sum -c - >/dev/null 2>&1; then
        cp -f "$SOURCE_FILE" "$installer.tmp"
        mv "$installer.tmp" "$installer"
      fi
      ;;
    url)
      [ -n "$URL" ] || die 'ONEC_INSTALL_URL is empty' 23
      if [ ! -s "$installer" ] || ! echo "$EXPECTED_SHA  $installer" | sha256sum -c - >/dev/null 2>&1; then
        rm -f "$installer.tmp"
        curl --fail --location --retry 5 --retry-all-errors \
          --connect-timeout 20 --max-time 1800 \
          "$URL" -o "$installer.tmp"
        mv "$installer.tmp" "$installer"
      fi
      ;;
    none|'') die 'platform absent and no installer source configured' 24 ;;
    *) die "unsupported ONEC_INSTALL_SOURCE=$resolved" 25 ;;
  esac

  echo "$EXPECTED_SHA  $installer" | sha256sum -c - >/dev/null \
    || die 'installer SHA-256 mismatch' 26
  chmod 0700 "$installer"

  # Official Linux layouts are versioned under /opt/1cv8/<arch>/<A.B.C.D>.
  # Keep existing versions side-by-side so a failed upgrade never destroys the
  # last known-good platform. The stable TaskForge symlink is switched only
  # after the requested exact version has installed successfully.
  log "installing platform version=$VERSION components=$COMPONENTS"
  "$installer" --mode unattended --enable-components "$COMPONENTS"

  onec_bin="$(find_exact_onec 2>/dev/null || true)"
  [ -n "$onec_bin" ] || die 'installer finished but the requested exact 1cv8 version was not found under /opt/1cv8' 27
  mkdir -p "$PLATFORM_ROOT/taskforge"
  ln -sfn "$onec_bin" "$STABLE_BIN"
  {
    printf 'version=%s\n' "$VERSION"
    printf 'sha256=%s\n' "$EXPECTED_SHA"
    printf 'installed_at=%s\n' "$(date -u +%FT%TZ)"
  } > "$MARKER"
  chmod -R a+rX "$PLATFORM_ROOT"
  log "platform installed version=$VERSION binary=$onec_bin"
}

prepare_stable_binary() {
  onec_bin="$(find_exact_onec 2>/dev/null || true)"
  [ -n "$onec_bin" ] || die 'installed platform has no executable for the requested exact version' 28
  if [ -x "$STABLE_BIN" ] && [ "$(readlink -f "$STABLE_BIN" 2>/dev/null || true)" = "$(readlink -f "$onec_bin")" ]; then
    return 0
  fi
  mkdir -p "$PLATFORM_ROOT/taskforge"
  ln -sfn "$onec_bin" "$STABLE_BIN"
}

prepare_template() {
  template_dir="$RUNTIME_ROOT/templates/code"
  template="$template_dir/1Cv8.1CD"
  if [ -s "$template" ]; then
    log 'file-infobase template already exists'
    return 0
  fi

  rm -rf "$template_dir"
  mkdir -p "$template_dir" /tmp/onec-bootstrap-home
  export HOME=/tmp/onec-bootstrap-home
  rm -f /tmp/create-infobase.log /tmp/create-infobase.result
  log 'creating empty file-infobase template'
  xvfb-run -a -s '-screen 0 1024x768x24' \
    "$STABLE_BIN" CREATEINFOBASE "File=$template_dir;DBFormat=8.3.8;" \
    /DisableStartupMessages /DisableStartupDialogs \
    /Out /tmp/create-infobase.log /DumpResult /tmp/create-infobase.result || {
      cat /tmp/create-infobase.log >&2 2>/dev/null || true
      die 'CREATEINFOBASE failed' 29
    }
  [ -s "$template" ] || {
    cat /tmp/create-infobase.log >&2 2>/dev/null || true
    die 'CREATEINFOBASE did not create 1Cv8.1CD' 30
  }
  chmod -R a+rX "$RUNTIME_ROOT"
  log 'file-infobase template created'
}

mkdir -p "$PLATFORM_ROOT" "$RUNTIME_ROOT" "$CACHE_ROOT"
validate_version
if marker_matches; then
  log "platform already installed version=$VERSION"
else
  install_platform
fi
prepare_stable_binary
prepare_template

"$STABLE_BIN" /? >/dev/null 2>&1 || true
printf 'ready\n'
