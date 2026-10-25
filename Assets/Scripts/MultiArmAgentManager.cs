using System.Collections.Generic;
using SofaRobotAI.Agent;
using SofaRobotAI.Backend;
using SofaRobotAI.Core;
using SofaRobotAI.Surgery;
using SofaRobotAI.Surgery.Scenarios;
using UnityEngine;

namespace SofaRobotAI
{
    /// <summary>
    /// Bootstraps and orchestrates the whole cell:
    ///  1. selects and initializes the physics backend (SofaUnity or Unity PhysX),
    ///  2. spawns N multi-DOF arms, one embodied AI agent per arm,
    ///  3. spawns payloads, creates pick-and-place tasks and keeps allocating them,
    ///  4. renders a small HUD so the init scene is self-explanatory at Play.
    /// The scene only needs this one component - everything else is built procedurally.
    /// </summary>
    public class MultiArmAgentManager : MonoBehaviour
    {
        [Header("Arms")]
        [Tooltip("Number of arms (and thus agents) in the cell")]
        [Range(1, 8)] public int armCount = 3;
        [Tooltip("Degrees of freedom per arm: 4-7 (4-5 recommended for surgical use)")]
        [Range(4, 5)] public int dofPerArm = 5; // surgical arms are 4-5 DOF by design
        [Tooltip("Arm base spacing along X. 1.0 keeps the central wound within 4-5 DOF arm reach.")]
        public float armSpacing = 1.0f;

        [Header("AI brains")]
        public BrainType brainType = BrainType.Heuristic;
        [Tooltip("OpenAI-compatible chat/completions endpoint, e.g. https://api.openai.com/v1/chat/completions")]
        public string llmEndpoint = "https://api.openai.com/v1/chat/completions";
        public string llmModel = "gpt-4o-mini";
        [Tooltip("Leave empty to read SOFAROBOT_LLM_API_KEY from the environment")]
        public string llmApiKey;

        [Header("Surgery mode")]
        [Tooltip("Surgical scenarios with SOFA soft tissue + needle coupling. Off = pick-and-place mode.")]
        public bool suturingMode = true;
        [Tooltip("Scenario loaded on start (see SofaRobotAI.Surgery.Scenarios.ScenarioRegistry)")]
        public string startupScenario = "vessel";

        [Header("Agent API")]
        [Tooltip("Local HTTP JSON API for external AI agents (127.0.0.1)")]
        public bool agentApiEnabled = true;
        public int agentApiPort = 8765;

        [Header("Payloads & tasks")]
        [Range(1, 16)] public int payloadCount = 5;
        [Tooltip("Payloads spawn stratified: payload i goes into the workspace of arm i%N, so every arm always has reachable work (ignores payloadAreaCenter/Size)")]
        public bool stratifiedSpawning = true;
        public Vector2 payloadAreaCenter = new Vector2(0f, 1.2f); // fallback: world XZ where cubes spawn
        public Vector2 payloadAreaSize = new Vector2(4.2f, 0.6f);
        [Tooltip("Z of the per-arm drop bins (one bin in front of each arm)")]
        public float dropBinsZ = 0.8f;
        public float dropBinRadius = 0.35f;

        [Header("Physics backend")]
        public PhysicsBackendMode backend = PhysicsBackendMode.UnityPhysX;

        public enum BrainType { Heuristic, Llm }

        // Runtime state ---------------------------------------------------- //
        public IRobotPhysicsBackend ActiveBackend { get; private set; }
        public readonly List<ArmAgent> Agents = new List<ArmAgent>();
        public readonly List<RobotTask> Tasks = new List<RobotTask>();
        public SurgicalScenario ActiveScenario { get; private set; }
        public bool PublicPaused { get; private set; }
        public AgentApi.SurgicalAgentApi Api { get; private set; }
        public int DeliveredCount { get; private set; }
        public int Round { get; private set; } = 1;
        public int RemainingTaskCount
        {
            get
            {
                int n = 0;
                foreach (var t in Tasks) if (!t.done) n++;
                return n;
            }
        }

