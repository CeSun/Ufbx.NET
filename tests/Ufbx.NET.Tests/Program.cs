using System;

namespace Ufbx.NET.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "mathvec")
            {
                return MathVectorCheck.Run(args);
            }
            if (args.Length > 0 && args[0] == "streamcheck")
            {
                return StreamCheck.Run(args);
            }
            if (args.Length > 0 && args[0] == "hash")
            {
                return HashCommands.Run(args);
            }
            if (args.Length > 0 && args[0] == "goldens")
            {
                return HashCommands.Run(args);
            }
            Console.WriteLine("Usage: Ufbx.NET.Tests mathvec <vectors-file> [filter] | streamcheck" +
                " | hash <path-to-file> [frame]" +
                " | goldens <golden-file> [--data-dir <dir>] [--filter <substr>] [--max N]");
            return 1;
        }
    }
}
