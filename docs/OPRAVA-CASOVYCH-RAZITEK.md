# Oprava zacházení s časem pořízení

Větev `fix/zachovani-casovych-razitek`. Tři změny ve dvou souborech, 50 přidaných řádků.
Řeší jeden problém ve třech projevech: **aplikace ztrácela datum pořízení u souborů,
které nemají EXIF.**

## Stav ověření

| | Výsledek |
|---|---|
| Překlad jádra (.NET 10.0.401, `net10.0` bez WPF) | **0 chyb, 0 varování** |
| Testová sada | **13 z 15 prochází** |
| Regrese proti původnímu kódu | **žádná** — tytéž 2 testy padají i bez úprav |
| Nový test `Copy_PreservesSourceLastWriteTime` | s vypnutou opravou **padá**, se zapnutou **prochází** |

Ověřeno na Linuxu proti skutečným souborům v repu, přes dočasný projekt `net10.0`,
který zahrnuje celé jádro kromě `ThumbnailService.cs` (jediný soubor jádra závislý na WPF).

**Dva testy padají z důvodů prostředí, ne kvůli těmto změnám:**

- `Move_RollsBackVerifiedTargetWhenSourceCannotBeDeleted` — dělá zdroj nesmazatelným
  přes `FileAttributes.ReadOnly`. Na Windows `File.Delete` odmítne, na Linuxu se právo
  mazat bere z adresáře, takže smazání projde a k vrácení nedojde. Test je vázaný
  na Windows.
- `Resolve_UsesDetailedCountryDatasetForOkor` — potřebuje `Data/CZ.zip`
  a `cities500.zip`, které dočasný projekt nekopíruje do výstupu.

**Co ověřené není:** sestavení WPF vrstvy (`net10.0-windows`). Žádná z úprav se jí
nedotýká ani nemění veřejné rozhraní jádra, ale build na Windows je stále potřeba.

## Proč to bylo potřeba

Datum pořízení se hledá ve třech krocích: **EXIF → název souboru → čas souboru.**
Poslední krok se týká zhruba pětiny materiálu — měřeno 2026-09-23 na reálných datech:

| Složka | Souborů | EXIF | Název | Čas souboru |
|---|---|---|---|---|
| `Latest Backup/pic` | 3 977 | 62,6 % | 18,8 % | **18,6 %** |
| `Gary/Pictures` | 319 | 88,4 % | 0 % | **11,6 %** |

U fotografií, které byly několikrát přesunuté mezi disky, zálohami a NAS, je čas
souboru nespolehlivý. Změřený příklad z reálných dat: video
`When James Haskell trained Martin Bayfield.mp4` má uvnitř kontejneru
`creation_time` **2016-08-13**, ale čas souboru **2018-07-31** — přesun v roce 2018
skutečné datum přepsal.

## Co se změnilo

### 1. Kopírování zachovává časová razítka
`Services/OperationExecutor.cs` — přidána metoda `PreserveTimestamps`, volaná z `CopyAsync`.

`CopyAsync` kopíruje přes `FileStream`, což razítka **nepřenáší** (na rozdíl od `File.Copy`),
a `File.SetLastWriteTime` se v projektu nevolalo nikde. Každý běh tedy nastavil cíli
aktuální čas.

Nejhorší na tom bylo, že aplikace ničila přesně tu informaci, ze které sama čerpá,
když chybí EXIF. Po prvním běhu bylo u těch souborů datum nenávratně pryč.

Razítka se nastavují na dočasný `.partial` soubor. `File.Move` je při přejmenování
na cílové jméno zachová, takže pořadí kroků zůstalo nedotčené. Nastavení razítek
nemění obsah, takže kontrolu SHA-256, která následuje, nijak neovlivní.

Selhání zápisu razítek se polyká — některé cíle (SMB, FAT) ho odmítnou a samotná
kopie tím neztrácí platnost.

### 2. Záložní datum bere starší z obou časů
`Services/MetadataReaderService.cs` — místo `item.FileCreatedAt` se použije starší
z `FileModifiedAt` a `FileCreatedAt`.

Na Windows kopírování souboru `LastWriteTime` **zachovává**, ale `CreationTime`
nastaví na teď. Původní kód si vybral právě tu hodnotu, která přesun nepřežije.
Starší z obou je u opakovaně stěhovaného materiálu výrazně lepší odhad.

**Řetězec `"Čas souboru"` zůstal nezměněný záměrně.** Filtruje se podle něj
v `SidecarMetadataService.cs:80` a testuje se v `SidecarMetadataServiceTests.cs:16`.
Přejmenování na třeba `"Čas souboru (nejistý)"` by tiše vyplo přepisování data
z Google Takeout JSONu — sidecar by přestal takto označené položky považovat
za přepsatelné. Pokud se ten popisek má změnit, musí se změnit na všech třech místech.

### 3. Datum z videa se už neposouvá o časovou zónu
`Services/MetadataReaderService.cs` — nová metoda `BuildCaptureDate`.

Původní kód označil `DateTimeKind.Local` **každé** datum z metadat. U EXIF
`Date/Time Original` je to správně, protože EXIF ukládá místní čas bez zóny.
Ale `Media Create Date` a `Creation Time` (QuickTime/MP4, obojí je
v `PreferredDateTags`) jsou **UTC**.

Ověřeno na reálném souboru: `VID20230216144850.mp4` má v názvu 14:48:50,
`creation_time` uvnitř 13:51:09 UTC — rozdíl je hodina zimního posunu plus dvě
minuty do dopsání souboru.

Důsledek chyby: video natočené mezi půlnocí a posunem UTC spadlo do složky
předchozího dne. V ČR jde o 1–2 hodiny denně, tedy kolem 4–8 % záznamů.

Rozlišuje se podle jména adresáře metadat — `QuickTime` nebo `MP4` v názvu
znamená UTC, cokoli jiného se bere jako dosud.

## Co ověřit na Windows

```powershell
.\.dotnet\dotnet.exe build .\FotoArchiv.App\FotoArchiv.App.csproj
.\.dotnet\dotnet.exe test .\FotoArchiv.Tests\FotoArchiv.Tests.csproj
```

Stávajících 14 testů musí projít beze změny — žádná z úprav nemění veřejné rozhraní.

Testy, které k tomu chybí a stálo by za to je doplnit:

1. `OperationExecutor` — zkopírovat soubor se známým `LastWriteTime` a ověřit,
   že cíl má stejný. Tenhle test by chybu odhalil a dosud neexistuje.
2. `MetadataReaderService` — soubor, kde `CreationTime` je novější než
   `LastWriteTime`, musí dostat `CapturedAt` rovné tomu staršímu.
3. `BuildCaptureDate` — tentýž `DateTime` z adresáře `QuickTime Movie Header`
   a z `Exif SubIFD` musí vyjít různě, právě o posun zóny.

## Co tahle větev neřeší

V `docs/REVIZE-2026-09-23.md` je dalších jedenáct nálezů. Záměrně sem nepatří,
protože nesouvisí s datem pořízení. Za pozornost stojí hlavně:

- skener nepřeskakuje `@eaDir`, `#recycle` a `#snapshot`, takže na Synology
  načte náhledy jako plnohodnotné fotografie;
- karanténní složka se přeskakuje podle natvrdo zapsaného `_DuplicatesReview`,
  ačkoli `QuarantineFolderName` je nastavitelné;
- pojistka „cíl uvnitř zdroje" žije jen v `MainViewModel`, ne v jádru.
