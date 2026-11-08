#!/bin/sh
# Checks that a built file stays under a size limit (design decision D3: the library under 3 MB).
#
# Usage:
#   scripts/check-size.sh <file> <limit in KiB> <label>
#
# Prints the size, warns past 80% of the limit, and fails past the limit. In GitHub Actions the warning and the
# error are annotations, and the size goes to the job summary.
set -eu

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <file> <limit in KiB> <label>" >&2
  exit 2
fi

file=$1
limit_kib=$2
label=$3

if [ ! -f "$file" ]; then
  echo "SIZE CHECK: $file not found" >&2
  exit 1
fi

bytes=$(wc -c < "$file" | tr -d ' ')
kib=$(( (bytes + 1023) / 1024 ))
percent=$(( kib * 100 / limit_kib ))
line="$label: $kib KiB of $limit_kib KiB ($percent%)"
echo "$line"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  echo "- $line" >> "$GITHUB_STEP_SUMMARY"
fi

if [ "$kib" -gt "$limit_kib" ]; then
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::error::$label is over its size limit: $line"; fi
  echo "SIZE CHECK FAILED: $line" >&2
  exit 1
fi

if [ $(( kib * 5 )) -gt $(( limit_kib * 4 )) ]; then
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$label is past 80% of its size limit: $line"; fi
  echo "SIZE WARNING: past 80% of the limit"
fi
