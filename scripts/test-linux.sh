#!/usr/bin/env bash
# The Linux gate: build and test Zorg.Imaging in a Linux container, on Podman or Docker.
#
#   scripts/test-linux.sh [--variant alpine|ubuntu] [--aot] [--args "X"] [--repeat N] [--no-oracles] [--fallbacks]
#
#   --variant     alpine (default, musl, every oracle tool) or ubuntu (glibc; no oxipng package there).
#   --aot         Also publish and run samples/Zorg.Imaging.AotSmoke with Native AOT.
#   --args "X"    Extra arguments for dotnet test, such as --args "--filter-class *Crc32Tests".
#   --repeat N    Run the unit tests N times in one container, to chase a flaky test.
#   --no-oracles  Don't set ZORG_REQUIRE_ORACLES=1. By default a missing oracle tool fails a test.
#   --fallbacks   Also run the unit tests with AVX-512, AVX2 and then all SIMD switched off, to test every fallback.
#
# Copied from Zorg.Ssh's gate. The tree goes in as a tar stream of the files git knows (tracked and
# untracked, not ignored), including uncommitted edits. Nothing is bind-mounted from the host. NuGet
# packages are cached in the named volume zorg-imaging-nuget, test corpora in zorg-imaging-corpora.
# Every step has a timeout. Set ZORG_IMAGING_CONTAINER_CLI to force the container program.
set -euo pipefail

variant=alpine
aot=0
oracles=1
testargs=
repeat=1
fallbacks=0
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) variant="$2"; shift ;;
    --aot) aot=1 ;;
    --args) testargs="$2"; shift ;;
    --repeat) repeat="$2"; shift ;;
    --no-oracles) oracles=0 ;;
    --fallbacks) fallbacks=1 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
case "$variant" in alpine|ubuntu) ;; *) echo "Unknown variant: $variant" >&2; exit 2 ;; esac
# Ubuntu has no oxipng package, so only the Alpine image can require every oracle.
if [ "$variant" = ubuntu ]; then oracles=0; fi

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

find_cli() {
  if [ -n "${ZORG_IMAGING_CONTAINER_CLI:-}" ]; then echo "$ZORG_IMAGING_CONTAINER_CLI"; return; fi
  for name in docker podman; do
    if command -v "$name" >/dev/null 2>&1; then command -v "$name"; return; fi
  done
  local known="${LOCALAPPDATA:-}/Programs/Podman/podman.exe"
  if [ -n "${LOCALAPPDATA:-}" ] && [ -x "$known" ]; then echo "$known"; return; fi
  echo "No docker or podman found. Set ZORG_IMAGING_CONTAINER_CLI." >&2
  exit 1
}
cli="$(find_cli)"
image="zorg-imaging-gate:$variant"
volume=zorg-imaging-nuget
corpora=zorg-imaging-corpora
name="zorg-imaging-gate-$$"

stage() { printf '\n== %s (%s)\n' "$1" "$(date +%H:%M:%S)"; }

# Every engine call has a timeout. Without one, a stalled engine hangs the run for ever.
run_cli() { local limit="$1"; shift; timeout "$limit" "$cli" "$@"; }

cleanup() { run_cli 60 rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# Host networking everywhere, on purpose. On the Podman machine used for development, the default rootless
# network gave about 100 KB/s, which is why apk and apt-get never finished and why a restore took minutes.
# With --network host the same download ran at full speed.
stage "Image $image"
start=$SECONDS
run_cli 1200 build --network host -t "$image" -f "scripts/linux/Dockerfile.$variant" scripts/linux
echo "image step: $((SECONDS - start)) s"

env_args=(-e "ZORG_REQUIRE_ORACLES=$oracles" -e "ZORG_GATE_AOT=$aot" -e "ZORG_GATE_TEST_ARGS=$testargs" -e "ZORG_GATE_REPEAT=$repeat" -e "ZORG_GATE_FALLBACKS=$fallbacks")

stage "Tree"
files="$(mktemp)"
trap 'rm -f "$files"; cleanup' EXIT
{ git ls-files -z --cached --others --exclude-standard | tr '\0' '\n'; } | sort -u | while IFS= read -r f; do
  [ -f "$f" ] && printf '%s\n' "$f"
done > "$files"
echo "$(wc -l < "$files") files"

stage "Run"
tar -cf - -T "$files" | run_cli 3000 run --rm -i --network host --name "$name" \
    -v "$volume:/root/.nuget/packages" -v "$corpora:/src-cache" \
    "${env_args[@]}" \
    "$image" sh -c 'mkdir -p /src && tar -xf - -C /src && cd /src && sh scripts/linux/gate-inner.sh'
