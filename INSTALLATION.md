# Installation

Diese Anleitung gilt für den self-contained Windows-x64-Build mit
`ZVT2SumUpGateway.exe` und `ZVT2SumUp.Tools.exe`.

## Voraussetzungen

- Windows 10 oder Windows 11, 64 Bit
- ein eigenes beziehungsweise autorisiertes SumUp-Händlerkonto
- ein unterstützter SumUp Reader, Solo oder klassisches Terminal
- ein ZVT-fähiges Kassensystem oder ein isolierter Simulator
- Administratorrechte nur für Installation oder Entfernung des Windows-Diensts

Ein separates .NET Runtime wird nicht benötigt.

## 1. Herkunft und Integrität prüfen

Solange kein signiertes öffentliches Release existiert, nur das Artefakt eines
erfolgreichen Workflows im erwarteten Repository verwenden. Beide Dateien vor
dem ersten Start prüfen:

```powershell
Get-FileHash .\ZVT2SumUpGateway.exe -Algorithm SHA256
Get-FileHash .\ZVT2SumUp.Tools.exe -Algorithm SHA256
```

Die Werte müssen exakt mit dem Build-Protokoll beziehungsweise dem vom
Verantwortlichen separat bereitgestellten Manifest übereinstimmen. Eine
Prüfsumme schützt vor unbemerkten Änderungen, ersetzt aber keine
Authenticode-Signatur und keinen Herkunftsnachweis.

