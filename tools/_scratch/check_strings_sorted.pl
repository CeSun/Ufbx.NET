my $src = shift;
my $port = shift;

open my $fh, "<", $src or die $!;
my @c;
my $in = 0;
while (my $l = <$fh>) {
    if (!$in) { $in = 1 if $l =~ /ufbxi_strings\[\] *=/; next; }
    last if $l =~ /^\};/;
    if ($l =~ /^\s*\{\s*(?:ufbxi_([A-Za-z0-9_]+)|"([^"]*)")\s*,/) {
        my $name = defined $1 ? $1 : $2;
        push @c, $name;
    }
}
close $fh;
print "c_count=", scalar(@c), "\n";

open my $pf, "<", $port or die $!;
my %val;
my @p;
my $pin = 0;
while (my $l = <$pf>) {
    if ($l =~ /^\s*(?:public )?static readonly string ([A-Za-z0-9_]+) = "([^"]*)";/) {
        $val{$1} = $2;
    }
    if (!$pin) { $pin = 1 if $l =~ /All = new string\[\]/; next; }
    if ($l =~ /^\s*\};/) { $pin = 0; next; }
    if ($l =~ /^\s*([A-Za-z0-9_]+),/) { push @p, $1; }
}
close $pf;
print "port_count=", scalar(@p), "\n";

my $ndiff = 0;
my $n = @c < @p ? @c : @p;
for my $i (0 .. $n - 1) {
    if ($c[$i] ne $p[$i]) { print "ORDER_DIFF $i: c=[$c[$i]] port=[$p[$i]]\n"; $ndiff++; if ($ndiff > 5) { last } }
}
print "order_diff=$ndiff\n";

my @v = map { exists $val{$_} ? $val{$_} : die "no literal for $_" } @p;
my $bad = 0;
for my $i (1 .. $#v) {
    if ($v[$i - 1] ge $v[$i]) { print "NOT_SORTED $i: [$v[$i-1]] vs [$v[$i]]\n"; $bad++; if ($bad > 8) { last } }
}
print "value_unsorted=$bad\n";
print "first=[$v[0]] last=[$v[-1]]\n";
