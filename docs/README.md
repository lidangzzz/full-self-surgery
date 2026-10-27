# SofaRobotAI Documentation

## Core References

| Document | Contents |
|---|---|
| [**API.md**](API.md) | Complete HTTP API reference — all endpoints, parameters, examples, error handling, stall detection |
| [**SCENARIOS_EN.md**](SCENARIOS_EN.md) | 69-scenario catalogue with arm configs, procedure mapping, department breakdown |
| [**OPERATIONS.md**](OPERATIONS.md) | Setup, running, scenario control, arm control, screenshots, manual suturing, troubleshooting |

## Test Reports & Evidence

| Document | Contents |
|---|---|
| [API_MICRO_SUTURE_REPORT.md](API_MICRO_SUTURE_REPORT.md) | DLS-IK micro-step suturing: 4/4, 0 violations, 34 frames |
| [EIGHT_ARM_API_DEMO_REPORT.md](EIGHT_ARM_API_DEMO_REPORT.md) | 8-arm full-API surgery: 17/17 checks passed |
| [CODEX_TEST_REPORT.md](CODEX_TEST_REPORT.md) | Comprehensive external agent API test reports |
| [MICRO_SUTURE_PROTOCOL.md](MICRO_SUTURE_PROTOCOL.md) | Safety protocol design document |
| [API_SUTURE_TEAR_REPORT.md](API_SUTURE_TEAR_REPORT.md) | Tear scenario manual suturing report |

## Scenario-Specific (Chinese originals)

| Document | Contents |
|---|---|
| [AGENT_API.md](AGENT_API.md) | Chinese API reference (detailed, with workflow recipes) |
| [SCENARIOS.md](SCENARIOS.md) | Chinese scenario manual (detailed technique notes) |

## Evidence Images

| Directory | Contents |
|---|---|
| `images/` | Surgery screenshots, camera wall, brain/liver models, suture evidence |
| `surgery_frames/` | Step-by-step suturing protocol frames |
| `surgery_video.mp4` | Complete suturing video (4fps) |

## Tools

| Script | Purpose |
|---|---|
| `../tools/micro_suture.py` | Five-gate safety protocol for step-by-step suturing |
| `../tools/eight_arm_api_demo.py` | End-to-end 8-arm surgery via all API surfaces |
| `../tools/launch_unity_detached.sh` | Detached editor launch |
| `../tools/gen_brain.py` | Brain FEM lattice generator |
| `../tools/gen_liver_sofa.py` | Official liver model transform + bindings |
