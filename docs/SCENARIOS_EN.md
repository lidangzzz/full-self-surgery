# Full Self Surgery — Scenario Catalogue (69 Scenarios)

## System Overview

```
┌────────────────────────────────────────────────────────────────┐
│  MultiArmAgentManager (scene orchestration hub)                │
│    ├─ SurgicalScenario ×69 (hot-swappable)                     │
│    │    ├─ Flagship ×7: vessel/heart/tendon/octo/tear/brain/liver │
│    │    └─ Catalogue ×62: s01…s98 (10 departments)            │
│    ├─ Robot arms: 2-8 × 4-5 DOF (ArticulationBody)            │
│    ├─ Suture executor: pierce→through→pull→cinch FSM           │
│    ├─ SOFA backend: FEM tissue + needle/cardiac coupling      │
│    ├─ 6-camera wall: main + front/back/left/right/top          │
│    └─ Agent API: 127.0.0.1:8765 (HTTP JSON, see API.md)        │
└────────────────────────────────────────────────────────────────┘
```

## Shared Foundation

All scenarios reuse (persists across switches):
- **Tissue pad**: SOFA hexahedral FEM (20×4×12, corotational, 3kPa) — or liver-warp style
- **Surgical drape**: teal sterile drape with central fenestration
- **Vasculature**: deterministic symmetric layout (left artery + right vein, S-curve, branches)
- **6-camera wall**: 3×2 grid PIP display with labels
- **Needle coupling**: 6mm sphere, penalty contact, activates during tissue phases

## Flagship Scenarios

### [1] `vessel` — Microvascular Anastomosis (default)
- **Mapping**: #3 STA-MCA bypass / #21-22 replantation / #30 lymphatic-venous
- **Arms**: 3 × 5 DOF, rotating suturer
- **Wound**: Main vessel (r=14mm) severed, 9 stitches in 3 batches

### [2] `heart` — Beating-Heart CABG
- **Mapping**: #34 Off-pump coronary artery bypass
- **Arms**: 2 × 5 DOF (1 suturer + 1 stabilizer)
- **Wound**: S-shaped coronary (r=18mm), 4 stitches; SOFA cardiac pusher 1.2Hz

### [3] `tendon` — Flexor Tendon Repair
- **Mapping**: #25 Zone II flexor tendon repair
- **Arms**: 2 × 4 DOF
- **Wound**: Tendon ends (r=16mm), circumferential 4 stitches

### [4] `octo` — Eight-Arm Replantation
- **Mapping**: #22 Multi-digit replantation
- **Arms**: 8 × 5 DOF in arc layout (radius 1.05m)
- **Wound**: Replantation vessel (r=13mm), 12 stitches in 8 batches (one per arm)
- **Verified**: `tools/eight_arm_api_demo.py` — 17/17 checks passed

### [5] `tear` — Vessel Incision Suturing
- **Mapping**: #1 aneurysm clipping / #17 open-globe repair
- **Arms**: 2 × 4 DOF **three-segment tool arms** (tweezers + needle holder with curved needle)
- **Wound**: r=11mm vessel incised longitudinally (110mm), 4 stitches
- **Verified**: API step-by-step manual suturing with screenshot verification

### [6] `brain` — Brain Microsurgery
- **Mapping**: #1 aneurysm / #10 trigeminal decompression
- **Arms**: 2 × 4 DOF three-segment tool arms
- **Brain**: Life-size (169×146×126mm) SOFA FEM — 5,280-node hexahedral (E=6kPa, ν=0.40, 1.4kg)
- **Visual**: 35,120-vertex anatomical surface (gyri/sulci, Sylvian fissure, central sulcus, cerebellum folia, brainstem)
- **Vasculature**: Circle of Willis + ACA + bilateral MCA/PCA + superior sagittal sinus
- **Wound**: Pial vessel incision, 4 stitches

### [7] `liver` — Liver Tumor Surgery
- **Mapping**: #56 living donor hepatectomy / #57 pediatric liver transplant
- **Arms**: 2 × 4 DOF three-segment tool arms
- **Liver**: **Official SofaUnity demo model** (liver.msh, 181 nodes/596 tets) with official FEM parameters (E=3kPa, ν=0.3, 1.5kg, official fixed nodes)
- **Visual**: liver3-HD.obj (3,002 vertices) skinned to FEM
- **Tumor**: Lobulated HCC (~36mm) on right lobe superior surface with tumor feeder artery
- **Wound**: Capsular vessel incision, 4 stitches
- **Verified**: `tools/micro_suture.py` — 4/4, 0 gate violations, 34 screenshot frames

## Catalogue Scenarios (62)

All parameterized from the same machinery (SOFA FEM + needle coupling + suture executor):

### Neurosurgery (10)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s01 | Aneurysm clipping | 3×5 | 6 |
| s02 | AVM feeder occlusion | 3×5 | 6 |
| s03 | STA-MCA bypass | 3×5 | 8 |
| s04 | High-flow EC-IC bypass | 3×5 | 8 |
| s05 | Brainstem safe entry | 2×5 | 4 |
| s06 | Facial nerve preservation | 3×5 | 6 |
| s07 | Skull base duroplasty | 3×5 | 8 |
| s08 | Endoscopic skull base repair | 2×5 | 6 |
| s09 | Spinal duraplasty | 2×5 | 5 |
| s10 | Trigeminal decompression | 2×5 | 4 |

