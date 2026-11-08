#!/usr/bin/env bash
# Makes the small JPEG test files and libjpeg-turbo's decodes of them, in the gate image, so the JPEG tests have
# references on machines without the tools. The pictures are ImageMagick plasma fractals with fixed seeds.
# Run from the repository root after scripts/test-linux.sh has built the image once.
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'
cli="${ZORG_IMAGING_CONTAINER_CLI:-$(command -v podman || command -v docker || echo "$LOCALAPPDATA/Programs/Podman/podman.exe")}"
out=tests/Zorg.Imaging.Tests/Codecs/Jpeg/Files
mkdir -p "$out"
"$cli" run --rm -i --network host zorg-imaging-gate:alpine sh -c '
set -e
mkdir -p /out && cd /out
magick -seed 7 -size 67x45 plasma:fractal ppm:src.ppm
magick -seed 3 -size 33x17 plasma:fractal ppm:small.ppm
cjpeg -quality 75 -sample 2x2 src.ppm > s420.jpg
cjpeg -quality 90 -sample 1x1 src.ppm > s444.jpg
cjpeg -quality 80 -sample 2x1 src.ppm > s422.jpg
cjpeg -quality 80 -sample 1x2 src.ppm > s440.jpg
cjpeg -quality 80 -sample 4x1 src.ppm > s411.jpg
cjpeg -quality 85 -grayscale src.ppm > grey.jpg
cjpeg -quality 95 -rgb src.ppm > rgb.jpg
cjpeg -quality 75 -sample 2x2 -progressive src.ppm > progressive.jpg
cjpeg -quality 75 -grayscale -progressive small.ppm > progressive-grey.jpg
cjpeg -quality 70 -sample 2x2 -restart 1 -optimize small.ppm > restart.jpg
cjpeg -quality 50 -sample 2x2 -progressive -restart 2B small.ppm > progressive-restart.jpg
for f in *.jpg; do djpeg -pnm "$f" > "${f%.jpg}.pnm"; done
for s in 1/2 1/4 1/8; do
  n=$(echo $s | tr / -)
  djpeg -pnm -scale $s s420.jpg > s420-scale$n.pnm
  djpeg -pnm -scale $s progressive.jpg > progressive-scale$n.pnm
  djpeg -pnm -scale $s grey.jpg > grey-scale$n.pnm
done
# CMYK: ImageMagick writes YCCK with an Adobe marker; its own decodes, by the device formula, are the references.
magick src.ppm -colorspace CMYK -quality 90 ycck.jpg
magick src.ppm -colorspace CMYK -sampling-factor 2x2,1x1,1x1,2x2 -quality 85 ycck-subsampled.jpg
for f in ycck ycck-subsampled; do magick $f.jpg -colorspace sRGB -depth 8 ppm:$f.pnm; done
rm src.ppm small.ppm
tar -cf - .
' | tar -xf - -C "$out"
ls -l "$out"
