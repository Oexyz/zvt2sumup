# Änderungsprotokoll

Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/). Das
Projekt verwendet semantische Versionsnummern.

## [1.0.1] - 2026-08-11

### Behoben

- Die nicht installierende Live-Release-Prüfung verwendet nun einen isolierten
  Benutzer-Tempordner und funktioniert ohne Administratorrechte. Die eigentliche
  Updateinstallation bleibt weiterhin UAC- und ACL-geschützt.
- Build- und Release-Workflows verwenden exakt das in `global.json` festgelegte
  .NET SDK, damit RID-spezifische Locked-Restores reproduzierbar bleiben.
- Die Release-Existenzprüfung unterscheidet sicher zwischen einem noch nicht
  vorhandenen Release und einem echten GitHub-API-Fehler.

## [1.0.0] - 2026-08-11

### Hinzugefügt

- native .NET-10-Lösung mit acht getrennten Projekten
- deutsche WinForms-Verwaltungsoberfläche im ZVT2SumUp-Design
- gemeinsame Gateway-EXE für GUI und Windows-Dienst
- separate self-contained Tools-EXE
- interaktiver Kassen-Simulator als Standardstart der Tools-EXE mit
  persistenter ZVT-Verbindung, APDU-Hexanzeige und getrennten
  Sicherheitsbestätigungen
- vollständiges Leeren der ZVT-Antwortsequenz bis Completion oder Abort, damit
  Statusframes niemals in den folgenden Kassenvorgang hineinragen
- ZVT-13.13-Registry, TCP-RAW/Length-Prefixed- und COM-Framing
- SumUp Reader/Solo, klassische Checkout-Kompatibilität, Transaktionen und
  vollständige/teilweise Refunds
- atomare Konfiguration, DPAPI-Secrets, Journal und Belegvorlagen
- sichere GitHub-Release-Prüfung mit verpflichtendem SHA-256-Manifest
- 42 lokale Unit- und Integrationstests
- GitHub Actions Build und Release mit exakt zwei EXE-Dateien
- MIT-Lizenz und vollständige Drittanbieterhinweise

### Sicherheit

- Loopback als Standardbindung und Warnung bei externer Adresse
- Redaction von Authorization, Keys, Tokens, Passwörtern und Pairing-Codes
- Redaction geheimnishaltiger JSON- und Query-String-Werte
- ACL-Härtung vor dem ersten Secret-Schreibvorgang und automatische
  Wiederverdeckung kurzzeitig angezeigter Secrets
- keine automatischen POST-Retries für Zahlungen oder Refunds
- getrennte Bestätigungsschalter für reale Zahlung, Refund, Kassenschnitt und
  RAW-APDUs
- Updatehosts auf ausgewählte HTTPS-GitHub-Domains begrenzt
- vollständiger Zwei-EXE-Updater mit exakter GitHub-Host-Allowlist, eindeutigem
  SHA-256-Manifest, ZIP-/PE-/Versionsprüfung, Smoke-Tests und Rollback

### Architektur

- Integer-Cent statt unkontrollierter Fließkomma-Geldwerte
- prüfsummenpflichtige GitHub Releases für normale Updates
- deterministische KORONA-RAW-Antwortreihenfolge
- Desktop und Dienst verwenden dieselbe Core- und Gateway-Implementierung
