# SofaRobotAI 外部 Agent API 测试报告

- **测试日期**：2026-10-24
- **API 基地址**：`http://127.0.0.1:8765`
- **测试方式**：按 `docs/AGENT_API.md` 与 `docs/SCENARIOS.md` 对真实运行实例发送 HTTP JSON 请求；未修改任何项目代码。
- **总体结果**：7 项中 **6 项 PASS、1 项 FAIL**。失败项为第 5 项“机械臂操控闭环”中的 `Home` 归位与 `Resume` 归还自治。

| # | 测试项 | 结果 |
|---|---|---|
| 1 | 连通性与全量状态读取 | **PASS** |
| 2 | 机械臂状态读取 | **PASS** |
| 3 | 场景目录 | **PASS** |
| 4 | 场景切换 ×4 | **PASS** |
| 5 | 机械臂操控闭环 | **FAIL** |
| 6 | 全局暂停/恢复 | **PASS** |
| 7 | vessel 自治缝合回归 | **PASS** |

---

## 1. 连通性：`GET /api/state`

**结果：PASS**

- HTTP 状态：`200`
- `Content-Type`：`application/json`
- JSON 解析：成功
- `scenario`：`vessel`
- `scenarioTitle`：`显微血管吻合`
- 臂数：`3`
- `backend`：`SofaUnity (SOFA co-simulation)`
- `backendStatus`：`running, SOFA SofaAdvancePhysicsAPI, scene: Assets/StreamingAssets/SOFA/robot_cell.scn`
- 初始缝合进度：`stitchesDone=1`，`stitchesTotal=9`
- `paused`：`false`

## 2. 机械臂状态读取

**结果：PASS**

初始 `vessel` 场景下读取到 3 台机械臂。每台臂均有 `joints`、`tcp`、`role`、`status` 字段；每台 `dof=5`，且 `len(joints)=5`，`tcp` 均为 3 维坐标。

| 臂 | dof | role | status | tcp (m) | joints (rad) |
|---|---:|---|---|---|---|
| Arm_1 | 5 | Suturer | `suture: Pull stitch 2/3 (tip 0.08 m, v 0.06)` | `[-0.094378, 0.698341, 0.898420]` | `[0.788463, 1.141031, 0.298030, -0.105103, -0.011824]` |
| Arm_2 | 5 | Suturer | `waiting for task` | `[0.010503, 1.644601, 0.026700]` | `[0.339156, 0.218646, -0.432827, 0.249353, -0.010217]` |
| Arm_3 | 5 | Suturer | `waiting for task` | `[0.863551, 1.562444, -0.208909]` | `[0.910030, 0.077780, -0.395696, -0.289953, -0.947523]` |

补充数值：Arm_1 `tcpSpeed=0.060657 m/s`，Arm_2 `tcpSpeed=0.000000 m/s`，Arm_3 `tcpSpeed=0.117671 m/s`。

## 3. 场景目录：`GET /api/scenarios`

**结果：PASS**

- HTTP 状态：`200`
- 目录条目数：`65`，满足 `>=50`
- 每条目录字段均包含：`id`、`title`、`refs`、`armCount`、`dof`

抽样 3 个科室条目如下：

| 科室 | id | title | refs | armCount | dof |
|---|---|---|---|---:|---:|
| 神经外科 | `s03` | `颞浅动脉—大脑中动脉吻合` | `#3 颞浅动脉—大脑中动脉搭桥术` | 3 | 5 |
| 心脏与大血管 | `s31` | `二尖瓣瓣环成形缝合` | `#31 复杂二尖瓣修复术` | 3 | 5 |
| 肝胆胰与移植 | `s51` | `胰肠吻合重建` | `#51 胰十二指肠切除术` | 3 | 5 |

另核对介入/内镜场景 `s97`：`ESD 黏膜缺损关闭`，`armCount=2`，`dof=5`，字段完整。

## 4. 场景切换 ×4

**结果：PASS**

每次均使用 `POST /api/scenario`，请求体为 `{"id":"<场景ID>"}`；每次返回 HTTP `200` 与 `{"ok":true}`，等待 3 秒后读取 `GET /api/state`。

