# Full Self Surgery Agent API 手册

> **用途**：供外部 AI Agent（LLM、脚本、自动化测试框架）读取仿真状态、读取机械臂状态、
> 并通过调用 API 操作 67 个手术场景与机械臂。
>
> **基地址**：`http://127.0.0.1:8765`（仅本机回环；Unity Play 模式下自动监听，
> 端口由 `MultiArmAgentManager.agentApiPort` 配置）。
> **协议**：HTTP/1.1 + JSON（UTF-8）。所有 GET 有 ~0.25s 快照缓存；命令在下一帧主线程生效。

---

## 0. 快速冒烟（30 秒确认可用）

```bash
curl -s http://127.0.0.1:8765/api/state | python3 -m json.tool | head -30
curl -s http://127.0.0.1:8765/api/scenarios | python3 -c "import json,sys;print(len(json.load(sys.stdin)['scenarios']),'scenarios')"
curl -s -X POST http://127.0.0.1:8765/api/scenario -H 'Content-Type: application/json' -d '{"id":"heart"}'
sleep 3
curl -s http://127.0.0.1:8765/api/state | python3 -c "import json,sys;d=json.load(sys.stdin);print(d['scenario'],len(d['arms']),'arms')"
```

预期：目录返回 67 条；切换后 state 中 `scenario` 变为 `heart`、臂数变为 2。

---

## 1. `GET /api/state` —— 全量状态快照

**响应字段**：

| 字段 | 类型 | 含义 |
|---|---|---|
| `timestamp` | string | 快照生成的 UTC 时间（ISO 8601）。**连续两次 GET 若 timestamp 不变 = 快照停更**（正常不应发生；API 会自动解除编辑器暂停） |
| `frame` | int | 快照生成时的引擎帧号（每次 GET 应递增） |
| `scenario` | string | 当前场景 ID（如 `vessel` / `s51`） |
| `scenarioTitle` | string | 场景中文名 |
| `paused` | bool | 是否全局暂停（暂停时一切执行器与 SOFA 步进冻结） |
| `editorPaused` | bool | **编辑器 Play 模式是否被暂停**（仿真时间冻结：快照仍刷新、命令排队等待恢复——Agent 检测到 true 时应先 `POST /api/control {"action":"resume"}` 之外的编辑器操作，或提示人工恢复） |
| `backend` | string | 物理后端名称（`SofaUnity (SOFA co-simulation)` 表示原生 SOFA 在跑） |
| `backendStatus` | string | 后端详情（版本/场景文件/运行状态） |
| `stitchesDone` / `stitchesTotal` | int | 当前场景缝合进度（总针数/已完成） |
| `sofaTime` | float | SOFA 原生仿真时钟（秒）。持续递增 = 求解器真实步进 |
| `sofaFps` | float | SOFA 原生求解 FPS（专用步进线程，与 Unity 渲染帧率解耦） |
| `sofaSteps` | long | 已完成的原生步数（单调递增计数器） |
| `sofaStepMs` | long | 最近一次原生 step 耗时/正在进行的 step 已耗时（毫秒）。持续增长且 >数秒 = 求解器卡死（主循环不受影响，仍可读取/切换） |
| `arms[]` | array | 每台机械臂的实时状态（见下） |
| `tasks[]` | array | 任务（缝合批次）进度，`stitches[]` 逐针暴露 `entry`/`exit`/`completed`（手动缝合的目标点即来自这里） |
| `tissue` | object | SOFA 组织状态：`live`（原生镜像）、`maxDelta`（历史最大形变 m）、`boundsMin/Max`（当前包围盒） |

**`arms[i]` 字段**：

