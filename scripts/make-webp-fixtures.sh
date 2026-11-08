#!/usr/bin/env bash
# Makes the small WebP test files and libwebp's decodes of them (PAM, RGBA), in the gate image, so the WebP tests have
# references on machines without the tools. The pictures are ImageMagick plasma fractals with fixed seeds.
# Run from the repository root after scripts/test-linux.sh has built the image once.
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'
cli="${ZORG_IMAGING_CONTAINER_CLI:-$(command -v podman || command -v docker || echo "$LOCALAPPDATA/Programs/Podman/podman.exe")}"
out=tests/Zorg.Imaging.Tests/Codecs/Webp/Files
mkdir -p "$out"
"$cli" run --rm -i --network host zorg-imaging-gate:alpine sh -c '
set -e
mkdir -p /out /work && cd /work
magick -seed 7 -size 67x45 plasma:fractal png24:photo.png
magick -seed 3 -size 33x17 plasma:fractal -alpha set -channel A -fx "i/w" +channel png32:alpha.png
magick -size 40x24 xc:white -fill "#d02020" -draw "rectangle 4,4 20,18" -fill "#2050c0" -draw "circle 30,12 30,4" png24:logo.png
magick -seed 5 -size 29x13 plasma:fractal -colors 3 png24:three.png
magick -seed 5 -size 31x9 plasma:fractal -colors 12 png24:twelve.png
cd /out
cwebp -quiet -lossless -z 0 /work/photo.png -o lossless-z0.webp
cwebp -quiet -lossless -z 6 /work/photo.png -o lossless-z6.webp
cwebp -quiet -lossless -z 9 /work/photo.png -o lossless-z9.webp
cwebp -quiet -lossless -exact /work/alpha.png -o lossless-alpha.webp
cwebp -quiet -lossless /work/logo.png -o lossless-logo.webp
cwebp -quiet -lossless /work/three.png -o lossless-palette3.webp
cwebp -quiet -lossless /work/twelve.png -o lossless-palette12.webp
cwebp -quiet -near_lossless 40 /work/photo.png -o near-lossless.webp
cwebp -quiet -lossless -z 9 /work/logo.png -o frame2.webp
webpmux -frame lossless-alpha.webp +100+6+4 -frame frame2.webp +100 -loop 0 -o animated.webp
cwebp -quiet -q 80 /work/photo.png -o lossy.webp
cwebp -quiet -q 80 /work/alpha.png -o lossy-alpha.webp
for f in *.webp; do [ "$f" = animated.webp ] || dwebp -quiet -pam "$f" -o "${f%.webp}.pam"; done
rm -f frame2.webp frame2.pam
tar -cf - .
' | tar -xf - -C "$out"
ls -l "$out"
