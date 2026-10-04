#!/bin/bash
# Batch P mutation control: apply one literal string replacement to the port, run the scale-helper
# differential, then restore from tools/_scratch/<basename>.bak and prove byte equality.
# Usage: _mut_sh.sh <tag> <repo-relative-path> <old> <new>
#
# NOTE: this uses python, NOT perl -- perl is not on PATH on this host any more, and the
# batch J/K sweeps that used it would now silently no-op. The replacement is done on BYTES so
# a BOM or CRLF in the target cannot change the match; `old` must occur exactly once (a single
# line: argv cannot carry a newline, and an anchor that is a substring of another site matches
# more than once).
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

# The harness compiles src/ into its own assembly, so the mutation is invisible until rebuilt.
# (This is why the run cannot just reuse a stale bin/.)
bout=$(dotnet build "$WIN/tools/ShCheck" -c Release -v q --nologo 2>&1)
bcode=$?
if [ $bcode -ne 0 ]; then
    cp "$bak" "$file"
    cmp "$bak" "$file" || { echo "$tag: RESTORE FAILED"; exit 1; }
    echo "$tag | BUILD FAILED | $(printf '%s\n' "$bout" | grep -m1 -E 'error' | cut -c1-160)"
    exit 0
fi

out=$(cd /c/Workspace/_analyze_ufbx && "$WIN/tools/ShCheck/bin/Release/net8.0/ShCheck.exe" \
    "$WIN/tools/sh_oracle.txt" "$WIN/tools/sh_corpus.txt" 2>&1)
code=$?
mm=$(printf '%s\n' "$out" | grep -oE 'mismatches [0-9]+' | head -1)
first=$(printf '%s\n' "$out" | grep -m1 -E "^  oracle:|^EXCEPTION|Unhandled" | cut -c1-160)

cp "$bak" "$file"
cmp "$bak" "$file" || { echo "$tag: RESTORE FAILED"; exit 1; }
echo "$tag | exit=$code | ${mm:-no-summary} | ${first:-}"