        // Suture-mode progress -------------------------------------------- //
        public int TotalStitches
        {
            get { int n = 0; foreach (var t in Tasks) if (t is SutureTask st) n += st.stitches.Count; return n; }
        }

        public int CompletedStitches
        {
            get { int n = 0; foreach (var t in Tasks) if (t is SutureTask st) n += st.CompletedStitches; return n; }
        }

        public int SutureTaskCount
        {
            get { int n = 0; foreach (var t in Tasks) if (t is SutureTask) n++; return n; }
        }

        public int SuturedVessels
        {
            get { int n = 0; foreach (var t in Tasks) if (t is SutureTask st && st.done) n++; return n; }
        }

        private Transform _payloadRoot;
        private readonly List<Vector3> _dropBins = new List<Vector3>(); // one per arm
        private List<Surgery.SutureTask> _sutureBatches;
        private int _activeSutureBatch = -1;

        // ------------------------------------------------------------------ //

        private void Start()
        {
            ActiveBackend = CreateBackend();
            if (!ActiveBackend.Initialize(this))
            {
                Debug.LogWarning($"[MultiArmAgentManager] backend '{ActiveBackend.Name}' unavailable, " +
                                 "falling back to Unity PhysX.\n" + ActiveBackend.Status);
                ActiveBackend = new UnityPhysicsBackend();
                ActiveBackend.Initialize(this);
            }
            Debug.Log($"[MultiArmAgentManager] physics backend: {ActiveBackend.Name} ({ActiveBackend.Status})");

            SpawnArms();

            if (suturingMode)
            {
                // The live SOFA tissue mirror persists across scenario switches.
                if (ActiveBackend is SofaUnityBackend sofa && sofa.Coupling != null)
                    TissueDeformationSync.Create(transform, sofa.Coupling,
                        OrganCellBuilder.TissueNx, OrganCellBuilder.TissueNy, OrganCellBuilder.TissueNz,
                        new Color(0.86f, 0.60f, 0.58f));
                SwitchScenario(startupScenario);
            }
            else
            {
                BuildDropBins();
                SpawnPayloadsAndTasks();
            }

            if (agentApiEnabled)
            {
                Api = gameObject.AddComponent<AgentApi.SurgicalAgentApi>();
                Api.Initialize(this, agentApiPort);
            }
        }

        /// <summary>Tears the current surgical scenario down and builds another
        /// one (arms respawned with the scenario's count/DOF/roles).</summary>
        public void SwitchScenario(string id)
        {
            SurgicalScenario scenario = ScenarioRegistry.Find(id);
            if (scenario == null)
            {
                Debug.LogWarning($"[MultiArmAgentManager] unknown scenario '{id}' " +
                                 $"(available: {string.Join(", ", ScenarioIds())})");
                return;
            }

            TearDownSurgery();
            ActiveScenario = scenario;
            armCount = scenario.ArmCount;
            dofPerArm = scenario.DofPerArm;
            SpawnArms();

            List<SutureTask> batches = scenario.Build(transform, Agents, ActiveBackend as SofaUnityBackend);
            for (int i = 0; i < Agents.Count; i++)
            {
                ArmRole role = scenario.RoleFor(i);
                Agents[i].SetRole(role);
                if (role != ArmRole.Suturer) Agents[i].ClearNeedle();
                if (role == ArmRole.Stabilizer) Agents[i].SetStabilizePoint(scenario.StabilizePoint);
            }

            _sutureBatches = batches;
            Tasks.AddRange(batches);
            int total = 0;
            foreach (SutureTask t in batches) total += t.stitches.Count;
            SutureVisuals.Reset(total);
            ActivateNextSutureBatch();

            Debug.Log($"[MultiArmAgentManager] scenario '{scenario.Id}' ({scenario.Title}): " +
                      $"{armCount} arms x {dofPerArm} DOF, {batches.Count} batches / {total} stitches");
        }

