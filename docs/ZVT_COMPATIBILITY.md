# ZVT-Kompatibilitätsmatrix

Grundlage ist PA00P015 Revision 13.13. Das urheberrechtlich geschützte
Spezifikationsdokument wird von diesem Projekt nicht weiterverteilt. Die Matrix
beschreibt den tatsächlich implementierten und getesteten Stand.

## Transport und Kodierung

| Bereich | Status | Verhalten |
|---|---|---|
| TCP-Längenrahmen | Unterstützt, getestet | Kurze und erweiterte Längen, fragmentierte Eingaben |
| RAW-APDU über TCP | Unterstützt, getestet | Automatische Erkennung für KORONA |
| Dauerhafte TCP-Verbindung | Unterstützt | Idle-Timeout `0` bedeutet unbegrenzt |
| Mehrere TCP-Clients | Unterstützt | kontrolliert parallel, Zahlungen je Terminal serialisiert |
| Serielles Framing | Unterstützt, getestet | DLE-Escaping und LRC-Prüfung |
| COM-Parameter | Unterstützt | standardmäßig 9600, 8N1 |
| BCD | Unterstützt, getestet | strikte Validierung und Beträge in Minor-Units |
| BMP-Felder | Unterstützt, getestet | Betrags- und Feldextraktion |
| BER-TLV | Unterstützt, getestet | kurze und erweiterte Längen |
| ACK/NACK | Unterstützt, getestet | Eingehende `80`/`84` werden ignoriert; nicht unterstützte Befehle erhalten `84 83 00` |
| Druckzeilen/Textblöcke | Unterstützt | `06 D1` und `06 D3`, Zeilen auf 40 Zeichen umgebrochen |

## Kassenkommandos

| Kommando | Bedeutung | Status | Antwort und Grenze |
|---|---|---|---|
| `05 01` | Status-Enquiry | Unterstützt, getestet | SumUp-Zugriff bestimmt Ready/Out-of-order, danach Completion |
| `06 00` | Registrierung | Unterstützt, getestet | prüft SumUp, übernimmt Währungscode, meldet Terminal-ID und Completion |
| `06 01` | Zahlung | Unterstützt, Hardware getestet | Checkout, Polling, Journal, Status `04 0F`, Beleg und Completion |
| `06 02` | Abmeldung | Unterstützt | Completion ohne Cloudmutation |
| `06 10` | Turnover Totals | Unterstützt, getestet | offene terminalbezogene Journalsumme, schließt nichts |
| `06 12` | Print Turnover Receipts | Unterstützt | wiederholt letzten vorhandenen Beleg; sonst Fehler `83` |
| `06 18` | Reset | Unterstützt | löscht nur flüchtige letzte Transaktionsreferenz, ändert nicht die Systemzeit und löscht kein Journal |
| `06 20` | Wiederholbeleg | Unterstützt, getestet | druckt letzten Gateway-Beleg; sonst Function not possible |
| `06 30` | Storno | Unterstützt, getestet | vollständige Rückerstattung der letzten erfolgreichen Transaktion |
| `06 31` | Refund | Unterstützt, getestet | vollständige oder angeforderte Teilrückerstattung der letzten Transaktion |
| `06 50` | Kassenschnitt | Unterstützt, getestet | terminalbezogene Summe, Beleg und optionales Schließen offener Posten |
| `06 52` | Partial Reconciliation | Unterstützt, getestet | liefert Summe, druckt nicht und schließt keine Posten |
| `06 70` | Diagnose | Unterstützt | SumUp-Verbindungstest und Completion beziehungsweise No connection |
| `06 79` | Selftest | Unterstützt | SumUp-Test plus lesbarer Status; keine Zahlung |
| `06 91` | Datum/Uhrzeit setzen | Akzeptiert | bestätigt Erfolg, verändert die Windows-Systemzeit absichtlich nicht |
| `06 93` | Initialisierung | Unterstützt | Completion ohne destruktive Änderung |
| `06 B0` | Abbruch | Unterstützt, getestet | bricht aktive Zahlung ab und versucht Reader-Checkout zu terminieren |

## Bekannte, aber nicht unterstützte Kommandos

Alle Registry-Einträge außerhalb der expliziten Tabelle oben sowie unbekannte
Kassenbefehle werden nicht stillschweigend simuliert. Die Antwort lautet
deterministisch `84 83 00` (Function not possible). Dazu gehören insbesondere
Pre-Authorisation, Trinkgeldbuchung, Kartenaktivierung, OPT, Displaysteuerung,
Softwareupdate über ZVT und transparente Karten-APDUs.

## Erfolgreiche KORONA-RAW-Reihenfolge

```text
Kasse -> Gateway: 06 01 ...
Gateway -> Kasse: 80 00 00
Gateway -> Kasse: 04 0F ... Ergebnis und Betrag ...
Gateway -> Kasse: 06 0F 00
```

Im erkannten RAW-Modus werden optionale `04 FF`, `06 D1` und `06 D3`
unterdrückt. Im Length-Prefixed- oder seriellen Betrieb können verständliche
Zwischenstatus- und Belegframes zusätzlich gesendet werden.

## Fehlerabbildung

| Situation | ZVT-Reaktion |
|---|---|
| ungültige oder fehlende Betragsdaten | Abort mit Protocol error |
| Betrag kleiner oder gleich null | Abort mit Amount too small |
| SumUp nicht erreichbar | Zwischenstatus und Abort mit No connection |
| Zahlung abgelehnt/fehlgeschlagen | Zwischenstatus und Abort mit System error |
| Zahlungstimeout oder Benutzerabbruch | Zwischenstatus und Abort mit Timeout/Key abort |
| Storno ohne letzte Transaktion | Abort mit Reversal not possible |
| nicht unterstütztes Kommando | `84 83 00` |
| interne Ausnahme | redigiertes Log und Abort mit System error |

## Teststatus und offene Interoperabilität

Automatisiert geprüft sind Framing, Codec, ACK/NACK, Fehler, Timeout,
Serialisierung, Abbruch auf derselben Verbindung, KORONA-Reihenfolge, Journal,
Belege und SumUp-HTTP-Verträge. Ein echter SumUp-Reader wurde mit abgelehnten
Kleinstbeträgen sowie einer erfolgreichen autorisierten Zahlung geprüft.

Vor einem Produktionseinsatz verbleiben immer installationsspezifische Tests:

- vollständiger KORONA-Verkauf inklusive realem Kassenbon und TSE-Prozess
- längerer Dauerlauf mit Reconnects und Kassenschnitt über den konkreten Standort
- COM-Betrieb mit der tatsächlich eingesetzten Hardware
- klassische SumUp-Terminalvariante, falls kein Reader/Solo genutzt wird
- Abbruch während verschiedener Terminalphasen
- vollständige und teilweise Rückerstattung im konkreten Händlerkonto
- Recovery des installierten Diensts nach einem kontrollierten Testausfall

Die manuellen Schritte stehen in [TESTING.md](TESTING.md).
