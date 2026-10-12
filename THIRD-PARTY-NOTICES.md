# Third-party notices

Sonar Preset Switcher's own source has no third-party dependencies — no NuGet packages, base class
library only. The notices below cover what a **released build** ships or builds with.

## Bundled in the release

The release is self-contained, so it carries a copy of the .NET runtime:

| Component | Licence | Copyright |
| --- | --- | --- |
| .NET Runtime (`Microsoft.NETCore.App`) | MIT | © .NET Foundation and Contributors |
| .NET Windows Desktop Runtime — WPF and Windows Forms (`Microsoft.WindowsDesktop.App`) | MIT | © .NET Foundation and Contributors |

Their full licence texts, and the runtime's own `THIRD-PARTY-NOTICES.TXT`, are copied verbatim from the
runtime packs at publish time into the `licenses` folder next to the executable.

## Build tooling

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php), © Jordan Russell and
Martijn Laan, under the Inno Setup licence.

## Not included

These are separate products the app can work with. None of their code or assets is distributed here.

- **SteelSeries GG / Sonar** — proprietary, © SteelSeries ApS. The app calls GG's local HTTP interface
  on the user's own machine.
- **Equalizer APO** — GPL-2.0. The app writes a plain-text configuration file for it and links to its
  download page.
- **OBS Studio / obs-websocket** — GPL-2.0. The app is a client of the obs-websocket network protocol.

## Trademarks

Not affiliated with, endorsed by, or supported by SteelSeries. "SteelSeries", "GG" and "Sonar" are
trademarks of their respective owner, used only to describe compatibility. "OBS" and "Equalizer APO"
belong to their respective projects. "Windows" and ".NET" are trademarks of the Microsoft group of
companies.
