// Reconstruct the xorshift generator from tools/mathvec_impl.c and compare
// against tools/math_vectors.txt to confirm the argument encoding.
const M = (1n << 64n) - 1n;
let state = 0x9e3779b97f4a7c15n;
function next() {
  state ^= (state << 13n) & M;
  state ^= state >> 7n;
  state ^= (state << 17n) & M;
  return state & M;
}
function d_from_bits(u) {
  const b = Buffer.alloc(8);
  b.writeBigUInt64LE(u);
  return b.readDoubleLE(0);
}
function bitsOf(d) {
  const b = Buffer.alloc(8);
  b.writeDoubleLE(d, 0);
  return b.readBigUInt64LE(0);
}
function randDouble(mode) {
  let u = next();
  if (mode === 0) { u &= 0x7ff0000000000000n; u |= 1n & next(); }
  else if (mode === 1) { u &= 0x4330000000000000n; }
  else if (mode === 2) { u &= 0x4030000000000000n; u |= (next() & 0x000fffffffffffffn); }
  else if (mode === 3) {
    u &= 0x7ff0000000000000n;
    u |= (next() & 0x000fffffffffffffn);
    if ((u >> 52n) === 0x7ffn) u &= ~0x000fffffffffffffn;
  } else if (mode === 4) { u &= 0x3ff0000000000000n; u |= (next() & 0x000fffffffffffffn); }
  else { u &= 0x7fffffffffffffffn; }
  return d_from_bits(u);
}
const specials = [0.0, -0.0, 1.0, -1.0, 0.5, 2.0, 1e10, 1e-10, 1e300, 1e-300,
  1.0 / 0.0, -1.0 / 0.0];

const FN = ['sqrt', 'abs', 'rint', 'floor', 'ceil', 'isnan', 'nextafter', 'fmin',
  'fmax', 'copysign', 'sin', 'cos', 'tan', 'asin', 'acos', 'atan', 'atan2', 'pow'];
const TWO = new Set(['nextafter', 'fmin', 'fmax', 'copysign', 'atan2', 'pow']);

const lines = require('fs').readFileSync(process.argv[2], 'utf8').split('\n');
let firstBad = -1, argMismatch = 0, resMismatch = 0, checked = 0;
for (let i = 0; i < 4000; i++) {
  let x = randDouble(i % 5);
  let y = randDouble((i + 2) % 5);
  if (i < specials.length) x = specials[i];
  const xb = bitsOf(x), yb = bitsOf(y);
  for (let f = 0; f < FN.length; f++) {
    const idx = i * FN.length + f;
    const parts = (lines[idx] || '').trim().split(/\s+/);
    if (parts.length !== 3) { console.log(`line ${idx}: bad format`); continue; }
    const arg = BigInt('0x' + parts[1]);
    if (parts[0] !== FN[f]) { console.log(`line ${idx}: fn ${parts[0]} != ${FN[f]}`); return 1; }
    const expected = TWO.has(FN[f]) ? (xb | yb) : xb;
    checked++;
    if (arg !== expected) {
      argMismatch++;
      if (firstBad < 0) {
        firstBad = idx;
        console.log(`ARG MISMATCH line ${idx} fn=${FN[f]}`);
        console.log(`  file  = ${arg.toString(16).padStart(16, '0')}`);
        console.log(`  xb|yb = ${expected.toString(16).padStart(16, '0')}`);
        console.log(`  xb    = ${xb.toString(16).padStart(16, '0')}`);
        console.log(`  yb    = ${yb.toString(16).padStart(16, '0')}`);
      }
    }
  }
}
console.log(`checked ${checked} lines: argMismatch=${argMismatch} firstBadLine=${firstBad}`);
// Report a couple of known results to sanity check the model.
console.log('i=0 x=' + specials[0] + ' y bits=' + bitsOf((() => { let s = state; return 0; })()).toString(16));
module.exports = { randDouble, specials, FN, TWO, bitsOf, d_from_bits };
