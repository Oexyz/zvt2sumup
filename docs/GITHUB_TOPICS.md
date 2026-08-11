# GitHub-Beschreibung und Topics

Diese Metadaten werden beim Anlegen beziehungsweise vor der Veröffentlichung des
Repositorys gesetzt. Sie enthalten nur tatsächlich implementierte Funktionen.

## Beschreibung

```text
Native Windows payment gateway connecting ZVT 13.13 point-of-sale systems with SumUp Solo, Reader and terminal APIs.
```

## Topics

```text
zvt
zvt-protocol
zvt-13-13
sumup
sumup-api
sumup-solo
payment-gateway
payment-terminal
payments
pos
point-of-sale
korona
cash-register
windows
windows-11
winforms
windows-service
dotnet
dotnet-10
csharp
tcp
serial-port
com-port
self-contained
german
```

Nach Erstellung des Repositorys lassen sich Beschreibung und Topics mit der
GitHub CLI setzen. Dieser Befehl veröffentlicht keinen Release:

```powershell
gh repo edit Oexyz/zvt2sumup `
  --description 'Native Windows payment gateway connecting ZVT 13.13 point-of-sale systems with SumUp Solo, Reader and terminal APIs.' `
  --add-topic zvt,zvt-protocol,zvt-13-13,sumup,sumup-api,sumup-solo,payment-gateway,payment-terminal,payments,pos,point-of-sale,korona,cash-register,windows,windows-11,winforms,windows-service,dotnet,dotnet-10,csharp,tcp,serial-port,com-port,self-contained,german
```

Topics wie `official`, `certified`, `pci-compliant`, `tse`, `fiscal` oder
`sumup-official` werden bewusst nicht verwendet, weil sie eine nicht belegte
Zertifizierung oder Herstellerzugehörigkeit suggerieren würden.
