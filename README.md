# hwinfo-bridge

Exposes selected HWiNFO sensor readings over
`http://127.0.0.1:8765/sensors` so the arcane-wallpaper System Codex can show
real CPU/GPU/RAM/power data.

Works with **HWiNFO Free** (no Pro / shared memory required) — it reads the
sensors HWiNFO writes to `HKCU\Software\HWiNFO64\VSB` when you enable its
HWiNFO Gadget feature.

Runs as a hidden background process — no console window, no tray icon. Stop
it any time via Task Manager (`hwinfo-bridge.exe`).

## Install (recommended)

**PowerShell** (Windows Terminal, PowerShell 5.1 / 7):

```powershell
irm https://raw.githubusercontent.com/RainyTea/hwinfo-bridge/main/install.ps1 | iex
```

**cmd.exe** or the **Win+R Run dialog**:

```bat
powershell -ExecutionPolicy Bypass -NoProfile -Command "irm https://raw.githubusercontent.com/RainyTea/hwinfo-bridge/main/install.ps1 | iex"
```

Either command downloads the latest release zip from GitHub, extracts it to
`%LOCALAPPDATA%\HwInfoBridge`, registers autostart at login, and launches the
bridge. Re-run any time to update - your `config.json` is preserved.

Flags (PowerShell only, pass after piping into `iex`):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/RainyTea/hwinfo-bridge/main/install.ps1))) -NoAutostart -NoLaunch
```

- `-NoAutostart` - install without registering at login.
- `-NoLaunch` - install without starting it immediately.
- `-Version v0.1.0` - pin to a specific tag instead of the latest release.

To uninstall, kill `hwinfo-bridge.exe` in Task Manager, run
`%LOCALAPPDATA%\HwInfoBridge\hwinfo-bridge.exe --uninstall`, then delete the
folder.

## Restart after closing

If you killed `hwinfo-bridge.exe` via Task Manager and want it back without
rebooting, just relaunch the exe:

- **Win+R**, paste `%LOCALAPPDATA%\HwInfoBridge\hwinfo-bridge.exe`, Enter.
- **PowerShell:** `Start-Process "$env:LOCALAPPDATA\HwInfoBridge\hwinfo-bridge.exe"`
- **cmd.exe:** `start "" "%LOCALAPPDATA%\HwInfoBridge\hwinfo-bridge.exe"`

Or simply log out and back in - the autostart entry the installer registered
fires it automatically.

## One-time HWiNFO setup

1. Launch **HWiNFO64** and open the **Sensors** window.
2. Click the gear / **Configure** icon, go to the **HWiNFO Gadget** tab.
3. For each sensor you want exposed, click **Report value in Gadget**.
   Recommended set:
   - **Total CPU Usage**
   - **CPU (Tctl/Tdie)** on Ryzen, or **CPU Package** on Intel
   - **Core Clocks**
   - **CPU Package Power**
   - **GPU Core Load**
   - **GPU Temperature**
   - **GPU Power**
   - **GPU Clock**
4. Set the registry polling period to ~1000 ms.
5. Apply / OK. HWiNFO must keep running in the background for fresh data.

## Customize sensor labels (optional)

Edit `%LOCALAPPDATA%\HwInfoBridge\config.json`. Each `sensors.*` field is
matched case-insensitively against the labels HWiNFO writes to the registry:
**exact match wins**, otherwise the first substring hit. Defaults:

```json
{
  "port": 8765,
  "pollMs": 1000,
  "sensors": {
    "cpuUsage": "Total CPU Usage",
    "cpuTemp": "CPU (Tctl/Tdie)",
    "cpuWatts": "CPU Package Power",
    "cpuCoreClock": "Core Clocks",
    "gpuUsage": "GPU Core Load",
    "gpuTemp": "GPU Temperature",
    "gpuWatts": "GPU Power",
    "gpuCoreClock": "GPU Clock"
  }
}
```

To discover your machine's exact label strings, with HWiNFO + bridge running,
open <http://127.0.0.1:8765/labels> — that endpoint returns a live JSON dump
of every `Label` value HWiNFO is currently publishing. Restart the bridge
after editing config.

## Output shape

```json
{
  "cpu": {
    "name": "AMD Ryzen 9 5900X",
    "usage": 10,
    "temp": 52,
    "watts": 33,
    "coreClockMhz": 4840
  },
  "gpu": {
    "name": "NVIDIA GeForce RTX 4070",
    "usage": 19,
    "temp": 33,
    "watts": 50,
    "coreClockMhz": 2625
  },
  "memory": { "totalGb": 32, "usedGb": 14.2 },
  "storage": { "totalGb": 4000 },
  "system": { "watts": 83 }
}
```

`system.watts` is the sum of `cpu.watts + gpu.watts`. If a field is missing,
the wallpaper's System Codex hides that row.

## Troubleshooting

- **Nothing happens when I double-click** — that's expected, the app is
  designed to run hidden. Check **Task Manager -> Details** for
  `hwinfo-bridge.exe`. Open <http://127.0.0.1:8765/sensors> to verify it's
  serving.
- **Port already in use** — change `"port"` in `config.json`, then update
  `HWINFO_BRIDGE_URL` in `src/endpoints.ts` of the wallpaper to match.
- **All sensor values null** — HWiNFO isn't running, or "Report value in Gadget" isn't enabled for any sensor. Check
  <http://127.0.0.1:8765/labels>; if it returns `{}`, re-do the HWiNFO setup.
- **Wrong sensor matched** — open `/labels`, pick a unique substring of the
  exact label you want, and put it into `config.json`.
- **Wrong GPU shown** — the bridge prefers a discrete GPU (NVIDIA/AMD
  Radeon/Intel Arc) over an integrated one. If you have an unusual setup it
  may misdetect.

## Security

- Binds to `127.0.0.1` only — no network exposure.
- Runs as the invoking user — no admin / UAC required.
- Only reads from the registry; never writes (except the optional
  `--install`/`--uninstall` flags, which touch only `HKCU\…\Run`).
