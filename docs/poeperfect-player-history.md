# PoePerfect Player - Projektlogg

Det här dokumentet är tänkt som en levande projektdagbok för `PoePerfect Player`.

Syftet är att samla:

- hur projektet började
- vilka vägval vi gjort
- vad som fungerat bra
- vad som inte fungerat
- vilka större ändringar som byggts in
- var projektet står just nu

Det här dokumentet kan uppdateras löpande när vi arbetar vidare.

## Ursprung

Projektet började som en idé om en **superenkel M3U-spelare** med fokus på:

- läsa in en M3U-länk
- visa en kanallista
- spela upp vald stream
- kunna söka/filtera
- spara favoriter lokalt

Ganska tidigt blev det tydligt att målet inte var Android utan en **Windows-app** som skulle fungera bra på en vanlig Windows-maskin.

## Första tekniska vägvalet

Efter att ha vägt olika alternativ valdes:

- `C#`
- `.NET 8`
- `WPF`
- `LibVLCSharp`

Anledningen var att det var den bästa vägen för en snabb, stabil Windows-spelare med stöd för IPTV, live, film och serier.

Alternativ som diskuterades men inte valdes som förstaspår:

- webbapp/Electron
- Android-projekt
- mer avancerad databaslösning direkt från start

Bedömningen var att `WPF + LibVLCSharp` gav snabbast väg till en fungerande produkt på Windows.

## Projektstruktur och plats

Projektet låg först i en mapp under `AndroidStudioProjects`, vilket inte var idealiskt.

Det flyttades därför till:

- `C:\projectpeo\APTV`

Den platsen är den officiella arbetsmappen framåt.

## Första fungerande versionen

Den första versionen fick stöd för:

- M3U via länk eller fil
- kanallista
- uppspelning av vald stream
- sökning/filter
- lokala favoriter

Favoriter sparas lokalt under `%AppData%\APTV`.

## Tidiga prestandaproblem

När stora M3U-listor testades dök flera problem upp:

- laddning tog lång tid
- UI kunde bli segt eller frysa
- appen kunde hamna i `Not responding`
- `Avbryt` fungerade inte alltid bra nog

Det här ledde till flera förbättringar.

## Förbättringar kring laddning

Följande byggdes in för att få bättre respons:

- avbrytbar laddning
- testläge för att bara läsa in de första `100` kanalerna
- lokal cache av spellista
- debounce på sök
- batch-loading
- virtualiseringstänk i listor
- statusrad och progress

Målet var att appen skulle kännas responsiv även med stora IPTV-listor.

## Första större informationsstrukturen

Efter hand blev det tydligt att en enkel kanallista inte räckte, eftersom spellistorna innehöll:

- livekanaler
- filmer
- serier
- grupper/kategorier inom dessa

Därför byggdes appen ut så att innehållet delades upp i huvudsektioner:

- `Live`
- `Film`
- `Serier`

Detta blev grunden för hur appen organiserar innehållet.

## Första kategoriflödet

I ett tidigare steg testades ett flöde där appen visade kategorier direkt från cache och sedan försökte uppdatera från källan.

Det gav blandade resultat:

- cache kunde göra upplevelsen snabbare
- men kategorier kunde försvinna under uppdatering
- vissa delar uppförde sig inte så användarvänligt som tänkt

Det blev en viktig lärdom:

- användaren vill ha ett tydligt och stabilt flöde
- cache ska hjälpa, inte skapa förvirring

## Omsvängning i UX

Efter feedback styrdes appen om mot ett tydligare tvåstegsupplägg:

1. användaren väljer huvudkategori: `Live`, `Film` eller `Serier`
2. därefter visas rätt kategorier och innehåll för den sektionen

Det här blev en viktig förändring i hela upplevelsen.

## Startskärmen

Appen fick en riktig startsida inspirerad av TV-/streamingappar, där användaren först väljer:

- `Live`
- `Film`
- `Serier`