| 场景 | 手册基元/预期 | 实际 scenario | 实际 title | 臂数 | dof | stitchesDone / Total | 判定 |
|---|---|---|---|---:|---|---|---|
| `s03` | 神经 Tube，3×5，8 针 | `s03` | `颞浅动脉—大脑中动脉吻合` | 3 | `[5,5,5]` | `0 / 8` | PASS |
| `s31` | 心脏搏动 Surface，3×5，8 针 | `s31` | `二尖瓣瓣环成形缝合` | 3 | `[5,5,5]` | `0 / 8` | PASS |
| `s51` | 肝胆 Tube，3×5，8 针 | `s51` | `胰肠吻合重建` | 3 | `[5,5,5]` | `0 / 8` | PASS |
| `s97` | 内镜 Surface，2×5，6 针 | `s97` | `ESD 黏膜缺损关闭` | 2 | `[5,5]` | `1 / 6` | PASS |

四次切换的 `stitchesTotal` 分别为 `8、8、8、6`，均大于 0。

## 5. 机械臂操控闭环

**结果：FAIL**

### 5.1 切回 heart：PASS

- `POST /api/scenario {"id":"heart"}`：HTTP `200`，`{"ok":true}`
- 等待 3 秒后：
  - `scenario=heart`
  - `scenarioTitle=不停跳冠状动脉搭桥`
  - 臂数 `2`
  - Arm_1 `dof=5`，`control=auto`
  - 缝合进度 `1 / 4`

### 5.2 MoveTip 移动/接近：PASS

目标：`[0.0, 0.62, 0.90]`，命令：`POST /api/command {"action":"MoveTip","arm":0,"target":[0.0,0.62,0.90]}`。

- 命令响应：HTTP `200`，`{"ok":true}`
- 基线 TCP：`[0.002764, 0.499236, 0.912037]`
- 基线到目标距离：`0.121394 m`
- 首次观察到 `control="api"` 的延迟：`0.257 s`
- 轮询时长：约 `10 s`，有效采样 `39` 次
- 最大 `tcpSpeed`：`0.871437 m/s`
- 最小目标距离：`0.040308 m`
- 10 秒末目标距离：`0.047072 m`
- 10 秒末 TCP：`[0.032983, 0.598711, 0.925975]`
- TCP 相对基线位移：`0.104894 m`
- 10 秒末 `control="api"`，`status="api move -> (0.00,0.62,0.90)"`

该子项表明机械臂确实进入 API 接管态并显著向目标移动。

### 5.3 Home 归位：FAIL

发送 `POST /api/command {"action":"Home","arm":0}` 后返回 HTTP `200`、`{"ok":true}`，但观察约 2 秒（8 个采样）后：

- `control` 仍为 `api`
- `status` 仍为 `api move -> (0.00,0.62,0.90)`
- TCP 仍为 `[0.026633, 0.594215, 0.932603]`
- joints 为 `[0.496848, 0.758602, 0.742217, 0.906309, -0.185324]`，并未归零

手册要求 `Home` 使关节归零、夹爪张开；实际 joints 未归零，因此该子项 FAIL。

### 5.4 Resume 归还自治：FAIL

发送 `POST /api/command {"action":"Resume","arm":0}` 后返回 HTTP `200`、`{"ok":true}`，但观察约 2 秒（8 个采样）后：

- `control` 仍为 `api`，未回到 `auto`
- `status` 仍为 `api move -> (0.00,0.62,0.90)`
- TCP 与 joints 保持在 Home 后的停滞状态
- `paused=false`

手册要求 `Resume` 将该臂交还场景自治并使 `control` 回到 `auto`；实际未发生，因此该子项 FAIL。

### 5.5 第 5 项综合判定

MoveTip 子项通过，但整项“操控闭环”要求 Home 归位、Resume 归还自治并回到 `auto`。由于 Home 与 Resume 均未按手册完成，第 5 项整体判定 **FAIL**。

## 6. 暂停/恢复：`POST /api/control`

**结果：PASS**

测试时当前场景为 `heart`，初始 `paused=false`，`stitchesDone=1`，`stitchesTotal=4`。

### Pause

- `POST /api/control {"action":"pause"}`：HTTP `200`，`{"ok":true}`
- 约 3 秒窗口内采样 `6` 次
- 全部采样 `paused=true`
- `stitchesDone` 全部保持 `1`，取值集合为 `{1}`
- Arm_1 `tcpSpeed=0.000000 m/s`

