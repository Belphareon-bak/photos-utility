namespace FotoArchiv.Zkouska;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "metadata") return await MetadataPrikaz.SpustAsync(args[1..]);
        if (args.Length >= 3 && args[0] == "beh") return await BehPrikaz.SpustAsync(args[1], args[2], args.Contains("--provest"));
        Console.WriteLine("Pouziti:");
        Console.WriteLine("  metadata <soubor...>              co jadro vycte z jednotlivych souboru");
        Console.WriteLine("  beh <zdroj> <cil> [--provest]     cely pruchod: sken, duplicity, plan; s --provest i kopie");
        return 1;
    }
}
