// Which ufbx.h `ufbx_abi` functions still have no public C# counterpart in src/Ufbx.NET?
// Naming rule of the port: C name minus the `ufbx_` prefix, snake_case -> PascalCase.
const fs = require("fs");
const path = require("path");

const abi = fs.readFileSync(process.argv[2], "utf8").trim().split(/\r?\n/).filter(Boolean);

function walk(dir, out) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith(".cs")) out.push(p);
  }
  return out;
}

const files = walk("src/Ufbx.NET", []);
const decls = new Map(); // pascal name -> [file, line]
for (const f of files) {
  const lines = fs.readFileSync(f, "utf8").split(/\r?\n/);
  lines.forEach((l, i) => {
    const m = /^\s*(?:public|internal)\s+(?:static\s+)?(?:readonly\s+)?(?:unsafe\s+)?([\w<>?,\[\].\s]+?)\s+(\w+)\s*(\(|=>|\{)/.exec(l);
    if (!m) return;
    if (!/^\s*public\b/.test(l) && !/^\s*internal\b/.test(l)) return;
    const name = m[2];
    if (!decls.has(name)) decls.set(name, [f, i + 1, /^\s*public\b/.test(l) ? "public" : "internal"]);
  });
}

const pascal = (n) => n.replace(/^ufbx_/, "").split("_").map(s => s.charAt(0).toUpperCase() + s.slice(1)).join("");
const missing = [], found = [];
for (const n of abi) {
  const p = pascal(n);
  if (decls.has(p)) found.push([n, p, decls.get(p)]);
  else missing.push([n, p]);
}
console.log("found: " + found.length + "  missing: " + missing.length);
for (const [n, p] of missing) console.log("MISS " + n + " -> " + p);
