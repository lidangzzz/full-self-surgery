#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SofaRobotAI.SceneSetup
{
    /// <summary>
    /// Opens the multi-arm scene, enables the SofaUnity backend and enters
    /// Play mode automatically. Used to launch the demo in one command:
    ///   Unity -projectPath &lt;project&gt; -executeMethod SofaRobotAI.SceneSetup.LaunchDemo.Run
    /// </summary>
    public static class LaunchDemo
    {
        private const string ScenePath = "Assets/Scenes/MultiArmAgentScene.unity";

        public static void Run()
        {
            KeepAliveInBackground();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var mgr = Object.FindAnyObjectByType<MultiArmAgentManager>();
            if (mgr != null)
            {
                mgr.backend = Backend.PhysicsBackendMode.SofaUnity;
                EditorSceneManager.SaveScene(scene); // persist SOFA as this machine's default
                Debug.Log("[LaunchDemo] backend set to SofaUnity, scene saved");
            }
            else
            {
                Debug.LogWarning("[LaunchDemo] MultiArmAgentManager not found - playing scene as-is");
            }

            EditorApplication.isPlaying = true;
            Debug.Log("[LaunchDemo] entering Play mode");
        }
        /// <summary>Play mode must keep simulating while the editor window
        /// is unfocused/occluded: agents drive the sim from another process
        /// (terminal, IDE), and Unity's default focus throttling would
        /// silently freeze the main loop.</summary>
        private static void KeepAliveInBackground()
        {
            Application.runInBackground = true;
#if UNITY_EDITOR
            UnityEditor.PlayerSettings.runInBackground = true; // persist
            UnityEditor.EditorPrefs.SetInt("InteractionMode", 2); // no throttling
#endif
        }
    }
}
#endif
