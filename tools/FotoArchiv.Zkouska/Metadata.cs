using FotoArchiv.App.Models;
using FotoArchiv.App.Services;
using MetadataExtractor;

namespace FotoArchiv.Zkouska;

// "metadata <soubor...>": co z jednoho souboru vycte jadro a proc
internal static class MetadataPrikaz
{
    public static async Task<int> SpustAsync(string[] soubory)
    {
        var reader = new MetadataReaderService();
        foreach (var soubor in soubory)
        {
            var kind = Path.GetExtension(soubor).ToLowerInvariant() switch
            {
                ".mov" or ".mp4" or ".m4v" or ".avi" => MediaKind.Video,
                ".xmp" or ".aae" or ".json" => MediaKind.Sidecar,
                _ => MediaKind.Image
            };
            var item = await reader.ReadAsync(Path.GetFullPath(soubor), Path.GetDirectoryName(Path.GetFullPath(soubor))!, kind, CancellationToken.None);
            Console.WriteLine($"=== {Path.GetFileName(soubor)}");
            Console.WriteLine($"   druh            : {item.Kind}");
            Console.WriteLine($"   rozmery         : {item.Dimensions}  format: {item.Format ?? "-"}");
            Console.WriteLine($"   datum porizeni  : {item.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-"}");
            Console.WriteLine($"   zdroj data      : {item.CaptureDateSource}");
            Console.WriteLine($"   slozka podle dne: {item.CapturedAt?.ToString("yyyy-MM-dd") ?? "Bez_data"}");
            Console.WriteLine($"   vyrazeno        : {item.RejectionReason ?? "ne"}");
            if (item.Error is not null) Console.WriteLine($"   chyba cteni     : {item.Error}");
            // vsechny datove znacky, ktere knihovna vidi - at je videt, co PreferredDateTags minul
            try
            {
                foreach (var d in ImageMetadataReader.ReadMetadata(soubor))
                    foreach (var t in d.Tags.Where(t => t.Name.Contains("Date", StringComparison.OrdinalIgnoreCase)
                                                     || t.Name.Contains("Creat", StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"     znacka  [{d.Name}] {t.Name} = {t.Description}");
            }
            catch (Exception e) { Console.WriteLine($"     (metadata nelze vypsat: {e.GetType().Name})"); }
        }
        return 0;
    }
}
