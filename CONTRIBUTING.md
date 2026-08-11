# Mitwirken

Beiträge sind willkommen, sobald das Repository öffentlich freigegeben ist.
Bei zahlungsrelevantem Code gelten bewusst strenge Anforderungen.

## Entwicklungsumgebung

- Windows 10/11 x64
- .NET 10 SDK gemäß `global.json`
- PowerShell 5.1 oder neuer
- keine echten SumUp-Zugangsdaten für Build oder Tests

```powershell
dotnet restore .\Zvt2SumUp.slnx --locked-mode
dotnet build .\Zvt2SumUp.slnx -c Release --no-restore
dotnet test .\tests\Zvt2SumUp.Tests\Zvt2SumUp.Tests.csproj -c Release --no-build
```

## Sicherheitsregeln

- Niemals Secrets, produktive Konfigurationen, Journale oder unbearbeitete Logs
  committen.
- Keine echten Zahlungen in automatisierten Tests, CI oder Review-Schritten.
- HTTP-POSTs für Zahlungen/Refunds nicht mit automatischen Retries versehen.
- Geldwerte als Integer-Minor-Units oder `decimal`, nie unkontrolliert als
  `float`/`double` verarbeiten.
- Neue Logwerte durch `SensitiveDataRedactor` führen und zusätzlich auf
  unbekannte sensible Felder prüfen.
- Externe Bindung, schwächere ACLs, neue Downloadhosts oder neue
  Prozessstarts benötigen eine dokumentierte Bedrohungsanalyse.
- Dienstinstallation oder -entfernung nie in Tests ausführen.
- Neue zustandsändernde CLI-Befehle benötigen einen eindeutigen
  Bestätigungsschalter.

## Codekonventionen

- Nullable Reference Types und Implicit Usings bleiben aktiviert.
- Warnungen sind Fehler; keine pauschalen Suppressions ohne Begründung.
- Asynchrone I/O mit `CancellationToken`; UI-Thread nicht blockieren.
- Geschäftslogik gehört in Core/Infrastructure, nicht in Forms oder Entry-Points.
- Öffentliche Fehlermeldungen verständlich auf Deutsch und ohne Secrets.
- Neue ZVT-Kommandos explizit in Registry, Handler, Matrix und Tests aufnehmen.

## Tests

Jede Verhaltensänderung benötigt den kleinsten deterministischen Test. Besonders
relevant sind:

- exakte APDU-Bytes und Reihenfolge
- Fragmentierung, erweiterte Längen und ungültige Eingaben
- Timeout, Abbruch, Dispose und Parallelität
- idempotente Journalwirkung
- Fake-HTTP-Verträge ohne echte SumUp-Verbindung
- Redaction aller neu eingeführten Secretnamen
- Reject-by-default bei Updates und nicht unterstützten Kommandos

Hardwaretests sind manuell, mit autorisiertem Händlerkonto und bewusst
bestätigtem Kleinstbetrag auszuführen. Ergebnis nur anonymisiert dokumentieren.

## Pull Request

Ein Pull Request sollte enthalten:

- Problem und Sicherheitsauswirkung
- gewählte Lösung und verworfene Alternativen
- ausgeführte Build-/Testbefehle
- manuelle Tests, falls notwendig
- Aktualisierung der relevanten Dokumentation

Keine funktionale Änderung zusammen mit großen, unabhängigen Formatierungen
einreichen. Vor dem PR `Publish.ps1` ausführen und prüfen, dass das Endpaket
exakt zwei EXE-Dateien enthält.

## Schwachstellen

Keine Sicherheitskorrektur mit ungeklärten Exploitdetails öffentlich
einreichen. Zuerst privat gemäß [SECURITY.md](SECURITY.md) koordinieren.
