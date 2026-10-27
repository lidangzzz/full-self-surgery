# SofaRobotAI — Multi-Arm AI Agent Surgical Simulation

A Unity 3D project built on [SofaUnity](https://github.com/InfinyTech3D/SofaUnity) (SOFA physics engine integration): **N multi-DOF surgical robot arms, each controlled by a pluggable AI agent brain**, performing autonomous pick-and-place and **surgical suturing demonstrations** — needle-driven wound closure on SOFA soft-tissue FEM organs with real-time deformation.

```
Quick start (macOS / Windows / Linux):  Unity 6000.6 LTS + Unity PhysX backend
Full SOFA co-simulation:                macOS arm64 (verified) / Windows x64
```

## What's Inside

Open `Assets/Scenes/MultiArmAgentScene.unity` and press Play. **69 surgical scenarios** (7 handcrafted flagships + 62 parameterized catalogue entries covering all 10 surgical departments from a 100-procedure catalogue):

**Switching**: keys `1-9` for direct access / `PgUp` `PgDn` to browse / on-screen buttons / Agent API.

### Flagship Scenarios

| Scenario | Procedure | Arms | Highlights |
|---|---|---|---|
| `vessel` Microvascular anastomosis | #3/#21-22/#30 | 3 × 5 DOF | Dense vascular network, 9-stitch vessel closure |
| `heart` Beating-heart CABG | #34 | 2 × 5 DOF | SOFA cardiac pusher, suturing on pulsating surface |
| `tendon` Flexor tendon repair | #25 | 2 × 4 DOF | Circumferential 4-stitch tendon anastomosis |
| `octo` Eight-arm replantation | #22 | 8 × 5 DOF | Full API orchestration of 8 arms, 12 stitches |
| `tear` Vessel incision repair | #1/#17 | 2 × 4 DOF | Three-segment tool arms (tweezers + needle holder) |
| `brain` Brain microsurgery | #1/#10 | 2 × 4 DOF | Life-size SOFA FEM brain (35k-vertex anatomical surface) |
| `liver` Liver tumor surgery | #56/#57 | 2 × 4 DOF | Official SofaUnity liver model + HCC tumor + vessel suturing |

The 62 catalogue scenarios (`s01`–`s98` in `ProcedureCatalog.cs`) span: Neurosurgery (10), Ophthalmology (6), Hand/Microsurgery (9), Cardiac (9), Thoracic (8), HPB/Transplant (7), GI/Pelvic (4), Urology/Gynecology (5), Orthopedics/ENT (2), Interventional/Endoscopy (2) — all parameterized instances of the same SOFA FEM tissue + needle-force coupling + suture executor machinery.

## Agent HTTP API

A local HTTP JSON API starts automatically during Play mode (default `127.0.0.1:8765`):

```bash
# Full state snapshot (arms, joints, TCP, tasks, tissue deformation)
curl http://127.0.0.1:8765/api/state | python3 -m json.tool

# Scenario catalogue (69 entries)
curl http://127.0.0.1:8765/api/scenarios | python3 -c "import json,sys;print(len(json.load(sys.stdin)['scenarios']),'scenarios')"

# Switch scenario (arms reconfigure automatically)
curl -X POST http://127.0.0.1:8765/api/scenario -H 'Content-Type: application/json' -d '{"id":"heart"}'

# Direct arm control (MoveTip = needle-tip target)
curl -X POST http://127.0.0.1:8765/api/command -H 'Content-Type: application/json' \
     -d '{"action":"MoveTip","arm":0,"target":[0.0,0.60,0.90]}'

# High-quality screenshot from any of 7 cameras (main + 5 wall + screen composite)
curl "http://127.0.0.1:8765/api/screenshot?camera=top&size=8k" -o top.png
```

### API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/state` | Full snapshot: scenario, arms (joints/TCP/speed/role/needle), task progress, tissue deformation, solver health |
| `GET` | `/api/scenarios` | 69-entry scenario catalogue with procedure mapping |
| `GET` | `/api/calibration` | Full per-arm geometry/drive/joint calibration |
| `GET` | `/api/cameras` | Camera list with positions and viewport rects |
| `GET` | `/api/screenshot` | PNG capture from any camera (up to 8K, 2× supersampled + 8× MSAA) |
| `POST` | `/api/scenario` | `{"id":"vessel"}` — switch scenario |
| `POST` | `/api/command` | `{"action":"MoveTcp|MoveTip|Gripper|Home|Resume|Cinch","arm":0,"target":[x,y,z]}` |
| `POST` | `/api/control` | `{"action":"pause"|"resume"}` — global pause/resume |

**Stall detection**: if the Unity main loop stalls >3s, the API returns `503 {"error":"simulation main loop stalled","staleSeconds":...}` instead of serving stale data.

### Screenshot Quality

All single-camera captures use **2× supersampling + 8× MSAA** with GPU box-filter downsampling:
- Default: 4K (3840×2160)
- `size=8k`: 7680×4320
- `size=sd`: 960×540 (fast polling)
- `width`/`height`: custom (320–7680)
- `camera=screen`: native-resolution full-screen composite (6-grid video wall)

## Architecture

```
MultiArmAgentManager            Single entry point: backend init, N-arm spawn, task scheduling, HUD
 ├── IRobotPhysicsBackend       Physics backend abstraction
 │    ├── SofaUnityBackend      SofaContext loads .scn, native SOFA stepping on dedicated thread
 │    └── UnityPhysicsBackend   Any platform: ArticulationBody + PhysX (default, zero deps)
 ├── ArmAgent ×N                Per-arm embodied agent: decision cadence + skill execution
 │    ├── IAgentBrain           Pluggable brain
 │    │    ├── HeuristicBrain   DLS-IK servo + grasp/place strategy (offline, real-time)
 │    │    └── LlmAgentBrain    OpenAI-compatible /chat/completions high-level planning
 │    └── SutureExecutor        Stitching FSM: approach→pierce→through→pull→cinch
 ├── RobotArmController         Per-arm control: DLS-IK ServoToward, gripper, physics grasp
 ├── RobotArmBuilder            Procedural multi-DOF kinematic chain (3-7 DOF presets)
 ├── CameraWall                 6-grid video wall: main + front/back/left/right/top cameras
 └── Surgery/                   Surgical subsystem
      ├── OrganCellBuilder      Operating table + organs/vessels/incisions/stitch points
      ├── NeedleTool            Procedural curved needle + thread LineRenderer
      ├── SutureExecutor        Stitch FSM (suspends during API control)
      ├── TissueDeformationSync Live SOFA FEM vertex mirror with liver warp
      ├── BrainDeformationSync  35k-vertex anatomical brain surface skinned to SOFA FEM
      ├── LiverDeformationSync  Official SofaUnity liver model + tumor, skinned to FEM
      └── SutureVisuals         Knots, cinch bands, shrinking blood pool, thread path
```

### Key Technical Features

| Feature | Detail |
|---|---|
| **DLS-IK Servo** | Damped least-squares inverse kinematics (not Jacobian-transpose): 2.2mm convergence, no dead zone, Cartesian straight-line trajectory |
| **Dedicated SOFA Thread** | Native solver runs on separate thread; Unity main loop immune to solver stalls |
| **Dual-level Health** | Main-loop heartbeat + solver FPS both exposed via `/api/state` |
| **Safety Protocol** | Five-gate micro-step suturing: step size, vessel distance, contact zone, tissue tension, settle verification |
| **SOFA Brain FEM** | Life-size cerebrum: 5,280-node hexahedral FEM (E=6kPa, ν=0.40), 35k-vertex anatomical render surface with medial fissure, gyri/sulci, Sylvian fissure, cerebellum, brainstem |
| **Official Liver Model** | SofaUnity's own `liver.msh` (181 nodes/596 tets) with official FEM parameters + `liver3-HD.obj` visual surface |
| **Supersampled Screenshots** | 2× supersample + 8× MSAA + GPU box filter: anti-aliased at any resolution |

## Catalogue Scenarios (62 entries)

All parameterized from the same surgical machinery:

| Dept | Count | Representative |
|---|---|---|
| Neurosurgery | 10 | s01 aneurysm clipping, s03 STA-MCA bypass |
| Ophthalmology | 6 | s14 retinal detachment, s17 open-globe repair |
| Hand/Microsurgery | 9 | s21 fingertip replantation, s30 lymphatic-venous anastomosis |
| Cardiac Surgery | 9 | s31 mitral valve repair, s37 arterial switch |
| Thoracic | 8 | s41 bronchial sleeve, s49 lung transplant |
| HPB/Transplant | 7 | s51 pancreaticojejunostomy, s57 pediatric liver transplant |
| GI/Pelvic | 4 | s61 low anterior resection |
| Urology/Gynecology | 5 | s73 kidney transplant |
| Orthopedics/ENT | 2 | s86 rotator cuff repair |
| Interventional | 2 | s97 ESD, s98 POEM |

## Setup

### Quick Start (Unity PhysX only, no SOFA)

```bash
git clone <this-repo>
# Open in Unity 6000.6 LTS → Assets/Scenes/MultiArmAgentScene.unity → Play
```

### Full Setup (with SofaUnity native SOFA)

```bash
git clone <this-repo>
cd full-self-surgery
./setup-sofaunity.sh   # clones SofaUnity (~1.2GB) + applies Unity 6 compat patch
# Open in Unity → Play
```

### Headless Smoke Test

```bash
Unity -batchmode -nographics -projectPath . \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunFromCommandLine
```

### Detached GUI Launch (for API-driven development)

```bash
./tools/launch_unity_detached.sh   # starts editor + play mode + API server
```

## Tools

| Script | Purpose |
|---|---|
| `tools/micro_suture.py` | Five-gate safety protocol: step-by-step vessel suturing with screenshot verification |
| `tools/eight_arm_api_demo.py` | End-to-end 8-arm surgery via all API surfaces (17 checks) |
| `tools/launch_unity_detached.sh` | Detached editor launch (survives terminal exit) |
| `tools/gen_brain.py` | Brain FEM lattice generator → brain.vtk |
| `tools/gen_brain_surface.py` | High-res brain surface + skinning bindings |
| `tools/gen_liver_sofa.py` | Official liver model transform + bindings |

## Documentation

| Document | Contents |
|---|---|
| [`docs/AGENT_API.md`](docs/AGENT_API.md) | Complete HTTP API reference (all endpoints, parameters, examples) |
| [`docs/SCENARIOS.md`](docs/SCENARIOS.md) | 69-scenario catalogue with arm configs and technique notes |
| [`docs/API_MICRO_SUTURE_REPORT.md`](docs/API_MICRO_SUTURE_REPORT.md) | DLS-IK micro-step suturing results (4/4, 0 violations) |
| [`docs/EIGHT_ARM_API_DEMO_REPORT.md`](docs/EIGHT_ARM_API_DEMO_REPORT.md) | 8-arm full-API surgery demo (17/17 pass) |
| [`docs/CODEX_TEST_REPORT.md`](docs/CODEX_TEST_REPORT.md) | Comprehensive external agent API test reports |

## Known Issues

- `SofaUnity_modules` submodule doesn't exist on GitHub (upstream issue) — doesn't affect Core functionality
- macOS native library missing 9 entry points (25.12 ABI vs master C# gap) — shimmed; SOFA scene loading/stepping/gravity all work
- SofaUnity tested on Unity 2020/2021 + Windows; this project verified on macOS + Unity 6000.6.7f1 (arm64)
- Scene, arms, tasks all procedurally generated; `MultiArmAgentScene.unity` contains only camera/lights/floor/Manager

## Project Structure

```
Assets/
├── SofaUnity/              # Official plugin (1.2GB, not in repo; setup script clones + patches)
├── Scripts/
│   ├── Core/               # ArmSpec, RobotArmBuilder, RobotArmController, CameraWall
│   ├── Agent/              # IAgentBrain, Heuristic/Llm brains, ArmAgent, TaskSystem
│   ├── AgentApi/           # SurgicalAgentApi (HTTP server, screenshots, staleness detection)
│   ├── Backend/            # Physics backend abstraction + SofaUnity/Unity implementations
│   ├── Surgery/            # SutureExecutor, NeedleTool, organ sync, scenario framework
│   │   └── Scenarios/      # 69 scenario definitions (7 flagship + 62 catalogue)
│   └── MultiArmAgentManager.cs
├── SceneSetup/Editor/      # SceneBuilder + PlayModeSmokeTest
├── Shaders/                # BrainSurface (vertex-color Lambert), FlipY
├── StreamingAssets/SOFA/   # .scn scenes + .vtk FEM meshes + .obj surfaces + bindings
├── Scenes/                 # MultiArmAgentScene.unity
└── Shaders/                # Custom shaders
docs/                       # API docs, scenario manual, test reports, evidence images
tools/                      # Protocol scripts, generators, launch scripts
setup-sofaunity.sh          # Clone SofaUnity + apply Unity 6 compat patch
sofaunity-unity6-compat.patch
```

## License

This project integrates [SofaUnity](https://github.com/InfinyTech3D/SofaUnity) and [SOFA](https://www.sofa-framework.org/). See their respective licenses.
