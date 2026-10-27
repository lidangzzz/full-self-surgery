using System;
using System.Reflection;
using UnityEngine;

namespace SofaRobotAI.Backend
{
    /// <summary>
    /// SOFA co-simulation backend built on the SofaUnity plugin
    /// (Assets/SofaUnity, github.com/InfinyTech3D/SofaUnity).
    ///
    /// What it does when the native SofaVerseAPI library is available
    /// (Windows x64, or macOS via SOFA_MacOS_v26.06.zip from the releases):
    ///  - creates a SofaContext (the SofaUnity simulation entry point),
    ///  - applies the cell's gravity/timestep,
    ///  - loads Assets/StreamingAssets/SOFA/robot_cell.scn so deformable
    ///    payloads and the cell environment are simulated by SOFA itself.
    ///
    /// The articulated arms themselves are always driven through Unity
    /// ArticulationBodies in this init scene; the public SofaUnity build does
    /// not ship an articulated-robot controller (that lives in the private
    /// SofaUnity "Modules" repo), so SOFA here enriches the environment
    /// physics rather than replacing the joint solver.
    ///
    /// On platforms without the native library, this backend reports
    /// unavailable and the manager silently falls back to
    /// <see cref="UnityPhysicsBackend"/>.
    /// </summary>
    public class SofaUnityBackend : IRobotPhysicsBackend
    {
        public string Name => "SofaUnity (SOFA co-simulation)";
        public string Status { get; private set; } = "not initialized";

        // The vendored SofaContext runs its native stepping synchronously on
        // the Unity main thread (sofaPhysicsAPI_step). A diverging CG solve
        // ("denominator threshold" warnings) then hangs the whole editor:
        // the HTTP thread keeps serving a stale snapshot while commands are
        // never consumed - exactly the 2026-10-24 failure. SofaContext's own
        // async mode is not an option either: this public native build accepts
        // asyncStep() without ever advancing the scene (verified: sofaTime
        // stays 0 after a successful kick). So stepping is moved to a dedicated
        // thread here: the same proven sync step() API, called off-thread,
        // with IsSofaUpdating=false keeping SofaContext.Update() out of the way.
        private static readonly FieldInfo ImplField = typeof(SofaUnity.SofaContext)
            .GetField("m_impl", BindingFlags.NonPublic | BindingFlags.Instance);

        private SofaUnity.SofaContext _sofaContext;
        // SceneFileManager resolves paths relative to Application.dataPath (Assets/).
        // brain_cell = robot_cell + the SOFA-simulated brain (both FEM bodies
        // coexist; every scenario keeps its pad, the brain scenario shows the
        // brain on top).
        private const string ScenePath = "/StreamingAssets/SOFA/brain_cell.scn";

        /// <summary>Native needle-tissue force coupling (null when unavailable).</summary>
        public Surgery.NeedleTissueCoupling Coupling { get; private set; }
        public Transform SofaRoot => _sofaContext != null ? _sofaContext.transform : null;

        /// <summary>Native solver FPS as reported by SofaContext (0 = stalled,
        /// -1 = no context). Exposed through /api/state for stall detection.</summary>
        public float SimulationFps
        {
            get
            {
                var impl = Impl as SofaUnityAPI.SofaContextAPI;
                return impl != null ? impl.GetSimulationFPS() : -1f;
            }
        }

        /// <summary>Native simulation clock (s) - direct proof that the solver
        /// actually advances, independent of the (mode-dependent) FPS counter.</summary>
        public float SimulationTime
        {
            get
            {
                var impl = Impl as SofaUnityAPI.SofaContextAPI;
                return impl != null ? impl.GetTime() : -1f;
            }
        }

