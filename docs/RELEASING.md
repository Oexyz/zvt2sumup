# Sicherer Releaseprozess

Der normale Buildworkflow veröffentlicht bewusst kein GitHub Release. Der
separate Releaseworkflow reagiert ausschließlich auf einen bewusst gepushten
SemVer-Tag, dessen Wert exakt mit `Directory.Build.props` übereinstimmen muss.

## Freigabegates

- alle vorhandenen Tests erfolgreich
- Build mit 0 Warnungen und 0 Fehlern
- Gateway-GUI-, Service- und Tools-Smoke-Test erfolgreich
- Endpaket enthält exakt `ZVT2SumUpGateway.exe` und
  `ZVT2SumUp.Tools.exe`
- keine Secrets oder produktiven Daten im Repository, Workflow, Artefakt oder
  Screenshot
- manuelle KORONA-/SumUp-Abnahme dokumentiert
- Versionsnummer und Changelog aktualisiert
- MIT-Lizenz und Drittanbieterhinweise vollständig enthalten
- Abhängigkeiten und Security Advisories geprüft
- Authenticode-Status und SmartScreen-Hinweis korrekt dokumentiert
- GitHub-Beschreibung und Topics gemäß `GITHUB_TOPICS.md` gesetzt

## Lokaler Kandidat

```powershell
.\Publish.ps1
```

Das Skript erzeugt einen neuen datierten Ordner, das unveränderte Zwei-EXE-
Verzeichnis, `ZVT2SumUp-win-x64.zip` und `checksums.sha256`. Das Manifest enthält
exakt den Hash des ZIPs, den der Updater erwartet. Smoke-Tests laufen an den
finalen EXE-Dateien. Der Kandidat darf nicht aus alten `bin`-/`obj`-Ordnern
zusammengesetzt werden.

## GitHub Actions

`.github/workflows/build-windows.yml`:

- besitzt nur `contents: read`
- pinnt Drittanbieter-Actions auf vollständige Commit-SHAs
- regeneriert das Icon und verlangt einen diff-freien Stand
- führt Restore im Locked Mode, Release-Build und Tests aus
- veröffentlicht beide Projekte self-contained für `win-x64`
- validiert Namen, Anzahl und Mindestgröße der Dateien
- gibt SHA-256-Werte in der Job-Zusammenfassung aus
- bewahrt das Testartefakt nur 14 Tage auf

`.github/workflows/release-windows.yml`:

- läuft nur für einen Tag im Muster `vMAJOR.MINOR.PATCH`
- verlangt exakte Übereinstimmung von Tag und Projektversion
- überschreibt kein bereits vorhandenes Release
- wiederholt Secretdatei-, Icon-, Build-, Test-, Publish- und Smoke-Prüfungen
- erzeugt ein ZIP mit exakt zwei Stamm-EXE-Dateien
- erzeugt `checksums.sha256` ohne BOM und mit exakt dem ZIP-Hash
- lädt beide Releaseassets zusätzlich als zeitlich begrenzten Buildnachweis hoch
- erstellt erst im letzten Schritt das GitHub Release

Der Releasejob besitzt als einziger Workflow `contents: write` und ist dem
GitHub Environment `release` zugeordnet. Vor jedem Release sollte dort ein
manuelles Approval eingerichtet werden. Geschützte beziehungsweise
unveränderliche Tags, Artifact Attestation und Windows Authenticode bleiben
zusätzliche empfohlene Repositoryeinstellungen; sie werden nicht fälschlich als
bereits vorhanden dargestellt.

## Nach der Veröffentlichung

1. Release von einem sauberen System herunterladen.
2. SHA-256 und gegebenenfalls Attestation/Authenticode unabhängig prüfen.
3. Paket auf einer frischen Windows-10- und Windows-11-VM starten.
4. Prüfen, dass keine separate .NET-Installation benötigt wird.
5. Installation, Dienststart, Datenpersistenz und Deinstallation testen.
6. Erst danach das Release als stabil ankündigen.

Bei Abweichung Release nicht nachträglich still ersetzen. Betroffenen Tag und
Artefakt zurückziehen, Ursache dokumentieren, Schlüssel bei möglichem Leak
rotieren und eine neue Version veröffentlichen.
