# Full Self Surgery — Operations Guide

## Quick Start

### 1. Clone and Setup

```bash
git clone <repository-url>
cd full-self-surgery

# Option A: Quick start (Unity PhysX only, no SOFA)
# Just open in Unity 6000.6 LTS and press Play

# Option B: Full setup (with native SOFA)
./setup-sofaunity.sh
```

### 2. Run in Editor

1. Open `Assets/Scenes/MultiArmAgentScene.unity` in Unity
2. Press **Play** — the API server starts automatically at `127.0.0.1:8765`
3. Browse scenarios with `PgUp`/`PgDn` or number keys `1-9`

### 3. Headless / Detached Mode

```bash
# Detached GUI launch (survives terminal exit, starts play mode + API)
./tools/launch_unity_detached.sh

# Verify API is running
curl http://127.0.0.1:8765/api/state | python3 -m json.tool | head -5
```

### 4. Headless Smoke Test

```bash
Unity -batchmode -nographics -projectPath . \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunFromCommandLine
# Exit code 0 = pass
```

## Scenario Control

### Via API

```bash
# List all 69 scenarios
curl http://127.0.0.1:8765/api/scenarios | python3 -m json.tool

# Switch to liver tumor surgery
curl -X POST http://127.0.0.1:8765/api/scenario \
     -H 'Content-Type: application/json' -d '{"id":"liver"}'

# Check current state
curl http://127.0.0.1:8765/api/state | python3 -c "
import json,sys; d=json.load(sys.stdin)
print(f'Scenario: {d[\"scenario\"]}')
print(f'Arms: {len(d[\"arms\"])}')
print(f'Stitches: {d[\"stitchesDone\"]}/{d[\"stitchesTotal\"]}')
print(f'SOFA FPS: {d[\"sofaFps\"]}')"
```

### Via Keyboard (in Game view)

| Key | Action |
|---|---|
| `1`-`9` | Jump to scenario |
| `PgDn` / `PgUp` | Next / previous scenario |
| `P` | Pause / resume |

## Arm Control

### Direct Control (API)

```bash
# Move arm 0's needle tip to a world position
curl -X POST http://127.0.0.1:8765/api/command \
     -H 'Content-Type: application/json' \
     -d '{"action":"MoveTip","arm":0,"target":[0.0,0.60,0.90]}'

# Return arm to autonomous control
curl -X POST http://127.0.0.1:8765/api/command \
     -H 'Content-Type: application/json' \
     -d '{"action":"Resume","arm":0}'
```

### Calibration Data

```bash
# Get full arm calibration (geometry, drive params, joint limits)
curl http://127.0.0.1:8765/api/calibration | python3 -m json.tool
```

## Screenshots

```bash
# Default 4K from top camera
curl "http://127.0.0.1:8765/api/screenshot?camera=top" -o top.png

# 8K maximum quality
curl "http://127.0.0.1:8765/api/screenshot?camera=top&size=8k" -o top8k.png

# Fast polling (for tight loops)
curl "http://127.0.0.1:8765/api/screenshot?camera=top&size=sd" -o fast.png

# Full 6-camera grid wall composite
curl "http://127.0.0.1:8765/api/screenshot?camera=screen" -o wall.png
```

## Manual Suturing Protocol

See `tools/micro_suture.py` for the complete implementation. Summary:

1. Read stitch plan from `state.tasks[0].stitches`
2. For each stitch:
   - Position tweezers at near wound edge (counter-traction)
   - Hover needle above entry point
   - Descend vertically to pierce tissue
   - Move horizontally under wound to exit point
   - `Cinch` to tie the knot
   - Lift needle vertically
3. Each step: command → settle → screenshot → verify safety gates

### Safety Gates (per step)

| Gate | Rule |
|---|---|
| Step size | ≤40mm in tissue, unlimited at altitude |
| Vessel distance | Needle tip ≥6mm from any hepatic vessel |
| Contact zone | Needle only in wound corridor when below surface+8mm |
| Tissue tension | FEM displacement increment ≤2.5mm per step |
| Settle | Arm speed <0.08 m/s before next command |

## Troubleshooting

### API not responding
- Check Unity is in Play mode
- Check port 8765 isn't occupied: `lsof -i :8765`
- Check the main loop hasn't stalled (the API returns 503 with `staleSeconds` if so)

### SOFA backend unavailable
- On macOS: verify native dylibs exist in `Assets/SofaUnity/Core/Plugins/Native/macOS/`
- Falls back to Unity PhysX automatically

### Editor stuck on scene backup dialog
- Run `./tools/launch_unity_detached.sh` (clears stale `Library/EditorInstance.json`)

### Screenshots upside down
- Fixed in current version (deterministic CPU flip for Metal)
- If seeing old files, they were generated before the fix
