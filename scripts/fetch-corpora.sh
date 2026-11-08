#!/bin/sh
# Fetches the test corpora into .cache/corpora (gitignored), checking every file. The compliance tests
# (tests/Zorg.Imaging.ComplianceTests) read them from there, and skip when they are missing.
#
# Usage:
#   scripts/fetch-corpora.sh            # all of them, about 25 MB
#   scripts/fetch-corpora.sh pngsuite   # or webp, orientation
#
# What is fetched, and why none of it is committed:
#   pngsuite     PngSuite 2017-07-19 by Willem van Schaik (http://www.schaik.com/pngsuite/): every colour type,
#                bit depth and interlace, and broken files. "Free for any purpose, without fee." 68 KB, SHA-256 pinned.
#   webp         libwebp-test-data (chromium.googlesource.com/webm/libwebp-test-data) at a pinned commit: the
#                VP8 and VP8L conformance vectors. No licence is stated in the repository, so it is fetched, never
#                committed. 19.5 MB.
#   orientation  exif-orientation-examples by recurser (github.com/recurser/exif-orientation-examples, MIT) at a
#                pinned commit: one photo saved with EXIF orientations 0 to 8, landscape and portrait. 5.4 MB.
#
# The git-hosted files are checked against the blob ids in scripts/corpora/*.txt, which come from the hosts'
# own trees at the pinned commits, so a changed or corrupted file is refused.
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
cache="$root/.cache/corpora"
manifests="$root/scripts/corpora"

PNGSUITE_URL=http://www.schaik.com/pngsuite/PngSuite-2017jul19.tgz
PNGSUITE_SHA256=0294b244c95a8342c01b00010cf34abdcabc7c6a34fd0fe1bd963917537bfdc8
WEBP_BASE=https://chromium.googlesource.com/webm/libwebp-test-data/+/06ddd96e276c2c638a72d39d3c0f340afd61978c
ORIENTATION_BASE=https://raw.githubusercontent.com/recurser/exif-orientation-examples/219294e144531b0c01247913cb58b6f5531b5081

for tool in curl git tar base64; do
  command -v "$tool" > /dev/null || { echo "fetch-corpora: $tool is needed" >&2; exit 1; }
done

sha256() {
  if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# Fetches each "<blob id> <size> <name>" line of a manifest with the given command, checking the blob id.
fetch_manifest() {
  manifest=$1 folder=$2 fetch=$3
  mkdir -p "$folder"
  fetched=0 kept=0
  # tr drops the carriage returns of a manifest checked out with CRLF line endings.
  tr -d '\r' < "$manifest" > "$folder/.manifest"
  while read -r id _ name; do
    [ -n "$name" ] || continue
    target="$folder/$name"
    if [ -f "$target" ] && [ "$(git hash-object "$target")" = "$id" ]; then
      kept=$((kept + 1))
      continue
    fi

    "$fetch" "$name" "$target.part"
    actual=$(git hash-object "$target.part")
    if [ "$actual" != "$id" ]; then
      rm -f "$target.part"
      echo "fetch-corpora: $name has blob id $actual, expected $id" >&2
      exit 1
    fi

    mv "$target.part" "$target"
    fetched=$((fetched + 1))
  done < "$folder/.manifest"
  rm -f "$folder/.manifest"
  echo "  $fetched fetched, $kept already here"
}

webp_file() {
  # gitiles serves file contents base64-encoded with ?format=TEXT.
  curl -fsSL --retry 5 "$WEBP_BASE/$1?format=TEXT" | base64 -d > "$2"
}

orientation_file() {
  curl -fsSL --retry 5 -o "$2" "$ORIENTATION_BASE/$1"
}

fetch_pngsuite() {
  echo "PngSuite"
  folder="$cache/pngsuite" archive="$cache/PngSuite-2017jul19.tgz"
  if [ -f "$folder/.complete" ]; then
    echo "  already here"
    return
  fi

  mkdir -p "$cache"
  curl -fsSL --retry 5 -o "$archive.part" "$PNGSUITE_URL"
  actual=$(sha256 "$archive.part")
  if [ "$PNGSUITE_SHA256" = PNGSUITE_SHA256_PENDING ]; then
    echo "  SHA-256 $actual (not yet pinned in this script)"
  elif [ "$actual" != "$PNGSUITE_SHA256" ]; then
    rm -f "$archive.part"
    echo "fetch-corpora: PngSuite has SHA-256 $actual, expected $PNGSUITE_SHA256" >&2
    exit 1
  fi

  mv "$archive.part" "$archive"
  rm -rf "$folder"
  mkdir -p "$folder"
  tar -xzf "$archive" -C "$folder"
  rm -f "$archive"
  touch "$folder/.complete"
  echo "  $(find "$folder" -name '*.png' | wc -l | tr -d ' ') files"
}

which=${1:-all}
case "$which" in
  all | pngsuite | webp | orientation) ;;
  *) echo "usage: $0 [all|pngsuite|webp|orientation]" >&2; exit 2 ;;
esac

if [ "$which" = all ] || [ "$which" = pngsuite ]; then
  fetch_pngsuite
fi

if [ "$which" = all ] || [ "$which" = webp ]; then
  echo "libwebp-test-data"
  fetch_manifest "$manifests/libwebp-test-data.txt" "$cache/webp" webp_file
fi

if [ "$which" = all ] || [ "$which" = orientation ]; then
  echo "exif-orientation-examples"
  fetch_manifest "$manifests/exif-orientation-examples.txt" "$cache/orientation" orientation_file
fi
