# ScoutR

A portable dual-pane file manager for Windows with a single folder tree, in the style of Windows 10.

- One folder tree on the left. It follows the **active pane**; a dot marks the folder of the other pane.
- Two folder panes side by side. Click a pane to make it active.
- Copy / move to the other pane (`F5` / `F6`), cut / copy / paste, rename (`F2`), new folder (`F7`), delete to Recycle Bin (`Del`).
- `Tab` switches pane. `Backspace` goes up. `Alt+Left` goes back.
- Updates itself from GitHub Releases.

See [`docs/mockup.html`](docs/mockup.html) for the interface mockup (open it in a browser).

## Status

First scaffold. Not yet built or run on Windows. Planned next:

- Detail / icon / list views, preview pane
- Recursive search
- Properties dialog, right-click menu
- Name-conflict dialog and progress for long copies
- Italian translation (`Strings.it.resx`)

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet run --project src/ScoutR/ScoutR.csproj
```

## Languages

English is the base language. Texts live in `src/ScoutR/Resources/Strings.resx`.
To add Italian, add `Strings.it.resx` next to it with the same keys.

## Releases and auto-update

Push a tag to publish a release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The `Release` workflow builds a self-contained single-file app, packs it with
[Velopack](https://velopack.io) and uploads it to GitHub Releases. A running copy checks for a newer
release at startup and offers **Restart to update**. Updates only apply to a copy that came from the
Velopack package (Setup or Portable zip), not to a build started from the IDE.
