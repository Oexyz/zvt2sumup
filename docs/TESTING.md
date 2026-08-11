# Testnachweise und manuelle Abnahme

Stand: 11. August 2026. Automatisierte Tests sind deterministisch, benötigen
keine Administratorrechte und verwenden keine echten SumUp-Zugangsdaten oder
Zahlungen.

## Automatisierte Abnahme

```powershell
dotnet restore .\Zvt2SumUp.slnx --locked-mode
dotnet build .\Zvt2SumUp.slnx -c Release --no-restore
dotnet test .\tests\Zvt2SumUp.Tests\Zvt2SumUp.Tests.csproj -c Release --no-build
```

Aktueller lokaler Nachweis:

- Release-Build: erfolgreich, 0 Warnungen, 0 Fehler
- Tests: 42 bestanden, 0 fehlgeschlagen
- Gateway `--smoke-test`: Exitcode 0
- Gateway `--layout-smoke-test`: alle Tabs und Übersichtskarten bei 960 × 640 vollständig sichtbar
- Gateway `--service-smoke-test`: Exitcode 0
- Tools `help`: Exitcode 0
- Tools-Standardstart: Kassensimulator-Menü sichtbar und sauber beendbar
- Kassensimulator gegen lokalen Fake-Gateway: Registrierung, ACK, Completion
  und persistente TCP-Verbindung erfolgreich
- Zahlungsdialog ohne exakte Textbestätigung: keine Autorisierungs-APDU gesendet
- Status-Info und Completion werden vor dem folgenden Kassenbefehl vollständig
  gelesen und jeweils quittiert
- self-contained Paket: exakt zwei EXE-Dateien

## Abgedeckte Testgruppen

- BCD-Roundtrip und strikte Fehlerfälle
- kurze und erweiterte ZVT-Längen
- BMP-Betrag und BER-TLV
- TCP-RAW und Length-Prefix über fragmentierte Eingaben
- serielles DLE-Framing und LRC
- Revision-13.13-Registry und Reject-by-default
- Registrierung, Zahlung, Refund, Kassenschnitt, Ablehnung und Timeout
- Serialisierung paralleler Zahlungen je Terminal
- Abbruch auf derselben Verbindung und Checkout-Terminierung
- KORONA-Reihenfolge und Unterdrückung optionaler Frames
- Journal-Deduplizierung, Parallelzugriff, terminalbezogenes Schließen und
  Sicherung beschädigter Dateien
- verschachtelte Belegplatzhalter, optionale Zeilen und paralleler Zähler
- Konfigurationsdatei enthält ausschließlich verschlüsselte Secret-Referenzen
- Secret-Redaction und Konfigurations-Pfadschutz
- SumUp Reader-Checkout in Minor-Units, Affiliate-Header und aktuelle
  Transaktions-/Refundverträge
- keine automatische Wiederholung des Refund-POSTs
- sichere Ablehnung ungültiger Pairing-Codes und Updatequellen
- verpflichtendes und eindeutiges SHA-256-Manifest für Updates
- exakte Bindung an GitHub-Repository, Releaseversion und Assetnamen
- exakte GitHub-Host-Allowlist einschließlich Redirectprüfung
- Ablehnung zusätzlicher ZIP-Einträge sowie falscher PE-/Releaseversionen
- geschützter Stagingpfad ohne Reparse Points
- atomarer Zwei-EXE-Commit und vollständiger Rollback

## Hardwaretest

Ein autorisierter SumUp-Reader wurde mit dem nativen C#-Gateway getestet:

- drei Versuche über jeweils 0,01 EUR wurden am Terminal beendet beziehungsweise
  von der verwendeten Karte abgelehnt; das Gateway meldete Fehler statt Erfolg
- ein anschließend ausdrücklich autorisierter Versuch über 1,00 EUR wurde als
  `PAID` erkannt
