#!/usr/bin/env python3
"""Cross-check the S1 port's graph dump against tools/graph_oracle.txt.

Both sides emit the same record grammar (tools/graph_oracle.c `dump_graph`); the port dump
omits the leading per-file index `fi` because its file is delimited by a `=== <relpath> ===`
header.  Compares, per completed corpus file:
  * G-equivalent counts (elements / connections)
  * E records: element_id, type, typed_id, fbx_id, name payload, prop count
  * P records: per-prop (elem_id, ix), name payload, type, flags, key, int, reals, str, blob
  * C records: (ix, src, dst), src_prop / dst_prop name payloads
Prints a per-file summary and exits non-zero on any mismatch.
"""
import sys, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ORACLE = os.path.join(ROOT, "tools", "graph_oracle.txt")
DUMP = os.path.join(ROOT, "tools", "S1SelfCheck", "_dump.txt")
CORPUS = os.path.join(ROOT, "tools", "graph_corpus.txt")

def load_corpus():
    rels = []
    with open(CORPUS, "r", encoding="utf-8") as f:
        for line in f:
            t = line.strip()
            if t:
                rels.append(t)
    return rels

def parse_oracle(path):
    """-> dict[fi] = {'G': [...], 'E': {elem_idx: rec}, 'P': {(elem_idx,ix): rec}, 'C': {ix: rec}}"""
    files = {}
    cur = None
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            tok = line.split(" ")
            tag = tok[0]
            if tag == "K":
                continue
            if tag == "G":
                fi = int(tok[1])
                cur = files.setdefault(fi, {"G": tok[1:], "E": {}, "P": {}, "C": {}})
                cur["G"] = tok[1:]
            elif tag == "E":
                fi = int(tok[1])
                idx = int(tok[2])
                rec = tok[2:]  # element_id type typed_id fbx_id + name(4) + num_props
                files.setdefault(fi, {"G": None, "E": {}, "P": {}, "C": {}})["E"][idx] = rec
            elif tag == "P":
                fi = int(tok[1])
                key = (int(tok[2]), int(tok[3]))
                files.setdefault(fi, {"G": None, "E": {}, "P": {}, "C": {}})["P"][key] = tok[2:]
            elif tag == "C":
                fi = int(tok[1])
                ix = int(tok[2])
                files.setdefault(fi, {"G": None, "E": {}, "P": {}, "C": {}})["C"][ix] = tok[2:]
            else:
                raise SystemExit("unknown oracle tag: " + tag)
    return files

def parse_dump(path):
    """-> dict[relpath] = {'top': int, 'E': {idx: rec}, 'P': {(idx,ix): rec}, 'C': {ix: rec},
                            'root_id': str, 'templates': int}"""
    files = {}
    cur = None
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("=== ") and line.endswith(" ==="):
                cur = {"E": {}, "P": {}, "C": {}, "top": 0, "root_id": None, "templates": 0}
                files[line[4:-4]] = cur
                continue
            if cur is None:
                continue
            tok = line.split(" ")
            tag = tok[0]
            if tag == "top_nodes":
                cur["top"] = int(re.match(r"top_nodes \((\d+)\)", line).group(1))
            elif tag == "E-count":
                pass
            elif tag == "E":
                idx = int(tok[1])
                cur["E"][idx] = tok[1:]
            elif tag == "P":
                key = (int(tok[1]), int(tok[2]))
                cur["P"][key] = tok[1:]
            elif tag == "C-count":
                pass
            elif tag == "C":
                ix = int(tok[1])
                cur["C"][ix] = tok[1:]
            elif tag.startswith("root_id="):
                cur["root_id"] = line
            elif tag.startswith("templates="):
                cur["templates"] = int(tok[0].split("=")[1])
    return files

def cmp_rec(label, a, b, errs):
    if a != b:
        errs.append("%s\n      oracle: %s\n      port  : %s" % (label, " ".join(a), " ".join(b)))

def main():
    rels = load_corpus()
    oracle = parse_oracle(ORACLE)
    dump = parse_dump(DUMP)

    total_errs = 0
    ok_files = 0
    for rel, mine in sorted(dump.items()):
        fi = rels.index(rel) if rel in rels else None
        errs = []
        if fi is None:
            errs.append("file not in corpus list")
            orc = None
        else:
            orc = oracle.get(fi)

        if orc is None:
            errs.append("no oracle records for fi=%s" % fi)
        else:
            # element set
            if set(orc["E"].keys()) != set(mine["E"].keys()):
                errs.append("element index set differs: oracle=%d port=%d" % (len(orc["E"]), len(mine["E"])))
            for idx in sorted(set(orc["E"].keys()) & set(mine["E"].keys())):
                cmp_rec("E[%d]" % idx, orc["E"][idx], mine["E"][idx], errs)
            # props
            if set(orc["P"].keys()) != set(mine["P"].keys()):
                missing = sorted(set(orc["P"]) - set(mine["P"]))
                extra = sorted(set(mine["P"]) - set(orc["P"]))
                errs.append("prop key set differs: missing=%s extra=%s" % (missing[:6], extra[:6]))
            for key in sorted(set(orc["P"].keys()) & set(mine["P"].keys())):
                cmp_rec("P[%d,%d]" % key, orc["P"][key], mine["P"][key], errs)
            # connections
            if set(orc["C"].keys()) != set(mine["C"].keys()):
                errs.append("connection index set differs: oracle=%d port=%d" % (len(orc["C"]), len(mine["C"])))
            for ix in sorted(set(orc["C"].keys()) & set(mine["C"].keys())):
                cmp_rec("C[%d]" % ix, orc["C"][ix], mine["C"][ix], errs)
            # counts vs G
            g = orc["G"]
            ne = int(g[4]); nc = int(g[5])
            if ne != len(mine["E"]):
                errs.append("G num_elements=%d vs port E=%d" % (ne, len(mine["E"])))
            if nc != len(mine["C"]):
                errs.append("G num_conns=%d vs port C=%d" % (nc, len(mine["C"])))

        status = "OK  " if not errs else "FAIL"
        print("%s %s  elements=%d props=%d conns=%d top_nodes=%d"
              % (status, rel, len(mine["E"]), len(mine["P"]), len(mine["C"]), mine["top"]))
        for e in errs:
            print("      " + e)
        total_errs += len(errs)
        if not errs:
            ok_files += 1

    print()
    print("files compared: %d   clean: %d   mismatches: %d" % (len(dump), ok_files, total_errs))
    return 0 if total_errs == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
