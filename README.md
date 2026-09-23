# FotoArchiv

FotoArchiv je česká desktopová aplikace pro kontrolované sjednocení fotografií a videí z více telefonů a adresářů. Pracuje rekurzivně, nejdříve vyhodnotí duplicity a teprve po jejich uzavření připraví plán organizace.

## Výchozí workflow

1. Přidejte jeden nebo více zdrojových adresářů.
2. Spusťte samostatnou kontrolu duplicit.
3. U každé skupiny ponechte doporučenou, vybranou, nebo všechny varianty.
4. Případné vyřazené varianty přesuňte do `_DuplicatesReview`.
5. Zkontrolujte nebo upravte lokalitu a zvolte cílový adresář.
6. Připravte náhled cest a až potom spusťte kopírování nebo přesun.

Výchozí struktura je `Lokalita\yyyy-MM-dd\001.ext`, například `Okoř\2025-09-12\001.heic`. Datum lze vypnout a fotografie z více dnů sloučit přímo pod lokalitu. Volitelně lze před lokalitu přidat stát a region.

## Duplicity

- **Absolutní jistota:** stejná délka a shodný SHA-256 celého souboru.
- **Vysoká jistota:** téměř shodný perceptuální otisk, shodný kryptografický otisk normalizovaného obrazového vzorku, stejné rozlišení a srovnatelné množství obrazových dat.
- **Nízká jistota:** vizuální podobnost nebo shodné technické parametry bez dostatečného potvrzení.

Výběr nejlepší varianty je oddělený od jistoty shody. Přednost má RAW/originální formát, potom rozlišení, bitová hloubka, velikost souboru a úplnost metadat. Aplikace doporučuje, ale sama duplicity nemaže.

## Bezpečnost souborů

- Existující cílový soubor se nikdy nepřepisuje.
- Kopie vzniká nejprve jako dočasný soubor a před dokončením se porovná SHA-256 se zdrojem.
- Přesun maže zdroj až po úspěšném ověření kopie.
- Každá operace se zapisuje do lokální SQLite historie.
- Vrácení běhu je povoleno pouze tehdy, pokud se kontrolní hash od provedení nezměnil.
- Cíl uvnitř zdrojového stromu je zakázaný, aby další sken znovu nenačetl výstup.

## Vývoj

Projekt používá WPF a .NET 10 pro Windows x64.

```powershell
.\.dotnet\dotnet.exe test .\FotoArchiv.Tests\FotoArchiv.Tests.csproj
.\.dotnet\dotnet.exe run --project .\FotoArchiv.App\FotoArchiv.App.csproj
.\.dotnet\dotnet.exe publish .\FotoArchiv.App\FotoArchiv.App.csproj -c Release -r win-x64 --self-contained true
```

Offline geolokace používá `cities500.zip`, detailní `CZ.zip` a `admin1CodesASCII.txt` ve složce `FotoArchiv.App\Data`. Další země lze doplnit vložením příslušného GeoNames ZIPu do stejné složky.