        /// <summary>Elapsed ms inside the currently running (or last) native
        /// step. A value that keeps growing while stepping is enabled means
        /// the solver itself hung - visible through /api/state while Unity
        /// keeps serving fresh snapshots of everything else.</summary>
        public long CurrentStepElapsedMs
        {
            get
            {
                long end = System.Threading.Interlocked.Read(ref _stepEndTicks);
                long from = end != 0 ? end : System.Threading.Interlocked.Read(ref _stepStartTicks);
                return from == 0 ? 0
                    : (long)((System.Diagnostics.Stopwatch.GetTimestamp() - from)
                             * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        public long CompletedSteps => System.Threading.Interlocked.Read(ref _stepCount);

        private object Impl
        {
            get { try { return _sofaContext != null ? ImplField?.GetValue(_sofaContext) : null; }
                  catch (Exception) { return null; } }
        }

        private System.Threading.Thread _stepper;
        private volatile bool _stepperRunning;
        private volatile bool _stepperPaused = true; // starts paused until scene loads
        private long _stepStartTicks, _stepEndTicks, _stepCount;

        public bool Initialize(MultiArmAgentManager manager)
        {
            bool supported =
                Application.platform == RuntimePlatform.WindowsEditor ||
                Application.platform == RuntimePlatform.WindowsPlayer ||
                Application.platform == RuntimePlatform.OSXEditor ||
                Application.platform == RuntimePlatform.OSXPlayer;
            if (!supported)
            {
                Status = "unavailable: no SofaVerseAPI native build for this platform " +
                         $"({Application.platform}), using Unity PhysX fallback";
                return false;
            }

            try
            {
                var host = new GameObject("SofaUnity_Context");
                UnityEngine.Object.DontDestroyOnLoad(host);
                _sofaContext = host.AddComponent<SofaUnity.SofaContext>();
                _sofaContext.Gravity = Physics.gravity;
                _sofaContext.TimeStep = Time.fixedDeltaTime;

                // The native context must actually be alive: SofaContext swallows
                // native load failures in its own Awake, so probe it explicitly.
                IntPtr simu = _sofaContext.GetSimuContext();
                string version = _sofaContext.SofaVersion;
                if (simu == IntPtr.Zero || string.IsNullOrEmpty(version) ||
                    version == "undefined" || version == "Unknown version")
                {
                    Status = $"unavailable: native SofaVerseAPI did not initialize " +
                             $"(simu=0x{simu.ToInt64():X}, version=\"{version}\") - " +
                             "check the dylib/DLL set and its system dependencies, see README";
                    Debug.LogWarning("[SofaUnityBackend] " + Status);
                    return false;
                }

                // First scene assignment: ChangeScenePath refuses until a scene is
                // already loaded, so go through the SceneFileManager directly.
                _sofaContext.SceneFileMgr.changeSceneFilename(ScenePath);
                if (!string.IsNullOrEmpty(_sofaContext.SceneFileMgr.SceneFilename) &&
                    _sofaContext.SceneFileMgr.checkFileExists())
                {
                    _sofaContext.LoadSofaScene();
                }
                else
                {
                    Debug.LogWarning($"[SofaUnityBackend] scene not found at Assets{ScenePath}; " +
                                     "SOFA runs with an empty graph.");
                }

                // Needle-tissue force coupling addresses scene objects by name
                // (NeedleTipDOFs / TissueDOFs), so it needs the graph loaded.
                Coupling = new Surgery.NeedleTissueCoupling(simu, _sofaContext.transform);

                // Native stepping moves to the dedicated thread below; keep
                // SofaContext.Update() from ever stepping (and from pumping
                // native messages concurrently with the stepper thread).
                _sofaContext.IsSofaUpdating = false;
                _sofaContext.CatchSofaMessages = false;
                StartStepperThread();
                _stepperPaused = false; // run immediately after scene load

                Status = $"running (dedicated solver thread), SOFA {version}, scene: Assets{ScenePath}";
                return true;
            }
            catch (Exception e) // DllNotFoundException and friends
            {
                Status = "unavailable: " + e.GetType().Name + " - " + e.Message +
                         " (install the SofaVerseAPI DLLs, see README)";
                Debug.LogWarning("[SofaUnityBackend] " + Status);
                return false;
            }
        }

        /// <summary>Drives the SOFA needle-tip collision sphere (no-op when the
        /// coupling is not up).</summary>
        public void PushNeedleTip(Vector3 worldPos, bool active) =>
            Coupling?.PushNeedleTip(worldPos, active);

        /// <summary>Starts/stops the native simulation stepping.</summary>
        public void SetSimulationRunning(bool running)
        {
            _stepperPaused = !running;
        }

        private void StartStepperThread()
        {
            if (_stepper != null) return;
            _stepperRunning = true;
            _stepper = new System.Threading.Thread(StepperLoop)
            {
                IsBackground = true,
                Name = "SofaSolverStepper",
                Priority = System.Threading.ThreadPriority.AboveNormal,
            };
            float timestep = Mathf.Max(0.005f, Time.fixedDeltaTime); // main thread
            _stepper.Start(timestep);
            Debug.Log("[SofaUnityBackend] solver stepping moved to dedicated thread " +
                      "(Unity main loop is protected from native solver hangs)");
        }

        /// <summary>Real-time stepping loop: one proven sync step() per fixed
        /// timestep. If a native step never returns, this thread hangs but the
        /// Unity main thread - rendering, HTTP API, commands, scenario state -
        /// keeps running, and CurrentStepElapsedMs exposes the stall.</summary>
        private void StepperLoop(object timestepArg)
        {
            var impl = Impl as SofaUnityAPI.SofaContextAPI;
            if (impl == null) { _stepperRunning = false; return; }
            float timestep = (float)timestepArg;
            while (_stepperRunning)
            {
                if (_stepperPaused)
                {
                    System.Threading.Thread.Sleep(25);
                    continue;
                }
                System.Threading.Interlocked.Exchange(ref _stepStartTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                try
                {
                    impl.step(); // blocks only THIS thread, never Unity's
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SofaUnityBackend] native step threw: {e.Message}");
                    System.Threading.Thread.Sleep(250);
                }
                System.Threading.Interlocked.Exchange(ref _stepEndTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                System.Threading.Interlocked.Increment(ref _stepCount);

                long spentMs = (long)((System.Threading.Interlocked.Read(ref _stepEndTicks) -
                                       System.Threading.Interlocked.Read(ref _stepStartTicks))
                                      * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                int sleepMs = Math.Max(0, (int)(timestep * 1000f) - (int)spentMs - 1);
                if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs);
            }
        }

        public void Shutdown()
        {
            if (_sofaContext == null) return;
            // Stop the stepper BEFORE touching native state from the main
            // thread. If a native step is hung, skip native cleanup rather
            // than corrupting the context (a stale context leaks; the editor
            // itself keeps running and reports the stall through the API).
            _stepperRunning = false;
            if (_stepper != null && !_stepper.Join(2000))
            {
                Debug.LogWarning("[SofaUnityBackend] stepper thread stuck in a native step - " +
                                 "skipping native scene cleanup to keep the editor alive");
                return;
            }
            _stepper = null;
            try
            {
                _sofaContext.ClearSofaScene();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SofaUnityBackend] shutdown ignored native error: " + e.Message);
            }
            UnityEngine.Object.Destroy(_sofaContext.gameObject);
            _sofaContext = null;
        }
    }
}