### Resume

- `POST /api/control {"action":"resume"}`：HTTP `200`，`{"ok":true}`
- 约 3 秒窗口内采样 `6` 次
- 全部采样 `paused=false`

说明：由于第 5 项失败后 Arm_1 仍停留在 `control="api"`，heart 场景在该复验窗口内 `stitchesDone` 仍为 `1`。本项按手册的全局暂停/恢复语义验证 `state.paused` 与暂停期间进度不变，判定 PASS。

## 7. vessel 自治缝合回归

**结果：PASS**

- `POST /api/scenario {"id":"vessel"}`：HTTP `200`，`{"ok":true}`
- 等待 3 秒后：`scenario=vessel`，`stitchesDone=0`，`stitchesTotal=9`
- 连续观察约 20 秒，采样 `20` 次
- `t=0.001 s`：`stitchesDone=0`
- `t=10.115 s`：`stitchesDone=1`
- `t=18.208 s`：`stitchesDone=2`
- 20 秒末：`stitchesDone=2`，增长 `+2`
- 期间 `paused=false`，三台臂 `control` 均为 `auto`
- Arm_1 在 20 秒末 `tcpSpeed=0.406021 m/s`，说明执行器仍在运动

内置自治缝合在 vessel 场景中持续运行，判定 PASS。

---

## 最终状态

最终读取到的状态为：

- `scenario=vessel`
- `scenarioTitle=显微血管吻合`
- `paused=false`
- 臂数 `3`
- `dof=[5,5,5]`
- `control=[auto,auto,auto]`
- `stitchesDone=9`
- `stitchesTotal=9`

场景已按要求最终留在 `vessel`。

## 总结论

外部 Agent **能够完整读取系统状态、场景目录和每台机械臂的 `joints/tcp/role/status` 等状态**，并能够可靠完成 `vessel/heart/s03/s31/s51/s97` 的场景切换。`MoveTip` 也能够使 arm 0 进入 `control="api"` 并实际向目标移动。

但外部 Agent **不能完整操控这些场景**：`Home` 未将关节归位，`Resume` 未把 `control` 从 `api` 恢复为 `auto`，因此直接操控闭环无法按手册完成。全局暂停/恢复与 vessel 内置自治缝合回归均正常。

因此，结论是：**读取能力完整；场景切换与 MoveTip 可用；但机械臂操控闭环存在 Home/Resume 缺陷，外部 Agent 尚不能完整操作这些场景。**

---

# 复测（修复后代码实例）

- **复测时间**：2026-10-24 07:20–07:30 America/Chicago
- **API 基地址**：`http://127.0.0.1:8765`
- **复测方式**：仅通过 HTTP JSON API 请求与轮询；未修改项目代码，未改动 Unity 进程或代码文件。
- **复测结果**：4 项中 **1 项 PASS、3 项 FAIL**。
- **重要现象**：实例启动初期 `vessel` 状态仍在推进（`stitchesDone` 从 `2` 达到 `6`），但随后所有 POST 均继续返回 HTTP `200` 与 `{"ok":true}`，状态快照却完全停止变化：场景切换、`MoveTip`、`Home`、单臂 `Resume`、全局 `pause/resume` 均未被状态机消费。因此本轮不能确认两个已修复 bug 在有效执行路径上的行为；观测到的是更上游的“命令接受但状态不应用/主循环停止推进”问题。

| 复测 # | 测试项 | 结果 |
|---|---|---|
| R1 | `GET /api/state` 读取 `vessel` | **PASS** |
| R2 | heart 机械臂操控闭环 | **FAIL** |
| R3 | 全局控制与单臂 Resume 互不干扰 | **FAIL / 未有效执行** |
| R4 | 切回 vessel 并观察 15 秒自治进度 | **FAIL** |

## R1. `GET /api/state`：vessel 初始状态

**结果：PASS**

首次请求返回：

- HTTP 状态：`200`
- JSON 解析：成功
- `scenario=vessel`
- `scenarioTitle=显微血管吻合`
- `backend=SofaUnity (SOFA co-simulation)`
- `paused=false`
- 臂数：`3`
- `dof=[5,5,5]`
- `stitchesDone=2`，`stitchesTotal=9`
- Arm_1：`control=auto`，`tcpSpeed=0.577188 m/s`，`tcp=[-0.054428,0.580538,0.906998]`

