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
- Před odstraněním zdroje se na finálním cíli znovu ověří SHA-256 a časy souboru. Na Windows se vyžaduje zachování času poslední změny i vytvoření.
- Přesun maže zdroj až po úspěšném ověření kopie.
- Záměr operace se uloží do lokální SQLite historie **před** změnou souborů. Po přerušení aplikace ověří zdroj a cíl podle SHA-256 a doplní výsledek; nejednoznačný stav zablokuje další přesuny bez mazání souborů.
- Rozpracovaná kopie má v historii vlastní přesnou cestu; po přerušení ji aplikace uklidí pouze při jednoznačně ověřeném stavu zdroje a cíle.
- Vrácení běhu kontroluje SHA-256 a zapisuje vlastní záměr před změnou souborů. Již úspěšně vrácené položky znovu nevrací; u změněných souborů nebo obsazené původní cesty se zastaví.
- Cíl uvnitř zdrojového stromu je zakázaný, aby další sken znovu nenačetl výstup.

Pro skutečné soubory používejte nejprve kopii vzorku. Karanténa duplicit soubory **přesouvá** i tehdy, když je následná organizace v režimu kopírování. Starý katalog se při prvním otevření migruje bez smazání historie.

## Vývoj

Projekt používá WPF a .NET 10 pro Windows x64.

```powershell
dotnet test .\FotoArchiv.Tests\FotoArchiv.Tests.csproj
dotnet run --project .\FotoArchiv.App\FotoArchiv.App.csproj
dotnet publish .\FotoArchiv.App\FotoArchiv.App.csproj -c Release -r win-x64 --self-contained true
```

Offline geolokace používá `cities500.zip`, detailní `CZ.zip` a `admin1CodesASCII.txt` ve složce `FotoArchiv.App\Data`. Další země lze doplnit vložením příslušného GeoNames ZIPu do stejné složky.
