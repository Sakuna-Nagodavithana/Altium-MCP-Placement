---
name: altium-schematic-mcp
description: >-
  Queries real Altium schematic connectivity through the altium-schematic MCP server
  (get_net, get_component, trace_connection). Runs mandatory net fanout audits for
  decoupling caps, shunt caps, and parallel branches — trace_connection alone is
  incomplete. For power/decoupling counts (3v3 to VDDP3P, VDDA), use trace_power_path from the IC pin.
  For PCB placement, use build_ic_placement_plan / generate_all_ic_cluster_plan (pin_near + series chains)
  and update_placement_move to fix positions before Apply in Altium.
  For PCB validation, use get_pcb_design_summary, list_pcb_tracks, list_pcb_planes after export.
  Use when the user asks how parts connect, power rails, pin-specific decoupling,
  RF matching, signal paths between designators, IC cluster placement, PCB trace widths/planes,
  EasyEDA Loader, Altium MCP, or attaches schematic PDFs. MCP data first; PDFs are reference only.
disable-model-invocation: true
---

# Altium Schematic MCP

Select MCP server **altium-schematic** (or remote ngrok URL). Read [reference.md](reference.md) for connection JSON.

## Golden rule

**PDF schematics are reference only.** For connectivity, net membership, and paths between parts, **always call MCP tools first**. PDFs can mislead; MCP reads compiled Altium net data from `connectivity.json`.

If the user attaches a PDF (e.g. `Duel_ESP32.pdf`) **and** MCP is available, use the PDF for context (sheet names, block layout) but **cite connections from MCP tool results**.

## Prerequisites

1. Altium Designer open with project loaded (`Duel_Esp_LoRa` or current project).
2. EasyEDA Loader extension installed; MCP server running (auto-starts ~3s after Altium loads).
3. Export healthy: `get_connectivity_status` → `has_usable_nets: true`, `schema_version` ≥ 5 (PCB tracks/planes when exported from Altium panel).

If `has_usable_nets` is false, tell the user to open the MCP panel in Altium once (auto-export runs) or wait for background export, then call `reload_connectivity`.

## MCP connection

Read [reference.md](reference.md) for URLs and agent config templates.

**Local (same PC as Altium):**

- MCP URL: `http://127.0.0.1:8787/mcp`
- Health: `http://127.0.0.1:8787/health`
- Agent info: `http://127.0.0.1:8787/api/info`
- Auth header: `Authorization: Bearer <MCP_API_KEY>` (key in `Documents/AltiumEE/mcp-settings.json` → `ApiKey`)

**Remote (other AI agent / cloud):**

- Run ngrok: `altium-mcp/scripts/start-ngrok.ps1`
- Set `MCP_PUBLIC_URL` in `altium-mcp/.env`, restart MCP server
- MCP URL: `https://<your-subdomain>.ngrok-free.dev/mcp` (same Bearer header; URL changes when ngrok restarts)

## Critical: `trace_connection` is not enough

`trace_connection` = **shortest series path only**. Decoupling caps, shunt caps, and other parallel parts share a **net** with the signal but are **not** on that path.

**You must run the net fanout audit** (full steps in [topology.md](topology.md)) before answering. Skipping it causes missed C* caps — the most common agent failure.

## Workflow

Copy and track:

```
- [ ] reload_connectivity
- [ ] get_connectivity_status (confirm has_usable_nets)
- [ ] get_pin_connection on start/end pins → note net names
- [ ] trace_connection(from, to, from_pin?, to_pin?) → series path only
- [ ] NET FANOUT AUDIT (mandatory):
      for each net in path + endpoint nets → get_net(name)
      for each passive on those nets → get_component → classify shunt/series/decap
- [ ] If IC involved → get_net on power rails (3v3, VCC, …) for decoupling caps
- [ ] Answer: Series | Parallel/shunts | Decoupling — then diagram
```

