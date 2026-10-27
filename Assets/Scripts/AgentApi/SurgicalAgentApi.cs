using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using SofaRobotAI.Agent;
using SofaRobotAI.Surgery;
using SofaRobotAI.Surgery.Scenarios;
using UnityEngine;

namespace SofaRobotAI.AgentApi
{
    /// <summary>
    /// Control &amp; observation API for external AI agents.
    ///
    /// In-process: <see cref="BuildStateJson"/> / <see cref="EnqueueCommand"/>.
    /// Over HTTP (default 127.0.0.1:8765):
    ///   GET  /api/state       - full snapshot (scenario, arms, tasks, tissue)
    ///   GET  /api/scenarios   - the scenario catalogue
    ///   POST /api/scenario    - {"id":"vessel|heart|tendon"} switch scenario
    ///   POST /api/command     - {"action":"MoveTcp|MoveTip|Gripper|Home|Resume",
    ///                            "arm":0, "target":[x,y,z], "open":0.5}
    ///   POST /api/control     - {"action":"pause"|"resume"}
    /// Commands are queued and applied on the main thread next Update.
    /// </summary>
    public class SurgicalAgentApi : MonoBehaviour
    {
        private MultiArmAgentManager _mgr;
        private AgentHttpServer _server;
        private readonly ConcurrentQueue<ApiCommand> _inbox = new ConcurrentQueue<ApiCommand>();

        private string _stateJsonCache = "{}";
        private string _scenarioJsonCache = "[]";
        private string _calibrationJsonCache = "{}";
        private string _cameraJsonCache = "{\"cameras\":[]}";
        private float _lastCacheRefresh;
        private long _heartbeatTicks;   // Stopwatch timestamp of the last Update
        private int _lastRefreshFrame;  // frame captured with the last snapshot
        internal const float StaleAfterSeconds = 3f;

        // Screenshot pipeline: HTTP thread sets the request flag, the main
        // thread renders Camera.main into a RT and caches the PNG bytes.
        private volatile bool _shotRequested;
        private string _shotCameraId; // main | screen | front|back|left|right|top
        private byte[] _pngCache;
        private int _pngGeneration;
        private readonly object _pngLock = new object();
        // Per-request capture resolution. Camera renders go to an off-screen
        // RenderTexture, so they are NOT limited by the window/screen size -
        // the default is 4K; up to 8K is accepted.
        private int _shotWidth = 3840, _shotHeight = 2160;
        internal const int ShotMinPx = 320, ShotMaxPx = 7680;

        /// <summary>True when the Unity editor play-mode is paused (sim time
        /// frozen; GETs keep refreshing but sim/commands wait).</summary>
        public bool EditorPaused
        {
            get
            {
#if UNITY_EDITOR
                return UnityEditor.EditorApplication.isPaused;
#else
                return false;
#endif
            }
        }

        public void Initialize(MultiArmAgentManager manager, int port)
        {
            _mgr = manager;
            System.Threading.Interlocked.Exchange(ref _heartbeatTicks,
                System.Diagnostics.Stopwatch.GetTimestamp());
#if UNITY_EDITOR
            // Editor-level watchdog: runs even while play mode is paused and
            // can therefore clear a pause that froze the play-mode loop (the
            // in-Update guard below can never execute in that state).
            UnityEditor.EditorApplication.update -= EditorWatchdog;
            UnityEditor.EditorApplication.update += EditorWatchdog;
#endif
            _server = new AgentHttpServer(port, this);
            try
            {
                _server.Start();
                Debug.Log($"[AgentApi] listening on http://127.0.0.1:{port}/api/state");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentApi] HTTP server failed to start: {e.Message}"); // keep in-process API alive
            }
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorWatchdog;
#endif
            _server?.Stop();
        }

#if UNITY_EDITOR
        private void EditorWatchdog()
        {
            if (UnityEditor.EditorApplication.isPlaying && UnityEditor.EditorApplication.isPaused)
            {
                UnityEditor.EditorApplication.isPaused = false;
                if (!_warnedAutoUnpause)
                {
                    _warnedAutoUnpause = true;
                    Debug.LogWarning("[AgentApi] editor play-mode pause detected by watchdog - cleared " +
                                     "(the simulation must stay alive for external agents)");
                }
            }
        }
#endif

