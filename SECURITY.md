# Sicherheitsrichtlinie

ZVT2SumUp verarbeitet zahlungsrelevante Befehle. Sicherheitsmeldungen werden
daher vertraulich und mit minimalen Daten behandelt.

## Unterstützte Versionen

Sicherheitskorrekturen werden grundsätzlich nur für die neueste stabile
Versionslinie bereitgestellt.

| Version | Unterstützt |
|---|---|
| `1.0.x` | ja |
| ältere Versionen | nein |

## Schwachstelle vertraulich melden

1. Im GitHub-Repository **Security** -> **Advisories** -> **New draft security
   advisory** wählen.
2. Keine öffentliche Issue und keinen Pull Request mit Exploitdetails öffnen.
3. Nur die kleinste reproduzierbare Beschreibung senden: betroffene Version,
   Voraussetzung, Auswirkung und redigierte Reproduktionsschritte.
4. Niemals echte API- oder Affiliate-Keys, Pairing-Codes, Authorization-Header,
   Kartendaten, vollständige Transaktions- oder Reader-IDs, Händlerdaten,
   unbearbeitete Logs, `secrets.dat` oder produktive Journaldateien anhängen.
5. Falls ein Nachweis zwingend Konfiguration benötigt, ausschließlich lokale
   Fakes und erfundene Werte verwenden.

Eine Meldung sollte nach Möglichkeit enthalten:

- Art und Schwere der vermuteten Schwachstelle
- betroffene Datei, Funktion oder Protokollsequenz
- reproduzierbares Verhalten ohne reale Zahlung
- erwartetes sicheres Verhalten
- bekannte Umgehungen oder Minderungsmaßnahmen

Die Projektverantwortlichen bestätigen den Eingang, priorisieren die Meldung
und koordinieren Korrektur und Veröffentlichung über das private Advisory.

## Sofortmaßnahmen bei offengelegtem Schlüssel

Ein versehentlich veröffentlichter Schlüssel gilt als kompromittiert, auch wenn
der Beitrag schnell gelöscht wurde:

1. Schlüssel im SumUp-System sofort widerrufen beziehungsweise rotieren.
2. Aktive Checkouts und Transaktionen auf Auffälligkeiten prüfen.
3. Öffentliche Logs, Workflow-Artefakte und Caches auf weitere Kopien prüfen.
4. Neuen Schlüssel nur über die App speichern.
5. Ursache vertraulich über ein Security Advisory melden.

Das Umschreiben der Git-Historie allein macht einen veröffentlichten Schlüssel
nicht wieder sicher.

## Sicherheitsgrenzen

- ZVT/TCP ist standardmäßig auf Loopback beschränkt, enthält aber keine eigene
  Verschlüsselung oder Clientauthentisierung.
- Windows DPAPI `LocalMachine` und ACLs schützen ruhende Secrets gegen normale
  Benutzer. Lokale Administratoren, `SYSTEM`, kompromittierte Prozesse und ein
  kompromittiertes Betriebssystem liegen außerhalb dieser Grenze.
- SHA-256 schützt die Integrität eines Downloads gegenüber einem erwarteten
  Manifest. Ohne signierte Herkunft oder Attestation beweist eine Prüfsumme
  allein nicht den Herausgeber.
- Das Gateway verarbeitet keine PIN und fordert keine vollständige Kartennummer
  an. SumUp-Terminal, SumUp-Plattform und Händlerkonto bleiben eigene
  Vertrauensbereiche.
- Der Gateway-Beleg ist kein fiskalischer KORONA-/TSE-Bon.

Das ausführliche Bedrohungsmodell steht in
[docs/SECURITY_AND_PRIVACY.md](docs/SECURITY_AND_PRIVACY.md).

## Nicht als Schwachstelle melden

- SmartScreen-Warnung bei einem derzeit unsignierten Build
- fehlende Unterstützung eines in der Matrix ausdrücklich nicht unterstützten
  ZVT-Kommandos
- Händler-, Terminal- oder Netzwerkfehlkonfiguration ohne Sicherheitsauswirkung
- Ergebnisse von automatischen Scannern ohne nachvollziehbaren Datenfluss oder
  reproduzierbare Auswirkung

Solche Fälle gehören ohne sensible Daten in den normalen
[Supportprozess](SUPPORT.md).