复测脚本开始时再次读取到 `vessel`、`3` 臂、`paused=false`，此时进度已自然推进为 `stitchesDone=6/9`。这说明读取接口在复测开始时正常，且仿真最初确实在推进。

## R2. heart 机械臂操控闭环

**结果：FAIL**

### R2.1 切换 heart 与 MoveTip

- `POST /api/scenario {"id":"heart"}`：HTTP `200`，`{"ok":true}`
- 等待 3 秒后读取：仍为 `scenario=vessel`、`stitchesDone=6/9`，未切换。
- 多次发送场景命令后，状态最终变为 `heart`，但该场景已经完成：`stitchesDone=4/4`，Arm_1 `control=auto`、`status="waiting for task"`。
- 在该 `heart` 状态下发送：

```json
{"action":"MoveTip","arm":0,"target":[0.0,0.62,0.90]}
```

- 命令响应：HTTP `200`，`{"ok":true}`
- 轮询约 `6.0 s`，采样 `31` 次
- `control="api"` 次数：`0/31`
- TCP 全部保持为 `[-0.041257,0.688182,1.013553]`
- TCP 位移：`0.000000 m`
- `status` 全部保持 `waiting for task`

命令未被应用，无法进入有效 API 接管态，因此不满足 `control=="api"` 与 TCP 明显移动的判据。

### R2.2 Home

在 MoveTip 未生效的状态下继续发送 `POST /api/command {"action":"Home","arm":0}` 以检查命令队列是否有任何响应：

- 命令响应：HTTP `200`，`{"ok":true}`
- 轮询 `5.132 s`，采样 `21` 次
- 所有采样均为 `control="auto"`、`status="waiting for task"`
- 所有采样的最大关节绝对值均保持 `0.752523 rad`
- 21 个采样中，满足“每个关节 `|弧度|<0.05`”的采样数为 `0`
- 5 秒末 joints 仍为：

```json
[0.408893, 0.752523, 0.679728, 0.684918, -0.180403]
```

Home 未被状态机执行。

### R2.3 单臂 Resume

- `POST /api/command {"action":"Resume","arm":0}`：HTTP `200`，`{"ok":true}`
- 轮询 `5.133 s`，采样 `21` 次
- 所有采样均为 `paused=false`
- 所有采样均为 `control="auto"`、`status="waiting for task"`

由于此前 MoveTip 从未把该臂置为 `control="api"`，本轮没有出现“从 `api` 回到 `auto`”的有效转换；也没有观察到 `suturing/stabilizing` 类活动状态。该子项不能视为通过。

### R2.4 判定

MoveTip、Home、Resume 的 HTTP 入队响应均为 `ok:true`，但对应状态均完全不变。R2 整体 **FAIL**。本轮失败形态不同于上一轮“MoveTip 生效但 Home/Resume 不生效”：本轮 MoveTip 本身未被消费，因此两个指定修复未能被有效验证。

## R3. 全局控制与单臂 Resume 互不干扰

**结果：FAIL / 未有效执行**

执行序列与结果：

1. `POST /api/control {"action":"pause"}`
   - HTTP：`200`
   - 响应：`{"ok":true}`
   - 等待 `0.5 s` 后读取：`paused=false`，未变为暂停态。
2. `POST /api/command {"action":"Resume","arm":0}`
   - HTTP：`200`
   - 响应：`{"ok":true}`
   - 等待 `0.5 s` 后读取：仍为 `paused=false`，Arm_1 `control=auto`、`status="waiting for task"`。
3. `POST /api/control {"action":"resume"}`
   - HTTP：`200`
   - 响应：`{"ok":true}`
   - 等待 `0.5 s` 后读取：仍为 `paused=false`。

由于全局 `pause` 没有被应用到 `state.paused`，无法构造“全局暂停保持不变、单臂 Resume 独立执行”的有效验证条件。虽然两次 Resume 的 HTTP 分发未再表现出“全局 resume 截获单臂 Resume”的直接证据，但状态机未消费命令，因此不能判定互不干扰语义已修复，R3 **FAIL**。

