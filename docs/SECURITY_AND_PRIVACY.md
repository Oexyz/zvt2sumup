# Sicherheit und Datenschutz

Dieses Dokument beschreibt Schutzmaßnahmen, verbleibende Risiken und sichere
Betriebsannahmen. Es ist kein PCI-DSS-, Datenschutz- oder Rechtsgutachten.

## Schützenswerte Werte

| Wert | Speicherung | Schutz |
|---|---|---|
| SumUp API-Key | `secrets.dat` | DPAPI `LocalMachine`, feste Entropie, restriktive Verzeichnis-ACL wird bei Secret-Schreibvorgängen angewendet |
| Affiliate-Key/App-ID | `secrets.dat` | wie API-Key |
| Pairing-Code | nicht als normales Konfigurationsfeld gespeichert | Eingabe validiert, in Fehlermeldungen/Logs redigiert |
| Merchant-/Reader-/Terminal-ID | `config.ini` | nicht als Authentisierungsgeheimnis behandelt, aber in Supportdaten zu pseudonymisieren |
| Transaktions-/Checkout-ID und Auth-Code | Journal/Beleg | nur lokal; vor öffentlicher Weitergabe entfernen |
| Händler- und Belegtexte | `receipt_templates.ini` | lokale Geschäftsdaten, ACL des Datenordners |
| Logs | `logs\` | relative Pfade ausschließlich innerhalb des Datenordners; Redaction vor Ausgabe |

API-Key, Affiliate-Key und Pairing-Code werden in der Oberfläche verdeckt.
Eine bewusst aktivierte Klartextanzeige der beiden gespeicherten Schlüssel endet
nach zehn Sekunden oder sobald das Fenster den Fokus verliert automatisch.

`config.ini` enthält für Geheimnisse nur Referenzen wie
`{{encrypted:secrets.dat:api_key}}`. Echte Schlüssel werden ausschließlich über
die Oberfläche in den DPAPI-Store geschrieben.

## DPAPI und ACLs

`DataProtectionScope.LocalMachine` erlaubt sowohl Desktop-App als auch
Windows-Dienst auf demselben Computer den Zugriff. Das ist für den gemeinsamen
Dienstkontext notwendig, bedeutet aber:

- Die Datei ist nicht auf einen einzelnen Benutzer beschränkt.
- Lokale Administratoren und `SYSTEM` sind Teil der Vertrauensbasis.
- Eine kopierte `secrets.dat` ist auf einem anderen Computer normalerweise nicht
  entschlüsselbar, ist aber trotzdem wie ein Geheimnis zu behandeln.
- Nach Computerneuinstallation, Domänenumzug oder Wiederherstellung auf
  anderer Hardware müssen Schlüssel neu hinterlegt werden.

Beim Speichern von Geheimnissen entfernt ZVT2SumUp die Vererbung rekursiv und
Vollzugriff für `SYSTEM` und Administratoren sowie Änderungsrechte für den
einrichtenden Benutzer zu setzen. Schlägt diese Härtung fehl, wird der
Speichervorgang sichtbar als Fehler gemeldet. Administratoren sollen das
Ergebnis trotzdem prüfen:

```powershell
icacls "$env:ProgramData\ZVT2SumUp"
```

Unerwartete Benutzer oder Gruppen müssen vor dem Produktionseinsatz entfernt
werden. Die Anwendung darf nicht aus einem weltweit beschreibbaren Ordner
betrieben werden.

## Netzwerkmodell

### ZVT-Seite

Standard ist `127.0.0.1:20007`. ZVT/TCP ist nicht zusätzlich durch TLS,
Clientzertifikate oder ein Gateway-Passwort geschützt. Wer den Port erreichen
kann, kann ZVT-Befehle einschließlich Zahlungen senden. Deshalb:

- Loopback verwenden, wenn Kasse und Gateway auf demselben Rechner laufen.
- Bei externer Bindung ein eigenes Kassennetz/VLAN und eine eingehende
  Windows-Firewallregel nur für die feste Kassen-IP verwenden.
- Den Port niemals direkt ins Internet, Gast-WLAN oder allgemeine Büronetz
  freigeben.
- Portweiterleitungen und Cloud-Tunnel vermeiden.
- Netzwerkzugriffe am Host überwachen.

COM-Verbindungen reduzieren die IP-Angriffsfläche, erfordern aber eine
vertrauenswürdige Treiber- und Gerätebasis.

### SumUp-Seite

Der typisierte Client kommuniziert mit `https://api.sumup.com`. Timeouts und
Cancellation sind gesetzt. Der Authorization-Header wird nicht bewusst
protokolliert und bekannte Secretmuster werden redigiert. Nicht-idempotente
Zahlungs- und Refund-POSTs werden nicht automatisch wiederholt.

### GitHub-Seite

Nur ein manueller Update-Check kontaktiert `api.github.com`. Die ursprünglichen
Asset-URLs müssen exakt zum konfigurierten Repository, zur Releaseversion und
zu den festen Dateinamen gehören. Downloads und Weiterleitungen sind zusätzlich
auf vier exakte GitHub-HTTPS-Hosts begrenzt. Akzeptiert werden nur
`ZVT2SumUp-win-x64.zip`, `checksums.sha256`, genau ein Hash-Eintrag für dieses
ZIP und exakt zwei ausführbare PE-Dateien in dessen Stamm.

