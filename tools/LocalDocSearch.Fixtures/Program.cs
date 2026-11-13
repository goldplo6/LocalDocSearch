namespace LocalDocSearch.Fixtures;

internal static class Program
{
    private static int Main(string[] args)
    {
        var encodingOnly = args.Length == 2 && args[0] == "--encoding";
        var longOnly = args.Length == 2 && args[0] == "--long";
        if ((!(encodingOnly || longOnly) && args.Length != 1) ||
            (args.Length == 1 && args[0].StartsWith("--", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(args[^1]))
        {
            Console.Error.WriteLine("Usage: LocalDocSearch.Fixtures [--encoding|--long] <output-directory>");
            return 2;
        }

        try
        {
            if (encodingOnly) EncodingSamples.CreateAll(args[1]);
            else if (longOnly) LongDocuments.CreateAll(args[1]);
            else SampleDocuments.CreateAll(args[0]);
            Console.WriteLine($"Created {(encodingOnly || longOnly ? 4 : 8)} original fixtures{(longOnly ? " and README" : "")} in {Path.GetFullPath(args[^1])}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
