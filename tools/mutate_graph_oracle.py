#!/usr/bin/env python3
"""Mutation driver for graph_oracle.c.

Each mutation is a single, surgical edit to the *oracle* that must make
tools/GraphCheck report a structural violation (i.e. GRAPH CHECK FAIL).  The
oracle is restored from tools/graph_oracle.c.bak between rounds, so the file is
left byte-identical when the script finishes.

Run from C:/Workspace/ufbx-cs:
    python tools/mutate_graph_oracle.py
"""
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "graph_oracle.c")
# The pristine snapshot is taken at startup and removed at exit, so the repo keeps no
# permanent `.bak` (task boundary: leave the workspace exactly as it was).
SNAP = os.path.join(ROOT, "graph_oracle.pristine.tmp.c")
MUT_ORACLE = os.path.join(ROOT, "graph_oracle.mut.txt")
UFAX = "C:/Workspace/_analyze_ufbx"
ZIG = "C:/Users/cesun/AppData/Local/Temp/zig-toolchain/zig-x86_64-windows-0.16.0"

# name -> (old, new) exact-substring replacements; each must hit exactly once.
MUTATIONS = [
    ("A: E.fbx_id column prints element_id",
     "(unsigned long long)ids[i]);",
     "(unsigned long long)e->element_id);"),

    ("B: E record drops num_props",
     'printf(" %zu\\n", e->props.props.count);',
     'printf("\\n");'),

    ("C: connection src_prop bytes upper-cased",
     "emit_name(c->src_prop.data, c->src_prop.length);",
     "emit_name_upper(c->src_prop.data, c->src_prop.length);"),

    ("D: props dedup disabled (every P emitted twice)",
     "dump_prop(fi, e->element_id, ix, &e->props.props.data[ix]);",
     "dump_prop(fi, e->element_id, ix, &e->props.props.data[ix]);\n\t\t\t"
     "dump_prop(fi, e->element_id, ix, &e->props.props.data[ix]);"),

    ("E: prop value not dispatched by flags (INT token always)",
     'if (flags & UFBX_PROP_FLAG_VALUE_INT) {\n\t\tprintf(" %lld", (long long)p->value_int);\n\t} else {\n\t\tprintf(" -");\n\t}',
     'printf(" %lld", (long long)p->value_int);'),
]

# Helper the C mutation needs to compile.
UPPER_HELPER = '''
static void emit_name_upper(const char *data, size_t len)
{
	printf(" %zu %016llx", len, (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 64) {
		char tmp[64];
		size_t n = len > 64 ? 64 : len;
		for (size_t i = 0; i < n; i++) {
			char c = data[i];
			tmp[i] = (c >= 'a' && c <= 'z') ? (char)(c - 32) : c;
		}
		printf(" %s", emit_hex_into(hex_buf, tmp, n));
	} else {
		printf(" -");
	}
	printf(" %d", is_static_ptr(data) ? 1 : 0);
}
'''


def sh(cmd, cwd=None, env=None):
    return subprocess.run(cmd, shell=True, cwd=cwd, env=env,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True)


def compile_oracle(env):
    return sh("zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off "
              "-I C:/Workspace/_analyze_ufbx tools/graph_oracle.c -o tools/graph_oracle.exe",
              cwd=os.path.dirname(ROOT), env=env)


def run_oracle(env):
    exe = os.path.join(ROOT, "graph_oracle.exe")
    with open(MUT_ORACLE, "w", newline="") as fh:
        r = subprocess.run([exe], cwd=UFAX, stdout=fh,
                           stderr=subprocess.PIPE, text=True, env=env)
    return r


def run_check():
    return sh("dotnet run --project tools/GraphCheck -c Release -- "
              "tools/graph_oracle.mut.txt C:/Workspace/_analyze_ufbx",
              cwd=os.path.dirname(ROOT))


def main():
    if not os.path.exists(SRC):
        print("missing " + SRC)
        return 1
    shutil.copyfile(SRC, SNAP)
    pristine = open(SNAP, "r", encoding="utf-8").read()
    env = dict(os.environ)
    env["PATH"] = ZIG + ";" + env.get("PATH", "")

    results = []
    for name, old, new in MUTATIONS:
        text = pristine
        if name.startswith("C:"):
            # inject the helper right after emit_name()'s closing brace
            anchor = "static void emit_name(const char *data, size_t len)"
            assert text.count(anchor) == 1, "emit_name anchor"
            end = text.index(anchor)
            end = text.index("\n}\n", end) + 3
            text = text[:end] + UPPER_HELPER + text[end:]
        n = text.count(old)
        assert n == 1, "%s: anchor matched %d times" % (name, n)
        text = text.replace(old, new)
        with open(SRC, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)

        c = compile_oracle(env)
        if c.returncode != 0:
            results.append((name, "COMPILE FAIL", c.stdout.strip().splitlines()[-1:]))
            continue
        run_oracle(env)
        r = run_check()
        out = r.stdout
        verdict = "GRAPH CHECK PASS" if "GRAPH CHECK PASS" in out else "GRAPH CHECK FAIL"
        viol = ""
        for line in out.splitlines():
            if "structural violations:" in line:
                viol = line.strip()
        first = ""
        for line in out.splitlines():
            if line.startswith("VIOLATION "):
                first = line.strip()
                break
        results.append((name, verdict, [viol, first]))

    # restore the pristine oracle and rebuild the real artifacts
    shutil.copyfile(SNAP, SRC)
    if os.path.exists(MUT_ORACLE):
        os.remove(MUT_ORACLE)
    if os.path.exists(SNAP):
        os.remove(SNAP)
    c = compile_oracle(env)
    assert c.returncode == 0, "restore compile failed:\n" + c.stdout
    with open(os.path.join(ROOT, "graph_oracle.txt"), "w", newline="") as fh:
        r = subprocess.run([os.path.join(ROOT, "graph_oracle.exe")], cwd=UFAX,
                           stdout=fh, stderr=subprocess.PIPE, text=True, env=env)
    assert r.returncode == 0, "restore oracle run failed:\n" + r.stderr
    print("restored graph_oracle.c / .exe / .txt from pristine")

    print("=" * 78)
    for name, verdict, info in results:
        print("%-52s %s" % (name, verdict))
        for i in info:
            print("      " + i)
    return 0


if __name__ == "__main__":
    sys.exit(main())
