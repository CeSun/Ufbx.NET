#!/bin/bash
# Which ufbx.h `ufbx_abi` names still have no public C# counterpart in src/Ufbx?
# (A counterpart is any `public ... Name(` or `public ... Name =>` member, per the port's naming rule:
#  C name minus `ufbx_`, snake_case -> PascalCase.)
cd C:/Workspace/ufbx-cs || exit 1
missing=0
while read -r n; do
  [ -z "$n" ] && continue
  pascal=$(printf '%s' "${n#ufbx_}" | awk -F'_' '{for(i=1;i<=NF;i++){printf "%s%s", toupper(substr($i,1,1)), substr($i,2)}}')
  hit=$(grep -rnE "public .*(\b| )${pascal}(\s|\s*)\(.*$" src/Ufbx/ 2>/dev/null | head -1)
  [ -z "$hit" ] && hit=$(grep -rnE "public .*\b${pascal} *=>" src/Ufbx/ 2>/dev/null | head -1)
  [ -z "$hit" ] && hit=$(grep -rnE "public .*\b${pascal} *\{" src/Ufbx/ 2>/dev/null | head -1)
  if [ -n "$hit" ]; then
    printf "OK   %-46s %s\n" "$n" "${hit%%:*}:${hit##*:}" | cut -c1-150
  else
    printf "MISS %-46s -> %s\n" "$n" "$pascal"
    missing=$((missing+1))
  fi
done < /tmp/abi_names.txt
echo "total missing: ${missing}"