## R4. 切回 vessel 并观察 15 秒

**结果：FAIL**

- `POST /api/scenario {"id":"vessel"}`：HTTP `200`，`{"ok":true}`
- 等待 3 秒后：仍为 `scenario=heart`、`paused=false`、`stitchesDone=4/4`
- 连续观察 `15.214 s`，采样 `31` 次
- 所有采样均为：`scenario=heart`、`paused=false`、`stitchesDone=4`、`stitchesTotal=4`
- `stitchesDone` 增长：`4 → 4`，增量 `+0`
- 15.214 秒末仍为 `heart 4/4`

另在复测早期，状态曾停留在 `vessel 6/9` 并连续观察 `15.205 s`、采样 `31` 次，进度同样保持 `6 → 6`、增量 `+0`。这进一步支持“状态推进停止”而非仅 heart 场景自身的问题。

## 复测最终状态

复测结束时实际读取到：

- `scenario=heart`
- `scenarioTitle=不停跳冠状动脉搭桥`
- `paused=false`
- `stitchesDone=4`
- `stitchesTotal=4`
- 臂数 `2`
- Arm_1 `control=auto`
- Arm_1 `status="waiting for task"`
- Arm_1 `tcp=[-0.041257,0.688182,1.013553]`
- Arm_1 最大关节绝对值 `0.752523 rad`

虽然最后已按要求发送切回 `vessel` 的 HTTP 请求，但该请求未被状态快照应用，最终状态未能回到 `vessel`。

## 复测总体结论

本轮复测中，外部 Agent **可以完整读取 HTTP 状态快照**：`GET /api/state` 返回结构化 JSON，包含场景、进度、每臂 `joints/tcp/control/status`、SOFA 后端与组织状态。

但本轮实例在自然推进到中途后进入静止快照/命令不消费状态：所有 POST 均返回 `200 {"ok":true}`，但场景切换、`MoveTip`、`Home`、单臂 `Resume`、全局 `pause/resume` 都不改变状态。特别是 `MoveTip` 在 `31` 次采样中 `0` 次进入 `control="api"`，全局 `pause` 后 `paused` 仍为 `false`。因此，本轮不能确认“Resume 归单臂”和“Home 保持态”两个修复在有效执行路径上已通过。

**复测后的当前结论是：外部 Agent 可以读取状态，但不能在该实例上可靠、完整地操控场景或机械臂。** HTTP `{"ok":true}` 只能证明请求被监听器接受，不能证明命令被仿真主循环执行。需要在不发生快照冻结/命令队列停止消费的 fresh 未完成场景实例上重跑 R2–R4，才能对这两个修复做出有效确认。

---

# 终验（最终验证）

- **终验时间**：2026-10-24 10:44–10:47 America/Chicago（UTC 15:44–15:47）
- **API 基地址**：`http://127.0.0.1:8765`
- **终验方式**：仅使用 HTTP API（`GET /api/state`、`GET /api/screenshot`、`POST /api/command`、`POST /api/scenario`）；未修改代码，未操作 Unity UI。
- **终验结果**：**FAIL（0/4 功能项通过）**。HTTP 监听器仍可返回 `200 {"ok":true}`，但状态快照完全冻结，截图主线程渲染超时，命令未被主循环消费。

| 终验 # | 测试项 | 结果 |
|---|---|---|
| F1 | 新鲜度：timestamp 与 frame 连续变化 | **FAIL** |
| F2 | 截图：PNG、960×540、>30KB、两次字节不同 | **FAIL** |
| F3 | 操控闭环：MoveTcp→api，Home→归零，Resume→auto | **FAIL** |
| F4 | 场景切换：s31 校验后切回 vessel | **FAIL** |

## F1. 新鲜度：连续两次 `GET /api/state`

**结果：FAIL**

两次请求均返回 HTTP `200`，实际间隔 `1.010071 s`：

- 第 1 次：`timestamp=2026-10-24T15:37:20.8528510Z`，`frame=1618`
- 第 2 次：`timestamp=2026-10-24T15:37:20.8528510Z`，`frame=1618`
- `timestamp` 变化：`false`
- `frame` 变化：`false`，增量 `0`

判据要求 timestamp 与 frame 均变化；二者均完全不变，因此 **FAIL**。