        public IEnumerable<string> ScenarioIds()
        {
            foreach (var s in ScenarioRegistry.All) yield return s.Id;
        }

        public int CurrentScenarioIndex => ActiveScenario == null ? -1 : ScenarioRegistry.IndexOf(ActiveScenario.Id);

        /// <summary>Switches by catalogue index (PageUp/PageDown walking).</summary>
        public void SwitchScenarioIndex(int index)
        {
            if (index < 0 || index >= ScenarioRegistry.All.Length) return;
            SwitchScenario(ScenarioRegistry.All[index].Id);
        }

        private void TearDownSurgery()
        {
            ActiveScenario?.Park(ActiveBackend as SofaUnityBackend);
            ActiveScenario = null;

            foreach (ArmAgent agent in Agents)
                if (agent != null && agent.Arm != null)
                    Destroy(agent.Arm.gameObject);
            Agents.Clear();
            Tasks.Clear();
            _sutureBatches = null;
            _activeSutureBatch = -1;

            // Scenario visuals live under "...Cell" children; the persistent
            // SOFA tissue mirror ("TissuePad_SOFA") survives.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name.EndsWith("Cell")) Destroy(child.gameObject);
            }
        }

        /// <summary>Pauses/resumes agents, executors and the SOFA stepping.</summary>
        public void SetPaused(bool paused)
        {
            PublicPaused = paused;
            if (ActiveBackend is SofaUnityBackend sofa)
                sofa.SetSimulationRunning(!paused);
        }

        private void FixedUpdate()
        {
            if (!suturingMode || PublicPaused) return;

            var sofa = ActiveBackend as SofaUnityBackend;
            if (sofa == null) return;

            // Needle-tissue force coupling: drive the SOFA collision sphere to
            // the tip of the needle of whichever arm is currently suturing.
            ArmAgent active = Agents.Find(a => a.Task is SutureTask);
            bool couple = active != null && active.NeedleInTissue;
            sofa.PushNeedleTip(couple ? active.Needle.Tip.position : Vector3.zero, couple);

            ActiveScenario?.Tick(this, sofa);
        }

        private void Update()
        {
            if (!suturingMode)
            {
                CheckDeliveries();
                TaskAllocator.AssignPending(Agents, Tasks);
                RestartRoundWhenDone();
                return;
            }

            // Hotkeys 1..9 jump into the catalogue; PageUp/PageDown walk it, P pauses.
            for (int i = 0; i < 9 && i < ScenarioRegistry.All.Length; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    SwitchScenario(ScenarioRegistry.All[i].Id);
            }
            int cur = CurrentScenarioIndex;
            if (Input.GetKeyDown(KeyCode.PageDown))
                SwitchScenarioIndex((cur + 1) % ScenarioRegistry.All.Length);
            if (Input.GetKeyDown(KeyCode.PageUp))
                SwitchScenarioIndex((cur - 1 + ScenarioRegistry.All.Length) % ScenarioRegistry.All.Length);
            if (Input.GetKeyDown(KeyCode.P))
                SetPaused(!PublicPaused);

            if (PublicPaused) return;

            // One arm sutures at a time: hand the next batch to the next arm
            // when the current one finishes, so arms never share the wound.
            if (_sutureBatches != null && _activeSutureBatch >= 0 &&
                _activeSutureBatch < _sutureBatches.Count &&
                _sutureBatches[_activeSutureBatch].done)
            {
                ActivateNextSutureBatch();
            }
        }

