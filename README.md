# Dame Online

Responsive Mehrspieler-Dame fuer Handy, Tablet und PC mit:

- Echtzeit-Partien ueber WebSockets
- Anmeldung per Name
- Statistik pro Spielername
- 10x10-Europabrett
- Host-gesteuerte Regelkonfiguration
- Lokalen Farbanpassungen fuer die eigenen Steine

## Start

Voraussetzung: .NET 8 SDK

```powershell
dotnet run
```

Danach im Browser oeffnen:

```text
http://localhost:5000
```

## Hinweise

- Der Host erstellt einen Raumcode, der zweite Spieler tritt mit demselben Code bei.
- Statistiken werden in `App_Data/players.json` gespeichert.
- Die aktuelle Version ist als MVP umgesetzt und konzentriert sich auf Echtzeitspiel, Regelumschaltung, Zeitmessung und Persistenz.
