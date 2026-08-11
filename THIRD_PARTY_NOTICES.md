# Hinweise zu Drittkomponenten

ZVT2SumUp bindet keine SumUp-Binärbibliothek ein. Die Laufzeit basiert auf .NET
und den unten aufgeführten NuGet-Paketen.

## Laufzeitabhängigkeiten

| Paketgruppe | Verwendete Version | Lizenz laut NuGet-Metadaten |
|---|---:|---|
| Microsoft.Extensions.Hosting und WindowsServices | 10.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT |
| System.IO.Ports | 10.0.0 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT |
| System.ServiceProcess.ServiceController | 10.0.0 | MIT |

Die self-contained Veröffentlichung enthält außerdem .NET-Runtimekomponenten
und transitive `Microsoft.Extensions.*`-/`System.*`-Pakete. Deren exakte,
aufgelöste Liste ist reproduzierbar mit:

```powershell
dotnet list .\Zvt2SumUp.slnx package --include-transitive
```

Microsoft-.NET-Pakete oben verweisen in ihren NuGet-Metadaten auf die
[MIT-Lizenz](https://licenses.nuget.org/MIT). Urheber- und Lizenztexte der
jeweiligen Pakete bleiben maßgeblich.

## Nur Entwicklung und Tests

| Paket | Verwendete Version | Lizenz laut NuGet-Metadaten |
|---|---:|---|
| Microsoft.NET.Test.Sdk | 18.0.1 | MIT |
| xunit.v3 | 3.2.2 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |

Testpakete gehören nicht zum Endanwender-Paket mit den zwei EXE-Dateien.

## Externe Dienste und Spezifikationen

- SumUp ist ein externer Zahlungsdienst. ZVT2SumUp nutzt dessen öffentliche API,
  verteilt aber keine SumUp-Software.
- PA00P015 Revision 13.13 diente als Kompatibilitätsreferenz. Das PDF ist nicht
  Bestandteil dieses Repositorys oder des Buildartefakts.
- GitHub wird ausschließlich für Quellcode-Automation und die manuell
  angestoßene Release-/Updateprüfung verwendet.

Marken und Inhalte bleiben Eigentum ihrer jeweiligen Inhaber. Dieses Dokument
ändert keine Lizenzbedingungen und ersetzt nicht die Originaltexte der Pakete.
