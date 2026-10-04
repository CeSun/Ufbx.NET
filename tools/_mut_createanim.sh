#!/bin/bash
# Batch K mutation control: apply one literal string replacement to the port, run the create_anim
# differential, then restore from tools/_scratch/<basename>.bak and prove byte equality.
# Usage: _mut_createanim.sh <tag> <repo-relative-path> <old> <new>
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
' "$file" "$old" "$new" || exit 1

out=$(cd /c/Workspace/_analyze_ufbx && dotnet run --project "$WIN/tools/CreateAnimCheck" -c Release -- \
    "$WIN/tools/create_anim_oracle.txt" "$WIN/tools/create_anim_corpus.txt" 2>&1)
code=$?
mm=$(printf '%s\n' "$out" | grep -oE 'mismatches [0-9]+' | head -1)
first=$(printf '%s\n' "$out" | grep -A2 -m1 "MISMATCH #1" | tr '\n' '~' | cut -c1-300)

cp "$bak" "$file"
cmp "$bak" "$file" || { echo "$tag: RESTORE FAILED"; exit 1; }
echo "$tag | exit=$code | ${mm:-no-summary} | ${first:-}"