### Ophthalmology (6)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s14 | Retinal detachment repair | 2×5 | 5 |
| s15 | Macular epiretinal membrane | 2×5 | 4 |
| s17 | Open-globe laceration repair | 2×5 | 8 |
| s18 | Trabeculectomy flap | 2×5 | 5 |
| s19 | IOL scleral fixation | 2×5 | 4 |
| s20 | Strabismus muscle recession | 2×5 | 4 |

### Hand Surgery / Microsurgery (9)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s21 | Fingertip artery anastomosis | 3×5 | 6 |
| s22 | Multi-digit vessel reconstruction | 3×5 | 9 |
| s23 | Wrist radial arch repair | 3×5 | 8 |
| s24 | Toe transfer vessel anastomosis | 3×5 | 8 |
| s26 | Brachial plexus exploration | 3×5 | 6 |
| s27 | Peripheral nerve graft | 3×5 | 6 |
| s28 | Fibula flap vessel anastomosis | 3×5 | 7 |
| s29 | DIEP perforator anastomosis | 3×5 | 7 |
| s30 | Lymphatic-venous anastomosis | 3×5 | 8 |

### Cardiac Surgery (9)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s31 | Mitral valve annuloplasty | 3×5 | 8 |
| s32 | Aortic valve repair | 2×5 | 6 |
| s33 | Aortic root replacement | 3×5 | 9 |
| s35 | Aortic arch replacement | 3×5 | 9 |
| s36 | Thoracoabdominal aneurysm | 3×5 | 9 |
| s37 | Arterial switch (TGA) | 3×5 | 8 |
| s38 | Neonatal aortic arch | 3×5 | 7 |
| s39 | AVSD patch closure | 3×5 | 8 |
| s40 | Aortic root abscess reconstruction | 3×5 | 8 |

### Thoracic (8)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s41 | Bronchial sleeve resection | 3×5 | 8 |
| s42 | Bronchovascular sleeve | 3×5 | 9 |
| s44 | Carina resection | 3×5 | 8 |
| s45 | Tracheal slide tracheoplasty | 3×5 | 8 |
| s46 | Esophagogastric anastomosis | 3×5 | 8 |
| s48 | Tracheoesophageal fistula | 2×5 | 6 |
| s49 | Lung transplant bronchus | 3×5 | 8 |
| s50 | Chest wall reconstruction | 3×5 | 9 |

### HPB / Transplant (7)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s51 | Pancreaticojejunostomy | 3×5 | 8 |
| s53 | Pancreatic duct-jejunum | 3×5 | 6 |
| s54 | Hilar bile duct reconstruction | 3×5 | 8 |
| s56 | Donor hepatectomy | 3×5 | 7 |
| s57 | Pediatric liver transplant | 3×5 | 8 |
| s58 | Split liver transplantation | 3×5 | 9 |
| s60 | Intestinal transplant | 3×5 | 8 |

### GI / Pelvic (4)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s61 | Low anterior resection | 3×5 | 8 |
| s64 | Ileal pouch-anal anastomosis | 3×5 | 7 |
| s66 | Rectovaginal fistula | 2×5 | 6 |
| s67 | Complex abdominal wall hernia | 3×5 | 10 |

### Urology / Gynecology (5)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s72 | Pyeloplasty | 3×5 | 7 |
| s73 | Kidney transplant | 3×5 | 8 |
| s74 | Urethral stricture | 2×5 | 6 |
| s76 | Vas deferens-epididymis | 3×5 | 6 |
| s78 | Neobladder reconstruction | 3×5 | 9 |

### Orthopedics / ENT (2)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s86 | Rotator cuff repair | 3×5 | 8 |
| s90 | Vocal cord microsurgery | 2×5 | 4 |

### Interventional / Endoscopy (2)
| ID | Procedure | Arms | Stitches |
|---|---|---|---|
| s97 | ESD defect closure | 2×5 | 6 |
| s98 | POEM tunnel closure | 2×5 | 6 |

## Switching Methods

| Method | Action | Notes |
|---|---|---|
| Keyboard | `1`-`9` | First nine (1-7 are flagships) |
| Keyboard | `PgDn` / `PgUp` | Browse (69 cycle) |
| Mouse | HUD buttons | On-screen prev/next |
| API | `POST /api/scenario {"id":"s51"}` | External agent |

Switch is instant: old arms destroyed → new arms built (count/DOF/roles/needles) → new tissue color/vasculature/wound → new batch tasks activated.

## Test Entry Points

```bash
# Scenario cycling + API end-to-end (headless)
Unity -batchmode -nographics -projectPath . \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunScenariosFromCommandLine

# Single-scenario suture regression
Unity -batchmode -nographics -projectPath . \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunSurgeryFromCommandLine
```

Editor menu: `SofaRobotAI → Build Multi-Arm AI Scene` / `Run Play-Mode Smoke Test`