        private bool _warnedAutoUnpause;

        private void Update()
        {
            System.Threading.Interlocked.Exchange(ref _heartbeatTicks,
                System.Diagnostics.Stopwatch.GetTimestamp());
#if UNITY_EDITOR
            // The agent API must never face a silently frozen simulation:
            // Error Pause / a stray pause would stop command processing while
            // the HTTP thread keeps serving the last cached snapshot.
            if (UnityEditor.EditorApplication.isPaused)
            {
                UnityEditor.EditorApplication.isPaused = false;
                if (!_warnedAutoUnpause)
                {
                    _warnedAutoUnpause = true;
                    Debug.LogWarning("[AgentApi] editor play-mode pause detected and cleared " +
                                     "(agent API keeps the simulation alive)");
                }
            }
#endif
            while (_inbox.TryDequeue(out ApiCommand cmd))
            {
                try { Apply(cmd); }
                catch (Exception e) { Debug.LogWarning($"[AgentApi] command '{cmd.action}' failed: {e.Message}"); }
            }

            // Real time, not sim time: keep snapshots fresh even while the
            // editor play-mode is paused (agents must be able to see that).
            if (Time.realtimeSinceStartup - _lastCacheRefresh > 0.25f)
            {
                _lastCacheRefresh = Time.realtimeSinceStartup;
                _lastRefreshFrame = Time.frameCount;
                _stateJsonCache = BuildStateJson();
                _scenarioJsonCache = BuildScenarioJson();
                _calibrationJsonCache = BuildCalibrationJson();
                _cameraJsonCache = BuildCameraJson();
            }

            if (_shotRequested)
            {
                _shotRequested = false;
                string camId = _shotCameraId;
                _shotCameraId = null;
                if (camId == "screen")
                {
                    StartCoroutine(CaptureScreenCoroutine()); // completes at end-of-frame
                }
                else
                {
                    Camera cam = string.IsNullOrEmpty(camId) || camId == "main"
                        ? Camera.main
                        : Core.CameraWall.Instance != null ? Core.CameraWall.Instance.Find(camId) : null;
                    byte[] png = CaptureScreenshot(cam, _shotWidth, _shotHeight);
                    if (png != null)
                    {
                        lock (_pngLock)
                        {
                            _pngCache = png;
                            _pngGeneration++;
                        }
                    }
                }
            }
        }

