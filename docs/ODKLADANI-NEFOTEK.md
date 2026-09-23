# Soubory, které nejsou fotografie, jdou stranou

Součást větve `fix/zachovani-casovych-razitek`.

## Co bylo špatně

`MediaItem.Error` se nastavoval, ale **nikdo podle něj nefiltroval**. Soubor s příponou
`.jpg`, který obrázek ve skutečnosti nebyl — poškozený, oříznutý nebo jen přejmenovaný —
dostal `Error` od `MagickImageInfo` a pak putoval do archivu mezi fotografie, včetně
pořadového čísla v řadě `001`, `002`…

## Co se změnilo

**Rozpoznání** — `MetadataReaderService` nastaví `MediaItem.RejectionReason`, když:

- `MagickImageInfo` selže nebo nevrátí rozměry. Magick určuje formát **podle obsahu,
  ne podle přípony**, takže tohle chytí i přejmenovaný soubor.
- soubor má nulovou délku (platí i pro sidecary).

U videí se na selhání metadat nespoléhá — nečitelná metadata nejsou důkaz, že video
je vadné. Prázdný soubor se pozná i tam.

**Odložení** — `OrganizationPlanner` je směruje do `RejectedFolderName`
(výchozí `_NeniFoto`) pod cílovou složkou, se zachováním cesty od zdrojového kořene.
Do číslování nevstupují, takže v archivu nevznikají díry v řadě. Sidecar odchází
stranou spolu se svou fotografií, místo aby zůstal osiřelý.

**Sken** — `MediaScanner.EnumerateFiles` přijímá jména služebních složek.
`MainViewModel` mu předává obě z nastavení, takže se odložené soubory ani karanténa
při dalším skenu nenačtou znovu jako zdrojová data.

## Dvě starší chyby vyřešené při tom

- **Karanténa se přeskakovala podle natvrdo zapsaného `_DuplicatesReview`**, ačkoli
  `QuarantineFolderName` je nastavitelné (nález 4 v `REVIZE-2026-09-23.md`).
  Seznam je teď parametr, ne konstanta.
- **Skener nepřeskakoval `@eaDir`, `#recycle` a `#snapshot`** (nález 5). Na Synology
  načítal náhledy z `@eaDir` jako plnohodnotné fotografie. Doplněno do výchozího seznamu.
- **`Path.GetRelativePath` mohl vyvést cíl mimo určenou složku** (nález 8), když
  `SourceRoot` nebyl předkem `FilePath` — vrácená cesta pak obsahovala `..`.
  Nová `SafeRelativePath` to hlídá a používá ji jak odkládání, tak karanténa.

## Ověření

| | Výsledek |
|---|---|
| Testů celkem | **18 z 20 prochází** |
| Nové testy | 5, všechny procházejí |
| S vypnutým směrováním | `Build_MovesUnusableFileAsideAndKeepsNumberingIntact` a `Build_KeepsSidecarWithItsRejectedPrimary` **padají** |
| Zbylé 2 selhání | prostředím, padají i na nedotčeném kódu |

Nové testy:

1. `Build_MovesUnusableFileAsideAndKeepsNumberingIntact` — fotka dostane `001.jpg`,
   vadný soubor jde do `_NeniFoto/phone/rozbity.jpg`.
2. `Build_KeepsSidecarWithItsRejectedPrimary` — sidecar následuje svou fotografii.
3. `EnumerateFiles_SkipsSynologyServiceFoldersAndConfiguredNames` — `@eaDir`,
   `#recycle`, `_NeniFoto` i vlastní jméno z nastavení.
4. `Read_MarksFileThatOnlyPretendsToBeAnImage` — textový soubor s příponou `.jpg`.
5. `Read_MarksEmptyFile` — nulová délka.
