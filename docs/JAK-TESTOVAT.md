# Jak FotoArchiv otestovat na vlastním vzorku

**Vždy na kopii.** Zkopíruj si z NAS vzorek do samostatné složky (třeba `D:\vzorek`)
a cíl dej jinam (`D:\vzorek-archiv`). Kopii dělej tak, aby se zachovaly časy souborů
(Průzkumník je zachová, `robocopy /COPY:DAT` taky).

Dobrý vzorek má kolem 100–300 souborů a obsahuje:
- fotky z různých telefonů, včetně **HEIC z iPhonu** a videí `.MOV`,
- videa, jejichž název **neobsahuje datum** (`IMG_1234.MOV`) — tam se pozná oprava data z videa,
- něco z Google Takeout, pokud tam jsou JSONy,
- záměrně jednu fotku dvakrát a jednu zmenšenou kopii.

## 1. Rychle, bez okna: konzolová zkouška
```powershell
cd tools\FotoArchiv.Zkouska
..\..\.dotnet\dotnet.exe run -- beh D:\vzorek D:\vzorek-archiv
```
Projdi výpis:
- **ODKUD SE VZALO DATUM** — řádek „cas souboru <- nespolehlive" má být co nejkratší.
  Soubory, které tam jsou, si otevři přes `run -- metadata <soubor>`; vypíše všechny
  datové značky, takže je vidět, jestli nějakou přehlížíme.
- **VYRAZENO** — nemá tam být žádná skutečná fotka.
- **PLAN** — názvy složek mají být obce nebo části měst.

S `--provest` soubory zkopíruje a každý ověří (SHA-256 a čas poslední změny).
Na konci musí být `jiny cas: 0   chyba: 0`.

## 2. V aplikaci
Totéž projdi v okně a navíc:
- **vrácení běhu** — po kopírování dej Vrátit a zkontroluj, že cíl je prázdný;
- **přesun místo kopie** — jen na kopii vzorku; zkontroluj, že se zdroj vyprázdnil
  a vrácení ho obnoví.

## Co jde ověřit JEN na Windows
Tohle jsem na Linuxu ověřit nemohl:

| | Proč |
|---|---|
| sestavení a spuštění WPF okna | WPF na Linuxu neexistuje |
| test `Move_RollsBackVerifiedTargetWhenSourceCannotBeDeleted` | spoléhá na `FileAttributes.ReadOnly`, které na Linuxu smazání nezabrání |
| zachování **času vytvoření** (`CreationTime`) | Linux ho nastavit neumí; ověřen byl jen čas poslední změny |

```powershell
.\.dotnet\dotnet.exe test .\FotoArchiv.Tests\FotoArchiv.Tests.csproj
```
Na Windows musí projít všech 31.
