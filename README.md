# SofaRobotAI — SofaUnity 多机械臂 AI Agent 操作场景

基于 [SofaUnity](https://github.com/InfinyTech3D/SofaUnity)（SOFA 物理引擎的 Unity 集成）初始化的 Unity 3D 项目：
**N 台多自由度机械臂，每台由一个可插拔的 AI Agent 大脑控制**，在程序化生成的初始化场景中自主完成抓取-搬运-投放任务，并支持**手术缝合演示：3 个 SOFA 软体器官 + 血管，机械臂持针缝线关闭切口**。

```
打开即玩（macOS / Windows / Linux）:  Unity 6000.6 LTS + Unity PhysX 后端
完整 SOFA 协同仿真:                   macOS arm64（已验证）/ Windows x64
```

## 场景内容

运行 `Assets/Scenes/MultiArmAgentScene.unity` 并按 Play。**65 个可随时切换的手术场景**
（3 个手工旗舰 + 62 个参数化术式目录条目，覆盖 100 条术式目录的全部 10 个科室）：

**切换方式**：按键 `1-9` 直达前九个 / `PgUp` `PgDn` 翻页 / 左上角前后按钮 / Agent API。
每个场景携带：组织配色、血管网密度、伤口基元（管状吻合 `Tube` / 表面切口 `Surface`）、
是否搏动（SOFA 心跳推压球）、机械臂数量与自由度（全部 4-5 DOF）、针脚数与术式要点。

三个手工旗舰：

| 场景 | 术式目录映射 | 机械臂 | 特色 |
|---|---|---|---|
| `vessel` 显微血管吻合 | #3/#21-22/#30 | 3 × 5 DOF 轮流缝合 | 密布血管网组织垫，主干血管断裂 9 针关闭 |
| `heart` 不停跳冠脉搭桥 | #34 | 2 × 5 DOF（1缝+1稳） | SOFA 心跳推压球持续搏动表面缝合 |
| `tendon` 屈肌腱端端修复 | #25 | 2 × 4 DOF | 肌腱断端环形 4 针吻合 |

62 个目录场景 `s01`–`s98`（`ProcedureCatalog.cs`）按科室分布：神经外科 10、眼科 6、
手外/显微 9、心脏大血管 9、胸外/气道 8、肝胆胰移植 7、胃肠盆腔 4、泌尿妇科 5、
骨科耳鼻喉 2、介入内镜 2 —— 均为同一套 SOFA FEM 组织 + 针刺力耦合 + 缝合执行器的
参数化实例（`GET /api/scenarios` 返回全部 65 条及各自术式映射）。

组织为 SOFA FEM 软体（真实形变 + 针尖力耦合），`P` 键或 API 可暂停/恢复（同时暂停 SOFA 步进）。
**搬运模式（`suturingMode = false`）**：随机工件抓放演示。

## Agent API（外部 AI Agent 控制）

运行时自动开启本地 HTTP JSON API（默认 `127.0.0.1:8765`，
`MultiArmAgentManager.agentApiPort` 可改）：

```
GET  /api/state      # 全量快照：场景、每臂(关节角/TCP/速度/角色/持针)、任务进度、组织形变
GET  /api/scenarios  # 场景目录（术式映射、臂配置）
POST /api/scenario   {"id":"vessel|heart|tendon"}     # 切换场景（机械臂随之重配）
POST /api/control    {"action":"pause"|"resume"}       # 暂停/恢复（含 SOFA 步进）
POST /api/command    {"action":"MoveTcp|MoveTip|Gripper|Home|Resume",
                      "arm":0, "target":[x,y,z], "open":0.5}   # 直接操控单臂
```

命令经线程安全队列在主线程执行；`MoveTip` 以针尖为目标（带针时）。
进程内同等接口：`SurgicalAgentApi.BuildStateJson()/EnqueueCommand()`。
快速验证：`curl http://127.0.0.1:8765/api/state | python3 -m json.tool`。

## 架构

```
MultiArmAgentManager            场景唯一入口：初始化后端、生成 N 臂、任务调度、HUD
 ├── IRobotPhysicsBackend       物理后端抽象（Agent 层只依赖此接口）
 │    ├── SofaUnityBackend      SofaContext 加载 robot_cell.scn，
 │    │                         SOFA 仿真软体器官/血管（macOS arm64 已验证 / Windows）
 │    └── UnityPhysicsBackend   任意平台: ArticulationBody + PhysX（默认，零依赖）
 ├── ArmAgent ×N                单臂具身智能体：决策节奏 + 技能执行状态机
 │    ├── IAgentBrain           可插拔大脑
 │    │    ├── HeuristicBrain   雅可比转置伺服 + 抓放策略（零依赖，实时）
 │    │    └── LlmAgentBrain    OpenAI 兼容 /chat/completions 高层规划
 │    └── SutureExecutor        缝合状态机（持针任务时接管，见下）
 ├── RobotArmController         单臂控制 API：ServoToward()（阻尼最小二乘）、
 │                             夹爪、FixedJoint 物理抓取
 ├── RobotArmBuilder            程序化构建多自由度运动链（ArmSpec 4/6/7 DOF 预设）
 ├── TaskSystem                 RobotTask 基类 + ManipulationTask + 贪心分配器
 └── Surgery/                   手术缝合子系统
      ├── OrganCellBuilder      手术台 + 3 器官/血管/切口/针点（与 .scn 几何同步）
      ├── NeedleTool            程序化弯针（半圆管网格）+ 缝线 LineRenderer
      ├── SutureExecutor        入针→潜行→出针→拉线→收拢打结 FSM
      ├── SutureTask/StitchPoint 缝合任务与针点（入针/出针两侧）
      └── SutureVisuals         打结球、收拢带、切口收窄、缝线路径
```

缝合任务的技能执行由 `SutureExecutor` 接管（不走大脑轮询）：它把针尖目标
位置换算为 TCP 伺服目标，复用 `RobotArmController.ServoToward()`，每针完成后
更新缝线锚点与切口视觉。SOFA 侧的器官/血管是真实 FEM 体；针穿刺交互为
近似实现（公开 SofaUnity API 无组织切割/穿刺入口点，进阶需其私有模块或
SofaCarving 插件扩展）。

## 快速开始（任意平台，2 分钟）

1. 用 **Unity 6000.6 LTS**（本机已装 `6000.6.7f1-arm64`；2021.3/2022.3 亦可打开，
   注意本机 `/Applications/Unity/Hub/Editor/2022.3.47f1` 为残缺安装，需先补装）
2. 首次打开会编译 `Assets/SofaUnity`（官方插件，纯 C# 部分全平台可编译）
3. 打开 `Assets/Scenes/MultiArmAgentScene.unity`，按 **Play**
4. 观察 HUD：每台臂依次 `MoveTo → Grasp → 搬运 → Release → 投放`，自动循环

> macOS 提示：控制台会出现一条 SofaVerseAPI 仅 Windows 的警告，
> 属预期行为——后端自动回退 Unity PhysX，场景完全可玩。

在编辑器菜单 **SofaRobotAI → Build Multi-Arm AI Scene** 可随时重建该场景。

## 启用 SOFA 协同仿真（macOS arm64 / Windows x64）

SofaUnity 的原生库（SofaVerseAPI）发布渠道：

- **macOS (Apple Silicon)**：官方 Release `SofaUnity_v26.06.00` 附带
  `SOFA_MacOS_v26.06.zip`（223 个 dylib，SofaPhysicsAPI 25.12 构建）
- **Windows x64**：Release 的 `SOFA_v24.12-dll.zip.zip` 或内置 DLL 的 unitypackage

**macOS 步骤**（本机已验证可跑通）：

```bash
# 1. 系统依赖（官方 dylib 链接了 homebrew 路径，但 zip 里没带）
brew install glfw glew tinyxml2

# 2. 一键安装：克隆 SofaUnity + 打兼容补丁 + 下载并安装原生库
./setup-sofaunity.sh
```

3. Unity 中打开场景，`MultiArmAgentManager` 的 **backend** 改为 **SofaUnity**，Play
4. 验证：Console 出现 `[SofaUnity] loading sofa.ini...`（由原生侧打印），
   HUD 显示 `backend: SofaUnity (SOFA co-simulation)`，
   `SofaUnity_Context` 节点加载 `Assets/StreamingAssets/SOFA/robot_cell.scn`
   （FEM 软体立方体场景）；若原生库不可用会自动回退 PhysX 并给出原因

Windows 步骤相同，只是把 DLL 解压到
`Assets/SofaUnity/Core/Plugins/Native/Windows/x64/`。

**版本配对说明**：macOS zip 内的原生库是 25.12 ABI，比 git master 的 C# 绑定旧——
有 9 个入口点缺失（version、GUI init、场景图查询等），兼容补丁里已做惰性 shim
（缺失时返回安全值，SOFA 场景加载与步进不受影响）。
原生库还附带 `libArticulatedSystemPlugin`（SOFA 关节机器人插件），
后续把机械臂关节求解迁到 SOFA 侧可用它扩展 `SofaUnityBackend`。

## 配置项（MultiArmAgentManager Inspector）

| 字段 | 说明 |
|---|---|
| armCount / dofPerArm | N 台机械臂（1–8），每台 4/6/7 自由度 |
| brainType | `Heuristic`（离线）或 `Llm`（LLM 高层规划） |
| llmEndpoint / llmModel | 任意 OpenAI 兼容接口（vLLM、Ollama、DeepSeek 等） |
| llmApiKey | 留空则读环境变量 `SOFAROBOT_LLM_API_KEY` |
| payloadCount / payloadArea | 工件数量与生成区域 |
| dropBinsZ / dropBinRadius | 每臂料箱位置与判定半径 |
| backend | `UnityPhysX` 或 `SofaUnity`（不可用时自动回退） |

LLM 大脑的动作协议（高层规划 → 底层伺服执行）：

```json
{"skill": "MoveTo|Grasp|Release|Home|Idle", "target": [x, y, z], "reason": "short"}
```

## 命令行重建场景与冒烟测试

```bash
# 重建场景
Unity -batchmode -nographics -quit -projectPath <本项目路径> \
  -executeMethod SofaRobotAI.SceneSetup.SceneBuilder.BuildFromCommandLine

# headless 进入 Play 模式跑 15 秒，验证 N 臂生成/后端初始化/任务执行（退出码 0=通过）
Unity -batchmode -nographics -projectPath <本项目路径> \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunFromCommandLine

# 同上，但强制 SofaUnity 后端且要求原生 SOFA 真的初始化成功（不回退）
Unity -batchmode -nographics -projectPath <本项目路径> \
  -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunSofaBackendFromCommandLine
```

编辑器菜单：**SofaRobotAI → Build Multi-Arm AI Scene** / **Run Play-Mode Smoke Test**。

## 对 SofaUnity 上游代码的本地补丁

为使官方代码（面向 Unity 2020/2021 + Windows）在 Unity 6 下编译，以下文件做了最小修改；
升级 SofaUnity 时需要重新套用：

| 文件 | 修改 |
|---|---|
| `Core/Plugins/SofaUnityAPI/SofaUtils.cs`、`Core/Scripts/Core/System/PluginManager.cs` | 删除未使用的 `using Unity.VisualScripting;` |
| `Core/Scripts/Modules/CuttingTools/CuttingManager.cs`、`Core/Scripts/Modules/Objects/SofaSphereCollisionObject.cs` 等 | 移除误加在属性上的 `[SerializeField]`（Unity 6 编译器报错） |
| `Core/Plugins/SofaUnityAPI/SofaContextAPI.cs` | 为 25.12 原生库缺失的 9 个入口点加惰性 shim（EntryPointNotFoundException → 安全返回值），version 为空时回退 APIName |
| `Packages/manifest.json` | 新增 `com.unity.inputsystem`（SDataSaveLoad.cs 依赖） |

## 已知事项

- `SofaUnity_modules` 子模块在 GitHub 上不存在（私有/已删除），
  克隆时会失败——不影响 Core 功能，属上游仓库问题
- macOS 原生库缺 `sofaPhysicsAPI_version` 等 9 个入口点（25.12 ABI 与
  master C# 的代差），已 shim；SOFA 场景加载/步进/重力设置均正常，
  Unity 侧 SOFA 节点图镜像（DAGNode 查询）在该版本下退化为空
- SofaUnity 官方测试环境为 Unity 2020.3/2021.3 + Windows；本项目在
  macOS + Unity 6000.6.7f1 (arm64) 下验证了全部 C# 编译、场景生成、
  Play 模式多臂任务执行、以及 **SofaUnity 原生后端加载运行**
- 场景、机械臂、任务全部程序化生成，`MultiArmAgentScene.unity` 只包含
  相机、灯光、地板和 Manager，重新生成无副作用

## 目录结构

```
Assets/
├── SofaUnity/              # 官方插件（1.2G，不入库；./setup-sofaunity.sh 克隆+打补丁）
├── Scripts/
│   ├── Core/               # ArmSpec、RobotArmBuilder、RobotArmController、PayloadObject
│   ├── Agent/              # IAgentBrain、Heuristic/Llm 大脑、ArmAgent、TaskSystem
│   ├── Backend/            # 物理后端抽象 + SofaUnity/Unity 实现
│   └── MultiArmAgentManager.cs
├── SceneSetup/Editor/      # SceneBuilder + PlayModeSmokeTest（菜单 / -executeMethod）
├── StreamingAssets/SOFA/   # robot_cell.scn（SOFA 侧环境场景）
└── Scenes/                 # MultiArmAgentScene.unity（生成物）
setup-sofaunity.sh          # 克隆 SofaUnity + 应用 Unity 6 兼容补丁
sofaunity-unity6-compat.patch
```

首次克隆本仓库后：`./setup-sofaunity.sh`，再用 Unity 打开工程。
