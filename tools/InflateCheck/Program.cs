using System;

namespace UfbxTests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "inflatecorpus") return InflateCheck.DumpCorpus(args);
            if (args.Length >= 1 && args[0] == "netdiag") return InflateCheck.NetDiag(args);
            // No arguments (the documented invocation) runs the differential check;
            // `inflatecorpus` and `netdiag` are the auxiliary modes.
            return InflateCheck.Run(args);
        }
    }
}