        /// <summary>Composite capture of the whole Game view (all PIP tiles +
        /// HUD) - proof that the five-camera wall is actually displayed.</summary>
        private System.Collections.IEnumerator CaptureScreenCoroutine()
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            lock (_pngLock)
            {
                _pngCache = png;
                _pngGeneration++;
            }
        }

        /// <summary>Renders one camera at MAXIMUM sampling quality and encodes
        /// a PNG: 2x supersampling + 8x MSAA into a high-resolution target,
        /// GPU downsample to the requested size (bilinear at 2x = box filter),
        /// then the deterministic Metal vertical-flip fix.</summary>
        internal byte[] CaptureScreenshot(Camera cam, int width, int height)
        {
            if (cam == null) return null;

            width = Mathf.Clamp(width, ShotMinPx, ShotMaxPx);
            height = Mathf.Clamp(height, ShotMinPx, ShotMaxPx);

            int ss = 2;
            while (ss > 1 && ((long)width * ss > 16384 || (long)height * ss > 16384)) ss--;

            var rtHi = new RenderTexture(width * ss, height * ss, 24,
                                         RenderTextureFormat.ARGB32)
            { antiAliasing = 8 };

            var prev = cam.targetTexture;
            float prevAspect = cam.aspect;
            cam.aspect = (float)width / height;
            cam.targetTexture = rtHi;
            cam.Render();
            cam.targetTexture = prev;
            cam.aspect = prevAspect;

            RenderTexture rtFinal = rtHi;
            if (ss > 1)
            {
                rtFinal = new RenderTexture(width, height, 0,
                                            RenderTextureFormat.ARGB32);
                Graphics.Blit(rtHi, rtFinal);
            }


            var prevActive = RenderTexture.active;
            RenderTexture.active = rtFinal;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            // Deterministic CPU vertical flip: on Metal the RT rows are
            // top-left origin while the PNG encoder expects bottom-up, so
            // manual camera captures come out upside down without this.
            var px = tex.GetPixels32();
            for (int y = 0; y < height / 2; y++)
            {
                int a = y * width, b = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 t = px[a + x]; px[a + x] = px[b + x]; px[b + x] = t;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();

            byte[] png = tex.EncodeToPNG();
            rtHi.Release();
            if (rtFinal != rtHi) rtFinal.Release();
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(rtHi);
            if (rtFinal != rtHi) UnityEngine.Object.Destroy(rtFinal);
            return png;
        }


        /// <summary>HTTP-thread entry: requests a capture from one of the
        /// cameras (main / screen / front / back / left / right / top).</summary>
        internal byte[] RequestScreenshot(string cameraId = null, int timeoutMs = 8000,
                                          int width = 3840, int height = 2160)
        {
            int startGen;
            lock (_pngLock) startGen = _pngGeneration;
            _shotWidth = Mathf.Clamp(width, ShotMinPx, ShotMaxPx);
            _shotHeight = Mathf.Clamp(height, ShotMinPx, ShotMaxPx);
            _shotCameraId = cameraId;
            _shotRequested = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                System.Threading.Thread.Sleep(30);
                lock (_pngLock)
                    if (_pngGeneration > startGen && _pngCache != null)
                        return _pngCache;
            }
            return null;
        }

        internal static bool IsValidCameraId(string id)
        {
            return string.IsNullOrEmpty(id) || id == "main" || id == "screen" ||
                   id == "front" || id == "back" || id == "left" || id == "right" || id == "top";
        }

        internal string BuildCameraJson()
        {
            var list = new ApiCameraList { cameras = new List<ApiCamera>() };
            var wall = Core.CameraWall.Instance;
            if (wall != null)
            {
                foreach (var wc in wall.Cameras)
                {
                    var t = wc.camera.transform;
                    list.cameras.Add(new ApiCamera
                    {
                        id = wc.id, label = wc.label,
                        position = new[] { t.position.x, t.position.y, t.position.z },
                        rotation = new[] { t.eulerAngles.x, t.eulerAngles.y, t.eulerAngles.z },
                        viewport = new[] { wc.viewport.x, wc.viewport.y, wc.viewport.width, wc.viewport.height },
                    });
                }
            }
            else if (Camera.main != null)
            {
                var t = Camera.main.transform;
                list.cameras.Add(new ApiCamera
                {
                    id = "main", label = "主视角 MAIN",
                    position = new[] { t.position.x, t.position.y, t.position.z },
                    rotation = new[] { t.eulerAngles.x, t.eulerAngles.y, t.eulerAngles.z },
                    viewport = new[] { 0f, 0f, 1f, 1f },
                });
            }
            return JsonUtility.ToJson(list);
        }

        /// <summary>Main-loop liveness probe, safe to call from the HTTP
        /// thread. A snapshot whose heartbeat is older than
        /// <see cref="StaleAfterSeconds"/> cannot be refreshed and its queued
        /// commands cannot be applied - the API must say so instead of serving
        /// a silently frozen state (the failure mode of the 2026-10-24 run,
        /// where a diverged sync SOFA solve stalled the whole main thread).</summary>
        internal bool IsSimStale(out double staleSeconds, out int lastFrame)
        {
            long last = System.Threading.Interlocked.Read(ref _heartbeatTicks);
            staleSeconds = last == 0
                ? double.MaxValue
                : (System.Diagnostics.Stopwatch.GetTimestamp() - last) / (double)System.Diagnostics.Stopwatch.Frequency;
            lastFrame = _lastRefreshFrame;
            return staleSeconds > StaleAfterSeconds;
        }


        // ------------------------------------------------------------------ //
        //  Snapshot (read side)
        // ------------------------------------------------------------------ //

        public string CurrentStateJson => _stateJsonCache;
        internal string CurrentCalibrationJson => _calibrationJsonCache;
        public string CurrentScenarioJson => _scenarioJsonCache;
        public string CurrentCameraJson => _cameraJsonCache;

        internal string BuildStateJson()
        {
            if (_mgr == null) return "{}";
            var state = new ApiState
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                frame = Time.frameCount,
                scenario = _mgr.ActiveScenario?.Id ?? "",
                scenarioTitle = _mgr.ActiveScenario?.Title ?? "",
                paused = _mgr.PublicPaused,
                editorPaused = EditorPaused,
                backend = _mgr.ActiveBackend?.Name ?? "",
                backendStatus = _mgr.ActiveBackend?.Status ?? "",
                stitchesDone = _mgr.CompletedStitches,
                stitchesTotal = _mgr.TotalStitches,
                sofaFps = _mgr.ActiveBackend is Backend.SofaUnityBackend sofa ? sofa.SimulationFps : -1f,
                sofaTime = _mgr.ActiveBackend is Backend.SofaUnityBackend sofa2 ? sofa2.SimulationTime : -1f,
                sofaStepMs = _mgr.ActiveBackend is Backend.SofaUnityBackend sofa3 ? sofa3.CurrentStepElapsedMs : -1L,
                sofaSteps = _mgr.ActiveBackend is Backend.SofaUnityBackend sofa4 ? sofa4.CompletedSteps : -1L,
                liverDelta = Surgery.LiverDeformationSync.LastInstance != null
                    ? Surgery.LiverDeformationSync.LastInstance.MaxAbsDelta : -1f,
                arms = new List<ApiArm>(),
                tasks = new List<ApiTask>(),
            };

            for (int i = 0; i < _mgr.Agents.Count; i++)
            {
                ArmAgent a = _mgr.Agents[i];
                Vector3 tcp = a.Arm != null && a.Arm.Tcp != null ? a.Arm.Tcp.position : Vector3.zero;
                float[] joints = a.Arm != null ? a.Arm.JointPositions : new float[0];
                Vector3 tip = a.Needle != null && a.Needle.Tip != null
                    ? a.Needle.Tip.position : tcp;
                state.arms.Add(new ApiArm
                {
                    index = i,
                    name = a.Arm != null ? a.Arm.ArmName : "?",
                    role = a.Role.ToString(),
                    dof = a.Arm != null ? a.Arm.Dof : 0,
                    control = a.ApiControl ? "api" : "auto",
                    status = a.Status,
                    needle = a.Needle != null,
                    tcpSpeed = a.Arm != null ? a.Arm.TcpSpeed : 0f,
                    tcp = new[] { tcp.x, tcp.y, tcp.z },
                    tip = new[] { tip.x, tip.y, tip.z },
                    joints = joints,
                });
            }

            foreach (RobotTask t in _mgr.Tasks)
            {
                var st = t as SutureTask;
                var apiTask = new ApiTask
                {
                    kind = st != null ? "suture" : "other",
                    title = t.description,
                    assigned = t.assigned,
                    done = t.done,
                    stitchesDone = st != null ? st.CompletedStitches : 0,
                    stitchesTotal = st != null ? st.stitches.Count : 0,
                    stitches = new List<ApiStitch>(),
                };
                if (st != null)
                {
                    foreach (StitchPoint sp in st.stitches)
                    {
                        apiTask.stitches.Add(new ApiStitch
                        {
                            entry = new[] { sp.entry.x, sp.entry.y, sp.entry.z },
                            exit = new[] { sp.exit.x, sp.exit.y, sp.exit.z },
                            completed = sp.completed,
                        });
                    }
                }
                state.tasks.Add(apiTask);
            }

            var sync = TissueDeformationSync.LastInstance;
            if (sync != null)
            {
                Bounds b = sync.CurrentBounds;
                state.tissue = new ApiTissue
                {
                    live = true,
                    maxDelta = sync.MaxAbsDelta,
                    boundsMin = new[] { b.min.x, b.min.y, b.min.z },
                    boundsMax = new[] { b.max.x, b.max.y, b.max.z },
                };
            }
            else
            {
                state.tissue = new ApiTissue { live = false };
            }

            return JsonUtility.ToJson(state);
        }

        internal string BuildScenarioJson()
        {
            var list = new ApiScenarioList { scenarios = new List<ApiScenario>() };
            foreach (SurgicalScenario s in ScenarioRegistry.All)
            {
                list.scenarios.Add(new ApiScenario
                {
                    id = s.Id,
                    title = s.Title,
                    refs = string.Join("; ", s.SurgeryRefs),
                    armCount = s.ArmCount,
                    dof = s.DofPerArm,
                });
            }
            return JsonUtility.ToJson(list);
        }

        internal string BuildCalibrationJson()
        {
            if (_mgr == null) return "{}";
            var cal = new ApiCalibration { arms = new List<ApiArmCal>() };
            for (int i = 0; i < _mgr.Agents.Count; i++)
            {
                ArmAgent a = _mgr.Agents[i];
                var armCal = new ApiArmCal
                {
                    index = i,
                    name = a.Arm != null ? a.Arm.ArmName : "?",
                    dof = a.Arm != null ? a.Arm.Dof : 0,
                    role = a.Role.ToString(),
                    needle = a.Needle != null,
                    units = "meters/radians/degrees-as-noted",
                };
                if (a.Arm != null)
                {
                    Core.ArmSpec spec = a.Arm.Spec;
                    Transform t = a.Arm.transform;
                    armCal.basePosition = new[] { t.position.x, t.position.y, t.position.z };
                    Vector3 fwd = t.rotation * Vector3.forward;
                    armCal.facing = new[] { fwd.x, fwd.y, fwd.z };
                    armCal.reach = spec.Reach;
                    armCal.stiffness = spec.jointStiffness;
                    armCal.damping = spec.jointDamping;
                    armCal.forceLimit = spec.jointForceLimit;
                    armCal.fingerLength = spec.fingerLength;
                    armCal.fingerOpen = spec.fingerOpen;
                    armCal.fingerClosed = spec.fingerClosed;
                    armCal.graspRadius = spec.graspRadius;
                    armCal.joints = new List<ApiJointCal>(spec.joints.Count);
                    foreach (Core.JointSpec j in spec.joints)
                    {
                        armCal.joints.Add(new ApiJointCal
                        {
                            name = j.name,
                            axisLocal = new[] { j.axis.x, j.axis.y, j.axis.z },
                            linkLength = j.linkLength,
                            limitDeg = new[] { j.lowerLimitDeg, j.upperLimitDeg },
                            maxServoStepDeg = j.maxServoStepDeg,
                        });
                    }
                }
                if (a.Needle != null)
                {
                    Vector3 off = a.Needle.TipOffsetFromTcp;
                    armCal.needleTipOffsetTcp = new[] { off.x, off.y, off.z };
                }
                else
                {
                    armCal.needleTipOffsetTcp = new[] { 0f, 0f, 0f };
                }
                cal.arms.Add(armCal);
            }
            return JsonUtility.ToJson(cal);
        }

        // ------------------------------------------------------------------ //
        //  Commands (write side)
        // ------------------------------------------------------------------ //

        public void EnqueueCommand(ApiCommand cmd) => _inbox.Enqueue(cmd);

        private void Apply(ApiCommand cmd)
        {
            switch (cmd.action)
            {
                case "Scenario":
                case "scenario":
                    _mgr.SwitchScenario(cmd.id);
                    break;
                case "Pause":
                case "pause":
                    _mgr.SetPaused(true);
                    break;
                case "Unpause":
                    _mgr.SetPaused(false);
                    break;
                // NOTE: "Resume" is a per-ARM action (hand autonomy back);
                // global resume arrives here as "Unpause" from /api/control,
                // so the two never shadow each other.
                default:
                    ApplyArmCommand(cmd);
                    break;
            }
        }

        private void ApplyArmCommand(ApiCommand cmd)
        {
            if (cmd.arm < 0 || cmd.arm >= _mgr.Agents.Count)
            {
                Debug.LogWarning($"[AgentApi] arm index {cmd.arm} out of range");
                return;
            }
            ArmAgent agent = _mgr.Agents[cmd.arm];
            Vector3 target = cmd.target != null && cmd.target.Length == 3
                ? new Vector3(cmd.target[0], cmd.target[1], cmd.target[2])
                : Vector3.zero;

            switch (cmd.action)
            {
                case "MoveTcp": agent.ApiMoveTcp(target); break;
                case "MoveTip": agent.ApiMoveTip(target); break;
                case "Gripper": agent.ApiGripper(Mathf.Clamp01(cmd.open)); break;
                case "Home": agent.ApiHome(); break;
                case "Resume": agent.ApiResume(); break;
                case "Cinch":
                    if (!agent.ApiCinch())
                        Debug.LogWarning($"[AgentApi] Cinch rejected for arm {cmd.arm}: " +
                                         "needle tip is not at the current stitch exit " +
                                         "(drive the needle through entry->exit first)");
                    break;
                default:
                    Debug.LogWarning($"[AgentApi] unknown action '{cmd.action}'");
                    break;
            }
        }

        // ------------------------------------------------------------------ //
        //  DTOs
        // ------------------------------------------------------------------ //

        [Serializable] public class ApiState
        {
            public string timestamp;
            public int frame;
            public string scenario, scenarioTitle, backend, backendStatus;
            public bool paused, editorPaused;
            public int stitchesDone, stitchesTotal;
            public float sofaFps, sofaTime;
            public long sofaStepMs, sofaSteps;
            public float liverDelta;
            public List<ApiArm> arms;
            public List<ApiTask> tasks;
            public ApiTissue tissue;
        }

        [Serializable] public class ApiArm
        {
            public int index, dof;
            public string name, role, control, status;
            public bool needle;
            public float tcpSpeed;
            public float[] tcp, tip, joints;
        }

        [Serializable] public class ApiTask
        {
            public string kind, title;
            public bool assigned, done;
            public int stitchesDone, stitchesTotal;
            public List<ApiStitch> stitches;
        }

        [Serializable] public class ApiStitch
        {
            public float[] entry, exit;
            public bool completed;
        }

        [Serializable] public class ApiTissue
        {
            public bool live;
            public float maxDelta;
            public float[] boundsMin, boundsMax;
        }

        [Serializable] public class ApiCalibration { public List<ApiArmCal> arms; }

        [Serializable] public class ApiArmCal
        {
            public int index, dof;
            public string name, role, units;
            public bool needle;
            public float[] basePosition, facing, needleTipOffsetTcp;
            public float reach, stiffness, damping, forceLimit;
            public float fingerLength, fingerOpen, fingerClosed, graspRadius;
            public List<ApiJointCal> joints;
        }

        [Serializable] public class ApiJointCal
        {
            public string name;
            public float[] axisLocal, limitDeg;
            public float linkLength, maxServoStepDeg;
        }

        [Serializable] public class ApiScenario
        {
            public string id, title, refs;
            public int armCount, dof;
        }

        [Serializable] public class ApiScenarioList { public List<ApiScenario> scenarios; }

        [Serializable] public class ApiCamera
        {
            public string id, label;
            public float[] position, rotation, viewport;
        }

        [Serializable] public class ApiCameraList { public List<ApiCamera> cameras; }

        [Serializable] public class ApiCommand
        {
            public string action, id;
            public int arm = -1;
            public float[] target;
            public float open = 1f;
        }
    }

    /// <summary>Minimal HttpListener JSON API on the loopback interface.</summary>
    internal class AgentHttpServer
    {
        private readonly int _port;
        private readonly SurgicalAgentApi _api;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public AgentHttpServer(int port, SurgicalAgentApi api)
        {
            _port = port;
            _api = api;
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                try
                {
                    Handle(ctx);
                }
                catch (Exception e)
                {
                    try { Respond(ctx, 500, $"{{\"error\":\"{e.Message}\"}}"); }
                    catch { /* response already (partially) sent or client gone */ }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;

            if (method == "GET")
            {
                switch (path)
                {
                    case "/api/state":
                        if (StaleResponse(ctx, includeFrame: true)) return;
                        Respond(ctx, 200, _api.CurrentStateJson);
                        return;
                    case "/api/scenarios":
                        Respond(ctx, 200, _api.CurrentScenarioJson);
                        return;
                    case "/api/cameras":
                        Respond(ctx, 200, _api.CurrentCameraJson);
                        return;
                    case "/api/calibration":
                        Respond(ctx, 200, _api.CurrentCalibrationJson);
                        return;
                    case "/api/screenshot":
                    {
                        if (StaleResponse(ctx, includeFrame: true)) return;
                        string camId = ctx.Request.QueryString["camera"];
                        if (!SurgicalAgentApi.IsValidCameraId(camId))
                        {
                            Respond(ctx, 400, "{\"error\":\"unknown camera id\",\"valid\":" +
                                             "[\"main\",\"screen\",\"front\",\"back\",\"left\",\"right\",\"top\"]}");
                            return;
                        }
                        var (w, h) = ResolveShotSize(ctx.Request.QueryString);
                        byte[] png = _api.RequestScreenshot(camId, 8000, w, h);
                        if (png == null) { Respond(ctx, 503, "{\"error\":\"capture timeout\"}"); return; }
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "image/png";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                        ctx.Response.ContentLength64 = png.Length;
                        try
                        {
                            ctx.Response.OutputStream.Write(png, 0, png.Length);
                            ctx.Response.OutputStream.Close();
                        }
                        catch { /* client disconnected mid-transfer */ }
                        return;
                    }
                }
            }
            else if (method == "POST")
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = reader.ReadToEnd();

                // A stalled main loop never consumes queued commands; claiming
                // {"ok":true} in that state is a lie the 2026-10-24 audit
                // paid for. Fail fast and say why instead.
                if (StaleResponse(ctx, includeFrame: true, okFalse: true)) return;

                switch (path)
                {
                    case "/api/scenario":
                    {
                        var cmd = JsonUtility.FromJson<SurgicalAgentApi.ApiCommand>(body);
                        _api.EnqueueCommand(new SurgicalAgentApi.ApiCommand { action = "Scenario", id = cmd.id });
                        Respond(ctx, 200, "{\"ok\":true}");
                        return;
                    }
                    case "/api/control":
                    {
                        var cmd = JsonUtility.FromJson<SurgicalAgentApi.ApiCommand>(body);
                        _api.EnqueueCommand(new SurgicalAgentApi.ApiCommand
                        {
                            action = string.Equals(cmd.action, "pause", StringComparison.OrdinalIgnoreCase)
                                ? "Pause" : "Unpause",
                        });
                        Respond(ctx, 200, "{\"ok\":true}");
                        return;
                    }
                    case "/api/command":
                    {
                        var cmd = JsonUtility.FromJson<SurgicalAgentApi.ApiCommand>(body);
                        if (string.IsNullOrEmpty(cmd.action))
                        {
                            Respond(ctx, 400, "{\"error\":\"missing action\"}");
                            return;
                        }
                        _api.EnqueueCommand(cmd);
                        Respond(ctx, 200, "{\"ok\":true}");
                        return;
                    }
                }
            }

            Respond(ctx, 404, "{\"error\":\"not found\"}");
        }

        /// <summary>Screenshot resolution: size presets (sd/hd/fhd/2k/4k/8k/max)
        /// or explicit width/height. Default = 4K. Camera renders go to an
        /// off-screen RT so resolution is not limited by the window size;
        /// "screen" always captures at the display's native resolution.</summary>
        private static (int w, int h) ResolveShotSize(System.Collections.Specialized.NameValueCollection qs)
        {
            switch ((qs["size"] ?? "").ToLowerInvariant())
            {
                case "sd": return (960, 540);
                case "hd": return (1280, 720);
                case "fhd": return (1920, 1080);
                case "2k": return (2560, 1440);
                case "4k": return (3840, 2160);
                case "8k":
                case "max": return (7680, 4320);
            }
            if (int.TryParse(qs["width"], out int w) && int.TryParse(qs["height"], out int h))
                return (w, h);
            return (3840, 2160); // default 4K
        }

        /// <summary>503 responder for a stalled main loop; returns true when
        /// the request was answered (caller must return immediately).</summary>
        private bool StaleResponse(HttpListenerContext ctx, bool includeFrame, bool okFalse = false)
        {
            if (!_api.IsSimStale(out double staleSec, out int frame)) return false;
            string body = $"{{\"error\":\"simulation main loop stalled\"," +
                          $"\"staleSeconds\":{staleSec:F2}," +
                          (includeFrame ? $"\"frame\":{frame}," : "") +
                          (okFalse ? "\"ok\":false," : "") +
                          "\"hint\":\"Unity main thread is not consuming frames/commands; " +
 "check the editor/process before relying on this snapshot\"}";
            Respond(ctx, 503, body);
            return true;
        }

        private static void Respond(HttpListenerContext ctx, int code, string json)
        {
            // A client that disconnects mid-response (curl timeout) makes the
            // write throw AFTER headers were sent; an unguarded second
            // Respond from the loop's catch previously killed the listener
            // thread with "Cannot be changed after headers are sent".
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { /* client gone - nothing to do */ }
        }
    }
}