Der erhöhte Installer prüft Größe, ZIP-Struktur, SHA-256, Dateiversion und drei
Smoke-Tests. Sein zufälliger Stagingordner verweigert normalen Benutzern den
Zugriff. Der Austauschbefehl akzeptiert nur den dort erzeugten Plan und nur die
unverändert kopierte installierte Tools-EXE als Runner. Installations- und
Payloadpfade mit Reparse Points/Junctions werden abgelehnt. Der Austausch
verwendet feste Dateinamen, temporäre Write-through-Kopien und einen
Rollbackordner auf demselben Volume. Ein zuvor laufender Dienst
muss mit der neuen Version wieder den Status `Running` erreichen, sonst werden
beide alten EXE-Dateien wiederhergestellt.

Die Prüfsumme ist kein unabhängiger Herkunftsnachweis, wenn Repository und
Manifest gemeinsam kompromittiert sind. Authenticode und GitHub-Attestations
sind zusätzliche, für spätere Releases empfohlene Schutzschichten.

## Zahlungs- und Refundschutz

- Betrag muss positiv sein und wird als `long` in Cent geführt.
- Jede Zahlung erhält eine neue Referenz.
- Zahlungen je Terminal werden serialisiert.
- Doppelte Journalzahlungen werden anhand Transaction-/Checkout-ID abgefangen.
- Die CLI benötigt für `payment` den Schalter `--confirm-real-payment`.
- CLI-Refund, Kassenschnitt und RAW-APDUs benötigen eigene Bestätigungen.
- Eine in RAW eingebettete Zahlungs-, Refund- oder Kassenschnitt-APDU benötigt
  zusätzlich den jeweiligen fachlichen Bestätigungsschalter.
- Die GUI fordert vor vollständigen oder teilweisen Refunds eine Bestätigung.
- Automatisierte Tests verwenden ausschließlich Fakes und lösen keine echten
  Zahlungen aus.

Eine Bestätigung schützt nicht gegen ein bereits kompromittiertes lokales Konto
oder einen ungeschützten externen ZVT-Port.

## Protokollierung und Redaction

Die Redaction erkennt unter anderem Authorization-Werte, `api_key`,
`affiliate_key`, Tokens, Passwörter und Pairing-Codes. Sie ist eine
Tiefenverteidigung, keine Freigabe zum ungeprüften Veröffentlichen ganzer Logs.
Unbekannte Antwortfelder, Transaktions-IDs, Händlerdaten oder zukünftige
Secretformate können weiterhin sensibel sein.

Vor Support oder Screenshots:

1. Nie den Tab **Einrichtung** mit ausgefüllten Geheimnisfeldern aufnehmen.
2. Reader-, Merchant- und Transaktions-IDs maskieren.
3. Nur einen kurzen relevanten Logausschnitt kopieren.
4. Datei-Metadaten und Benutzernamen in Pfaden prüfen.
5. Im Zweifel ausschließlich eine textuelle, redigierte Zusammenfassung senden.

## Lokale Dateien und Ausfallsicherheit

Konfiguration, Journal, Secrets und Zähler werden über temporäre Dateien im
Zielordner geschrieben und anschließend atomar ersetzt. Das Journal ist durch
eine Prozesssperre geschützt; bei erkanntem JSON-/I/O-Schaden wird vor dem
Fehler eine datierte Sicherung erzeugt. Dies ersetzt kein externes Backup.

Backups müssen verschlüsselt, zugriffsbeschränkt und mit einer definierten
Aufbewahrungsfrist versehen sein. `secrets.dat` und lokale Konfigurationen
niemals in Git, Cloud-Synchronisation oder öffentliche Workflow-Artefakte legen.

## Datenschutz

ZVT2SumUp enthält keinen Telemetrie-, Analyse- oder Werbeclient. Es gibt kein
Remote-Plugin-System und keine automatische Hintergrundzahlung. Lokal können
Geschäfts-, Terminal- und Transaktionsmetadaten entstehen. Der Betreiber ist
für Rechtsgrundlage, Aufbewahrung, Zugriffskontrolle, Löschung und Erfüllung
betroffener Rechte verantwortlich.

Die Anwendung fordert keine PIN und keine vollständige Kartennummer an. Welche
personenbezogenen Daten SumUp selbst verarbeitet, richtet sich nach dem
Händlervertrag und den aktuellen SumUp-Datenschutzbedingungen.

## Empfohlene Produktionscheckliste

- Windows vollständig aktualisiert und Datenträgerverschlüsselung aktiviert
- dediziertes, nicht administratives Betriebskonto für den Alltag
- SumUp-Schlüssel mit minimal notwendigen Rechten und dokumentierter Rotation
- Gateway auf Loopback oder restriktives Kassennetz begrenzt
- Windows-Firewallregel überprüft
- ACL von `%ProgramData%\ZVT2SumUp` überprüft
- Dienstkonto und Recovery kontrolliert
- signierte beziehungsweise unabhängig verifizierte Buildherkunft
- verschlüsselte, getestete Backups von Journal und Vorlagen
- Logaufbewahrung und Löschprozess definiert
- Registrierung, Zahlung, Abbruch, Refund und Kassenschnitt mit dem konkreten
  Standort getestet
- Notfallweg zum Sperren des SumUp-Keys dokumentiert

## Schwachstellen

Sensible Probleme niemals öffentlich diskutieren. Vorgehen und Rotation bei
Leaks: [SECURITY.md](../SECURITY.md).