Målet var att det skulle kännas mer som en riktig medieapp och mindre som ett tekniskt verktyg.

## Browse-vyn

Den tidigare browse-vyn med mycket information i toppen upplevdes som för dashboard-lik.

Det som diskuterades och förbättrades:

- toppraden var för stor
- statusblock tog för mycket plats
- vänsterspalten var för uppdelad
- postergalleri kändes för glest

Resultatet blev en mer fokuserad browse-vy med:

- smalare och enklare header
- tydligare tillbaka-knapp
- vänsterspalt för kategorier
- innehållsyta till höger

## Fullscreen-spelare

Spelaren gjordes om så att innehåll startar direkt i fullscreen i stället för att bäddas in i browse-vyn.

Detta krävde flera iterationer:

- först syntes blå fokusramar
- taskbaren syntes
- fullscreen kunde hamna fel på flera skärmar
- kontroller syntes inte som tänkt

Efter flera justeringar byggdes ett bättre fullscreen-läge med:

- riktig skärm-boundshantering
- overlay-kontroller
- topprad som visas vid musrörelse
- bottenkontroller som visas och försvinner dynamiskt

## Film och serier - posterflöde

För film och serier blev det tydligt att stora gallerier med posters var tunga att rendera.

Följande lösningar infördes:

- placeholders med titel innan posterbild hunnit laddas
- lokal cache av posterbilder
- gradvis inladdning
- mer chunk-baserat renderingsflöde
- scrollbaserad påfyllning

Detta förbättrade upplevelsen, även om posterhantering har krävt flera iterationer.

## Live-sektionen

Det bestämdes att `Live` inte skulle använda samma postergrid som film och serier.

I stället byggdes en riktig kanallista med:

- kanalnamn
- kanalikon
- EPG-rad där data finns
- favoritmarkering

Det gjorde `Live` mer naturlig och snabbare att arbeta med.

## XMLTV / EPG

En XMLTV-länk lades till som valfri källa för `Live`.

Det gav stöd för:

- EPG
- `Nu/Nästa`
- bättre liveinformation

Samtidigt lärde vi oss att:

- XMLTV ibland kan komma avklippt eller trasig
- cache och retries behövs
- EPG ska vara valfritt och inte blockera resten av appen

## Serier - gruppering

En stor förbättring var att serier inte längre skulle visas som en lång lista av enskilda avsnitt.

I stället infördes:

- gruppering till serie
- undernivå för säsonger
- undernivå för avsnitt

Det gjorde serieupplevelsen mycket bättre.

Det krävde också specialfall för olika namnformat, till exempel:

- `S01E02`
- `1x02`
- felstavade varianter som `SO1E06`
- variationer som `Och`, `&` och `&amp;`

## Favoriter och senast spelade

Önskemål kom om att kunna arbeta bättre med återkommande innehåll.

Därför lades in:

- fasta specialkategorier för `Favoriter`
- fasta specialkategorier för `Senast spelade`

Senare förfinades det så att:

- `Senast spelade` inte visas i `Live`
- seriefavoriter gäller säsonger, inte enskilda avsnitt

## Sortering och döljning av kategorier

För att användaren skulle kunna anpassa sin playlist lokalt byggdes ett system för:

- sortera kategorier
- dölja kategorier
- markera/avmarkera alla

Det blev också tydligt att synliga kategorier måste ligga överst i sorteringsvyn, så att man inte behöver flytta kategorier upp och ner genom stora mängder dolda kategorier.

Det här förbättrades i senare iterationer.

## Sök

Sök lades till per huvudkategori:

- `Live`
- `Film`
- `Serier`

Målet var att sök ska kännas som ett separat läge inom sektionen, inte bara som ett filter i en enskild underkategori.

## Undertexter och ljudspår

För film och serier byggdes stöd för:

- val av ljudspår
- val av undertextspår

Det här kom till efter att behov uppstod under uppspelning av VOD-innehåll.

