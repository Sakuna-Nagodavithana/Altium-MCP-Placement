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

## Requirements

- **Altium Designer** (tested with AD24)
- **Visual Studio 2022 Build Tools** — “.NET desktop build tools” workload
- **Python 3.10+** (optional — only for `altium-mcp/`)
- Windows

Altium / DevExpress DLLs are **not** in this repo (licensing). `build-and-deploy.ps1` copies them from your Altium install.

---

## Quick start — build & install

1. **Close Altium** (it locks the extension DLL).
2. From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1
```

3. Restart Altium Designer.
4. Enable **Altium-MCP-Placement** if prompted.

The script builds `Altium-MCP-Placement.dll`, bundles `fab/recommended` stackups, and installs into your Altium `Extensions` folder. Edit paths at the top of `build-and-deploy.ps1` if your Altium install location differs.

---

## Control panel workflow (recommended)

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

## MCP server (AI connectivity)

```powershell
cd altium-mcp
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
copy .env.example .env
# Edit .env — generate a key with scripts\generate-api-key.ps1 for online mode
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

- Local MCP URL: `http://127.0.0.1:8787/mcp`  
- Health: `http://127.0.0.1:8787/health`  

**Never commit `.env`.** See `altium-mcp/SETUP-ONLINE.md` and `altium-mcp/AGENT-CONNECTION.md`.

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
