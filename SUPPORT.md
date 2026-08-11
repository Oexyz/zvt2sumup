# Support

## Vor einer Anfrage

1. Neueste getestete Version und erfolgreichen Windows-Build verwenden.
2. [Installation](INSTALLATION.md) und
   [ZVT-Matrix](docs/ZVT_COMPATIBILITY.md) prüfen.
3. `ZVT2SumUp.Tools.exe sumup-test` ausführen. Dieser Befehl löst keine Zahlung
   aus.
4. Bei TCP zuerst `127.0.0.1:20007`, Firewall und Portbelegung prüfen.
5. Bei COM `com-list` ausführen und Baudrate/Port kontrollieren.

## Öffentliche Issue

Eine normale Bugmeldung darf enthalten:

- ZVT2SumUp-Version beziehungsweise Commit
- Windows-Version und x64
- TCP oder COM sowie verwendete Framingart
- erwartetes und tatsächliches Verhalten
- kleinster reproduzierbarer Ablauf mit Fake oder Simulator
- kurzer, manuell redigierter Logausschnitt

Sie darf **nicht** enthalten:

- API-/Affiliate-Key, Token, Passwort oder Authorization-Header
- Pairing-Code oder `secrets.dat`
- vollständige Merchant-, Reader-, Checkout- oder Transaktions-ID
- Kartendaten, Kunden- oder Mitarbeiterdaten
- vollständige `config.ini`, Journal-, Beleg- oder Logdateien
- Screenshots des Einrichtungs-Tabs mit ausgefüllten Feldern

Platzhalter verwenden, zum Beispiel `<READER-ID>`, `<TRANSACTION-ID>` und
`<redigiert>`.

## Sicherheitsproblem

Vermutete Schwachstellen und jedes Secret-Leak gehören nicht in öffentliche
Issues. Stattdessen ein privates GitHub Security Advisory gemäß
[SECURITY.md](SECURITY.md) eröffnen.

## Zahlungsproblem

Bei einer möglicherweise doppelten, falschen oder unbekannten Zahlung:

1. Keine weiteren Testzahlungen senden.
2. Gateway stoppen und Zeitstempel notieren.
3. SumUp-Dashboard beziehungsweise Händler-Support prüfen.
4. Transaktionsdaten nicht öffentlich posten.
5. Refund nur nach eindeutiger Zuordnung und bewusster Bestätigung ausführen.

Dieses Community-Projekt kann keine SumUp-Abrechnung ändern und ersetzt nicht
den offiziellen Händler- oder Zahlungsdienstsupport.