Efteråt förbättrades även UI:t för dessa väljare, bland annat:

- bara visa ljudspårsväljare om flera ljudspår finns
- snyggare mörk stil
- bättre visning i kollapsat läge

## Problem som visat sig längs vägen

Flera typer av problem har dykt upp under arbetet:

- appen har kunnat frysa vid stora listor
- `Avbryt` fungerade först inte tillräckligt bra
- vissa categories/UI-delar blinkade vitt under laddning
- fullscreen fungerade inte direkt på flera skärmar
- kontroller låg ibland bakom videon
- posters kunde fastna i halvtrasigt läge
- `Senast spelade` kunde störa uppspelning om listan rebuildades för aggressivt
- vissa logotyper eller XMLTV-kopplingar fungerade inte som väntat

Många av dessa problem har successivt lösts genom iteration.

## Distribution

Appen fick senare ett distributionsspår med:

- eget produktnamn
- `PoePerfect Player`
- Windows-installer
- `Setup.exe`

Viktigt var också att inga privata M3U-/XMLTV-länkar skulle följa med i installern.

Det verifierades att användarens länkar ligger lokalt under `%AppData%\APTV` och inte bakas in i själva installationspaketet.

## Loggning

För att kunna felsöka problem på andra datorer byggdes smart loggning in.

Loggarna skrivs till:

- `%AppData%\APTV\logs`

Målet med loggningen är att kunna se:

- spellisteladdning
- cacheträffar/missar
- XMLTV-problem
- kategoriöppning
- uppspelningsproblem
- serverfel

Det här blev särskilt viktigt när appen testades av andra användare än utvecklingsmiljön.

## Viktig lärdom från kollegatest

När en kollega testade appen fungerade inte hans playlist först.

Loggningen visade att:

- hans app svarade med `HTTP 454` från leverantören
- det såg först ut som att länken kanske inte fungerade
- senare visade det sig att han troligen inte körde den senaste builden

När rätt build installerades fungerade det fint.

Det blev en viktig påminnelse om att:

- logging behövs
- distribution/versionering måste vara tydlig
- problem hos andra användare inte alltid betyder kodfel i själva kärnlogiken

## Namnresan

Appen har bytt namn flera gånger under resans gång.

Bland namn som förekommit:

- `APTV`
- `PoePlayerPerfect`
- `PoePerfect`
- `PoePerfect Player`

Den nuvarande identiteten är:

- `PoePerfect Player`

## Nuvarande inriktning

I nuläget har projektet utvecklats från en enkel M3U-listspelare till en betydligt mer komplett Windows-app med stöd för:

- Live
- Film
- Serier
- kategorier
- cache
- favoriter
- senast spelade
- fullscreen-uppspelning
- ljudspår
- undertexter
- XMLTV/EPG
- sortering/döljning av kategorier
- installer
- loggning

## Saker som fortfarande kan förbättras

Det finns fortfarande områden som kan förfinas vidare:

- mer konsekvent polish i UI
- ännu bättre hantering av vita blink/laddlägen
- mer robust ikon/logomatchning för livekanaler
- bättre licensmodell om appen ska säljas eller låsas per enhet
- bättre dokumentation för distribution, support och felsökning

## Förslag för framtida uppdateringar av detta dokument

När vi fortsätter arbeta kan dokumentet uppdateras med nya sektioner som:

- datum
- ändring
- varför ändringen gjordes
- vad som fungerade
- vad som fortfarande återstår

Exempel:

### 2026-04-XX

- byggde in `X`
- löste problem `Y`
- upptäckte att `Z` fortfarande behöver förbättras

## Status just nu

Projektet står idag som en fungerande Windows-app med egen installer och ett tydligt definierat produktnamn:

- `PoePerfect Player`

Det viktigaste nu är att fortsätta iterera med samma arbetssätt:

- testa
- observera
- justera
- dokumentera

Det här dokumentet är tänkt att vara navet för just den resan.