        private void ActivateNextSutureBatch()
        {
            _activeSutureBatch++;
            if (_sutureBatches == null || _activeSutureBatch >= _sutureBatches.Count)
            {
                Debug.Log("[MultiArmAgentManager] wound closed - all suture batches complete");
                return;
            }
            if (Agents.Count == 0) return;
            // Batches follow suturer-role arms in order.
            ArmAgent agent = null;
            int suturersSeen = 0;
            for (int i = 0; i < Agents.Count && suturersSeen <= _activeSutureBatch; i++)
            {
                if (Agents[i].Role == ArmRole.Suturer)
                {
                    agent = Agents[i];
                    suturersSeen++;
                }
            }
            if (agent == null || agent.Needle == null)
            {
                Debug.LogWarning("[MultiArmAgentManager] no needled suturer for the next batch");
                return;
            }
            agent.EquipNeedle(agent.Needle, _sutureBatches[_activeSutureBatch],
                              ActiveScenario?.ExecutorConfig);
            Debug.Log($"[MultiArmAgentManager] suture batch {_activeSutureBatch + 1}/{_sutureBatches.Count} " +
                      $"-> {agent.Arm.ArmName}");
        }

        private void OnDestroy()
        {
            ActiveBackend?.Shutdown();
        }

        // ------------------------------------------------------------------ //
        //  Spawning
        // ------------------------------------------------------------------ //

        private void SpawnArms()
        {
            _dropBins.Clear(); // one bin per arm; SpawnArms runs on every scenario switch
            float spacing = suturingMode && ActiveScenario != null && ActiveScenario.BaseSpacing > 0f
                ? ActiveScenario.BaseSpacing : armSpacing;
            bool toolArms = suturingMode && ActiveScenario != null &&
                            ActiveScenario.Structure == ArmStructure.ThreeSegmentTool;
            for (int i = 0; i < armCount; i++)
            {
                Vector3 basePos;
                Quaternion baseRot = Quaternion.identity;
                if (armCount > 3)
                {
                    // Arc layout around the operating table so every arm can
                    // reach the central wound (straight rows only work for 3).
                    // 1.05 m keeps the WORST-case arm (middle of the arc,
                    // farthest from the table) within ~1.18 m of the wound -
                    // comfortably inside the 5-DOF arm's practical workspace
                    // (the nominal 1.64 m reach loses ~0.3 m to joint limits
                    // and the Jacobian-transpose servo's convergence margin;
                    // at 1.25 m radius the middle arms stalled at ~1.36 m).
                    const float radius = 1.05f;
                    float span = Mathf.Clamp(armCount * 0.28f, 1.2f, 2.4f); // radians
                    float th = -span * 0.5f + span * i / (armCount - 1) - Mathf.PI * 0.5f;
                    Vector3 table = new Vector3(0f, 0f, 0.90f);
                    basePos = table + new Vector3(Mathf.Cos(th) * radius, 0f, Mathf.Sin(th) * radius);
                    basePos.y = 0f;
                    baseRot = Quaternion.LookRotation(table - basePos, Vector3.up);
                }
                else
                {
                    basePos = new Vector3((i - (armCount - 1) * 0.5f) * spacing, 0f, 0f);
                }
                float x = basePos.x;
                _ = x; // kept for HUD colour hue continuity
                var color = Color.HSVToRGB((float)i / armCount, 0.75f, 0.95f);

                ArmSpec spec = toolArms
                    ? ArmSpec.CreateThreeSegmentToolSpec($"Arm_{i + 1}")
                    : ArmSpec.CreatePreset($"Arm_{i + 1}", dofPerArm);
                RobotArmController arm = toolArms
                    ? RobotArmBuilder.BuildThreeSegmentToolArm(
                        spec, transform, basePos, color,
                        (RobotArmBuilder.SurgicalTool)ActiveScenario.ToolFor(i))
                    : RobotArmBuilder.Build(spec, transform, basePos, color);
                arm.transform.rotation = baseRot;

                var agent = arm.gameObject.AddComponent<ArmAgent>();
                agent.Configure(arm, CreateBrain(), this);
                if (toolArms && ActiveScenario.ToolFor(i) == ArmTool.NeedleHolder)
                    agent.AttachNeedle(Surgery.NeedleTool.Attach(
                        arm.Tcp, arm.Tcp.parent, radius: 0.030f));
                Agents.Add(agent);
                _dropBins.Add(new Vector3(x, 0.12f, dropBinsZ));
            }
        }