### Step 1 — Refresh data

Call `reload_connectivity` with no args (uses default export file).

### Step 2 — Verify export

Call `get_connectivity_status`. Require:

- `schema_version` ≥ 5 (4 = components/nets only; 5 adds PCB routing, planes, stackup)
- `has_usable_nets: true`
- `pins_with_net` > 0

### Step 3 — Answer connectivity questions

| User asks | MCP tools |
|-----------|-----------|
| What net is on U3 pin 5? | `get_pin_connection("U3", "5")` |
| What is on net X? | `get_net("X")` |
| How does P1 connect to U3? | `trace_connection("P1", "U3", from_pin="1")` plus `get_net` for shunts |
| **How does 3v3 reach IC1 VDDP3P / VDDA? How many decoupling caps?** | **`trace_power_path("IC1", "VDDP3P", source_rail="3v3")`** — start from the **pin**, not the rail |
| List RF parts | `list_components("ESP32")` or `get_sheet(...)` |
| **Cluster passives near IC pins** | `build_ic_placement_plan("IC1")` → preview; `generate_ic_placement_plan` / `generate_all_ic_cluster_plan` → write `placement_plan.json` |
| **Fix a bad placement target** | `get_placement_plan` → `update_placement_move("C10", x_mils, y_mils)` → user clicks Apply in Altium |
| **PCB validation (widths, planes, GND pour)** | `get_pcb_design_summary`, `list_pcb_tracks`, `list_pcb_planes` (requires fresh Export Now / panel open) |

### Placement: pin_near + series chains

Default layout **`pin_near`** places each support passive on a ray from the IC toward its linked pin.

When **`trace_connection`** shows a **series chain** (e.g. IC → R → C → R on local nets), the planner detects it and uses **`pin_chain`** layout: parts are placed **in order** along the same pin ray (closest to IC first).

Plan JSON includes:
- `chains` — detected series groups
- `verification` — per-part pin/net/ spacing checks (`all_ok`, `warn_count`)
- move fields: `primary_ic_pin`, `chainId`, `chainIndex`, `chainMembers`, `method` (`pin_near` or `pin_chain`)

**MCP cannot move PCB directly** — it edits `placement_plan.json`. User applies with Altium **Apply Plan** or **Cluster** buttons.

### PCB validation export (schema 5)

After export, `connectivity.json` → `pcb` includes:
- `routing.tracks[]` — net, layer, width (mils/mm), segment endpoints
- `routing.vias[]` — size, hole, layers
- `planes.polygons[]` — copper pours (GND, 3v3, etc.)
- `stackup.layers[]` — layer names/thickness
- `validation` — track width histogram, ground/power plane net list

Use **`get_pcb_design_summary`** first for counts; drill into **`list_pcb_tracks`** / **`list_pcb_planes`** for details.

### Power pin rule (fixes missed decoupling caps)

**Do not** trace `3v3` → `VDDP3P` as two nets — many IC pins share one rail net.

1. Pick the **specific IC pin** (name like `VDDP3P` or pin number).
2. Call **`trace_power_path(designator, pin, source_rail="3v3")`**.
3. Read **`local_decoupling_count`** (caps on filtered nets, e.g. C11 on NetC11_1) and **`rail_decoupling_count`** (caps on shared 3v3).
4. If **`connection_type`** is `direct_to_rail`, all rail caps serve that pin — you cannot split them without a series filter net.

