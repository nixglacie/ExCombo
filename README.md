# ExCombo

**Node-based job rotation flow editor for FFXIV (Dalamud plugin).**

Design your job rotation as a visual node graph. Flows replace hotbar actions in-game as you
build them. Use `/excombo` to open the editor.

## Install (for testers)

ExCombo is distributed through a custom Dalamud plugin repository. You need XIVLauncher +
Dalamud installed, with the game run under Dalamud at least once.

1. In-game, run `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Paste this URL, click **+**, then **Save**:
   ```
   https://raw.githubusercontent.com/Exora/ExCombo/main/pluginmaster.json
   ```
3. Run `/xlplugins`, search **ExCombo**, and click **Install**.
4. Run `/excombo` to open the editor.

**Updating:** when a new release is published, the Plugin Installer shows an **Update** button
for ExCombo — click it.

## Build from source

Prerequisites: .NET 10 SDK; XIVLauncher + Dalamud installed (Dalamud's dev libraries are pulled
from the default `%AppData%\XIVLauncher` location, or set `DALAMUD_HOME`).

```
dotnet build --configuration Release
```

Output (plugin zip + manifest) lands in `ExCombo/bin/Release/ExCombo/` as `latest.zip`.

For local iteration you can load it as a dev plugin: `/xlsettings` → **Experimental** → add the
full path to `ExCombo/bin/Release/ExCombo.dll` under Dev Plugin Locations, then enable it in
`/xlplugins` → **Dev Tools > Installed Dev Plugins**.

## Releasing

1. Bump `<Version>` in `ExCombo/ExCombo.csproj` and `AssemblyVersion` in `pluginmaster.json`
   (keep them in sync; `DownloadLink*` URLs stay fixed at `releases/latest`).
2. Tag and push:
   ```
   git tag vX.Y.Z
   git push --tags
   ```
3. The `.github/workflows/release.yml` workflow builds and publishes a GitHub Release with
   `latest.zip` attached. Testers then see the **Update** button.

Bump `DalamudApiLevel` in `pluginmaster.json` only when Dalamud's API level changes.