        private IAgentBrain CreateBrain() => brainType switch
        {
            BrainType.Llm => new LlmAgentBrain(this, llmEndpoint, llmModel, llmApiKey),
            _ => new HeuristicBrain(),
        };

        private void SpawnPayloadsAndTasks()
        {
            if (_payloadRoot == null)
            {
                _payloadRoot = new GameObject("Payloads").transform;
                _payloadRoot.SetParent(transform, false);
            }

            Tasks.Clear();
            DeliveredCount = 0;
            int armN = Mathf.Max(1, Agents.Count);
            for (int i = 0; i < payloadCount; i++)
            {
                int owner = i % armN; // stratified: cycle through arms
                Vector3 pos;
                if (stratifiedSpawning)
                {
                    // Spawn inside the owning arm's workspace strip so its reach
                    // covers both the payload and its bin by construction.
                    float ownerX = Agents.Count > 0 ? Agents[owner].Arm.transform.position.x : 0f;
                    float halfW = Mathf.Min(0.7f, armSpacing * 0.35f);
                    pos = new Vector3(ownerX + Random.Range(-halfW, halfW), 0.16f, Random.Range(0.9f, 1.45f));
                }
                else
                {
                    pos = new Vector3(
                        payloadAreaCenter.x + Random.Range(-payloadAreaSize.x, payloadAreaSize.x) * 0.5f,
                        0.16f,
                        payloadAreaCenter.y + Random.Range(-payloadAreaSize.y, payloadAreaSize.y) * 0.5f);
                }

                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"Cube_{Round}_{i + 1}";
                cube.transform.SetParent(_payloadRoot, false);
                cube.transform.position = pos;
                cube.transform.localScale = Vector3.one * 0.14f;
                cube.GetComponent<Renderer>().sharedMaterial =
                    new Material(Shader.Find("Standard")) { color = Color.HSVToRGB(Random.value, 0.55f, 0.9f) };
                var rb = cube.AddComponent<Rigidbody>();
                rb.mass = 0.2f;
                cube.AddComponent<PayloadObject>();

                Vector3 drop = stratifiedSpawning && _dropBins.Count == armN
                    ? _dropBins[owner]
                    : NearestBin(pos);
                Tasks.Add(new ManipulationTask
                {
                    payload = cube.GetComponent<PayloadObject>(),
                    DropPosition = drop + new Vector3(
                        Random.Range(-0.12f, 0.12f), 0f, Random.Range(-0.12f, 0.12f)),
                });
            }
        }

