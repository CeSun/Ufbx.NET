#!/bin/bash
# Batch J-补 regression battery. Runs every differential harness with the cwd/oracle paths each
# one expects and prints a short summary per harness, so a loader-side change can be re-verified
# in one command. Usage: tools/_scratch/battery.sh [only-label-substring]
set -u
UFBX=/c/Workspace/ufbx-cs
REF=/c/Workspace/_analyze_ufbx
WIN_UFBX=C:/Workspace/ufbx-cs
filter="${1:-}"

run() {
    local label="$1" cwd="$2"; shift 2
    if [ -n "$filter" ] && [[ "$label" != *"$filter"* ]]; then return; fi
    local out rc
    out=$(cd "$cwd" && "$@" 2>&1)
    rc=$?
    echo "== $label (exit=$rc)"
    printf '%s\n' "$out" | grep -E "ALL MATCH|PASS|FAIL|mismatch|MISMATCH|matched|files|records|checks|diverg|error|passed|failed|CHECK" | tail -3
}

dotnet_run() { # project args...
    local proj="$1"; shift
    dotnet run --project "$proj" -c Release -- "$@"
}

run build "$UFBX" dotnet build ufbx-cs.sln -c Release
run goldens "$UFBX" dotnet_run "$WIN_UFBX/tests/Ufbx.Tests" goldens tools/golden_hashes.txt
run streamcheck "$UFBX" dotnet_run "$WIN_UFBX/tests/Ufbx.Tests" streamcheck
run mathvec "$UFBX" dotnet_run "$WIN_UFBX/tools/MathVectorCheck" mathvec tools/math_vectors.txt
run util "$UFBX" dotnet_run "$WIN_UFBX/tools/UtilCheck" "$WIN_UFBX/tools/util_oracle.txt"
run s4a "$UFBX" dotnet_run "$WIN_UFBX/tools/S4aCheck" "$WIN_UFBX/tools/s4a_oracle.txt"
run inflate "$UFBX" dotnet_run "$WIN_UFBX/tools/InflateCheck"
run dom "$UFBX" dotnet_run "$WIN_UFBX/tools/DomCheck" "$WIN_UFBX/tools/dom_oracle.txt"
run graph "$UFBX" dotnet_run "$WIN_UFBX/tools/GraphCheck" "$WIN_UFBX/tools/graph_oracle.txt"
run load "$UFBX" dotnet_run "$WIN_UFBX/tools/LoadCheck" "$WIN_UFBX/tools/load_oracle.txt"
run s2geom "$UFBX" dotnet_run "$WIN_UFBX/tools/S2GeomCheck" "$WIN_UFBX/tools/dom_oracle.txt"
run s3scene "$UFBX" dotnet_run "$WIN_UFBX/tools/S3SceneCheck" "$WIN_UFBX/tools/s3_oracle.txt"
run hash "$UFBX" dotnet_run "$WIN_UFBX/tools/HashCheck"
run animcurve "$UFBX" dotnet_run "$WIN_UFBX/tools/AnimCurveCheck" "$WIN_UFBX/tools/animcurve_oracle.txt"
run ascii "$UFBX" dotnet_run "$WIN_UFBX/tools/AsciiCheck"
run numeric "$UFBX" dotnet_run "$WIN_UFBX/tools/NumericCheck"
run s4b "$REF" dotnet_run "$WIN_UFBX/tools/S4bCheck" "$WIN_UFBX/tools/s4b_oracle.txt" "$WIN_UFBX/tools/s4b_corpus.txt"
run s4c "$REF" dotnet_run "$WIN_UFBX/tools/S4cCheck" "$WIN_UFBX/tools/s4c_oracle.txt"
run s3bc "$REF" dotnet_run "$WIN_UFBX/tools/S3bcCheck" "$WIN_UFBX/tools/s3bc_oracle.txt"
run bake "$REF" dotnet_run "$WIN_UFBX/tools/BakeCheck" "$WIN_UFBX/tools/bake_oracle.txt" "$WIN_UFBX/tools/bake_corpus.txt"
run createanim "$REF" dotnet_run "$WIN_UFBX/tools/CreateAnimCheck" "$WIN_UFBX/tools/create_anim_oracle.txt" "$WIN_UFBX/tools/create_anim_corpus.txt"
# Batch L: the stream / stdio / open ABI family. Runs from $REF with BOTH the oracle and the
# corpus passed by absolute path -- the corpus, because MSYS re-encodes non-ASCII argv (the
# same reason every other corpus-driven harness here takes a file), and the oracle because
# `run` keeps the cwd and a relative path would resolve against $REF.
run stream "$REF" dotnet_run "$WIN_UFBX/tools/StreamCheck" "$WIN_UFBX/tools/stream_oracle.txt" "$WIN_UFBX/tools/stream_corpus.txt"
# Batch M: the thread pool ABI family. Same conventions as `stream` above (both paths absolute,
# cwd $REF): the variant table lives only in the C oracle, and the corpus entries are raw UTF-8
# byte strings. NOTE: the oracle binary itself is *not* rebuilt here -- regenerate it with the
# zig command in tools/pool_oracle.c's header whenever that file changes.
run pool "$REF" dotnet_run "$WIN_UFBX/tools/PoolCheck" "$WIN_UFBX/tools/pool_oracle.txt" "$WIN_UFBX/tools/pool_corpus.txt"
# Batch N: the skinning evaluation body. Same conventions as `pool` (cwd $REF, both paths
# absolute). NOTE: the oracle binary is *not* rebuilt here -- regenerate it with the zig command
# in tools/skin_oracle.c's header whenever that file or the corpus changes.
run skin "$REF" dotnet_run "$WIN_UFBX/tools/SkinCheck" "$WIN_UFBX/tools/skin_oracle.txt" "$WIN_UFBX/tools/skin_corpus.txt"
run pivot "$REF" dotnet_run "$WIN_UFBX/tools/PivotCheck" "$WIN_UFBX/tools/pivot_oracle.txt" "$WIN_UFBX/tools/pivot_corpus.txt"
# Batch P: scale helper nodes. NOTE: the oracle binaries are NOT rebuilt here -- see the zig
# command in the header of each tools/*_oracle.c.
run sh "$REF" dotnet_run "$WIN_UFBX/tools/ShCheck" "$WIN_UFBX/tools/sh_oracle.txt" "$WIN_UFBX/tools/sh_corpus.txt"
echo "== battery done"