附加诊断：终验前的 `POST /api/control {"action":"resume"}` 也返回 HTTP `200` 与 `{"ok":true}`，但 1 秒后快照仍停留在同一 timestamp/frame；同时 `paused=false`、`editorPaused=false`。这说明不是可查询的全局暂停或编辑器暂停状态，而是主循环/快照更新停止。

## F2. 截图：`GET /api/screenshot`

**结果：FAIL**

第 1 次请求（应保存 `/tmp/final_shot.png`）：

- HTTP：`503`
- `Content-Type`：`application/json`（不是 `image/png`）
- 响应体：`{"error":"capture timeout"}`
- 响应大小：`27 bytes`
- 耗时：`4.033510 s`
- `/tmp/final_shot.png`：未创建，有效文件大小 `0 bytes`

间隔 `3.000 s` 后第 2 次请求：

- HTTP：`503`
- `Content-Type`：`application/json`
- 响应体：`{"error":"capture timeout"}`
- 响应大小：`27 bytes`
- 耗时：`4.038331 s`

因为没有得到 PNG，无法验证 960×540 尺寸、`>30KB` 文件大小，也没有两张有效 PNG 可比较字节差异。两次 503 JSON 错误响应字节相同。**FAIL**。

## F3. 操控闭环（arm 0）

**结果：FAIL**

测试开始时当前场景为 `vessel`，arm 0 初始状态：

- `control=auto`
- `tcp=[-1.0447036027908325, 1.5702365636825562, -0.4017089009284973]`
- `joints=[0.11055268347263336, -0.0895504429936409, -0.28489282727241516, -0.3353777229785919, 0.0011104345321655273]`
- 最大关节绝对值：`0.3353777229785919 rad`

### F3.1 `MoveTcp [0.3,0.8,0.9]`

**结果：FAIL**

- `POST /api/command`：HTTP `200`，`{"ok":true}`
- 轮询时长：`0.998010 s`
- 采样数：`17`
- `control=="api"` 次数：`0/17`
- 末次采样：`control=auto`
- 末次 TCP：`[-1.0447036027908325, 1.5702365636825562, -0.4017089009284973]`
- TCP 位移：`0.000000 m`

未满足“1 秒内 `control=='api'`”。

### F3.2 `Home`

**结果：FAIL**

- `POST /api/command`：HTTP `200`，`{"ok":true}`
- 轮询时长：`4.998020 s`
- 采样数：`83`
- 满足全部关节 `|弧度|<0.05` 的采样数：`0/83`
- 末次最大关节绝对值：`0.3353777229785919 rad`
- 末次 joints 与命令前完全相同：
  `[0.11055268347263336, -0.0895504429936409, -0.28489282727241516, -0.3353777229785919, 0.0011104345321655273]`

未满足“5 秒内 joints 全部 `|弧度|<0.05`”。

### F3.3 单臂 `Resume`

**结果：FAIL / 未形成有效验证条件**

- `POST /api/command`：HTTP `200`，`{"ok":true}`
- 轮询时长：`0.948064 s`
- 采样数：`17`
- Resume 前：`control=auto`
- Resume 后：`control=auto`，`status="waiting for task"`

虽然字面读到 `auto`，但 MoveTcp 从未使该臂进入 `api`，因此没有发生有效的 `api → auto` 转换；不能证明单臂 Resume 修复已生效。

## F4. 场景切换抽查：`s31` → `vessel`

**结果：FAIL**

### F4.1 切换 `s31`

- `POST /api/scenario {"id":"s31"}`：HTTP `200`，`{"ok":true}`
- 等待：`3.000 s`
- 实际读取：`scenario=vessel`（不是 `s31`）
- 臂数：`3`
- 角色：`["Suturer", "Suturer", "Suturer"]`
- `Suturer` 存在：`true`
- `Stabilizer` 存在：`false`
- `stitchesTotal=9`（期望 `8`）
- `stitchesDone=9`
- 快照仍为：`timestamp=2026-10-24T15:37:20.8528510Z`，`frame=1618`

s31 的四项校验（场景 ID、3 臂、suturer+stabilizer、8 针）均未满足。

### F4.2 切回 `vessel`

