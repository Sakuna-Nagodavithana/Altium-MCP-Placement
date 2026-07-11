# Altium MCP — Examples

Every example below assumes the **net fanout audit** from [topology.md](topology.md) — not `trace_connection` alone.

## Example 1: P3 → IC2 (ESP32_B, antenna to LNA)

### Tools

```
get_pin_connection("P3", "1")
trace_connection(from_designator="P3", to_designator="IC2", from_pin="1", to_pin="1")
get_net("NetP3_1")
get_net("NetIC2_1")
get_component("R30")
```

### Series path (trace_connection)

```
P3.1 (NetP3_1) → R30 (0 Ω) → IC2.1 (NetIC2_1)
```

### Parallel (get_net on NetP3_1 / NetIC2_1)

If `get_net` shows only P3 + R30 on `NetP3_1` and R30 + IC2 on `NetIC2_1`, no extra shunts on those nodes — still confirm with `get_net`, do not assume.

### Diagram

```mermaid
flowchart LR
  P3 -->|NetP3_1| R30
  R30 -->|NetIC2_1| IC2
```

---

## Example 2: P1 → U3 (ESP32_A, RF switch)

**Wrong:** run `trace_connection` once and report “P1 → L6 → C26 → U3” only.

**Right:** series path + `get_net` on **each** net in the chain + `get_component` on every cap.

### Step A — Endpoint nets

```
get_pin_connection("P1", "1")   → NetC28_2
get_pin_connection("U3", "5")   → (target pin net)
```

### Step B — Series trace

```
trace_connection("P1", "U3", from_pin="1", to_pin="5")
```

Series: P1 → L6 (9.1 nH) → C26 (39 pF) → U3.5

Nets along series (call `get_net` on **each**):

- `NetC28_2`
- `NetC26_2`
- (and any net between C26 and U3.5 from trace steps)

### Step C — Parallel shunts (from get_net, not trace_connection)

**`get_net("NetC28_2")`** — antenna node:

| Pin | Part |
|-----|------|
| P1.1 | Antenna |
| L6.2 | Series inductor |
| C28.2 | Shunt leg |

**`get_component("C28")`** → pin 1 = GND, pin 2 = NetC28_2 → **3.3 pF shunt to GND**

**`get_net("NetC26_2")`** — after L6:

| Pin | Part |
|-----|------|
| L6.1 | Series |
| C27.2 | Shunt leg |
| C26.2 | Series cap |

**`get_component("C27")`** → pin 1 = GND → **3.3 pF shunt**

Always report shunts **in a separate section**, not buried in the series list.

### Step D — Other U3 pins (matching network)

`trace_connection` to U3.1 may show GND hops. Prefer `get_net` on:

- `NetC23_2` (U3.1, L5, C23.2) + `get_component("C23")` for GND shunt
- `NetC29_2`, `NetC33_2`, `NetC17_2` — same pattern

---

## Example 3: Decoupling caps on 3v3 (not on trace_connection path)

User asks: “What decouples U1?” or “What’s on 3v3?”

`trace_connection` will **not** show C10, C12, C13, … — they are parallel on the power rail.

### Tools

```
get_component("U1")              → find pins on net "3v3"
get_net("3v3")                   → lists ALL pins on rail (17 caps + IC pins + …)
```

For each `C*` in `get_net("3v3").connections`:

```
get_component("C10")   → 3v3 + GND, comment "0.1 uf"  → decoupling
get_component("C12")   → 3v3 + GND, comment "1 uf"    → bulk/decoupling
```

Report as:

```
### Decoupling on 3v3 (parallel, not in series path)
C5, C6, C7, C10, C12, C13, C14, C15, C16, … — each: one pin 3v3, one pin GND
(near U1 / on sheet ESP32_A — cite get_net membership, not PDF placement)
```

---

## Example 4: 3V3 to IC1 VDDA (ESP32_A, filtered analog supply)

User asks: “How does 3V3 reach VDDA_2 on IC1?”

