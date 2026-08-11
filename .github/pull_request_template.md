## Änderung

<!-- Problem und Lösung knapp beschreiben. Keine Secrets oder produktiven IDs einfügen. -->

## Sicherheitsauswirkung

- [ ] Keine Änderung an Zahlung, Refund, Secrets, Netzwerk, Updates oder Dienstrechten
- [ ] Sicherheitsrelevante Änderung ist unten mit Vertrauensgrenze und Reject-Verhalten beschrieben

## Prüfung

- [ ] `dotnet build .\Zvt2SumUp.slnx -c Release`
- [ ] `dotnet test .\tests\Zvt2SumUp.Tests\Zvt2SumUp.Tests.csproj -c Release --no-build`
- [ ] `Publish.ps1` erzeugt exakt zwei EXE-Dateien
- [ ] Keine echten Zahlungen in automatisierten Tests
- [ ] Dokumentation und Changelog aktualisiert

## Manuelle Tests

<!-- Nur redigierte Ergebnisse mit Fakes oder ausdrücklich autorisierter Hardware. -->