| 字段 | 类型 | 含义 |
|---|---|---|
| `index` | int | 臂序号（**命令中的 `arm` 参数用它**） |
| `name` | string | `Arm_1` / `Arm_2` / `Arm_3` |
| `role` | string | `Suturer` 持针缝合 / `Stabilizer` 稳定组织 / `Idle` 停靠 |
| `dof` | int | 自由度（4 或 5）——**读取机械臂状态的核心字段** |
| `control` | string | `auto` 场景自治 ｜ `api` 外部接管中 |
| `status` | string | 人类可读状态（如 `suture: Pierce stitch 2/3 (tip 0.01 m, v 0.03)`） |
| `needle` | bool | 是否持针 |
| `tcpSpeed` | float | TCP 速度 m/s（<0.06 视为静止） |
| `tcp` | float[3] | **TCP 世界坐标**（米，Y 向上，地面 y=0，组织面 y≈0.53） |
| `tip` | float[3] | **针尖世界坐标**（持针时；无针时等于 tcp） |
| `joints` | float[] | **各关节角（弧度）**，长度=dof，顺序从基座到腕 |

**示例响应**（节选）：

```json
{
  "scenario": "heart",
  "scenarioTitle": "不停跳冠状动脉搭桥",
  "paused": false,
  "backend": "SofaUnity (SOFA co-simulation)",
  "backendStatus": "running (dedicated solver thread), SOFA SofaAdvancePhysicsAPI, scene: Assets/StreamingAssets/SOFA/robot_cell.scn",
  "stitchesDone": 2, "stitchesTotal": 4,
  "arms": [
    { "index":0, "name":"Arm_1", "role":"Suturer", "dof":5, "control":"auto",
      "status":"suture: Through stitch 2/2 (tip 0.02 m, v 0.04)", "needle":true,
      "tcpSpeed":0.041, "tcp":[-0.05,0.62,0.92], "joints":[0.21,-0.83,1.12,-0.31,0.05] },
    { "index":1, "name":"Arm_2", "role":"Stabilizer", "dof":5, "control":"auto",
      "status":"stabilizing tissue", "needle":false,
      "tcpSpeed":0.003, "tcp":[0.16,0.58,0.90], "joints":[1.02,-0.71,0.44,-0.12,0.0] }
  ],
  "tasks": [ { "kind":"suture", "title":"heart_batch1 …", "assigned":true, "done":false,
               "stitchesDone":2, "stitchesTotal":2 } ],
  "tissue": { "live":true, "maxDelta":0.0187,
              "boundsMin":[-0.8,0.39,0.52], "boundsMax":[0.8,0.53,1.28] }
}
```

## 1b. `GET /api/screenshot` —— 画面截图（六机位 + 合成图，PNG）

```bash
curl -s http://127.0.0.1:8765/api/screenshot -o shot.png                        # 主视角，默认 4K 3840×2160
curl -s "http://127.0.0.1:8765/api/screenshot?camera=front" -o front.png        # 前机位（4K）
curl -s "http://127.0.0.1:8765/api/screenshot?camera=top&size=8k"  -o top8k.png # 顶机位 8K 7680×4320
curl -s "http://127.0.0.1:8765/api/screenshot?camera=top&size=sd"  -o topsd.png # 顶机位 960×540（快速轮询）
curl -s "http://127.0.0.1:8765/api/screenshot?camera=top&width=2048&height=1536" -o top.png  # 自定义分辨率
curl -s "http://127.0.0.1:8765/api/screenshot?camera=screen" -o screen.png      # 整屏合成（显示器原生分辨率）
```

- **分辨率**：单相机渲染走离屏 RenderTexture，**不受窗口/屏幕限制**。
  - **默认 4K（3840×2160）**
  - `size` 预设：`sd`(960×540) / `hd`(1280×720) / `fhd`(1920×1080) / `2k`(2560×1440) / `4k` / **`8k`=`max`(7680×4320)**
  - 或 `width`/`height` 自定义（范围 320..7680）
  - `camera=screen` 始终为**显示器原生分辨率**整屏合成（六宫格墙+HUD，本机 2560×1440）
  - 未知 camera id 返回 400 与合法列表
- **机制**：请求触发主线程渲染一帧并编码（等待上限 8 秒，失败返回 503 JSON）。**最高采样质量**：所有单相机截图均以 **2× 超采样 + 8× MSAA** 渲染后 GPU 精确降采样到目标尺寸（2× 下 bilinear=box filter，边缘零锯齿）；已修复 Metal 垂直翻转，输出正立
- **典型用法**：视觉 Agent 多机位观察术野（顶机位看缝合布局、前机位看进针
  角度、左右机位看器械位置）；与 `/api/state` 轮询互补

