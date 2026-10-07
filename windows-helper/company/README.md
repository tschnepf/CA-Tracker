# Company settings

Copy `ca-tracker-company.example.json` to `ca-tracker-company.json` (same folder) and edit it before
building the installer; `build.ps1 -Installer` ships it next to the app. It can also be placed in
`%ProgramData%\SMC-CA-Tracker\` on a PC.

| Setting | What it does |
|---|---|
| `companyName` | Shown in Settings ("Some settings are set by …") |
| `folders` | Default RFI / Submittal / returned folder names in each CA folder |
| `disciplines` | The disciplines offered and detected in folder names (`code`, `label`, `folderToken` as written in folder names, other `aliases`) |
| `syncIntervalMinutes` | How often tracked Helix projects and Procore projects sync in the background |
| `updateFeed` | Folder or https address holding `latest.json` and the installers (see below); plain `http://` is refused |
| `locked` | Settings people can't change in the app: `folders`, `disciplines`, `sync` |

## Publishing an update

1. Raise `<Version>` in `windows-helper/Directory.Build.props`.
2. Run `powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1 -Installer`.
3. Copy `installer\output\SMC-CA-Tracker-Setup-<version>.exe` and `installer\output\latest.json` to the
   `updateFeed` location.

Every CA Tracker checks the location at start and every six hours and offers **Update to <version>** in
the title bar; the installer then runs quietly and the app starts again.

`latest.json` looks like this (`build.ps1 -Installer` writes it):

```json
{ "version": "3.0.1", "installer": "SMC-CA-Tracker-Setup-3.0.1.exe", "sha256": "<hex>", "notes": "SMC CA Tracker 3.0.1" }
```

- `installer` must be a bare file name next to `latest.json`: another folder, a UNC path or a web address is ignored.
- `sha256` is required (`Get-FileHash -Algorithm SHA256 <installer>`); the app checks the copied installer
  against it and refuses to run one that differs.
- Versions compare on three parts (`3.0.1`); a fourth part is ignored.
