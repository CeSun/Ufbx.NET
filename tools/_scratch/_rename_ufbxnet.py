#!/usr/bin/env python3
# 把仓库里旧的 Ufbx / Ufbx.Tests / UfbxTests 字样批量改写成 Ufbx.NET / Ufbx.NET.Tests。
# 全程按字节处理（.gitattributes 规定全仓库不做行尾转换，这里也必须保持字节语义），
# 只在内容真的变化时写回。规则顺序敏感，不要调换。
import subprocess, sys, re, os

ROOT = r"C:/Workspace/ufbx-cs"

# (old, new, is_regex)
RULES_RAW = [
    ("tests/Ufbx.Tests",            "tests/Ufbx.NET.Tests",   False),
    ("tests\\Ufbx.Tests",           "tests\\Ufbx.NET.Tests",  False),
    ("Ufbx.Tests.csproj",           "Ufbx.NET.Tests.csproj",  False),
    ("Ufbx.Tests",                  "Ufbx.NET.Tests",         False),
    ("src\\Ufbx\\Ufbx.csproj",      "src\\Ufbx.NET\\Ufbx.NET.csproj", False),
    ("src/Ufbx/Ufbx.csproj",        "src/Ufbx.NET/Ufbx.NET.csproj",   False),
    ("src/Ufbx/Ufbx/",              "src/Ufbx.NET/Ufbx.NET/", False),
    ("src\\Ufbx\\Ufbx\\",           "src\\Ufbx.NET\\Ufbx.NET\\", False),
    ("src\\Ufbx",                   "src\\Ufbx.NET",          False),
    ("src/Ufbx",                    "src/Ufbx.NET",           False),
    ("Ufbx.csproj",                 "Ufbx.NET.csproj",        False),
    ("<RootNamespace>UfbxTests</RootNamespace>", "<RootNamespace>Ufbx.NET.Tests</RootNamespace>", False),
    ("namespace UfbxTests",         "namespace Ufbx.NET.Tests", False),
    (r"(?<![.\w])UfbxTests\b",      "Ufbx.NET.Tests",         True),
    (r"(?m)^namespace Ufbx$",       "namespace Ufbx.NET",     True),
    ("using Ufbx;",                 "using Ufbx.NET;",        False),
    ("<RootNamespace>Ufbx</RootNamespace>", "<RootNamespace>Ufbx.NET</RootNamespace>", False),
    ("<AssemblyName>Ufbx</AssemblyName>",   "<AssemblyName>Ufbx.NET</AssemblyName>",   False),
    (r'(?<![.\w])Ufbx\.(?=Ufbx)',   "Ufbx.NET.",              True),
    ('= "Ufbx",',                   '= "Ufbx.NET",',          False),
    ("ufbx-cs.slnx",                "ufbx.net.slnx",          False),
    ("ufbx-cs.sln",                 "ufbx.net.sln",           False),
]

rules = []
for old, new, is_re in RULES_RAW:
    if is_re:
        rules.append((re.compile(old.encode("ascii")), new.encode("utf-8")))
    else:
        rules.append((old.encode("utf-8"), new.encode("utf-8")))

files = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True).stdout.split()
changed = []
for rel in files:
    p = os.path.join(ROOT, rel)
    try:
        with open(p, "rb") as f:
            b = f.read()
    except OSError:
        continue
    orig = b
    for old, new in rules:
        if hasattr(old, "sub"):
            b = old.sub(new, b)
        else:
            b = b.replace(old, new)
    if b != orig:
        with open(p, "wb") as f:
            f.write(b)
        changed.append(rel)

print("changed %d / %d tracked files" % (len(changed), len(files)))
