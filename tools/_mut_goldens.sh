#!/bin/bash
# Batch J-补 mutation control for the loader-side float-literal fixes: revert one widened float32
# constant to a decimal double, run the goldens differential, then restore from
# tools/_scratch/<basename>.bak and prove byte equality.
# Usage: _mut_goldens.sh <tag> <repo-relative-path> <old> <new>
set -u
WIN=C:/Workspace/ufbx-cs
POS=/c/Workspace/ufbx-cs
tag="$1"; rel="$2"; old="$3"; new="$4"
file="$POS/$rel"
bak="$POS/tools/_scratch/$(basename "$rel").bak"

perl -e '
  my ($p,$o,$n)=@ARGV;
  open my $fh,"<",$p or die "open-in: $!"; binmode $fh; local $/; my $s=<$fh>; close $fh;
  my $c = () = ($s =~ /\Q$o\E/g);
  if($c != 1){ print "ERROR: $c occurrences\n"; exit 1 }
  $s =~ s/\Q$o\E/$n/;
  open my $oh,">",$p or die "open-out: $!"; binmode $oh; print $oh $s; close $oh;
' "$file" "$old" "$new" || { echo "$tag | APPLY FAILED"; exit 1; }

out=$(cd "$POS" && dotnet run --project tests/Ufbx.NET.Tests -c Release -- goldens tools/golden_hashes.txt 2>&1)
code=$?
summary=$(printf '%s\n' "$out" | grep -m1 "goldens:")
first=$(printf '%s\n' "$out" | grep -m1 -B1 -A1 "MISMATCH\|mismatched file" | tr '\n' '~' | cut -c1-200)

cp "$bak" "$file"
cmp "$bak" "$file" || { echo "$tag: RESTORE FAILED"; exit 1; }
echo "$tag | exit=$code | ${summary:-no-summary} | ${first:-}"
