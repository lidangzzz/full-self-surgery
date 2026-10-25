#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SofaRobotAI.SceneSetup
{
    /// <summary>
    /// Headless Play-mode smoke test: opens the init scene, runs it for a few
    /// seconds and asserts that the cell actually came alive (N arms spawned,
    /// backend initialized, agents holding tasks). Survives the play-mode
    /// domain reload by tracking state in EditorPrefs.
    ///
    ///   Unity -batchmode -nographics -projectPath &lt;project&gt; \
    ///     -executeMethod SofaRobotAI.SceneSetup.PlayModeSmokeTest.RunFromCommandLine
    ///
    /// Exit code 0 = pass, 1 = fail.
    /// </summary>
    public static class PlayModeSmokeTest
    {
        private const float PickRunSeconds = 15f;
        private const float SutureRunSeconds = 30f;
        private const float CycleSeconds = 36f;
        private const string ActivePref = "SofaRobotAI.SmokeTest.Active";
        private const string StartPref = "SofaRobotAI.SmokeTest.Start";
        private const string BackendPref = "SofaRobotAI.SmokeTest.Backend"; // "" | "SofaUnity"
        private const string SuturePref = "SofaRobotAI.SmokeTest.Suture";  // bool
        private const string CyclePref = "SofaRobotAI.SmokeTest.Cycle";    // bool (scenario cycling + API)

        [InitializeOnLoadMethod]
        private static void Bootstrap()
        {
            // Re-arm the tick loop after the play-mode domain reload wipes
            // static state and event subscriptions.
            if (EditorPrefs.GetBool(ActivePref, false))
                EditorApplication.update += TickWhilePlaying;
        }

        [MenuItem("SofaRobotAI/Run Play-Mode Smoke Test")]
        public static void Run()
        {
            EditorPrefs.SetBool(ActivePref, true);
            EditorPrefs.SetFloat(StartPref, (float)EditorApplication.timeSinceStartup);
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Scenes/MultiArmAgentScene.unity",
                UnityEditor.SceneManagement.OpenSceneMode.Single);

            var mgr = UnityEngine.Object.FindAnyObjectByType<MultiArmAgentManager>();

            // Optional backend override (set by the command-line variants).
            string backend = EditorPrefs.GetString(BackendPref, "");
            if (!string.IsNullOrEmpty(backend) && mgr != null &&
                System.Enum.TryParse(backend, out Backend.PhysicsBackendMode mode))
            {
                Debug.Log($"[SmokeTest] forcing backend: {mode}");
                mgr.backend = mode;
            }

            // Optional surgery mode (suturing demo).
            if (EditorPrefs.GetBool(SuturePref, false) && mgr != null)
            {
                Debug.Log("[SmokeTest] forcing suturing mode");
                mgr.suturingMode = true;
            }

            EditorApplication.update += TickWhilePlaying; // pre-reload instance
            EditorApplication.isPlaying = true;
        }

        public static void RunFromCommandLine()
        {
            EditorPrefs.DeleteKey(BackendPref);
            EditorPrefs.DeleteKey(SuturePref);
            Debug.Log("[SmokeTest] starting headless play-mode test");
            Run();
        }

        /// <summary>Variant that forces the SofaUnity backend and requires it to
        /// actually initialize (no PhysX fallback) for a pass.</summary>
        public static void RunSofaBackendFromCommandLine()
        {
            EditorPrefs.SetString(BackendPref, "SofaUnity");
            EditorPrefs.DeleteKey(SuturePref);
            Debug.Log("[SmokeTest] starting headless SofaUnity-backend test");
            Run();
        }

        /// <summary>Suturing demo variant: SofaUnity backend + surgery cell;
        /// requires at least one completed stitch within the run window.</summary>
        public static void RunSurgeryFromCommandLine()
        {
            EditorPrefs.SetString(BackendPref, "SofaUnity");
            EditorPrefs.SetBool(SuturePref, true);
            EditorPrefs.DeleteKey(CyclePref);
            Debug.Log("[SmokeTest] starting headless surgery (suturing) test");
            Run();
        }

        /// <summary>Cycles all surgical scenarios (10s each), switching one of
        /// them through the HTTP agent API, and asserts arm reconfiguration.</summary>
        public static void RunScenariosFromCommandLine()
        {
            EditorPrefs.SetString(BackendPref, "SofaUnity");
            EditorPrefs.SetBool(SuturePref, true);
            EditorPrefs.SetBool(CyclePref, true);
            Debug.Log("[SmokeTest] starting headless scenario-cycling + agent-API test");
            Run();
        }

        private static void TickWhilePlaying()
        {
            if (!EditorPrefs.GetBool(ActivePref, false)) return;

            float started = EditorPrefs.GetFloat(StartPref, -1f);
            bool suture = EditorPrefs.GetBool(SuturePref, false);
            bool cycle = EditorPrefs.GetBool(CyclePref, false);
            float runSeconds = cycle ? CycleSeconds : suture ? SutureRunSeconds : PickRunSeconds;
            double elapsed = EditorApplication.timeSinceStartup - started;

            // Safety: if play mode never engaged, bail out instead of hanging.
            if (!EditorApplication.isPlaying && elapsed > 90)
            {
                Finish(false, "play mode never started");
                return;
            }

            var manager = UnityEngine.Object.FindAnyObjectByType<MultiArmAgentManager>();

            // Scenario cycling across the catalogue; the heart switch goes
            // through the HTTP agent API to prove the external control path.
            if (cycle && manager != null)
            {
                double t = elapsed;
                if (t >= 6 && t < 6.5 && manager.ActiveScenario?.Id == "vessel")
                    ApiPostScenario(manager.agentApiPort, "heart"); // via API
                if (t >= 13 && t < 13.5 && manager.ActiveScenario?.Id == "heart")
                    manager.SwitchScenario("s03");                  // neuro tube
                if (t >= 20 && t < 20.5 && manager.ActiveScenario?.Id == "s03")
                    manager.SwitchScenario("s51");                  // HPB tube
                if (t >= 27 && t < 27.5 && manager.ActiveScenario?.Id == "s51")
                    manager.SwitchScenario("s97");                  // endoscopic surface
            }

            if (!EditorApplication.isPlaying || elapsed < runSeconds) return;

            bool pass = Evaluate(manager, out string summary);
            Debug.Log(summary);

            if (cycle && manager != null) pass &= EvaluateCycle(manager);
            if (suture && !cycle && manager != null) pass &= EvaluateSurgery(manager);

            // SofaUnity variant: falling back to PhysX means the native load failed.
            bool wantSofa = EditorPrefs.GetString(BackendPref, "") == "SofaUnity";
            if (wantSofa && manager != null && manager.ActiveBackend is not Backend.SofaUnityBackend)
            {
                Debug.LogError("[SmokeTest] SofaUnity backend did NOT initialize (fell back to " +
                               manager.ActiveBackend?.Name + "): " + manager.ActiveBackend?.Status);
                pass = false;
            }
            EditorPrefs.DeleteKey(BackendPref);
            EditorPrefs.DeleteKey(SuturePref);
            EditorPrefs.DeleteKey(CyclePref);
            Finish(pass, pass ? "all checks passed" : "see log above");
        }

        /// <summary>Scenario-cycling assertions: sampled scenarios rotated,
        /// arms rebuilt per scenario, registry >= 50, API answered.</summary>
        private static bool EvaluateCycle(MultiArmAgentManager manager)
        {
            var report = new StringBuilder();
            bool pass = true;

            // Registry size: the objective demands >= 50 selectable scenarios.
            int total = SofaRobotAI.Surgery.Scenarios.ScenarioRegistry.All.Length;
            report.AppendLine($"[SmokeTest] registry size: {total}");
            if (total < 50) { report.AppendLine("[SmokeTest] FAIL: fewer than 50 scenarios"); pass = false; }

            var sc = manager.ActiveScenario;
            report.AppendLine($"[SmokeTest] cycle end: scenario={sc?.Id} arms={manager.Agents.Count} " +
                              $"dof={manager.dofPerArm} stitches={manager.CompletedStitches}/{manager.TotalStitches}");
            if (sc == null || sc.Id != "s97") { report.AppendLine("[SmokeTest] FAIL: cycle did not end on s97"); pass = false; }
            if (sc != null && manager.Agents.Count != sc.ArmCount) { report.AppendLine("[SmokeTest] FAIL: arm count mismatch"); pass = false; }
            if (sc != null && manager.dofPerArm != sc.DofPerArm) { report.AppendLine("[SmokeTest] FAIL: dof mismatch"); pass = false; }
            foreach (var a in manager.Agents)
            {
                report.AppendLine($"[SmokeTest] {a.Arm.ArmName}: role={a.Role} dof={a.Arm.Dof} " +
                                  $"needle={a.Needle != null} | {a.Status}");
                if (a.Arm.Dof != manager.dofPerArm) pass = false;
            }

            // HTTP API round trip: state mentions the live scenario; the
            // catalogue listing exposes the full registry.
            try
            {
                string json = ApiGet(manager.agentApiPort, "/api/state");
                bool ok = json.Contains("\"scenario\":\"s97\"");
                report.AppendLine($"[SmokeTest] api state: {(ok ? "ok" : "BAD: " + json.Substring(0, Mathf.Min(200, json.Length)))}");
                if (!ok) pass = false;

                string scenarios = ApiGet(manager.agentApiPort, "/api/scenarios");
                int listed = CountOccurrences(scenarios, "\"id\"");
                bool ok2 = listed >= 50;
                report.AppendLine($"[SmokeTest] api scenarios listed: {listed} {(ok2 ? "ok" : "BAD")}");
                if (!ok2) pass = false;
            }
            catch (Exception e)
            {
                report.AppendLine($"[SmokeTest] FAIL: api unreachable: {e.Message}");
                pass = false;
            }

            Debug.Log(report.ToString());
            return pass;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // Tiny HTTP helpers (editor-side) -------------------------------- //
        private static string ApiGet(int port, string path)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return client.GetStringAsync($"http://127.0.0.1:{port}{path}").Result;
        }

        private static void ApiPostScenario(int port, string id)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var content = new System.Net.Http.StringContent("{\"id\":\"" + id + "\"}", Encoding.UTF8, "application/json");
            var resp = client.PostAsync($"http://127.0.0.1:{port}/api/scenario", content).Result;
            Debug.Log($"[SmokeTest] api POST /api/scenario '{id}' -> {(int)resp.StatusCode}");
        }

        /// <summary>Surgery-mode assertions: 3 vessels, every arm needled, at
        /// least one stitch cinched.</summary>
        private static bool EvaluateSurgery(MultiArmAgentManager manager)
        {
            bool pass = true;
            var report = new StringBuilder();
            report.AppendLine($"[SmokeTest] surgery: vessels={manager.SutureTaskCount} " +
                              $"stitches={manager.CompletedStitches}/{manager.TotalStitches} " +
                              $"closed={manager.SuturedVessels}");

            if (manager.SutureTaskCount != 3) pass = false;
            foreach (var agent in manager.Agents)
            {
                report.AppendLine($"[SmokeTest] {agent.Arm.ArmName}: needle={(agent.Needle != null ? "yes" : "NO")} | {agent.Status}");
                if (agent.Needle == null) pass = false;
            }
            if (manager.CompletedStitches < 1)
            {
                report.AppendLine("[SmokeTest] FAIL: no stitch completed within the window");
                pass = false;
            }

            // Needle-tissue force coupling: with the SofaUnity backend the
            // tissue mesh must be the live SOFA mirror and actually deformed
            // (gravity sag alone exceeds the threshold).
            var sync = SofaRobotAI.Surgery.TissueDeformationSync.LastInstance;
            float tissueDelta = sync != null ? sync.MaxAbsDelta : -1f;
            var b = sync != null ? sync.CurrentBounds : default;
            string tissueInfo = sync == null ? "static fallback"
                : $"SOFA live, maxDelta {tissueDelta:F4} m, bounds y[{b.min.y:F2}..{b.max.y:F2}] x[{b.min.x:F2}..{b.max.x:F2}] z[{b.min.z:F2}..{b.max.z:F2}]";
            report.AppendLine($"[SmokeTest] tissue mesh: {tissueInfo}");
            if (manager.ActiveBackend is Backend.SofaUnityBackend && (sync == null || tissueDelta < 0.0003f))
            {
                report.AppendLine("[SmokeTest] FAIL: SOFA tissue deformation not observed via readback");
                pass = false;
            }

            Debug.Log(report.ToString());
            return pass;
        }

        private static void Finish(bool pass, string why)
        {
            EditorPrefs.SetBool(ActivePref, false);
            EditorApplication.update -= TickWhilePlaying;
            Debug.Log($"[SmokeTest] {(pass ? "PASSED" : "FAILED")}: {why}");
            if (Application.isBatchMode)
                EditorApplication.Exit(pass ? 0 : 1);
            else
                EditorApplication.isPlaying = false;
        }

        private static bool Evaluate(MultiArmAgentManager manager, out string summary)
        {
            var report = new StringBuilder();
            if (manager == null)
            {
                summary = "[SmokeTest] FAIL: MultiArmAgentManager not found in play mode";
                Debug.LogError(summary);
                return false;
            }

            report.AppendLine($"[SmokeTest] backend: {manager.ActiveBackend?.Name} | {manager.ActiveBackend?.Status}");
            report.AppendLine($"[SmokeTest] agents: {manager.Agents.Count} (expected {manager.armCount})");

            bool pass = manager.Agents.Count == manager.armCount && manager.Tasks.Count >= 1;
            bool suture = EditorPrefs.GetBool(SuturePref, false);
            foreach (var agent in manager.Agents)
            {
                report.AppendLine($"[SmokeTest] {agent.Arm.ArmName}: dof={agent.Arm.Dof} brain={agent.Brain.Name} " +
                                  $"task={(agent.Task != null ? "yes" : "none")} status=\"{agent.Status}\" " +
                                  $"tcp=({agent.Arm.Tcp.position.x:F2},{agent.Arm.Tcp.position.y:F2},{agent.Arm.Tcp.position.z:F2})");
                if (agent.Arm.Dof != manager.dofPerArm) pass = false;
                // Pick&place: allocator must hand out tasks immediately. Suture:
                // a finished vessel legitimately leaves the agent taskless.
                if (agent.Task == null && !suture) pass = false;
            }
            report.AppendLine($"[SmokeTest] tasks remaining: {manager.RemainingTaskCount}/{manager.Tasks.Count}");
            summary = report.ToString();
            return pass;
        }
    }
}
#endif