## 1c. `GET /api/cameras` —— 机位清单与位置

```bash
curl -s http://127.0.0.1:8765/api/cameras | python3 -m json.tool
```

返回全部机位（主视角 + 前/后/左/右/顶五机位墙）：

| 字段 | 含义 |
|---|---|
| `id` | 截图用的机位 ID（main/front/back/left/right/top） |
| `label` | 显示名（画中画标签同款） |
| `position`[3] | 相机世界坐标（米） |
| `rotation`[3] | 相机欧拉角（度） |
| `viewport`[4] | 画中画小窗的归一化屏幕区域（x,y,w,h；main 为全屏） |

**六宫格视频墙常驻显示**：Game 视图被切为 3×2 六个大画面**同时显示**——
上行：主视角 / 前 / 后；下行：左 / 右 / 顶。每格带边框与中文标签
（主视角 MAIN、前 FRONT、后 BACK、左 LEFT、右 RIGHT、顶 TOP），
随场景常驻，切换场景不重建；`camera=screen` 截图即所见画面。
- `Content-Type: image/png`、`Cache-Control: no-store`（每次都是新帧）

## 2. `GET /api/scenarios` —— 场景目录（67 条）

```json
{ "scenarios": [
    { "id":"vessel", "title":"显微血管吻合",
      "refs":"#3 颞浅动脉—大脑中动脉搭桥术; #21 指尖再植术…", "armCount":3, "dof":5 },
    { "id":"s01", "title":"颅内动脉瘤瘤颈夹闭重建",
      "refs":"#1 复杂颅内动脉瘤显微夹闭术", "armCount":3, "dof":5 },
    { "id":"octo", "title":"八臂多指离断血管重建",
      "refs":"#22 多指离断再植术（多组血管重建与时间分配）", "armCount":8, "dof":5 },
    …共 67 条…
] }
```

字段：`id`（切换用）、`title`、`refs`（对应术式目录条目）、`armCount`、`dof`。
完整场景说明（基元/针数/搏动/要点）见 `docs/SCENARIOS.md`。

## 2b. `GET /api/calibration` —— 全臂标定参数（几何/传动/关节全量）

```bash
curl -s http://127.0.0.1:8765/api/calibration | python3 -m json.tool | head -40
```

返回当前场景**每台机械臂的完整标定**（随场景切换自动更新）：

| 字段 | 含义 |
|---|---|
| `basePosition`[3] | 基座世界坐标（米） |
| `facing`[3] | 基座朝向单位向量（基座旋转 × +Z） |
| `reach` | 名义可达半径 = 各连杆长度之和（米） |
| `stiffness` / `damping` / `forceLimit` | 关节驱动刚度/阻尼/力矩上限 |
| `fingerLength` / `fingerOpen` / `fingerClosed` / `graspRadius` | 夹爪几何与抓取半径 |
| `needleTipOffsetTcp`[3] | 针尖相对 TCP 的固定偏移（米） |
| `joints[]` | 每关节：`name`、`axisLocal`[3]（父连杆系旋转轴）、`linkLength`、`limitDeg`[下限,上限]、`maxServoStepDeg` |
| `units` | `"meters/radians/degrees-as-noted"` |

**与 `/api/state` 的分工**：`state.arms[i].joints` 是**实时关节角**（弧度，随帧变化）；
`calibration.arms[i].joints` 是**结构参数**（轴/杆长/限位/伺服步限，随场景确定）。
外部 Agent 做运动学计算/可达性判断时二者缺一不可。

## 3. `POST /api/scenario` —— 切换场景

```bash
curl -X POST http://127.0.0.1:8765/api/scenario \
     -H 'Content-Type: application/json' -d '{"id":"s51"}'
```

