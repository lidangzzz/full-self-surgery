# Full Self Surgery — Agent HTTP API Reference

> **Base URL**: `http://127.0.0.1:8765` (loopback only; starts automatically in Play mode)
> **Protocol**: HTTP/1.1 + JSON (UTF-8). State snapshots refresh ~4Hz; commands execute on the next Unity frame.

## Quick Start

```bash
curl http://127.0.0.1:8765/api/state | python3 -m json.tool | head -30
curl http://127.0.0.1:8765/api/scenarios | python3 -c "import json,sys;print(len(json.load(sys.stdin)['scenarios']),'scenarios')"
curl -X POST http://127.0.0.1:8765/api/scenario -H 'Content-Type: application/json' -d '{"id":"heart"}'
sleep 3
curl http://127.0.0.1:8765/api/state | python3 -c "import json,sys;d=json.load(sys.stdin);print(d['scenario'],len(d['arms']),'arms')"
```

---

## 1. `GET /api/state` — Full State Snapshot

**Response fields**:

| Field | Type | Description |
|---|---|---|
| `timestamp` | string | UTC ISO 8601 snapshot time. Unchanged between GETs = stalled |
| `frame` | int | Unity engine frame count (should increment) |
| `scenario` | string | Current scenario ID (e.g., `"vessel"`, `"s51"`) |
| `scenarioTitle` | string | Human-readable scenario name |
| `paused` | bool | Global pause state (freezes executors + SOFA stepping) |
| `editorPaused` | bool | Editor play-mode paused (auto-cleared by watchdog) |
| `backend` | string | Physics backend name (`SofaUnity (SOFA co-simulation)`) |
| `backendStatus` | string | Backend details (version/scene/running state) |
| `stitchesDone` / `stitchesTotal` | int | Current scenario suture progress |
| `sofaTime` | float | SOFA native simulation clock (seconds). Incrementing = solver alive |
| `sofaFps` | float | SOFA native solver FPS (dedicated thread) |
| `sofaSteps` | long | Completed native steps (monotonic counter) |
| `sofaStepMs` | long | Last native step duration (ms). Growing = solver hang (main loop unaffected) |
| `liverDelta` | float | Liver FEM max vertex displacement (m) — tissue tension proxy |
| `arms[]` | array | Per-arm real-time state (below) |
| `tasks[]` | array | Task (suture batch) progress with per-stitch entry/exit points |
| `tissue` | object | `{live, maxDelta, boundsMin[3], boundsMax[3]}` — SOFA tissue mirror |

**`arms[i]` fields**:

| Field | Type | Description |
|---|---|---|
| `index` | int | Arm ordinal (**use as `arm` parameter in commands**) |
| `name` | string | `"Arm_1"`, `"Arm_2"`, ... |
| `role` | string | `Suturer` (holds needle) / `Stabilizer` / `Idle` |
| `dof` | int | Degrees of freedom (3-5) |
| `control` | string | `"auto"` (autonomy) or `"api"` (external takeover) |
| `status` | string | Human-readable status |
| `needle` | bool | Whether this arm holds a needle |
| `tcpSpeed` | float | TCP speed m/s (<0.06 ≈ still) |
| `tcp` | float[3] | TCP world position (meters, Y-up, tissue surface y≈0.53-0.63) |
| `tip` | float[3] | Needle tip world position (equals tcp if no needle) |
| `joints` | float[] | Joint angles in radians, base to wrist |

**Example** (abbreviated):

```json
{
  "scenario": "liver", "scenarioTitle": "Liver Tumor Microsurgery",
  "paused": false, "backend": "SofaUnity (SOFA co-simulation)",
  "stitchesDone": 2, "stitchesTotal": 4,
  "sofaTime": 12.5, "sofaFps": 82.0, "sofaSteps": 12500,
  "arms": [
    {"index":0,"name":"Arm_1","role":"Stabilizer","dof":4,"control":"api",
     "status":"api move -> (0.00,0.60,0.90)","needle":false,
     "tcp":[0.01,0.60,0.90],"joints":[0.21,-0.83,1.12,0.05]},
    {"index":1,"name":"Arm_2","role":"Suturer","dof":4,"control":"api",
     "status":"api move -> (0.00,0.62,0.92)","needle":true,
     "tcp":[-0.05,0.62,0.92],"tip":[-0.05,0.60,0.90],
     "joints":[0.45,-0.91,0.78,-0.12]}
  ],
  "tasks": [{"kind":"suture","title":"liver_batch1","assigned":true,"done":false,
             "stitchesDone":2,"stitchesTotal":4,
             "stitches":[{"entry":[-0.01,0.62,0.83],"exit":[-0.01,0.62,0.87],"completed":true},
                          {"entry":[0.01,0.62,0.83],"exit":[0.01,0.62,0.87],"completed":true}]}],
  "tissue": {"live":true,"maxDelta":0.018,"boundsMin":[-0.8,0.39,0.52],"boundsMax":[0.8,0.53,1.28]}
}
```