- die Kasse erhielt ACK, Status `04 0F` mit 100 Cent und danach `06 0F 00`
- das lokale Journal speicherte exakt einen offenen Zahlungsposten über 100 Cent
- keine API-Keys, Pairing-Codes oder vollständigen Identifikatoren wurden in den
  Testnachweis übernommen

Eine Rückerstattung war nicht Bestandteil dieses konkreten Hardwaretests. Die
zugehörige Zahlung ist daher getrennt im Händlerkonto zu prüfen und bei Bedarf
bewusst über den autorisierten Refundprozess zu erstatten.

## Manueller Test mit SumUp Solo/Reader

Nur im eigenen Händlerkonto und unter direkter Aufsicht:

1. Aktuelle Journal- und Konfigurationssicherung anlegen.
2. Gateway auf `127.0.0.1:20007` konfigurieren.
3. API-Zugriff mit **Verbindung testen** prüfen; keine Zahlung entsteht.
4. Erwarteten Merchant und Reader anhand maskierter Kennzeichen kontrollieren.
5. Falls nötig Reader über einen frischen Pairing-Code koppeln; Code nicht
   dokumentieren.
6. `sumup-test` ausführen und Exitcode 0 erwarten.
7. Gateway starten, ZVT registrieren und Status Ready erwarten.
8. Eine vorher schriftlich festgelegte Kleinstzahlung aus der Kasse auslösen.
9. Betrag am Terminal nochmals kontrollieren und bewusst bestätigen.
10. Kassenantwort, SumUp-Dashboard und Journal gegeneinander abgleichen.
11. Abbruchtest mit einer neuen Kleinstzahlung durchführen und sicherstellen,
    dass kein `PAID`-Datensatz entsteht.
12. Refund nur anhand der eindeutigen Transaktion bewusst bestätigen und danach
    Dashboard sowie Journal prüfen.

## Manueller KORONA-Test

1. In KORONA einen ZVT-Zahlungsweg mit Host `127.0.0.1`, Port `20007` und
   passender Währung anlegen.
2. Gateway und Live-Log öffnen.
3. KORONA verbinden/registrieren. Die TCP-Verbindung soll offen bleiben.
4. Statusabfrage durchführen.
5. Einen eigenen Testartikel mit eindeutigem Preis bonieren.
6. Kartenzahlung auslösen und den Betrag auf Kasse und Terminal vergleichen.
7. Erfolg nur akzeptieren, wenn die Reihenfolge `04 0F` und `06 0F 00` sowie
   der SumUp-Status `PAID` zusammenpassen.
8. KORONA-/TSE-Bon und Gateway-Beleg getrennt prüfen. Der Gateway-Beleg darf
   nicht als Fiskalbeleg behandelt werden.
9. Wiederholbeleg testen.
10. Storno beziehungsweise Teilrefund an einer eindeutig identifizierten
    Testtransaktion durchführen.
11. Partial Reconciliation prüfen; offene Posten müssen offen bleiben.
12. Kassenschnitt prüfen; nur Posten des ausgewählten Terminals dürfen abhängig
    von `reset_after_print` geschlossen werden.
13. KORONA neu verbinden und einen längeren Leerlauf/Reconnect testen.

## Noch standortspezifisch offen

- produktiver KORONA-Dauerlauf über einen vollständigen Geschäftstag
- echte COM-Hardware und Treiberkombination am Zielstandort
- jede nicht beim Hardwaretest verwendete klassische SumUp-Terminalvariante
- Dienst-Recovery unter dem späteren produktiven Windows-Konto
- externe TCP-Bindung mit finaler Firewall/VLAN-Konfiguration
- Teilrefund und Kassenschnitt im finalen Händlerkonto
- Update eines installierten, laufenden Diensts aus einem signierten Release

Diese Punkte sind keine automatischen Freigaben. Vor Produktion müssen sie für
die konkrete Installation dokumentiert und bestanden sein.
