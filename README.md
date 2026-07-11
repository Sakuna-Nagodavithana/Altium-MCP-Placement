# Altium MCP Placement + EasyEDA Loader

Altium Designer extension that:

1. **Imports JLCPCB / EasyEDA parts** (symbol, footprint, 3D) into Altium libraries  
2. **Exports schematic + PCB connectivity** for AI agents (MCP)  
3. **Auto-places** passives around ICs (pin-accurate clusters)  
4. **Optimizes placement** with force-directed layout + simulated annealing (including 90° rotations)  
5. **Sets up net classes** (RF / PWR / HighSpeed / Logic) with design rules  

This repo is based on the original [EasyEDA Loader](https://github.com/) idea for Altium, extended with MCP placement tooling.

---

## Requirements

- **Altium Designer** (tested with AD24)  
- **Visual Studio 2022 Build Tools** with the “.NET desktop build tools” workload  
- **Python 3.10+** (for the MCP server under `altium-mcp/`)  
- Windows  

Altium and DevExpress DLLs are **not** shipped here (licensing). The build script copies them from your Altium install.

---

## Quick start — build & install the extension

1. Close Altium (it locks the extension DLL).  
2. From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1
```

3. Restart Altium Designer.  
4. Enable **Altium-MCP-Placement** if prompted (Extensions).

The script:

- Copies Altium / DevExpress reference assemblies from your AD install  
- Builds `EasyEDA-Loader` → `Altium-MCP-Placement.dll`  
- Installs into your Altium `Extensions` folder  

If your Altium path differs, edit the paths at the top of `build-and-deploy.ps1`.

---

## EasyEDA / JLCPCB part loader

With a schematic open:

1. Open the **EasyEDA Loader** dialog (extension menu).  
2. Search by LCSC part number (e.g. `C2040`) or use the BOM builder.  
3. Add to library — creates/updates libraries under `%USERPROFILE%\Documents\AltiumEE`.

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

**Never commit `.env`.** Use `.env.example` only. Rotate any key that was ever shared or committed by mistake.

See:

- `altium-mcp/SETUP-ONLINE.md` — ngrok / remote agents  
- `altium-mcp/AGENT-CONNECTION.md` — Cursor MCP config snippets  

---

## Auto-placement & board optimize

In the MCP Placement panel (Altium):

1. Export connectivity (or let the panel do it).  
2. **Auto-Place All Components** — IC clusters, decoupling, pin-facing rotations.  
3. **Optimize Board (Force + Anneal)** — HPWL springs, then simulated annealing with translate / ±90° rotate / swap.  

Default optimize clearance is ~28 mil (middle ground between assembly-tight and sparse).  
Ctrl+Z undoes placement changes in Altium.

---

## Repo layout

| Path | Purpose |
|------|---------|
| `EasyEDA-Loader/` | C# Altium extension source |
| `altium-mcp/` | Python MCP server + placement helpers |
| `Assets/` | Screenshots for docs |
| `Standalone/` | Optional WPF preview app (no Altium) |
| `tools/` | Parity / smoke scripts |
| `build-and-deploy.ps1` | Build + install into Altium |

User-generated files stay on your machine under `%USERPROFILE%\Documents\AltiumEE\` (`connectivity.json`, `placement_plan.json`, `mcp-settings.json`, etc.) — they are not part of this repo.

---

## Security notes

Do **not** put in git:

- `.env` / API keys / ngrok tokens  
- `mcp-settings.json`  
- Personal board exports with proprietary designs (unless you intend to publish them)  
- Altium or DevExpress proprietary DLLs  

---

## License

GNU GPL v3 — see [LICENSE](LICENSE).

## Credits

Inspired by [easyeda2kicad.py](https://github.com/uPesy/easyeda2kicad.py) and Altium library-loader API patterns.
