using System.Collections.Generic;
using UnityEngine;

namespace SofaRobotAI.Core
{
    /// <summary>
    /// 六宫格手术观察墙：主视角 + 前/后/左/右/顶五个环绕摄像头，把
    /// Game 视图切成 3×2 六个大画面**同时显示**（每个带边框与中文标
    /// 签）。任一机位可被外部 Agent 单独截图
    /// （GET /api/screenshot?camera=front|back|left|right|top|main|screen），
    /// 机位位置经 GET /api/cameras 读取。
    /// </summary>
    public class CameraWall : MonoBehaviour
    {
        public static CameraWall Instance { get; private set; }

        public class WallCamera
        {
            public string id;
            public string label;
            public Camera camera;
            public Rect viewport; // normalized screen rect (PIP tile)
        }

        public readonly List<WallCamera> Cameras = new List<WallCamera>();

        private static readonly (string id, string label, Vector3 pos, Rect cell)[] Specs =
        {
            ("front", "前 FRONT",  new Vector3(0f,    1.05f, 2.00f),  new Rect(1/3f, 0.5f, 1/3f, 0.5f)),
            ("back",  "后 BACK",   new Vector3(0f,    1.05f, -0.20f), new Rect(2/3f, 0.5f, 1/3f, 0.5f)),
            ("left",  "左 LEFT",   new Vector3(-1.75f, 1.00f, 0.90f), new Rect(0f,    0.0f, 1/3f, 0.5f)),
            ("right", "右 RIGHT",  new Vector3(1.75f, 1.00f, 0.90f),  new Rect(1/3f, 0.0f, 1/3f, 0.5f)),
            ("top",   "顶 TOP",    new Vector3(0f,    2.30f, 0.905f), new Rect(2/3f, 0.0f, 1/3f, 0.5f)),
        };

        private static readonly Vector3 WoundCenter = new Vector3(0f, 0.55f, 0.90f);
        private static readonly Rect MainCell = new Rect(0f, 0.5f, 1/3f, 0.5f);

        public static CameraWall Create(Transform parent)
        {
            var go = new GameObject("CameraWall");
            go.transform.SetParent(parent, false);
            return go.AddComponent<CameraWall>();
        }

        private void Awake()
        {
            Instance = this;
            // 主视角缩进左上格 —— 六个画面同时显示，不再独占整屏。
            if (Camera.main != null)
            {
                Camera.main.rect = MainCell;
                Cameras.Add(new WallCamera
                {
                    id = "main", label = "主视角 MAIN",
                    camera = Camera.main, viewport = MainCell,
                });
            }

            for (int i = 0; i < Specs.Length; i++)
            {
                var (id, label, pos, cell) = Specs[i];
                var camGo = new GameObject("Cam_" + id);
                camGo.transform.SetParent(transform, false);
                camGo.transform.position = pos;
                camGo.transform.LookAt(WoundCenter, Vector3.up);
                if (id == "top") camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = id == "top" ? 40f : 48f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.07f, 0.08f, 0.09f);
                cam.depth = 10 + i;

                // 3×2 grid cell (normalized screen rect).
                cam.rect = cell;

                Cameras.Add(new WallCamera { id = id, label = label, camera = cam, viewport = cell });
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Finds a wall camera by id (null when unknown).</summary>
        public Camera Find(string id)
        {
            foreach (var wc in Cameras) if (wc.id == id) return wc.camera;
            return null;
        }

        private void OnGUI()
        {
            // Border + label every grid cell so the wall reads clearly in the
            // Unity Game view (and in the composite screen capture).
            foreach (var wc in Cameras)
            {
                var cell = new Rect(
                    wc.viewport.x * Screen.width,
                    (1f - wc.viewport.y - wc.viewport.height) * Screen.height,
                    wc.viewport.width * Screen.width,
                    wc.viewport.height * Screen.height);
                GUI.Box(cell, ""); // thin border around the whole cell
                var label = new Rect(cell.x + 6f, cell.y + 4f, cell.width - 12f, 20f);
                GUI.Box(new Rect(label.x - 4f, label.y - 2f, label.width + 8f, label.height + 2f), "");
                GUI.Label(label, wc.label);
            }
        }
    }
}
