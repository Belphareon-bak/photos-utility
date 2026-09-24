# FotoArchiv.Zkouska

Konzolová zkouška jádra FotoArchivu **bez grafického rozhraní**. Překládá přímo
zdrojové soubory z `FotoArchiv.App` (ne kopie), kromě `ThumbnailService.cs`,
který jako jediný závisí na WPF. Proto běží na Windows i na Linuxu a testuje
přesně ten kód, který používá aplikace.

## Příkazy

```
dotnet run -- metadata <soubor...>
```
Co jádro vyčte z jednotlivých souborů: rozměry, datum pořízení, **odkud se datum
vzalo**, do jaké složky by soubor šel a jestli se vyřadí. Navíc vypíše všechny
datové značky, které knihovna v souboru vidí — tak se pozná, když FotoArchiv
nějakou přehlíží.

```
dotnet run -- beh <zdroj> <cil>
```
Celý průchod ve stejném pořadí jako aplikace: sken, metadata, sidecary, lokality,
duplicity, plán. **Jen náhled, nic nekopíruje.** Vypíše, odkud se bralo datum,
co se vyřadí do `_NeniFoto`, skupiny duplicit s doporučením a plán.

```
dotnet run -- beh <zdroj> <cil> --provest
```
Totéž a navíc soubory **zkopíruje** (nikdy nepřesouvá) a u každého ověří,
že cíl existuje, má stejný SHA-256 a stejný čas poslední změny jako zdroj.

U duplicit se automaticky rozhoduje jen to, co je bezpečné: u shody bit po bitu
se ponechá doporučená kopie. Podobné snímky se jen vypíšou, rozhodnutí o nich
je v aplikaci na uživateli.

## Jak testovat na vlastních datech

**Vždy na kopii.** Zkopíruj si vzorek do samostatné složky a cíl dej mimo ni —
nástroj odmítne cíl uvnitř zdroje.

Windows:
```powershell
cd tools\FotoArchiv.Zkouska
..\..\.dotnet\dotnet.exe run -- beh D:\vzorek D:\vzorek-vystup
```

Linux (SDK v `~/.dotnet`):
```bash
cd tools/FotoArchiv.Zkouska
~/.dotnet/dotnet run -- beh ~/vzorek ~/vzorek-vystup
```

Geolokace potřebuje `FotoArchiv.App/Data/*.zip`; když chybí, zkouška to vypíše
a lokality nechá prázdné.
