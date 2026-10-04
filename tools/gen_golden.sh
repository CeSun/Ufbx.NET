#!/bin/bash
# Replicates misc/generate_hashses.py logic
cd "C:/Workspace/_analyze_ufbx"
OUT="C:/Workspace/ufbx-cs/tools/golden_hashes.txt"
: > "$OUT"
count=0
while IFS= read -r path; do
  file="$path"
  case "$file" in
    *_fail_*|*/fuzz/*|*/obj_fuzz/*|*/mtl_fuzz/*) continue ;;
  esac
  case "$file" in
    *" "*) continue ;;
  esac
  case "$file" in
    *.fbx)
      prev=""
      for i in 0 1 2 3 4 5 6 7 8 9; do
        frame=$((i*i))
        h=$(./test/hash_scene.exe "$file" --frame "$frame" 2>/dev/null) || continue
        h=$(echo "$h" | tr -d '[:space:]')
        [ -z "$h" ] && continue
        if [ "$h" = "$prev" ]; then break; fi
        echo "$h $frame $file" >> "$OUT"
        prev="$h"
      done
      ;;
    *.obj|*.mtl)
      h=$(./test/hash_scene.exe "$file" 2>/dev/null) || continue
      h=$(echo "$h" | tr -d '[:space:]')
      [ -z "$h" ] && continue
      echo "$h 0 $file" >> "$OUT"
      ;;
  esac
  count=$((count+1))
done < <(find data -type f | LC_ALL=C sort)
echo "DONE files=$count lines=$(wc -l < "$OUT")" 
