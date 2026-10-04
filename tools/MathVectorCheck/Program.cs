using System;

namespace Ufbx.NET.Tests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "mathvec") return MathVectorCheck.Run(args);
            Console.WriteLine("Usage: MathVectorCheck mathvec <vectors-file> [fn,fn,...]");
            return 1;
        }
    }
}
