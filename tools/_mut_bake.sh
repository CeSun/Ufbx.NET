#!/bin/bash
# Batch J mutation control: apply one literal string replacement to the port, run the
# bake differential, then restore from tools/_scratch/*.bak and verify byte equality.
# Usage: _mut_bake.sh <tag> <repo-relative-path> <old> <new>
#
# 2026-10-04 (batch Q): rewritten from perl to python. **`perl` is no longer on PATH on this
# host**, which made the batch J/K sweeps silently no-op -- `perl ... || exit 1` would have
# aborted, but the historical sweeps were run while perl still existed, so their numbers stand
# while any *new* run with the old script would have produced nothing at all. The replacement is
# done on BYTES, and `old` must occur exactly once.
set -u
WIN=C:/Workspace/ufbx-cs
POS=/c/Workspace/ufbx-cs
PY=C:/Users/cesun/.workbuddy/binaries/python/versions/3.13.12/python.exe
tag="$1"; rel="$2"; old="$3"; new="$4"
file="$POS/$rel"
bak="$POS/tools/_scratch/$(basename "$rel").bak"

"$PY" - "$file" "$old" "$new" <<'PYEOF' || exit 1
import sys
path, old, new = sys.argv[1], sys.argv[2].encode(), sys.argv[3].encode()
with open(path, 'rb') as f:
    s = f.read()
n = s.count(old)
if n != 1:
    print("ERROR: %d occurrences" % n)
    sys.exit(1)
with open(path, 'wb') as f:
    f.write(s.replace(old, new))
PYEOF

out=$(cd /c/Workspace/_analyze_ufbx && dotnet run --project "$WIN/tools/BakeCheck" -c Release -- \
    "$WIN/tools/bake_oracle.txt" "$WIN/tools/bake_corpus.txt" 2>&1)
code=$?
mm=$(printf '%s\n' "$out" | grep -oE 'mismatches [0-9]+' | head -1)
first=$(printf '%s\n' "$out" | grep -A2 -m1 "MISMATCH #1" | tr '\n' '~' | cut -c1-300)

cp "$bak" "$file"
cmp "$bak" "$file" || { echo "$tag: RESTORE FAILED"; exit 1; }
echo "$tag | exit=$code | ${mm:-no-summary} | ${first:-}"