- 请求体：`{"id": "<场景ID>"}`
- 响应：`{"ok":true}`（未知 ID 记录警告、不切换）
- **效果**：机械臂按新场景重建（数量/DOF/角色/持针变化）→ 建议切换后 `sleep 2-3` 再读状态
- 幂等：切换到当前场景 = 重建一次

## 4. `POST /api/command` —— 直接操控机械臂

请求体（按 action 取用字段）：

```json
{ "action": "MoveTip", "arm": 0, "target": [0.0, 0.60, 0.90], "open": 0.4 }
```

| action | 必需字段 | 语义 |
|---|---|---|
| `MoveTcp` | `arm`, `target`[x,y,z] | TCP 世界坐标伺服移动（接管该臂） |
| `MoveTip` | `arm`, `target`[x,y,z] | **针尖**世界坐标伺服（持针时；否则等价 MoveTcp） |
| `Gripper` | `arm`, `open`(0-1) | 夹爪开度（0 闭合 / 1 全开），仅接管态生效 |
| `Home` | `arm` | **接管该臂**并驱动全部关节回零（弧度→0）、夹爪张开；保持零位直到下一条移动命令或 `Resume`（status 显示 `api home -> zero pose`） |
| `Resume` | `arm` | **交还场景自治**（该臂回到 `auto`，继续缝合任务） |
| `Cinch` | `arm` | **手动缝合收针/打结**：把你刚用 MoveTip 驱针穿过的当前针标记完成（生成线结+缝线+血池收缩）。服务端校验针尖距该针出针点 ≤5cm，防未缝先结 |

**手动缝合工作流**（`tear` 场景实测，见 `docs/API_SUTURE_TEAR_REPORT.md`）：
`state.tasks[0].stitches[j]` 读出第 j 针 entry/exit → `MoveTip(entry+上方悬停)` →
`MoveTip(entry)` 进针（针尖 y<0.548 时 SOFA 针-组织耦合激活）→
`MoveTip(exit)` 潜行穿出 → `Cinch` 收针 → `MoveTip(抬高)` 解除耦合转下一针。

**控制模型**：任一直控命令使该臂进入 `api` 接管态（`control:"api"`）——场景执行器暂停驱动
该臂，你的命令逐帧伺服执行；`Resume`（或切场景）归还。多臂可分别接管。

**坐标系**：世界米制、Y 向上。工作空间参考：组织面 y≈0.53、伤口中心 (0, 0.55, 0.90)、
机械臂基座 y=0 沿 x 分布；针尖目标建议 y ∈ [0.40, 0.70]、|x| ≤ 0.7、z ∈ [0.5, 1.3]。

**响应**：`{"ok":true}` 命令入队（下一帧应用）；`arm` 越界或 `action` 未知返回 `{"ok":true}`
但服务端记录警告（可在 Unity Console 查看）。

## 5. `POST /api/control` —— 全局暂停/恢复

```bash
curl -X POST http://127.0.0.1:8765/api/control -d '{"action":"pause"}'
curl -X POST http://127.0.0.1:8765/api/control -d '{"action":"resume"}'
```

暂停 = 全部执行器、机械臂伺服与 **SOFA 原生步进**同时冻结（`state.paused` 可查询）。
用途：读数验证、Agent 思考窗口、批次评估。

> 注意两个"恢复"的区别：`/api/control {"action":"resume"}` 是**全局**恢复仿真；
> `/api/command {"action":"Resume","arm":i}` 是**单臂**归还自治（`control` 回到 `auto`）。

## 5b. 停滞检测（503）—— 永不假装活着

SOFA 原生求解运行在**专用步进线程**上（`backendStatus` 标注 `dedicated solver thread`），
Unity 主循环只负责渲染、API、命令与机械臂伺服，因此原生求解器即便发散卡死也不会冻结
状态服务。在此之上，API 内置两级心跳：

1. **主循环心跳**：`Update()` 每 0.25s 刷新快照并记录 `frame`。若 HTTP 线程发现心跳
   停滞 >3s（主循环被外部卡死等罕见情形），则：
   - `GET /api/state`、`GET /api/screenshot` → `503 {"error":"simulation main loop stalled","staleSeconds":…,"frame":…}`
   - 所有 `POST`（scenario/command/control）→ `503 {"ok":false,"error":…,"staleSeconds":…}`（命令**未入队**，绝不返回虚假 `ok:true`）
