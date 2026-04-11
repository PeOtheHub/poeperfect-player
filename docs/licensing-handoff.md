# Licensiering - Handoff

Det här dokumentet sammanfattar den del av konversationen som handlade om licensiering för `PoePerfect Player`.

## Mål

- En licens ska bara kunna användas på en specifik enhet.
- Lösningen ska vara robustare än att låsa enbart mot MAC-adress.

## Slutsats hittills

Andra brukar normalt **inte** låsa en desktop-app mot bara MAC-adress. Det vanligaste är i stället:

1. användaren anger en licensnyckel
2. appen bygger ett maskin-id från flera hårdvarusignaler
3. appen skickar `licensnyckel + maskin-id` till en licensserver
4. servern binder licensen till den enheten
5. servern returnerar en signerad licens/token som sparas lokalt
6. appen verifierar vid nästa start att licensen hör till samma maskin

## Varför inte bara MAC-adress

- MAC-adresser kan spoofas
- de kan ändras beroende på Wi-Fi/Ethernet/docka/VPN/virtuella nätkort
- samma dator kan se ut som olika enheter beroende på hur den är ansluten
- det blir lätt supportproblem om nätverkskort byts eller slås av/på

## Rekommenderad modell

Använd ett **device fingerprint** i stället för MAC-only.

Exempel på signaler som kan ingå:

- Windows `MachineGuid`
- BIOS-/moderkorts-id
- disk-id
- CPU-info
- MAC-adress som en mindre del, men inte ensam

Fingerprintet bör hash:as innan det sparas eller skickas.

## Två vanliga upplägg

### 1. Online-aktivering

- användaren anger licensnyckel
- appen skickar `licensnyckel + maskin-id`
- servern aktiverar om licensen inte redan är bunden till annan enhet
- appen får tillbaka en signerad token/licensfil

Bra när man vill ha strikt kontroll över antal aktiveringar.

### 2. Offline-licensfil

- appen visar ett maskin-id
- administratören genererar en signerad licensfil för den enheten
- appen verifierar signaturen lokalt

Bra om kunden inte alltid har internet.

## Vad andra brukar göra vid "en licens = en enhet"

- 1 aktiv enhet per licens
- ny aktivering på annan enhet nekas
- eller kräver att gammal aktivering frigörs via adminpanel/support
- ofta finns också återkommande servervalidering eller möjlighet att återkalla en licens

## Rekommendation för PoePerfect Player

För Windows-appen är den rekommenderade vägen:

- inte MAC-only
- licensnyckel + maskin-fingerprint
- signerad licens/token
- servern sparar bara hashat maskin-id

## Möjligt nästa steg senare

När vi tar upp detta igen kan nästa steg vara att skissa ett konkret minimalt licenssystem för `PoePerfect Player`, till exempel:

- vilka maskinvärden som ska användas
- hur fingerprintet byggs
- hur databasen/tabellerna ska se ut
- hur aktivering och omaktivering ska fungera
- hur appen verifierar licensen lokalt
