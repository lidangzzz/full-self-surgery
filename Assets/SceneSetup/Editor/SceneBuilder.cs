#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SofaRobotAI.SceneSetup
{
    /// <summary>
    /// Builds the multi-arm AI initialization scene procedurally:
    /// camera, lighting, floor, and the MultiArmAgentManager that spawns
    /// N arms + agents + payloads at Play time. Usable from the menu or
    /// the command line:
    ///   Unity -batchmode -quit -projectPath &lt;project&gt; \
    ///     -executeMethod SofaRobotAI.SceneSetup.SceneBuilder.BuildFromCommandLine
    /// </summary>
    public static class SceneBuilder
    {
        private const string SceneDir = "Assets/Scenes";
        private const string ScenePath = SceneDir + "/MultiArmAgentScene.unity";

        [MenuItem("SofaRobotAI/Build Multi-Arm AI Scene")]
        public static void BuildAndSave()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            SetupCamera();
            SetupLight();
            BuildFloor();
            BuildManager();

            if (!Directory.Exists(SceneDir)) Directory.CreateDirectory(SceneDir);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[SceneBuilder] scene saved to {ScenePath}");
        }

        /// <summary>Entry point for `Unity -executeMethod` batch builds.</summary>
        public static void BuildFromCommandLine()
        {
            BuildAndSave();
            EditorApplication.Exit(0);
        }

        private static void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Close-up on the tissue pad (center 0,0.46,0.9) with the arms
            // working behind it.
            cam.transform.position = new Vector3(0f, 1.8f, -2.0f);
            cam.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            cam.fieldOfView = 48f;
            cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
            cam.clearFlags = CameraClearFlags.SolidColor;
        }

        private static void SetupLight()
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light == null) return;
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
        }

        private static void BuildFloor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(4f, 1f, 4f); // 40 x 40 m
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(0.55f, 0.57f, 0.6f);
            mat.SetFloat("_Glossiness", 0.15f);
            floor.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void BuildManager()
        {
            var go = new GameObject("MultiArmAgentManager");
            var mgr = go.AddComponent<MultiArmAgentManager>();
            // Init-scene configuration: 3 arms x 6 DOF, suture demo on.
            // Toggle suturingMode off for the pick-and-place variant.
            mgr.suturingMode = true;
        }
    }
}
#endif
