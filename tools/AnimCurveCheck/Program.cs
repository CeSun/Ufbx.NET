// AnimCurveCheck: bit-exactness harness for the S2 animation tangent solvers.
//
// tools/animcurve_oracle.c #includes the frozen ufbx.c (compiled with
// `-mcpu=x86_64 -ffp-contract=off`, see PORTING_NOTES.md 浮点语义) and dumps
// UFBXI_KEY_* flag/parameter vectors through the real `ufbxi_solve_auto_tangent{,_left,
// _right}` and `ufbxi_solve_tcb` internals. All inputs and outputs are IEEE-754 bit
// patterns, so this side compares bitwise. The oracle's `ufbxi_context` is zeroed, which
// matches the port's fresh `UfbxiContext` (opts zero-initialized, `key_clamp_threshold`
// is never assigned by the C loader either).
//
// Usage: dotnet run --project tools/AnimCurveCheck -c Release -- [tools/animcurve_oracle.txt]
using System;
using System.Globalization;

namespace AnimCurveCheck
{
    static class Program
    {
        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "tools/animcurve_oracle.txt";
            string[] lines = System.IO.File.ReadAllLines(path);

            // C: a zeroed `ufbxi_context` with zeroed opts (the loader default,
            // `key_clamp_threshold` is never assigned by ufbxi_load()).
            var uc = new Ufbx.NET.UfbxiContext();

            int total = 0, pass = 0;
            int byTagA = 0, byTagL = 0, byTagR = 0, byTagT = 0;

            foreach (string line in lines) {
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                total++;
                bool ok;
                switch (t[0]) {
                case "A": {
                    byTagA++;
                    uint flags = ParseU32(t[1]);
                    double prevTime = ParseF64(t[2]);
                    double time = ParseF64(t[3]);
                    double nextTime = ParseF64(t[4]);
                    double prevValue = ParseF64(t[5]);
                    double value = ParseF64(t[6]);
                    double nextValue = ParseF64(t[7]);
                    float weightLeft = ParseF32(t[8]);
                    float weightRight = ParseF32(t[9]);
                    float autoBias = ParseF32(t[10]);
                    uint expected = ParseU32(t[11]);
                    float result = Ufbx.NET.UfbxiAnimReader.SolveAutoTangent(uc,
                        prevTime, time, nextTime, prevValue, value, nextValue,
                        weightLeft, weightRight, autoBias, flags);
                    ok = Ufbx.NET.UfbxBitUtil.SingleToBits(result) == expected;
                    break;
                }
                case "L": {
                    byTagL++;
                    uint flags = ParseU32(t[1]);
                    double prevTime = ParseF64(t[2]);
                    double time = ParseF64(t[3]);
                    double prevValue = ParseF64(t[4]);
                    double value = ParseF64(t[5]);
                    float weightLeft = ParseF32(t[6]);
                    float autoBias = ParseF32(t[7]);
                    uint expected = ParseU32(t[8]);
                    float result = Ufbx.NET.UfbxiAnimReader.SolveAutoTangentLeft(uc,
                        prevTime, time, prevValue, value, weightLeft, autoBias, flags);
                    ok = Ufbx.NET.UfbxBitUtil.SingleToBits(result) == expected;
                    break;
                }
                case "R": {
                    byTagR++;
                    uint flags = ParseU32(t[1]);
                    double time = ParseF64(t[2]);
                    double nextTime = ParseF64(t[3]);
                    double value = ParseF64(t[4]);
                    double nextValue = ParseF64(t[5]);
                    float weightRight = ParseF32(t[6]);
                    float autoBias = ParseF32(t[7]);
                    uint expected = ParseU32(t[8]);
                    float result = Ufbx.NET.UfbxiAnimReader.SolveAutoTangentRight(uc,
                        time, nextTime, value, nextValue, weightRight, autoBias, flags);
                    ok = Ufbx.NET.UfbxBitUtil.SingleToBits(result) == expected;
                    break;
                }
                case "T": {
                    byTagT++;
                    double tension = ParseF64(t[1]);
                    double continuity = ParseF64(t[2]);
                    double bias = ParseF64(t[3]);
                    double slopeLeft = ParseF64(t[4]);
                    double slopeRight = ParseF64(t[5]);
                    bool edge = t[6] != "0";
                    uint expectedLeft = ParseU32(t[7]);
                    uint expectedRight = ParseU32(t[8]);
                    Ufbx.NET.UfbxiAnimReader.SolveTcb(out float outLeft, out float outRight,
                        tension, continuity, bias, slopeLeft, slopeRight, edge);
                    ok = Ufbx.NET.UfbxBitUtil.SingleToBits(outLeft) == expectedLeft
                        && Ufbx.NET.UfbxBitUtil.SingleToBits(outRight) == expectedRight;
                    break;
                }
                default:
                    Console.WriteLine($"UNKNOWN TAG {t[0]}");
                    return 1;
                }

                if (ok) pass++;
                else if (pass + (total - pass) <= 20 || total - pass <= 20) {
                    Console.WriteLine($"FAIL line {total}: {line}");
                }
            }

            Console.WriteLine($"animcurve: {total} vectors / {pass} passed / {total - pass} failed " +
                $"(A={byTagA} L={byTagL} R={byTagR} T={byTagT})");
            return pass == total ? 0 : 1;
        }

        static uint ParseU32(string hex) => uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        static double ParseF64(string hex) =>
            Ufbx.NET.UfbxBitUtil.FromInt64(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

        static float ParseF32(string hex) => Ufbx.NET.UfbxBitUtil.BitsToSingle(ParseU32(hex));
    }
}
