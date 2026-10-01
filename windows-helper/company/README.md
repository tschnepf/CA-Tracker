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
| `updateFeed` | Folder or https address holding `latest.json` and the installers (see below) |
| `centralServer` | The company's CA Tracker server, when there is one |
| `locked` | Settings people can't change in the app: `folders`, `disciplines`, `sync` |

## Publishing an update

1. Raise `<Version>` in `windows-helper/Directory.Build.props`.
2. Run `powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1 -Installer`.
3. Copy `installer\output\SMC-CA-Tracker-Setup-<version>.exe` and `installer\output\latest.json` to the
   `updateFeed` location.

Every CA Tracker checks the location at start and every six hours and offers **Update to <version>** in
the title bar; the installer then runs quietly and the app starts again.
