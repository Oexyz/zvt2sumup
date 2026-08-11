# Tools und Diagnose

`ZVT2SumUp.Tools.exe` ist die zweite Datei des Endanwenderpakets. Beim
Doppelklick startet sie den interaktiven **Kassensimulator** und hält eine
ZVT-TCP-Verbindung über mehrere Vorgänge offen. Fehlerausgaben werden durch die
zentrale Redaction geführt. Die reine Befehlsübersicht erscheint mit `help`.

## Kassensimulator

```powershell
.\ZVT2SumUp.Tools.exe
# identisch:
.\ZVT2SumUp.Tools.exe cash-register-simulator
```

Der Simulator liest standardmäßig Host und Port aus `config.ini`, zeigt jede
gesendete und empfangene APDU als Hex an und quittiert Gateway-Antworten. Er
bietet Registrierung, Zahlung, Storno, Refund, Kassenschnitt, Status, Diagnose,
Abbruch und bewusstes Trennen. Zahlungen verlangen Betrag plus exakte
Textbestätigung; Storno, Refund und Kassenschnitt besitzen jeweils eine eigene
Bestätigung. Eine externe Zieladresse benötigt zusätzlich `EXTERN`.

![Kassensimulator im sicheren Ausgangszustand](images/cash-register-simulator.png)

## Nur lesende beziehungsweise lokale Befehle

| Befehl | Wirkung | Reale Zahlung |
|---|---|---|
| `help` | Hilfe anzeigen | nein |
| `status` | Windows-Dienststatus über `sc.exe query` | nein |
| `com-list` | vorhandene COM-Ports anzeigen | nein |
| `com0com-detect` | bekannte lokale Installationspfade prüfen | nein; installiert nichts |
| `status-zvt` | ZVT-Status an konfigurierten Gatewayport senden | nein |
| `register` | ZVT-Registrierung senden | nein |
| `sumup-test` | API-Zugriff prüfen und Terminals auflisten | nein |
| `transactions` | letzte 20 SumUp-Transaktionen lesen | nein |
| `verify-release` | aktuelles stabiles GitHub-Release samt SHA-256 und beiden EXE-Smokes vollständig prüfen, danach Staging löschen | nein; installiert nichts |
| `gateway-simulator` | lokales Fake-Gateway auf `127.0.0.1:20008` | nein; keine SumUp-Verbindung |

`sumup-test` und `transactions` lesen den DPAPI-Store des lokalen Computers.
Ausgaben können Händler-, Reader- oder Transaktions-IDs enthalten und sind vor
Weitergabe zu pseudonymisieren.

## Dienstbefehle

| Befehl | Wirkung | Rechte |
|---|---|---|
| `install [gateway-exe]` | Dienst automatisch installieren und Recovery konfigurieren | Administrator |
| `uninstall` | nur Dienstregistrierung entfernen | Administrator |
| `start` | Dienst starten | nach Systemrichtlinie |
| `stop` | Dienst stoppen | nach Systemrichtlinie |
| `restart` | Dienst stoppen und starten | nach Systemrichtlinie |
| `run-console` | Gateway im aktuellen Terminal ausführen; `Strg+C` stoppt sauber | normal |
| `update --confirm-update` | neuestes stabiles GitHub-Release vollständig prüfen und installieren | Administrator; normalerweise nur über GUI |

`uninstall` löscht keine Daten unter `%ProgramData%\ZVT2SumUp`.

Die internen Befehle `apply-update` und `cleanup-update` werden ausschließlich
aus einem administrativ geschützten Staging mit validiertem Plan aufgerufen.
Sie sind keine öffentliche Update-Abkürzung und lehnen fehlende Bestätigung,
fremde Pfade sowie Reparse Points ab.

## Zustandsändernde ZVT-Befehle

Diese Befehle nur gegen ein eigenes beziehungsweise ausdrücklich autorisiertes
Terminal senden:

```powershell
# Reale Zahlung über 1 Cent
.\ZVT2SumUp.Tools.exe payment 1 --confirm-real-payment

# Refund der letzten vom Gateway bekannten Transaktion
.\ZVT2SumUp.Tools.exe refund --confirm-real-refund

# Kassenschnitt; kann offene Journalposten schließen
.\ZVT2SumUp.Tools.exe reconcile --confirm-reconciliation
```

`payment` interpretiert den Betrag als ganze Cent. Es gibt keine implizite
Euro-/Dezimalumrechnung.

## RAW-APDU

Beliebige APDUs sind ein Expertenwerkzeug und benötigen immer
`--confirm-raw-command`:

```powershell
.\ZVT2SumUp.Tools.exe raw 050100 --confirm-raw-command
```

Zusätzliche Schutzschalter werden anhand der geparsten APDU verlangt:

| RAW-Kommando | zusätzlicher Schalter |
|---|---|
| `06 01` Zahlung | `--confirm-real-payment` |
| `06 30` Storno oder `06 31` Refund | `--confirm-real-refund` |
| `06 50` Kassenschnitt | `--confirm-reconciliation` |
| `06 18` Reset | `--confirm-state-change` |

Andere RAW-Kommandos können trotzdem unerwartete Zustandsänderungen bewirken.
Der allgemeine Bestätigungsschalter ist kein Ersatz für Protokollkenntnis,
isolierte Tests und die Sicherung des Journals.

## Ziel und Framing

Kassenbefehle verwenden standardmäßig `127.0.0.1:20007` und RAW-APDU:

```powershell
.\ZVT2SumUp.Tools.exe status-zvt --host 127.0.0.1 --port 20007
.\ZVT2SumUp.Tools.exe status-zvt --length-prefixed
```

Ein fremder Host sollte nicht angegeben werden. Vor einem zustandsändernden
Befehl Host, Port, Händlerkonto, Terminal und Betrag im Klartext kontrollieren,
aber keine Secrets in die Kommandozeile schreiben.

## Hexausgabe

Gesendete und empfangene APDUs erscheinen als `TX` und `RX`. Sie enthalten
normalerweise keine API-Keys, können aber Transaktions-, Terminal- oder
Belegdaten darstellen. Auch Hexdumps sind vor öffentlicher Weitergabe zu prüfen.
