# DotNet Uninstall Tool UI

A lightweight cross‑platform (Windows / macOS) graphical user interface that wraps the official `dotnet-core-uninstall` command‑line utility from Microsoft.

> This project is a community UI helper. It is **not** an official Microsoft application. Uninstalling SDKs/runtimes can impact existing projects—read the safety notes below.

## Why does this exist?

Managing many installed .NET SDK and runtime versions becomes cumbersome. The official `dotnet-core-uninstall` tool provides the correct uninstall logic and safety rules (e.g. protecting required or currently in‑use SDKs), but it is console-only. This UI layers discoverability, visual status, and one‑click removal while preserving the tool’s rules.

## Key Features

- Detects and lists installed .NET SDKs & runtimes using the output of `dotnet-core-uninstall list` (parsing uninstallability reasons).
- Shows architecture, uninstallability, and reason (e.g. "Cannot uninstall SDK that is required...").
- One click uninstall (adds `-y` for non-interactive confirmation).
- Manual browse + persistent storage of the `dotnet-core-uninstall` binary path (env var can override).
- UI locking during operations to prevent re-entrancy.
- Status + error surface; reasons shown as tooltip and inline text.

## Requirements

| Item | Notes |
|------|-------|
| `dotnet-core-uninstall` binary | Obtain from the official Microsoft repository (release or self-built). Place anywhere readable. |

## Getting the `dotnet-core-uninstall` Tool

Download or build from: [dotnet/cli-lab](https://github.com/dotnet/cli-lab) (project: `dotnet-core-uninstall`). This UI does **not** bundle the binary. Keep the tool updated to ensure parsing remains accurate if its output format changes.

## Usage

1. Launch this app.
2. If auto-detection fails, either:
   - Set environment variable `DOTNET_UNINSTALL_TOOL` to the full path of the binary before launching, **or**
   - Click **Browse**, select the tool.
3. Review the listed SDKs & runtimes.
4. Uninstall an entry by pressing **Uninstall** (button is disabled if the reason indicates it is not allowed).
5. The UI locks and shows progress; when done, the list refreshes automatically.
6. Path is persisted (unless an env var overrides it next time).

## Safety / Disclaimer

- This UI only surfaces what the underlying tool allows. If the command line would refuse an uninstall, the UI should also reflect that.
- Always confirm you do not need a version for active projects or global build servers.
- If something fails, check the raw error message (captured from stderr/stdout) and consider running the tool manually for verbose diagnostics.
- Not affiliated with Microsoft. Use at your own risk.

## macOS Elevation Notes

On macOS the uninstall operation is wrapped in an AppleScript `do shell script ... with administrator privileges`. You will see the standard system authorization dialog. Cancelling it aborts the operation.

## Persistence

The chosen tool path is stored in the OS local app settings. Remove or move the binary? The next refresh will mark it missing until you re-apply a new path.

## Build / Run (Developer)

From repository root:

```bash
dotnet build DotNetUninstall/DotNetUninstall.csproj -c Debug
dotnet run --project DotNetUninstall/DotNetUninstall.csproj
```

## Project Structure (Simplified)

```text
DotNetUninstall/
  App.xaml / App.xaml.cs      - Application bootstrap (Uno single-project)
  Presentation/               - UI pages, view models, converters
  Models/                     - Data record for install entries
  ReadMe.md                   - This document
```

## Localization

Strings are currently inline. Future enhancement: move UI labels and status messages into resource files for easy translation.

## Roadmap / Ideas

- Inline streaming of uninstall process output.
- Filtering (SDK vs Runtime) and sorting by version.
- Theming toggle and high contrast improvements.
- Optional confirmation dialog before uninstall.
- Light telemetry (opt-in) for which commands are used (never collecting personal data).

## Contributing

PRs and issues welcome. Please:

1. Open an issue describing the change.
2. Keep UI changes accessible (keyboard navigation & contrast).
3. Add a brief test or sample output snippet if you adjust parsing.

## License

This project is licensed under the [MIT License](./LICENSE). You are free to use, modify, and distribute with attribution and inclusion of the license text.

## Acknowledgements

- Microsoft `dotnet-core-uninstall` team for the underlying logic.
- Uno Platform for cross-platform UI.

---

If the tool output format changes (new headings / different reason strings), parsing may silently skip entries. Open an issue with a redacted sample of the new `list` output so we can update the regex.
