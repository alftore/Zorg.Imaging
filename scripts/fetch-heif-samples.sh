#!/bin/sh
# Fetches a few AVIF and HEIC files into .cache/corpora/heif (gitignored), checking each against a pinned SHA-256.
# tests/Zorg.Imaging.SystemCodecs.Tests decodes them through the operating system, and skips them when they're missing.
#
# What is fetched, and why none of it is committed (they're small, but they're other people's pictures):
#   libavif test data (github.com/AOMediaCodec/libavif, tests/data) at a pinned commit: "same as libavif"
#     (BSD-2-Clause, Copyright 2019 Joe Drago), except kodim03, which Kodak released for unrestricted usage.
#     A photo, a 1x5 grid, ICC and EXIF, irot with alpha, and clap + irot + imir.
#   libheif examples (github.com/strukturag/libheif, examples/) at a pinned commit: MIT (examples/COPYING).
#     One HEIC and one AVIF photo. About 0.9 MB in all.
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
folder="$root/.cache/corpora/heif"
LIBAVIF=https://raw.githubusercontent.com/AOMediaCodec/libavif/90d3b6661e1475bb1652cba7d85c24f8c748ab07/tests/data
LIBHEIF=https://raw.githubusercontent.com/strukturag/libheif/12a259a5882dd99d45ec5fc77e296a3b620d1e2e/examples

sha256() {
  if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

mkdir -p "$folder"
while read -r sha url name; do
  target="$folder/$name"
  if [ -f "$target" ] && [ "$(sha256 "$target")" = "$sha" ]; then
    continue
  fi

  curl -sSfL --retry 3 -o "$target.part" "$url"
  actual=$(sha256 "$target.part")
  if [ "$actual" != "$sha" ]; then
    rm -f "$target.part"
    echo "fetch-heif-samples: $name has SHA-256 $actual, expected $sha" >&2
    exit 1
  fi

  mv "$target.part" "$target"
done <<EOF
e69c973a3ddf635412c9a0c6cda66798102d0030303614873b337f658983ef5d $LIBAVIF/io/kodim03_yuv420_8bpc.avif libavif-kodim03_yuv420_8bpc.avif
b371cc88244a873131e4d10ff9363d71ce4f41cf333bd4a491b38d970d9abd3b $LIBAVIF/abc_color_irot_alpha_irot.avif libavif-abc_color_irot_alpha_irot.avif
33f869fcf2a879913eb394982b8fc03e9a60c25831aa37622ddefa656fd39fc1 $LIBAVIF/clap_irot_imir_non_essential.avif libavif-clap_irot_imir_non_essential.avif
c9e04ff9d90d7093454750fa33b7543ee5479e0cfb151e2c3d2ce6a16c1651c1 $LIBAVIF/sofa_grid1x5_420.avif libavif-sofa_grid1x5_420.avif
961bc38b61e60b7651fa20efa24269ae2f35e4958822a81c908c9bbf9b3f66e1 $LIBAVIF/paris_icc_exif_xmp.avif libavif-paris_icc_exif_xmp.avif
54a0dc31d02b6f5d9d4b66027d4787861b7af15ffd8fab8eab963d10c5411469 $LIBHEIF/example.avif libheif-example.avif
7f8b363e4936c0666a25f64f3a92fda10bd8e5453be4592530b65a55dd98f3f2 $LIBHEIF/example.heic libheif-example.heic
EOF

echo "HEIF samples in $folder"