Details: [topology.md](topology.md#power-pin-audit-trace_power_path)

### Shared power net names (important)

On schematics, a **visual** power path (C8 → C9 → C10 → L2 → C11 → IC pin) often uses **one compiled net name** for the whole rail side — e.g. **`3v3`** for C8, C9, C10, and L2 pin 1 **all together**.

| What the drawing looks like | What MCP export shows |
|------------------------------|------------------------|
| Several caps “in series” along the 3V3 wire | Each cap: one pin **`3v3`**, other pin **`GND`** |
| Inductor input | L2 pin 1 on **`3v3`** |
| After the inductor | L2 pin 2 on **`NetC11_1`**; C11 and IC pin on **`NetC11_1`** |

**Do not** expect a different net label per cap on the rail. **`get_net("3v3")`** lists **every** decoupling cap on that rail (C8, C9, C10, C5, C6, …). That is **correct** net connectivity.

For a filtered pin (e.g. `VDD3P3_1` on `NetC11_1`):

- **`trace_power_path`** → series (**L2**), **`local_decoupling`** (C11 on `NetC11_1`), **`rail_decoupling`** (all caps on `3v3`, including C8/C9/C10)
- Use **`get_component`** on each cap for values (10 µF, 1 µF, 0.1 µF, …)
- Use the PDF/schematic only to **describe layout order**; MCP proves **net membership**

Only **`NetC11_1`**-style names appear when Altium names a net after a local node (often a cap pin). The rail side stays **`3v3`**.

### Step 4 — Series path (`trace_connection`)

Returns `steps`, `components_along_path`, `nets`, `mermaid`. Treat this as **input to the audit**, not the final answer.

Shortest path may route through **GND** (C28 → GND → C23). That is net-level connectivity, **not** the main RF series chain.

### Step 5 — Net fanout audit (mandatory)

See [topology.md](topology.md). Summary:

1. **`get_net(net_name)`** — lists **every** pin on that net (P1, L6, C28.2 together on `NetC28_2`).
2. **`get_component("C28")`** — both pins: signal net + GND → **shunt cap** (always mention).
3. **Power rails** — `get_net("3v3")` finds all bulk/decoupling caps (often 10+ parts); list relevant ones near the IC or on the sheet.

**Decoupling cap pattern:** `C*`, comment like `100nF` / `0.1 uf` / `1 uf`, one pin on `3v3` (or VCC), other on `GND`.

**Shunt cap pattern:** one pin on signal net from step 4, other pin on `GND`.

**Pin names:** Export often has pin **numbers** only (no `VDDA_2` label). Use `get_pin_connection` + schematic context; map symbol names to pin numbers when the user gives them.

## Output format

Use this structure:

```markdown
## Connection: {FROM} → {TO}

**Sheet:** (from get_component)
**Connected:** yes/no

### Series path
(from trace_connection — passives in line only)

### Parallel on shared nets (from get_net — required)
- NetC28_2: P1.1, L6.2, C28.2 — C28.1 → GND (3.3 pF shunt)
- NetC26_2: L6.1, C27.2, C26.2 — C27.1 → GND

### Decoupling / power (from get_net on supply rails)
- 3v3: C10, C12, C13, … (pin on 3v3, other pin on GND)

### Diagram
(series chain + parallel branches to GND, not a single line)

**Source:** Altium MCP export at {exportedAt from status}
```

## Do not

- Answer after **`trace_connection` only** — you will miss decoupling and shunt caps.
- Say “A connects to B” when `get_net` shows more parts on the same net.
- Expect **unique net names** for every cap along a drawn 3V3 line — C8, C9, C10, L2 pin 1 are often **all `3v3`**.
- Treat **`rail_decoupling`** listing many caps as wrong — that is correct for a shared power net.
- Guess nets or caps from PDF wire visuals.
- Hide GND shunts or power decoupling caps found in `get_net` / `get_component`.
- Use placement/proximity when MCP net data exists (except **`build_ic_placement_plan`** / cluster tools which use exported nets + PCB coords).
- Edit the schematic via MCP (read-only). Placement plan edits via **`update_placement_move`** are OK.

## Additional resources

- **Net fanout audit (read this):** [topology.md](topology.md)
- Tool list and JSON config: [reference.md](reference.md)
- Worked examples (P3→IC2, P1→U3, 3v3 decoupling): [examples.md](examples.md)
