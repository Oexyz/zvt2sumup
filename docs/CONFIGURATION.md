# Konfiguration

Die Oberfläche ist der bevorzugte Editor. `config.ini` kann für kontrollierte
Deployments gelesen werden, darf aber nie echte Secrets enthalten.

## Speicherorte

```text
%ProgramData%\ZVT2SumUp\
├── config.ini
├── secrets.dat
├── transaction_journal.json
├── receipt_templates.ini
├── receipt_counter.txt
├── logs\
└── updates\          # geschütztes Staging und update.log
```

Desktop-App, Dienst und Tools verwenden diese gemeinsamen Pfade.

## Standardkonfiguration

```ini
[gateway]
modus = tcp
tcp_host = 127.0.0.1
tcp_port = 20007
tcp_idle_timeout = 0
com_port = COM3
com_baudrate = 9600
waehrung = EUR
log_level = Information
log_datei = logs/zvt2sumup.log

[sumup]
merchant_code =
terminal_id =
zahlung_timeout = 120
api_key = {{encrypted:secrets.dat:api_key}}
affiliate_key = {{encrypted:secrets.dat:affiliate_key}}
affiliate_app_id = {{encrypted:secrets.dat:affiliate_app_id}}

[end_of_day]
source = local_journal
reset_after_print = true

[updates]
github_repository = Oexyz/zvt2sumup
```

Die Datei wird von der Anwendung atomar neu geschrieben. Kommentare oder
unbekannte Optionen können dabei verloren gehen; Anpassungen gehören in die
unterstützten Felder.

## Werte und Validierung

| Schlüssel | Werte/Grenzen | Standard |
|---|---|---|
| `modus` | `tcp` oder `com` | `tcp` |
| `tcp_host` | gültige IP-Adresse | `127.0.0.1` |
| `tcp_port` | 1 bis 65535 | `20007` |
| `tcp_idle_timeout` | Sekunden, mindestens 0; `0` unbegrenzt | `0` |
| `com_port` | nicht leer, zum Beispiel `COM3` | `COM3` |
| `com_baudrate` | 300 bis 4.000.000 | `9600` |
| `waehrung` | drei große ASCII-Buchstaben nach ISO 4217 | `EUR` |
| `log_level` | Trace, Debug, Information/Info, Warning/Warn, Error, Critical/Fatal | `Information` |
| `log_datei` | relativer Pfad innerhalb des Datenordners | `logs/zvt2sumup.log` |
| `zahlung_timeout` | 10 bis 3600 Sekunden | `120` |
| `merchant_code` | Merchant des autorisierten SumUp-Kontos | leer |
| `terminal_id` | ausgewähltes klassisches Terminal oder Reader | leer |
| `source` | derzeit ausschließlich `local_journal` | `local_journal` |
| `reset_after_print` | `true` oder `false` | `true` |
| `github_repository` | exakt `Eigentümer/Repository` mit sicheren Zeichen | `Oexyz/zvt2sumup` |

Eine externe TCP-Adresse wird nicht grundsätzlich blockiert, aber sichtbar
gewarnt. Sie erfordert eine restriktive Firewall und ein isoliertes Kassennetz.

## Geheimnisse

API-Key, Affiliate-Key und App-ID werden über die Oberfläche in
`secrets.dat` gespeichert. Die INI-Platzhalter sind keine auflösbaren
Kommandozeilen- oder Cloud-Secretreferenzen, sondern ein deutlicher Hinweis,
dass der Wert im lokalen DPAPI-Store liegt.

Nicht zulässig:

```ini
api_key = <NICHT_HIER_EINTRAGEN>
```

Falls ein Klartextwert jemals in Git, einem Log oder Supportkanal gelandet ist,
den Key sofort bei SumUp widerrufen und neu erzeugen. Bloßes Entfernen aus der
Datei genügt nicht.

## Belegvorlagen

`receipt_templates.ini` enthält die Sektionen `settings`, `merchant`, `fiscal`,
`sumup_display`, `payment`, `reversal` und `end_of_day`. Platzhalter werden bis
zu drei Durchläufen verschachtelt ersetzt; leere optionale Zeilen werden
entfernt. Ausgaben werden auf 40 Zeichen umgebrochen und `€` wird kompatibel als
`EUR` ausgegeben.

Typische Platzhalter:

- `{merchant_name}`, `{merchant_address_line1}`, `{merchant_address_line2}`
- `{amount}`, `{currency}`, `{date}`, `{time}`, `{receipt_number}`
- `{terminal_id}`, `{transaction_id}`, `{auth_code}`, `{status_text}`
- `{payment_count}`, `{refund_count}`, `{payment_total}`, `{refund_total}`
- `{total_amount}`, `{transaction_count}`

Die vorgesehenen TSE-Felder können Texte aufnehmen, werden vom Gateway aber
nicht selbst erzeugt oder fiskalisch signiert. Der Gateway-Beleg ersetzt keinen
Kassen-/TSE-Bon.