- `POST /api/scenario {"id":"vessel"}`：HTTP `200`，`{"ok":true}`
- 等待：`3.000 s`
- 最终读取：`scenario=vessel`
- 臂数：`3`
- `stitchesDone=9`，`stitchesTotal=9`
- `paused=false`，`editorPaused=false`

最终场景按要求停留在 `vessel`。但这不是一次有效的切换回归：s31 命令未生效，快照原本就停留在 `vessel`，因此“切回”只证明了最终字段值，不能证明场景切换命令被主循环执行。

## 终验总结

终验结束时，`GET /api/state` 仍返回同一个冻结快照：

- `timestamp=2026-10-24T15:37:20.8528510Z`
- `frame=1618`
- `scenario=vessel`
- `paused=false`
- `editorPaused=false`
- `stitchesDone=9/9`

从快照 timestamp 到终验完成时间（UTC `2026-10-24T15:47:01Z`），该快照已静止约 **9 分 40 秒**；期间所有 POST 均返回 `200 {"ok":true}`，但 MoveTcp、Home、Resume、s31 切换均未被状态消费。`GET /api/screenshot` 两次在约 4 秒后返回 `503 capture timeout`。

**"外部 Agent 能否完整读取状态（含截图）并操控场景"的终验判断为：否。** 当前实例只能读取到静态/陈旧 JSON 快照，不能获得新鲜状态或截图；操控命令虽然被 HTTP 层接受，但没有被仿真主循环执行。终验不通过，需要在主循环真正全速运行且 `frame/timestamp` 持续递增的实例上重跑本轮全部项目。

---

# 第三轮（2026-10-24 下午）：根因修复、八臂全 API 手术终验 —— **17/17 PASS**

## R5. 上一轮两大故障的根因

### 根因 1：SOFA 原生求解器同步运行在 Unity 主线程上

SofaUnity 的 `SofaContext.Update()` 默认以**同步模式**调用原生
`sofaPhysicsAPI_step`——CG 线性求解器直接跑在 Unity 主线程。当日志出现
`denominator threshold reached at first iteration of CG`（求解发散前兆）后，
一次原生求解即卡死整个主循环：HTTP 后台线程继续服务**冻结的旧快照**、
命令入队但永不消费、截图必然超时、`paused=false` 却 9 分多钟 frame/timestamp
纹丝不动——与终验 F1-F4 的全部症状逐条吻合（冻结时刻与 macOS
`IMKCFRunLoopWakeUpReliable` 唤醒失败日志同秒，该日志是主线程停摆的**结果**而非原因）。

原生异步模式不可用：实测 `asyncStep()` 返回成功但 `sofaTime` 恒为 0（该公开
原生构建的 async 路径为空实现，kick 首步亦无效）。

**修复**：`SofaUnityBackend` 将原生步进迁移到**专用 C# 线程**
（`SofaSolverStepper`，20ms 节拍，`IsSofaUpdating=false` + 关闭消息泵避免与
`SofaContext.Update` 竞争）。原生求解器即便永久卡死也**只挂起该线程**：
Unity 主循环、HTTP API、命令消费、场景切换全部存活，`sofaStepMs` 持续增长
即向 Agent 暴露求解器卡死。关停顺序保证先停线程再做原生清理；线程卡死时
跳过原生清理以保住编辑器。

### 根因 2：编辑器强杀残留触发"悬空场景备份"模态对话框

对加载了 SOFA 原生上下文的编辑器进程 `kill` 后，`Library/EditorInstance.json`
残留，后续每次启动都停在 `HandleDanglingSceneBackups` 模态对话框（进程采样
证实主线程阻塞于 `ShowAlertDialog`），表现为"启动后无日志、CPU 0%"。
`tools/launch_unity_detached.sh` 现在会在启动前清理该残留（并以
`start_new_session` 脱离终端会话，避免启动器退出连坐杀掉编辑器）。

### 附带修复

- `Home` 现在是**接管命令**：即使该臂处于 `auto` 也会进入 `api` 接管并
  **保持零位**（`_holdHome`），status 如实显示 `api home -> zero pose`
  （此前对 auto 臂发 Home 毫无效果、对 api 臂 status 仍显示 "api move"）。
