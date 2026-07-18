# Altium MCP Placement + EasyEDA Loader

Altium Designer extension that takes you from **JLCPCB parts → schematic → floorplan → pin-accurate placement → rules/stackup → DRC → fab**, with an optional **MCP server** so Cursor (or other agents) can read the design.

Remote: [Sakuna-Nagodavithana/Altium-MCP-Placement](https://github.com/Sakuna-Nagodavithana/Altium-MCP-Placement)

---

## What this does

| Area | Capability |
|------|------------|
| **Parts** | Import JLCPCB / EasyEDA symbols, footprints, 3D into Altium libraries |
| **Export** | Full schematic + PCB connectivity JSON for MCP / review |
| **Board Needs** | From MCU / RF / USB / power parts → recommend **layers, stackup, impedance, via sizes, heat actions** |
| **Stackup** | JLCPCB 2L / 4L / 6L advisor + curated `.stackupx` / `.RUL` under `fab/recommended/` |
| **Floorplan** | Preview **10** layout variants (auto size / mm / DXF), ★ = best by estimated wirelength |
| **Smart Place** | One-click: Needs → best floorplan → Auto-Place → Fanout → Rooms |
| **Auto-Place** | Pin-accurate passives around every IC (decaps, RF Pi/T matching, deconflict) |
| **Rooms** | Altium Room Definition (confinement) + optional Unions per IC cluster |
| **Fanout** | Plane vias next to decoupling / IC power–GND pads |
| **Optimize** | Force-directed HPWL + simulated annealing (±90° rotate / swap) |
| **Rules** | Net classes RF / PWR / HighSpeed / Logic + width / clearance / via styles |
| **Via stitch** | GND stitch along RF and HighSpeed (clock) routes |
| **DRC** | Altium batch DRC + MCP extras (power clearance, pad↔track, neckdown, via↔pad) with Jump UI |

Inspired by how **Altium** does rooms / Arrange Within Room / Optimal Placement Vector, and how modern placers combine **force-directed + annealing** — plus pin-accurate clustering that Altium’s generic arrange does not do.

---

## Setup (full guide)

### 1. Prerequisites

Install these on Windows first:

| Software | Why |
|----------|-----|
| **[Altium Designer](https://www.altium.com/)** (AD24 or similar) | Host for the extension |
| **[Visual Studio 2022 Build Tools](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022)** with workload **“.NET desktop build tools”** | Compiles the C# extension |
| **[Git](https://git-scm.com/)** | Clone this repo |
| **[Python 3.10+](https://www.python.org/downloads/)** (optional) | Only if you want the Cursor MCP server |

Confirm MSBuild exists (typical path):

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" -version
```

If that fails, install Build Tools and tick **.NET desktop build tools**.

### 2. Clone the repo

```powershell
git clone https://github.com/Sakuna-Nagodavithana/Altium-MCP-Placement.git
cd Altium-MCP-Placement
```

### 3. Point the build script at your Altium install (if needed)

Open `build-and-deploy.ps1` and check the paths near the top:

```powershell
$AltiumRoot = if (Test-Path "E:\Altium") { "E:\Altium" } else { "C:\Program Files\Altium\AD24" }
$AltiumGuid = "Altium Designer {2E34A225-0C0D-424C-B915-02F461E29B71}"
```

- Set `$AltiumRoot` to your real Altium folder (must contain `System\Altium.SDK.dll`).
- `$AltiumGuid` must match the folder name under `C:\ProgramData\Altium\` (open that folder in Explorer and copy the exact `Altium Designer {…}` name if yours differs).

The script copies Altium/DevExpress reference DLLs into `EasyEDA-Loader\Assemblies\` (gitignored) and installs the built extension into:

`C:\ProgramData\Altium\<AltiumGuid>\Extensions\Altium-MCP-Placement\`

### 4. Build and install the extension

**Close Altium Designer completely** (otherwise the DLL is locked).

From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1
```

You should see something like:

```text
==> Preparing Altium reference assemblies...
==> Building with ...\MSBuild.exe
  EasyEDA-Loader -> ...\Altium-MCP-Placement.dll
==> Installing into Altium Extensions folder...
Done. Restart Altium Designer ...
```

### 5. Enable the extension in Altium

1. Start **Altium Designer**.
2. If prompted, enable **Altium-MCP-Placement**.
3. Or: **DXP → Extensions and Updates** (or **Preferences → Extensions**) and make sure **Altium-MCP-Placement** is installed/enabled.
4. Open a PCB project (schematic + `.PcbDoc`).

### 6. Open the control panel

From a PCB or schematic document:

- Menu: **Tools** (or **File**) → look for **Altium MCP** / **MCP Panel** / **EasyEDAMcpPanel**
- Or run command **`EasyEDAMcpPanel`** if your menus differ

You should see the panel with steps **1 Prep → 2 Place → 3 Verify** and the green **Smart Place** button.

### 7. First-time recommended workflow

With your project PCB open:

1. **Board Needs** → review stackup / vias / heat → optionally **Apply stackup + via sizes**
2. **Stackup Advisor** → **Use This (Save + Open)** → in Altium: **Design → Layer Stack Manager → File → Load Stackup From File** (from `Documents\AltiumEE\recommended-stackups\`)
3. **Setup Net Classes & Rules**
4. **Smart Place (recommended)** — or use **Floorplan Preview** then Auto-Place manually
5. Nudge RF / antenna / connectors by hand
6. Route (priority: RF → HighSpeed → PWR → Logic)
7. **Stitch Vias** → **Full PCB DRC**

Exports and plans are written to:

`%USERPROFILE%\Documents\AltiumEE\`  
(`connectivity.json`, `placement_plan.json`, `fab-preference.json`, …)

### 8. Optional — MCP server for Cursor / AI agents

Only needed if you want agents to call tools against the exported design.

```powershell
cd altium-mcp
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
copy .env.example .env
# Edit .env — for online mode generate a key:
#   powershell -ExecutionPolicy Bypass -File .\scripts\generate-api-key.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

- MCP URL: `http://127.0.0.1:8787/mcp`
- Health: `http://127.0.0.1:8787/health`

In the Altium MCP panel, expand **MCP export & connection**, confirm the URL/key match `.env`, and use **Start Server** if the panel manages it.

Cursor config snippets: see [`altium-mcp/AGENT-CONNECTION.md`](altium-mcp/AGENT-CONNECTION.md).

**Never commit `.env`.** There is **no ngrok authtoken** (and no real API key) in this GitHub repo — only placeholders in `.env.example`. Put your own token on your machine with `ngrok config add-authtoken`.

### 8b. Optional — expose MCP online with ngrok

Local mode (§8) is enough for Cursor on the same PC. Use ngrok only if a remote agent must reach your MCP.

Full write-up: [`altium-mcp/SETUP-ONLINE.md`](altium-mcp/SETUP-ONLINE.md). Short version:

1. Create a free ngrok account: https://dashboard.ngrok.com/signup  
2. Copy your **authtoken** from: https://dashboard.ngrok.com/get-started/your-authtoken  
3. Install + authenticate (once):

```powershell
cd altium-mcp
powershell -ExecutionPolicy Bypass -File .\scripts\install-ngrok.ps1
ngrok config add-authtoken YOUR_NGROK_TOKEN
```

4. Create `.env` from example, generate an MCP API key, set transport for HTTP:

```powershell
copy .env.example .env
powershell -ExecutionPolicy Bypass -File .\scripts\generate-api-key.ps1
# Paste the key into MCP_API_KEY= in .env
# Set MCP_TRANSPORT=streamable-http
```

5. Two terminals:

```powershell
# Terminal 1 — MCP server (online)
powershell -ExecutionPolicy Bypass -File .\scripts\start-server-online.ps1

# Terminal 2 — public tunnel
powershell -ExecutionPolicy Bypass -File .\scripts\start-ngrok.ps1
```

6. Copy the `https://….ngrok-free.app` URL from ngrok → set `MCP_PUBLIC_URL=` in `.env` → restart Terminal 1.  
7. Point Cursor at that URL + Bearer key (see `cursor-mcp-remote.example.json`).

Notes:
- Free ngrok URLs change when you restart — update `.env` and Cursor each time.  
- Authtoken lives in ngrok’s local config (`%LOCALAPPDATA%\ngrok\ngrok.yml`), **not** in this repo.  
- Do not paste your authtoken into README, commits, or chat logs.

### Setup troubleshooting

| Problem | Fix |
|---------|-----|
| `MSBuild not found` / build fails | Install VS 2022 Build Tools + **.NET desktop build tools** |
| Copy Altium DLL failed | Fix `$AltiumRoot` in `build-and-deploy.ps1` |
| Deploy folder missing / wrong PC | Fix `$AltiumGuid` to match `C:\ProgramData\Altium\` |
| Extension not in menus | Restart Altium; enable extension; check `ProgramData\...\Extensions\Altium-MCP-Placement\` has the DLL |
| DLL locked / deploy fails | Close Altium completely, then re-run the script |
| Smart Place / export errors | Open the project `.PcbDoc` first, then open the MCP panel |
| Stackup “Use This” but board unchanged | You must load the `.stackupx` in **Layer Stack Manager** manually (Altium has no reliable API load) |

---

## Control panel workflow (after setup)

Open **Altium MCP Control Panel** from the extension menus. The UI is three steps:

### 1 — Prep (stackup & rules)
- **Board Needs** — auto-detect MCU / LoRa / ESP / USB / LDO / buck → pick 2L / 4L / 6L, impedance ballpark, via sizes, thermal checklist  
- **Stackup Advisor (JLCPCB)** — load curated stackup; default for ESP/LoRa: **4L 1.6 mm JLC04161H-7628** (Mid1 GND, Mid2 3v3)  
- **Setup Net Classes & Rules**  
- Expert Process Guide / Route Priority (RF → HighSpeed → PWR → Logic)

### 2 — Place
- **Smart Place (recommended)** — full pipeline in one click  
- Or manually: **Floorplan Preview** → **Auto-Place All** → **Fanout Decap Vias** → **Create Rooms** → **Optimize Board**

### 3 — Verify
- **Full PCB DRC (Errors)** — Jump / Re-Run  
- **Stitch Vias (RF / Clocks)** after routing  

**You still do by hand:** antenna keepout, exact connector seating, EPAD copper pours for heat, interactive RF matching. Smart Place is a strong first pass, not a finished RF board.

---

## Feature details

### Board Needs (rules, not AI)
Scans designators + comments and recommends:
- **2L** — simple digital only  
- **4L JLC7628** — ESP / LoRa / USB / MCU (typical)  
- **6L** — dense / many rails / fine-pitch  

Also lists impedance hints (RF ~50 Ω, USB ~90 Ω diff — **verify on [JLCPCB impedance](https://jlcpcb.com/impedance)**), via sizes per net class, and **heat hotspots** (buck / LDO / ESP / RF PA) with what to do (thermal vias, copper pours, keep RF away from inductors).

Pros find heat via datasheet Pd × θJA and IR on prototypes; we flag likely hot parts from the BOM.

### Floorplan Preview
- Board outline: **Auto** / **Manual mm** / **DXF** polyline  
- **10 layouts** (RF corners, L→R / R→L flow, connectors top/bottom, compact, …)  
- ★ = lowest estimated IC/connector wirelength + RF↔power separation  
- Apply moves ICs + connectors; optional Mech1 outline; optional Auto-Place after  

### Smart Place pipeline
1. Board Needs → save stackup preference + via profile  
2. Best floorplan by score → apply  
3. Pin-accurate Auto-Place for all IC clusters  
4. Fanout decap / power vias  
5. Create Rooms (+ Unions)  

### Placement engine (already strong locally)
- Passive ownership per IC, role tags (decoupling / power / signal)  
- Pin-accurate placement near real pad XY  
- RF matching Pi/T geometry when transceiver detected  
- Collision / keepout deconflict  
- Optional force-directed + SA packing (~28 mil spacing)

### Rooms & fanout
- `MCP - Room *` confinement rules + `MCP_Room_*` component classes  
- Fanout uses PWR/Logic/RF via styles from the rules profile (updated by Board Needs)

### Full DRC
Runs Altium’s batch DRC plus MCP checks; opens a results window with **Jump** and **Re-Run**.

---

## EasyEDA / JLCPCB part loader

With a schematic open:

1. Open the EasyEDA Loader dialog (extension menu).  
2. Search by LCSC (e.g. `C2040`) or use the BOM builder.  
3. Libraries land under `%USERPROFILE%\Documents\AltiumEE`.

---

## MCP server notes

Full install steps are in **Setup §8** above. Local URLs:

- `http://127.0.0.1:8787/mcp`
- `http://127.0.0.1:8787/health`

See also `altium-mcp/SETUP-ONLINE.md` and `altium-mcp/AGENT-CONNECTION.md`.

User exports live under `%USERPROFILE%\Documents\AltiumEE\` (`connectivity.json`, `placement_plan.json`, `floorplan_plan.json`, `fab-preference.json`, `board_needs_report.txt`, …) — not in git.

---

## Repo layout

| Path | Purpose |
|------|---------|
| `EasyEDA-Loader/` | C# Altium extension (placement, floorplan, DRC, stackup, UI) |
| `EasyEDA-Loader/Floorplan/` | Board outline / DXF / variants / preview / apply |
| `altium-mcp/` | Python MCP server + helpers |
| `fab/recommended/` | Curated JLCPCB stackups + `.RUL` (see `fab/README.md`) |
| `REPLACE-THIS-DLL/` | Extension `.Ins` / `.rcs` menu defs (built DLL is gitignored) |
| `build-and-deploy.ps1` | Build + install into Altium |
| `Assets/` | Docs screenshots |
| `Standalone/` | Optional WPF preview without Altium |

### Notable C# modules

| File | Role |
|------|------|
| `SmartPlacePipeline.cs` | One-click place chain + floorplan wirelength scorer |
| `BoardNeedsAdvisor.cs` | Stackup / impedance / vias / thermal from parts |
| `PcbStackupAdvisor.cs` | JLCPCB catalog + preference |
| `Floorplan/*` | Preview GUI, DXF outline, apply |
| `PlacementRooms.cs` | Altium confinement rooms |
| `DecapFanout.cs` | Power/GND fanout vias |
| `IcClusterRunner.cs` | Auto-Place / optimize entry points |
| `ForceDirectedOptimizer.cs` | Force + SA packing |
| `PcbFullDrc.cs` / `DrcResultsWindow.cs` | Full DRC UI |
| `ViaStitcher.cs` | RF/clock GND stitch |
| `McpControlWindow.xaml.cs` | Control panel (step cards + Smart Place) |
| `ExpertDesignPlaybook.cs` | Expert schematic→fab coaching |

---

## Security

Do **not** commit:

- `.env` / API keys / ngrok tokens  
- `mcp-settings.json`  
- Proprietary board exports  
- Altium or DevExpress DLLs  

`.gitignore` already excludes `*.dll`, `Assemblies/`, `Deploy/`, large fab clones, and local secrets.

---

## License

GNU GPL v3 — see [LICENSE](LICENSE).

## Credits

- EasyEDA / JLCPCB library patterns (inspired by [easyeda2kicad.py](https://github.com/uPesy/easyeda2kicad.py))  
- JLCPCB stackup libraries under `fab/` (ayberkozgur / gsuberland community data — see `fab/README.md`)  
- Placement ideas: Altium rooms / OPV docs; academic force-directed + SA PCB placers  