**Wrong:** `trace_connection` from a random part on `3v3` to IC1 and stop, or only list caps on the main `3v3` rail.

**Right:** find which IC1 pins are **not** directly on `3v3`, then audit their net.

### Tools

```
get_component("IC1")
get_pin_connection("IC1", "2")
get_net("NetC11_1")
get_component("L2")
get_component("C11")
get_net("3v3")
trace_connection("L2", "IC1", from_pin="1", to_pin="2")
```

### MCP result (Duel_Esp_LoRa)

- **IC1** = ESP32-S3FH4R2
- **IC1 pins 20, 29, 46, 55, 56** → directly on **`3v3`** (digital VDD, separate path)
- **IC1 pins 2 and 3** → on **`NetC11_1`** (`VDD3P3_1`, `VDD3P3_2`); **VDDA_2** is pin **56** on **`3v3`** directly

### Series path (filtered VDDA)

```
3v3 → L2 pin 1
L2 (2 nH) through → L2 pin 2 on NetC11_1
NetC11_1 → IC1 pin 2 and IC1 pin 3
```

### Parallel on NetC11_1

| Part | Role |
|------|------|
| **C11** (0.1 µF) | pin 1 on NetC11_1, pin 2 on GND — **local VDDA decoupling** |
| **L2 pin 2** | fed from 3v3 through L2 |
| **IC1 pin 2, 3** | VDDA inputs |

### Decoupling on main 3v3 (parallel, not in VDDA series path)

`get_net("3v3")` → C10, C12, C13, C14, C15, C16, C21, … each **3v3 ↔ GND**. These decouple the **rail**; **C11** decouples the **filtered node** after L2.

---

## Example 5: 3v3 to IC1 `VDD3P3_1` — shared net names

User asks: “What is between 3V3 and VDD3P3_1?”

**Wrong:** Expect C8, C9, C10 to have different net names because they are drawn in a line on the schematic.

**Right:** They are **all on net `3v3`** (parallel decoupling). Only **L2** splits to **`NetC11_1`**.

```
trace_power_path("IC1", "VDD3P3_1", source_rail="3v3")
get_component("C8")
get_component("C9")
get_component("C10")
get_component("C11")
get_component("L2")
get_net("3v3")
```

### MCP nets (Duel_Esp_LoRa)

| Ref | Value | Pin on rail / node | Other pin |
|-----|-------|-------------------|-----------|
| C8 | 10 µF | **`3v3`** | GND |
| C9 | 1 µF | **`3v3`** | GND |
| C10 | 0.1 µF | **`3v3`** | GND |
| L2 | 2 nH | pin 1 **`3v3`** → pin 2 **`NetC11_1`** | — |
| C11 | 0.1 µF | **`NetC11_1`** | GND |
| IC1.2 | VDD3P3_1 | **`NetC11_1`** | — |

Same net name **`3v3`** for C8/C9/C10 is **correct**. `rail_decoupling_count` includes **all** board caps on `3v3`, not only C8/C9/C10.

### `trace_power_path` summary

| Field | Value |
|-------|-------|
| `connection_type` | `filtered_path` |
| `series_components` | L2 (2 nH) |
| `local_decoupling_count` | 1 (C11 on NetC11_1) |
| `rail_decoupling_count` | all caps on `3v3` (includes C8, C9, C10, …) |

### Direct pin (e.g. `VDD3P3_RTC` pin 20 on `3v3`)

| Field | Value |
|-------|-------|
| `connection_type` | `direct_to_rail` |
| `local_decoupling_count` | 0 |
| `rail_decoupling_count` | all caps on `3v3` |

Report **`local_decoupling`** and **`rail_decoupling`** separately. Do not invent a unique net per cap on the rail.

---

## Example 6: User provides PDF only

1. Acknowledge PDF for sheet/block context only.
2. `reload_connectivity` + `get_connectivity_status`.
3. Run full workflow from [topology.md](topology.md).
4. Answer from MCP; note exported net names vs PDF labels (`NetP3_1` vs `RX_ESP`).