        private Vector3 NearestBin(Vector3 pos)
        {
            Vector3 best = _dropBins[0];
            float bestD = float.MaxValue;
            foreach (Vector3 bin in _dropBins)
            {
                float d = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(bin.x, bin.z));
                if (d < bestD) { bestD = d; best = bin; }
            }
            return best;
        }

        private void BuildDropBins()
        {
            foreach (Vector3 bin in _dropBins)
            {
                var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pad.name = $"DropBin_x{bin.x:F1}";
                Destroy(pad.GetComponent<Collider>()); // visual only, payloads must rest on the floor
                pad.transform.SetParent(transform, false);
                pad.transform.position = bin - new Vector3(0f, 0.02f, 0f);
                pad.transform.localScale = new Vector3(dropBinRadius * 2f, 0.01f, dropBinRadius * 2f);
                pad.GetComponent<Renderer>().sharedMaterial =
                    new Material(Shader.Find("Standard")) { color = new Color(0.2f, 0.75f, 0.35f, 0.6f) };
            }
        }

        private IRobotPhysicsBackend CreateBackend() => backend switch
        {
            PhysicsBackendMode.SofaUnity => new SofaUnityBackend(),
            _ => new UnityPhysicsBackend(),
        };

        // ------------------------------------------------------------------ //
        //  Task lifecycle
        // ------------------------------------------------------------------ //

        /// <summary>Called by an agent right after it released its payload.</summary>
        public void NotifyReleasedNearDrop(ArmAgent agent, ManipulationTask task)
        {
            if (task.payload == null || task.payload.IsGrasped) return;
            if (WithinDropZone(task.payload.transform.position))
                CompleteTask(agent, task);
        }

        private void CheckDeliveries()
        {
            foreach (ArmAgent agent in Agents)
            {
                if (!(agent.Task is ManipulationTask task)) continue;
                if (task?.payload == null || task.done) continue;
                if (!task.payload.IsGrasped && WithinDropZone(task.payload.transform.position))
                    CompleteTask(agent, task);
            }
        }

        private void CompleteTask(ArmAgent agent, ManipulationTask task)
        {
            task.done = true;
            task.payload.MarkDelivered();
            DeliveredCount++;
            agent.NotifyTaskCompleted();
        }

        private bool WithinDropZone(Vector3 pos)
        {
            if (pos.y > 0.6f) return false;
            foreach (Vector3 bin in _dropBins)
                if (Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(bin.x, bin.z)) < dropBinRadius)
                    return true;
            return false;
        }

        private void RestartRoundWhenDone()
        {
            if (RemainingTaskCount > 0) return;
            foreach (ArmAgent agent in Agents)
                if (agent.Task != null) return; // still finishing

            Round++;
            Debug.Log($"[MultiArmAgentManager] round {Round - 1} complete - respawning payloads");
            foreach (Transform child in _payloadRoot)
                Destroy(child.gameObject);
            SpawnPayloadsAndTasks();
        }

        // ------------------------------------------------------------------ //
        //  HUD
        // ------------------------------------------------------------------ //

        private void OnGUI()
        {
            GUI.Box(new Rect(10, 10, 560, 64 + Agents.Count * 22), "");
            int idx = CurrentScenarioIndex;
            string title = suturingMode
                ? $"<b>SofaRobotAI</b>  [{idx + 1}/{ScenarioRegistry.All.Length}] {ActiveScenario?.Title} ({ActiveScenario?.Id})  |  " +
                  $"{armCount}x{dofPerArm}DOF  |  {(PublicPaused ? "PAUSED (P)" : "running")}"
                : $"<b>SofaRobotAI</b>  pick&place  |  {armCount} arms x {dofPerArm} DOF";
            GUI.Label(new Rect(20, 16, 540, 24), title, Style());

            if (suturingMode)
            {
                if (GUI.Button(new Rect(20, 38, 70, 22), "<< 上一个"))
                    SwitchScenarioIndex((idx - 1 + ScenarioRegistry.All.Length) % ScenarioRegistry.All.Length);
                if (GUI.Button(new Rect(95, 38, 70, 22), "下一个 >>"))
                    SwitchScenarioIndex((idx + 1) % ScenarioRegistry.All.Length);
                GUI.Label(new Rect(175, 38, 385, 22),
                    $"按键1-9直达 / PgUp PgDn翻页 / P暂停 | {ActiveScenario?.Description}", Style());
            }

            int y = 66;
            foreach (ArmAgent agent in Agents)
            {
                GUI.Label(new Rect(20, y, 540, 20),
                    $"{agent.Arm.ArmName} [{agent.Role}]  {agent.Status}", Style());
                y += 22;
            }
            string footer = suturingMode
                ? $"stitches {CompletedStitches}/{TotalStitches} (batch {Mathf.Clamp(_activeSutureBatch + 1, 0, SutureTaskCount)}/{SutureTaskCount})   " +
                  $"API 127.0.0.1:{agentApiPort}/api/state"
                : $"delivered {DeliveredCount}/{Tasks.Count}   round {Round}";
            GUI.Label(new Rect(20, y, 540, 20), footer, Style());
        }

        private static GUIStyle _guiStyle;
        private static GUIStyle Style()
        {
            if (_guiStyle == null)
            {
                _guiStyle = new GUIStyle(GUI.skin.label) { richText = true };
            }
            return _guiStyle;
        }
    }
}
