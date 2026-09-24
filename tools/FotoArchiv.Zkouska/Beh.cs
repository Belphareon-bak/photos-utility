using System.Collections.Concurrent;
using System.Security.Cryptography;
using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Zkouska;

// "beh <zdroj> <cil> [--provest]": stejne poradi kroku jako MainViewModel -
// sken, metadata, sidecary, lokality, duplicity, plan. S --provest kopiruje
// (nikdy nepresouva) a overi kazdy cil: existenci, SHA-256 a cas posledni zmeny.
internal static class BehPrikaz
{
    public static async Task<int> SpustAsync(string zdroj, string cil, bool provest)
    {
        var ct = CancellationToken.None;
        zdroj = Path.GetFullPath(zdroj);
        cil = Path.GetFullPath(cil);
        if (cil.StartsWith(zdroj.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("STOP: cil lezi uvnitr zdroje.");
            return 2;
        }

        var settings = new AppSettings { DestinationRoot = cil, TransferMode = TransferMode.Copy };
        var varovani = new ConcurrentQueue<string>();
        var nalezy = new MediaScanner().EnumerateFiles([new SourceFolder(zdroj)], varovani.Enqueue, ct,
            [settings.QuarantineFolderName, settings.RejectedFolderName]);

        var reader = new MetadataReaderService();
        var polozky = new ConcurrentBag<MediaItem>();
        await Parallel.ForEachAsync(nalezy, ct, async (n, t) => polozky.Add(await reader.ReadAsync(n.Path, n.Root, n.Kind, t)));
        var media = polozky.ToList();
        await new SidecarMetadataService().ApplyAsync(media, null, ct);
        var serazene = media.OrderBy(i => i.CapturedAt).ThenBy(i => i.FilePath).ToList();
        var geo = new GeoNamesService();
        if (geo.IsAvailable) await geo.ResolveAsync(serazene, null, ct);
        var skupiny = await new DuplicateDetectionService().DetectAsync(serazene, null, ct);

        Nadpis($"SKEN  {zdroj}");
        Console.WriteLine($"  souboru: {media.Count}   fotek: {media.Count(i => i.Kind == MediaKind.Image)}   videi: {media.Count(i => i.Kind == MediaKind.Video)}   sidecaru: {media.Count(i => i.Kind == MediaKind.Sidecar)}");
        Console.WriteLine($"  geolokace: {(geo.IsAvailable ? "zapnuta" : "NEDOSTUPNA - chybi Data/*.zip")}");
        foreach (var v in varovani) Console.WriteLine($"  varovani: {v}");

        Nadpis("ODKUD SE VZALO DATUM PORIZENI");
        foreach (var g in serazene.GroupBy(i => ZdrojData(i.CaptureDateSource)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Count(),6}  {g.Key}");

        Nadpis("VYRAZENO (pujde do _NeniFoto)");
        var vyrazene = serazene.Where(i => i.RejectionReason is not null).ToList();
        if (vyrazene.Count == 0) Console.WriteLine("  nic");
        foreach (var i in vyrazene) Console.WriteLine($"  {Rel(zdroj, i.FilePath),-60} {i.RejectionReason}");

        Nadpis($"DUPLICITY ({skupiny.Count} skupin)");
        foreach (var s in skupiny.Take(15))
        {
            Console.WriteLine($"  {s.Id}  {s.Confidence}  {s.Items.Count} ks");
            foreach (var i in s.Items)
                Console.WriteLine($"     {(i.IsRecommendedKeep ? "PONECHAT" : "        ")}  {Rel(zdroj, i.FilePath),-52} {i.QualityLabel}");
        }
        // automaticky jen to, co je bezpecne: u shody bit po bitu ponechat doporucenou
        foreach (var s in skupiny.Where(s => s.Confidence == DuplicateConfidence.Exact))
            foreach (var i in s.Items) i.WillKeep = i.IsRecommendedKeep;

        var plan = new OrganizationPlanner().Build(serazene, settings);
        Nadpis($"PLAN ({plan.Count} polozek)");
        foreach (var g in plan.GroupBy(p => p.Action)) Console.WriteLine($"  {g.Key,-10} {g.Count()}");
        foreach (var p in plan.Take(40))
            Console.WriteLine($"  {Rel(zdroj, p.SourcePath),-46} -> {Rel(cil, p.TargetPath)}{(p.Warning is null ? "" : "   ! " + p.Warning)}");
        if (plan.Count > 40) Console.WriteLine($"  … a dalsich {plan.Count - 40}");

        if (!provest) { Console.WriteLine("\nNahled. S --provest se soubory zkopiruji do cile a overi."); return 0; }

        var katalog = new CatalogService(Path.Combine(cil, "_zkouska-katalog.db"));
        var runId = await new OperationExecutor(katalog).ExecuteAsync(plan, "Zkouska", null, ct);

        Nadpis($"OVERENI KOPII (beh #{runId})");
        int ok = 0, chyba = 0, spatnyCas = 0;
        foreach (var p in plan.Where(p => p.Action == PlannedAction.Copy))
        {
            if (p.Status != "Hotovo" || !File.Exists(p.TargetPath)) { chyba++; Console.WriteLine($"  NEPROVEDENO  {Rel(zdroj, p.SourcePath)}: {p.Status}"); continue; }
            var shoda = Hash(p.SourcePath) == Hash(p.TargetPath);
            var casOk = File.GetLastWriteTimeUtc(p.SourcePath) == File.GetLastWriteTimeUtc(p.TargetPath);
            if (!shoda) { chyba++; Console.WriteLine($"  OBSAH SE LISI  {Rel(cil, p.TargetPath)}"); }
            else if (!casOk) { spatnyCas++; Console.WriteLine($"  CAS NESEDI     {Rel(cil, p.TargetPath)}"); }
            else ok++;
        }
        Console.WriteLine($"\n  v poradku: {ok}   jiny cas: {spatnyCas}   chyba: {chyba}");
        Console.WriteLine($"  zdroj nezmenen: {(Directory.EnumerateFiles(zdroj, "*", SearchOption.AllDirectories).Count() == nalezy.Count + PocetOstatnich(zdroj, nalezy) ? "ano" : "ZKONTROLOVAT")}");
        return chyba == 0 && spatnyCas == 0 ? 0 : 3;
    }

    private static int PocetOstatnich(string zdroj, IReadOnlyList<(string Path, string Root, MediaKind Kind)> nalezy)
    {
        var videne = nalezy.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        return Directory.EnumerateFiles(zdroj, "*", SearchOption.AllDirectories).Count(f => !videne.Contains(f));
    }

    private static string ZdrojData(string s) => s switch
    {
        "Čas souboru" => "cas souboru  <- nespolehlive",
        "Název souboru" => "nazev souboru",
        "Neznámé" => "nezname",
        _ when s.StartsWith("Google") => "Google Takeout JSON",
        _ => $"metadata: {s}"
    };

    private static string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
    private static string Rel(string root, string p) => Path.GetRelativePath(root, p);
    private static void Nadpis(string t) { Console.WriteLine(); Console.WriteLine("== " + t); }
}
