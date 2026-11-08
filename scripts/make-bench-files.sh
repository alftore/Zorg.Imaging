#!/usr/bin/env bash
# Makes the large benchmark files in the gate image and times the native tools on them (one core each), so
# benchmarks/Zorg.Imaging.Benchmarks has the same photo in every format and something to compare with.
# The files aren't committed. Run from the repository root after scripts/test-linux.sh has built the image once:
#   scripts/make-bench-files.sh [folder]        (default .tmp/bench)
# then: ZORG_BENCH_JPEG_DIR=<folder> dotnet run -c Release --project benchmarks/Zorg.Imaging.Benchmarks -- --filter '*'
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'
cli="${ZORG_IMAGING_CONTAINER_CLI:-$(command -v podman || command -v docker || echo "$LOCALAPPDATA/Programs/Podman/podman.exe")}"
out="${1:-.tmp/bench}"
mkdir -p "$out"
"$cli" run --rm -i --network host zorg-imaging-gate:alpine sh -c '
set -e
cd /tmp
# A 12-megapixel "photo": a smooth fractal scaled up, with sensor-like noise, so every codec has real work.
magick -seed 11 -size 1000x750 plasma:fractal -resize 4000x3000! -attenuate 0.4 +noise Gaussian ppm:photo.ppm
cjpeg -quality 85 -sample 2x2 -optimize photo.ppm > photo.jpg
cjpeg -quality 85 -sample 2x2 -progressive photo.ppm > photo-progressive.jpg
cwebp -quiet -lossless -z 6 photo.ppm -o photo-lossless.webp
cwebp -quiet -q 80 photo.ppm -o photo-lossy.webp
magick photo.ppm -define png:compression-level=6 png24:photo.png
magick photo.ppm photo.gif
# Best of three, in seconds, from busybox time.
t() {
  best=
  for i in 1 2 3; do
    r=$(busybox time -p "$@" 2>&1 >/dev/null | awk "/^real/{print \$2}")
    if [ -z "$best" ] || [ "$(awk "BEGIN{print ($r < $best)}")" = 1 ]; then best=$r; fi
  done
  echo "$best s  $*"
}
{
  echo "Native tools, best of three runs, one core (gate image, $(uname -m)):"
  t djpeg -pnm -outfile /dev/null photo.jpg
  t djpeg -pnm -outfile /dev/null photo-progressive.jpg
  t djpeg -scale 1/4 -pnm -outfile /dev/null photo.jpg
  t cjpeg -quality 80 -sample 2x2 -optimize -outfile /dev/null photo.ppm
  t dwebp -pam -o /dev/null photo-lossless.webp
  t dwebp -pam -o /dev/null photo-lossy.webp
  t cwebp -quiet -q 80 photo.ppm -o /dev/null
  t cwebp -quiet -lossless -z 6 photo.ppm -o /dev/null
  t magick photo.png ppm:/dev/null
  t magick photo.ppm -resize 400x300 ppm:/dev/null
  t magick photo.gif ppm:/dev/null
  t magick photo.ppm gif:/dev/null
} > native-times.txt
tar -cf - photo.jpg photo-progressive.jpg photo-lossless.webp photo-lossy.webp photo.png photo.gif native-times.txt
' | tar -xf - -C "$out"
ls -l "$out"
cat "$out/native-times.txt"