---

## 2. `GET /api/scenarios` — Scenario Catalogue

Returns all 69 scenarios with procedure mapping:

```json
{"scenarios":[
  {"id":"vessel","title":"Microvascular Anastomosis",
   "refs":"#3 STA-MCA bypass; #21 Fingertip replantation","armCount":3,"dof":5},
  {"id":"liver","title":"Liver Tumor Microsurgery",
   "refs":"#56 Living donor hepatectomy","armCount":2,"dof":4},
  ...
]}
```

---

## 3. `GET /api/calibration` — Full Arm Calibration

Per-arm structural parameters (updated on scenario switch):

| Field | Description |
|---|---|
| `basePosition`[3] | Base world position (m) |
| `facing`[3] | Base orientation unit vector |
| `reach` | Nominal reach = sum of link lengths (m) |
| `stiffness` / `damping` / `forceLimit` | Joint drive parameters |
| `fingerLength` / `fingerOpen` / `fingerClosed` / `graspRadius` | Gripper geometry |
| `needleTipOffsetTcp`[3] | Needle tip offset from TCP (m) |
| `joints[]` | Per-joint: `name`, `axisLocal`[3], `linkLength`, `limitDeg`[min,max], `maxServoStepDeg` |
| `units` | `"meters/radians/degrees-as-noted"` |

**Distinction from `/api/state`**: `state.arms[i].joints` = **real-time joint angles** (radians, changes each frame); `calibration.arms[i].joints` = **structural parameters** (axes/links/limits, fixed per scenario).

---

## 4. `GET /api/cameras` — Camera Wall

Returns 7 cameras (main + 5 wall cameras + screen composite):

```json
{"cameras":[
  {"id":"main","label":"Main","position":[0,1.8,-2.0],"rotation":[25,0,0],"viewport":[0,0,1,1]},
  {"id":"front","label":"Front","position":[0,1.05,2.0],"rotation":[24,180,0],"viewport":[0.33,0.5,0.33,0.5]},
  ...
]}
```

The Game view is split into a **3×2 grid** with 6 simultaneous views: Main (top-left), Front (top-center), Back (top-right), Left (bottom-left), Right (bottom-center), Top (bottom-right).

---

## 5. `GET /api/screenshot` — High-Quality PNG Capture

```bash
# Default 4K (3840×2160), 2× supersampled + 8× MSAA
curl "http://127.0.0.1:8765/api/screenshot?camera=top" -o top.png

# 8K (7680×4320)
curl "http://127.0.0.1:8765/api/screenshot?camera=top&size=8k" -o top8k.png

# Fast polling (960×540)
curl "http://127.0.0.1:8765/api/screenshot?camera=top&size=sd" -o topsd.png

# Custom resolution
curl "http://127.0.0.1:8765/api/screenshot?camera=front&width=2048&height=1536" -o f.png

# Full-screen composite (native resolution, 6-grid wall + HUD)
curl "http://127.0.0.1:8765/api/screenshot?camera=screen" -o screen.png
```

**Resolution options**:
- Default: 4K (3840×2160)
- `size=sd|hd|fhd|2k|4k|8k|max`
- `width`/`height`: custom (320-7680)
- `camera=screen`: always native display resolution (not affected by size parameter)

**Quality pipeline**: 2× supersampling → 8× MSAA → GPU box-filter downsample → deterministic vertical flip (Metal fix) → lossless PNG. Images are upright at all resolutions.

---

## 6. `POST /api/scenario` — Switch Scenario

```bash
curl -X POST http://127.0.0.1:8765/api/scenario \
     -H 'Content-Type: application/json' -d '{"id":"s51"}'
```

- Request: `{"id": "<scenario_id>"}`
- Response: `{"ok":true}` (unknown ID logs warning, no switch)
- Effect: arms respawn with new scenario config (count/DOF/roles/needles), new tissue/vasculature/wound generated
- Idempotent: switching to current scenario rebuilds

---

## 7. `POST /api/command` — Direct Arm Control

Request body (fields used depend on action):

```json
{"action":"MoveTip","arm":0,"target":[0.0,0.60,0.90],"open":0.4}
```

| Action | Required Fields | Semantics |
|---|---|---|
| `MoveTcp` | `arm`, `target`[x,y,z] | TCP world-position servo (takes over the arm) |
| `MoveTip` | `arm`, `target`[x,y,z] | **Needle-tip** world-position servo (with needle; else = MoveTcp) |
| `Gripper` | `arm`, `open`(0-1) | Gripper open fraction (0=closed, 1=open) |
| `Home` | `arm` | **Takes over** and drives all joints to zero, gripper open; holds until next move or Resume |
| `Resume` | `arm` | **Returns control to autonomy** (arm goes back to `auto`) |
| `Cinch` | `arm` | **Manual suture tie-off**: marks the stitch the needle just passed through. Server validates needle tip is ≤5cm from stitch exit point |