- 八臂场景 `octo`：4-DOF 臂在弧形布局半径 1.25m 下最远臂到伤口 1.36m，
  超出 Jacobian-transpose 伺服实际收敛边界（卡在 `Pierce, tip 0.057m`，
  恰在 `Arrived()` 超时放行门槛 2×fineRadius=0.044m 之外形成死锁带）。
  改为 **5-DOF**（仍在手术臂 4-5 要求内）并将弧半径降至 **1.05m**（最远
  臂 1.18m，裕度 0.17m）：12 针全部完成。
- `SpawnArms` 每次场景切换泄漏一组 `_dropBins`（不清空重复累加）——已修。
- API 新增两级心跳（见 `AGENT_API.md` §5b）：主循环停滞 >3s 时
  `GET /api/state`、截图与全部 POST 返回 **503 + staleSeconds**，
  绝不再用 200/`ok:true` 伪装活着；求解器心跳
  `sofaTime/sofaSteps/sofaStepMs` 入 `/api/state`。

## R6. 八臂全 API 手术终验（`tools/eight_arm_api_demo.py`）

真实运行实例、纯 HTTP JSON 驱动，覆盖**全部 API 面**与**全部 8 台机械臂**：

| # | 检查项 | 结果 | 关键证据 |
|---|---|---|---|
| 1 | state 可读且新鲜 | **PASS** | frame 1193→1488，timestamp 变化 |
| 2 | 场景目录 ≥50 | **PASS** | 66 条（10 科室 + 4 旗舰） |
| 3 | octo 八臂场景存在 | **PASS** | armCount=8, dof=5 |
| 4 | 切换到 octo | **PASS** | scenario=octo |
| 5 | 8 臂生成 | **PASS** | 8 arms |
| 6 | 手术臂 DOF 范围 4-5 | **PASS** | [5×8] |
| 7 | calibration 全参数 | **PASS** | 8 臂 × 基座/朝向/可达/驱动/夹爪/针偏移/逐关节轴杆长限位 |
| 8 | MoveTcp 八臂全部接管 | **PASS** | control = api ×8 |
| 9 | MoveTcp 八臂真实位移 | **PASS** | 0.143-0.215 m/臂 |
| 10 | Gripper 八臂 | **PASS** | 0.2→1.0 开合循环 |
| 11 | Home 八臂归零 | **PASS** | max \|q\| = 0.0252 rad（判据 <0.05） |
| 12 | Resume 八臂归还自治 | **PASS** | control = auto ×8 |
| 13 | 全局暂停 | **PASS** | paused=true |
| 14 | 暂停期间进度冻结 | **PASS** | stitches 不变 |
| 15 | 全局恢复 | **PASS** | paused=false |
| 16 | **八臂完成整台手术** | **PASS** | **12/12 针**，sofaFps≈752 |
| 17 | 截图 | **PASS** | 960×540 PNG，214952 字节 |

**结果：17/17 PASS**（报告：`docs/EIGHT_ARM_API_DEMO_REPORT.md`，截图：
`docs/images/octo_surgery.png`）。上一轮全部 FAIL 的 Home、Resume、MoveTcp
闭环、截图与场景切换在有效执行路径上全部通过。

## R7. 稳定性压力测试

修复后实例连续 60 秒高速切换 8 个场景 ×3 轮（s03/s31/s51/vessel/heart/s97/s61/s86）：

- **24/24 次切换成功**：每次 2.5s 后 scenario、臂数、组织状态全部就位
- 每次读取 frame 均递增（终帧 80350），无一次 503/停滞
- `sofaTime/sofaSteps` 持续递增，`sofaStepMs` 4-18ms 健康区间

## R8. 结论

1. "主循环冻结、命令不消费、快照停更"的根因（同步原生步进 + CG 发散）已
   **结构性消除**：原生求解器与 Unity 主循环彻底解耦，且两级心跳 + 503 语义
   保证任何未来停滞都被立即、诚实地暴露给外部 Agent。
2. 外部 AI Agent 现在可以**完整读取状态（含截图）并操控场景**：八臂同时
   接管/移动/夹爪/归位/归还，并完成一整台 12 针手术（17/17 验证）。
3. API 完备性证据链：state / scenarios / calibration / scenario / command
   （MoveTcp·MoveTip·Gripper·Home·Resume）/ control / screenshot 全部端到端可用。