Das ZIP vollständig in einen dauerhaft beschreibbaren Programmordner entpacken,
zum Beispiel `C:\Program Files\ZVT2SumUp\`. Keine EXE direkt aus dem ZIP und
nicht aus E-Mail-Anhängen starten.

## 2. Erster Start und sichere Grundeinstellung

1. `ZVT2SumUpGateway.exe` normal starten.
2. Unter **Einrichtung** den Transport auf **TCP** belassen.
3. Bind-Adresse `127.0.0.1`, Port `20007` und Idle-Timeout `0` verwenden, wenn
   die Kasse auf demselben Windows-Rechner läuft.
4. Währung, Log-Level und Zahlungstimeout kontrollieren.
5. Speichern. Die App legt `%ProgramData%\ZVT2SumUp\` an.

Eine Bindung an `0.0.0.0` oder eine LAN-Adresse vergrößert die Angriffsfläche.
Sie darf nur in einem getrennten, administrierten Kassennetz mit restriktiver
Windows-Firewallregel verwendet werden. ZVT über TCP besitzt in diesem Projekt
keine zusätzliche Transportverschlüsselung oder Clientauthentisierung.

## 3. SumUp einrichten

1. Einen API-Key mit nur den für Checkout, Reader, Transaktionen und Refunds
   benötigten Rechten über den vorgesehenen SumUp-Prozess erzeugen.
2. Den Key ausschließlich in das Geheimnisfeld der App einfügen. Er gehört
   weder in `config.ini` noch in Kommandozeilen, Screenshots oder Logs.
3. Optional Affiliate-Key und App-ID eintragen.
4. **Verbindung testen** verwenden. Dabei wird keine Zahlung ausgelöst.
5. Merchant Code automatisch übernehmen oder den autorisierten Merchant prüfen.
6. Bestehendes Terminal auswählen oder einen Reader/Solo über den kurzzeitig
   angezeigten 8- bis 9-stelligen Pairing-Code koppeln.
7. Pairing-Code anschließend verwerfen; er wird redigiert und darf nicht geteilt
   werden.

Geheimnisse werden mit Windows DPAPI im Modus `LocalMachine` verschlüsselt. Die
Datei ist damit an den Computer, nicht an ein exportierbares Passwort gebunden.
Sie sollte weder kopiert noch in Backups außerhalb des geschützten Systems
entschlüsselt werden.

## 4. Kasse anbinden

Für eine lokale Standardinstallation:

| Kassenwert | Einstellung |
|---|---|
| Protokoll | ZVT |
| Host | `127.0.0.1` |
| Port | `20007` |
| Verbindung | dauerhaft offen |
| Framing | RAW-APDU oder ZVT-TCP-Längenrahmen, automatisch erkannt |
| Währung | identisch zur Gateway-Konfiguration |

Empfohlene Reihenfolge:

1. Gateway starten.
2. ZVT-Registrierung senden.
3. Statusabfrage senden.
4. Erst danach eine bewusst autorisierte Kleinstzahlung testen.
5. Ergebnis sowohl in der Kasse als auch im SumUp-Dashboard kontrollieren.

Bei KORONA sendet das Gateway im RAW-Modus nach erfolgreicher Zahlung zuerst
`04 0F` und anschließend `06 0F 00`; optionale Zwischen- und Druckframes werden
in diesem Modus unterdrückt.

## 5. Windows-Dienst installieren

Die beiden EXE-Dateien müssen dauerhaft im selben Ordner bleiben. Eine als
Administrator gestartete PowerShell verwenden. Aus dem Quellrepository den
geprüften Paketordner explizit angeben:

```powershell
.\Install-Service.ps1 -PackageDirectory '.\artifacts\publish-YYYYMMDD-HHMMSS\ZVT2SumUp-win-x64'
```

Wurde das Skript bewusst neben die beiden geprüften EXE-Dateien kopiert, genügt
`.\Install-Service.ps1`. Mit `-Start` wird der Dienst nach erfolgreicher
Installation zusätzlich gestartet; ohne den Schalter bleibt der Start eine
separate bewusste Aktion.

Alternativ explizit:

```powershell
.\ZVT2SumUp.Tools.exe install .\ZVT2SumUpGateway.exe
.\ZVT2SumUp.Tools.exe start
.\ZVT2SumUp.Tools.exe status
```

Installiert wird:

- Dienstname `ZVT2SumUpGateway`
- Anzeigename `ZVT-zu-SumUp Gateway`
- Starttyp automatisch
- Programm `ZVT2SumUpGateway.exe --service`
- Recovery nach 5, 15 und 60 Sekunden

Die GUI fordert UAC nur nach einer ausdrücklichen Dienstaktion an. Während des
Dienstbetriebs die interaktive Gateway-Instanz nicht parallel auf demselben
TCP-Port oder COM-Port starten.

## 6. COM-Betrieb

Vorher verfügbare Ports nur lesen:

```powershell
.\ZVT2SumUp.Tools.exe com-list
.\ZVT2SumUp.Tools.exe com0com-detect
```

Standard: 9600 Baud, 8 Datenbits, keine Parität, ein Stoppbit. ZVT2SumUp
installiert keine Treiber und verändert keine COM-Zuordnung. com0com nur aus
einer vertrauenswürdigen Quelle und nach eigener Prüfung installieren.

## 7. Logs und Fehlerdiagnose

Logs liegen standardmäßig in
`%ProgramData%\ZVT2SumUp\logs\zvt2sumup.log`. Vor dem Weitergeben:

1. Kopie anlegen.
2. API-Keys, Pairing-Codes, Authorization-Header, Merchant-/Reader-IDs,
   Transaktions-IDs, Personen- und Geschäftsdaten entfernen.
3. Nur den kleinsten relevanten Ausschnitt teilen.

Die konfigurierbare Logdatei muss relativ innerhalb des ZVT2SumUp-Datenordners
liegen; Pfadtraversal und absolute Pfade werden abgelehnt.

## Aktualisieren

Die integrierte Prüfung läuft nur nach einem Klick auf **Updates**. Installation
erfordert eine zweite Bestätigung und UAC. Der Ablauf ist vollständig nativ:

1. stabiles Release aus dem konfigurierten GitHub-Repository abfragen
2. exakt `ZVT2SumUp-win-x64.zip` und `checksums.sha256` verlangen
3. Asset-URLs exakt an Repository, Releaseversion und feste Dateinamen binden
4. Download und jedes Weiterleitungsziel auf feste GitHub-HTTPS-Hosts begrenzen
5. ZIP gegen den eindeutigen SHA-256-Eintrag prüfen
6. exakt zwei Stamm-EXE-Dateien, Größen, PE-Köpfe und Releaseversionen prüfen
7. neues Gateway, Service-Host und Tools im geschützten Staging testen
8. laufende GUI beenden und einen zuvor laufenden Dienst stoppen
9. beide EXE-Dateien über einen gebundenen externen Updateprozess gemeinsam austauschen
10. Hashes nach dem Kopieren erneut prüfen
11. Dienst neu starten; bei Fehler beide alten Dateien zurückrollen
12. Staging nach Prozessende entfernen und Oberfläche wieder öffnen

Das Update verändert keine Konfiguration, Secrets, Journale, Belegvorlagen oder
Logs. Der Stagingordner erhält nur ACLs für Administratoren und `SYSTEM`.

Erlaubte Hosts sind exakt `api.github.com`, `github.com`,
`objects.githubusercontent.com` und `release-assets.githubusercontent.com`.
Andere Hosts sowie zusätzliche ZIP-Dateien, uneindeutige Manifeste, falsche
Versionen und Hashabweichungen werden abgelehnt.

SHA-256 beweist die Übereinstimmung mit dem Manifest desselben Releases. Bei
einem kompromittierten Repository könnte ein Angreifer beides ersetzen; deshalb
Repositoryinhaber, Workflowstatus und nach Möglichkeit Authenticode oder eine
GitHub-Attestation zusätzlich prüfen.

## Entfernen

```powershell
.\Uninstall-Service.ps1 -ToolsPath 'C:\Program Files\ZVT2SumUp\ZVT2SumUp.Tools.exe'
```

Das Skript entfernt nur die Dienstregistrierung. Konfiguration, verschlüsselte
Secrets, Journal, Belegvorlagen und Logs bleiben absichtlich erhalten. Diese
Daten nur nach separater Sicherung und bewusster Entscheidung löschen.