2. **求解器心跳**：`sofaTime`/`sofaSteps` 停止递增或 `sofaStepMs` 持续增长 = 原生求解器
   卡死/发散。此时其余功能（读状态、切场景、操控臂）仍正常，Agent 可据此决策（换场景/报告）。

> 2026-10-24 之前的失败模式——HTTP 返回 200 但快照冻结 9 分多钟、命令被无声丢弃——
> 由上述架构与 503 语义根除（详见 `docs/CODEX_TEST_REPORT.md` 终验章节）。

---

## 6. Agent 工作流配方

### 6.1 观察回路（10Hz 足够）

```python
import json, time, urllib.request

def state():
    with urllib.request.urlopen("http://127.0.0.1:8765/api/state") as r:
        return json.load(r)

s = state()
for a in s["arms"]:
    print(a["name"], a["role"], a["tcp"], a["joints"], a["status"])
```

### 6.2 闭环针尖移动（发送→轮询误差→停止）

```python
def cmd(action, arm=0, target=None, open_=None):
    body = {"action": action, "arm": arm}
    if target: body["target"] = target
    if open_ is not None: body["open"] = open_
    req = urllib.request.Request("http://127.0.0.1:8765/api/command",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    urllib.request.urlopen(req)

def tip():  # 持针臂的针尖 ≈ TCP - 针偏移；此处用 TCP 近似演示
    a = state()["arms"][0]
    return a["tcp"], a["tcpSpeed"]

cmd("MoveTip", 0, [0.0, 0.60, 0.90])          # 接管并伺服
for _ in range(120):                           # 最多 12 秒
    p, v = tip()
    if ((p[0]-0)**2 + (p[1]-0.60)**2 + (p[2]-0.90)**2) ** 0.5 < 0.02 and v < 0.08:
        break
    time.sleep(0.1)
cmd("Gripper", 0, open_=0.3)                   # 半闭持针
cmd("Resume", 0)                               # 归还自治，继续缝合
```

### 6.3 场景批量巡检（评测/回归）

```python
for sid in ["vessel", "heart", "tendon", "s03", "s51", "s97"]:
    post("/api/scenario", {"id": sid}); time.sleep(6)
    s = state()
    assert s["scenario"] == sid
    assert len(s["arms"]) == scenario_armcount[sid]
    assert all(a["dof"] in (4, 5) for a in s["arms"])
    print(sid, s["stitchesDone"], "/", s["stitchesTotal"])
```

### 6.4 与内置自治的协作模式

默认所有臂 `auto` 自治缝合。推荐 Agent 用法：
1. **监督**：轮询 `state`，盯 `stitchesDone/stitchesTotal` 与 `tissue.maxDelta`（形变>0 即 SOFA 活体在动）；
2. **介入**：对目标臂 `MoveTip/MoveTcp` 接管 → 完成自定义动作 → `Resume` 归还；
3. **调度**：`/api/scenario` 换术式、`/api/control` 冻结评估。

## 7. 约束与排错

| 现象 | 原因/处理 |
|---|---|
| 连接拒绝 | Unity 未运行或不在 Play 模式；或端口被占（改 `agentApiPort`） |
| 切场景后 state 仍旧 | 命令下一帧生效 + GET 有 0.25s 缓存，等 1s 再查 |
| `MoveTcp` 无反应 | 该臂可能为 `Idle` 角色或 `paused=true`；先 resume/换角色场景 |
| 针尖停不到目标 | 4-5 DOF 残差 ~2cm 属预期（执行器容差 2.2cm），闭环判据用 <0.025m |
| 数据全零 | SOFA 后端回退（检查 Unity Console 的 SofaUnityBackend 警告） |

**边界说明**：缝合交互为近似穿刺（接触压陷而非组织切割）；血管网为视觉层；
Unity 关闭 Play 后 API 随之停止。