**Control model**: any direct command puts the arm into `api` takeover mode (`control:"api"`) — the autonomous executor pauses for that arm; your commands execute frame-by-frame via DLS-IK servo. `Resume` (or scenario switch) returns control.

**Coordinate system**: world meters, Y-up. Workspace reference: tissue surface y≈0.53-0.63, wound center (0, 0.55-0.64, 0.90), arm bases y=0 along x.

**Response**: `{"ok":true}` (queued, next frame); out-of-range `arm` or unknown action returns `{"ok":true}` with server-side warning.

**Manual suturing workflow**:
```
state.tasks[0].stitches[j] → read entry/exit
→ MoveTip(entry + hover)    # approach
→ MoveTip(entry)            # pierce (needle-tissue coupling activates at y < surface + 0.015)
→ MoveTip(exit)             # pass under incision
→ Cinch(arm)                # tie knot (server validates tip position)
→ MoveTip(exit + pull_up)   # lift needle
```

---

## 8. `POST /api/control` — Global Pause/Resume

```bash
curl -X POST http://127.0.0.1:8765/api/control -d '{"action":"pause"}'
curl -X POST http://127.0.0.1:8765/api/control -d '{"action":"resume"}'
```

Pause = all executors, arm servos, and **SOFA native stepping** freeze simultaneously (`state.paused` queryable). Use for: observation, agent thinking window, batch evaluation.

> **Note**: `POST /api/control {"action":"resume"}` is **global** simulation resume; `POST /api/command {"action":"Resume","arm":i}` is **per-arm** autonomy return.

---

## 9. Stall Detection (503)

SOFA runs on a dedicated thread; Unity main loop handles rendering, API, commands, and arm servos independently. Two health levels:

1. **Main-loop heartbeat**: `Update()` refreshes snapshots every 0.25s. If the HTTP thread detects heartbeat stalled >3s:
   - `GET /api/state`, `GET /api/screenshot` → `503 {"error":"simulation main loop stalled","staleSeconds":...,"frame":...}`
   - All `POST` → `503 {"ok":false,...}` (command NOT enqueued)

2. **Solver heartbeat**: `sofaTime`/`sofaSteps` stop incrementing or `sofaStepMs` growing = native solver hang. Other functions (state, scenario switch, arm control) still work.

---

## 10. Agent Workflow Recipes

### 10.1 Observation Loop (10Hz sufficient)

```python
import json, time, urllib.request

def get_state():
    with urllib.request.urlopen("http://127.0.0.1:8765/api/state") as r:
        return json.load(r)

while True:
    s = get_state()
    print(f"scenario={s['scenario']} stitches={s['stitchesDone']}/{s['stitchesTotal']}")
    for arm in s["arms"]:
        print(f"  {arm['name']}: dof={arm['dof']} control={arm['control']} "
              f"tcp=({arm['tcp'][0]:.2f},{arm['tcp'][1]:.2f},{arm['tcp'][2]:.2f})")
    time.sleep(0.1)
```

### 10.2 Take Over an Arm

```python
import json, urllib.request

def cmd(action, arm, target=None, open_val=None):
    body = {"action": action, "arm": arm}
    if target: body["target"] = target
    if open_val is not None: body["open"] = open_val
    req = urllib.request.Request("http://127.0.0.1:8765/api/command",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"}, method="POST")
    urllib.request.urlopen(req)

cmd("MoveTip", 0, [0.0, 0.60, 0.90])   # arm 0 needle-tip to (0,0.60,0.90)
cmd("Gripper", 0, open_val=0.3)          # half-close gripper
cmd("Home", 0)                            # all joints to zero
cmd("Resume", 0)                          # return to autonomy
```

### 10.3 Multi-Camera Visual Servoing

```python
import urllib.request

def shot(camera="top", size="hd"):
    url = f"http://127.0.0.1:8765/api/screenshot?camera={camera}&size={size}"
    with urllib.request.urlopen(url) as r: return r.read()

# Visual loop: observe from top, act on arm
png = shot("top", "hd")       # what does the wound look like from above?
png = shot("front", "hd")     # what's the needle insertion angle?
png = shot("screen")           # full 6-grid wall composite
```

### 10.4 Full Manual Suturing

See `tools/micro_suture.py` for the complete five-gate safety protocol implementation.

---

## Error Responses

| HTTP | Body | Meaning |
|---|---|---|
| `400` | `{"error":"unknown camera id","valid":[...]}` | Invalid camera parameter |
| `503` | `{"error":"simulation main loop stalled","staleSeconds":...}` | Main loop frozen |
| `503` | `{"error":"capture timeout"}` | Screenshot render failed |
| `200` | `{"ok":true}` | Command queued (not yet executed) |
