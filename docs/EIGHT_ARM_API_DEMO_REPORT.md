# 八臂全 API 手术演示报告

- 时间：2026-10-24 16:30:05
- API：`http://127.0.0.1:8765`  场景：`octo`（八臂多指离断血管重建，8×5 DOF，12 针）
- 结果：**17/17 PASS**

| # | 检查项 | 结果 | 证据 |
|---|---|---|---|
| 1 | state readable & fresh (frame+timestamp advance) | PASS | frame 1193->1488, sofaFps=941.0 |
| 2 | catalogue >= 50 scenarios | PASS | 66 entries |
| 3 | octo eight-arm scenario present | PASS | 八臂多指离断血管重建 armCount=8 dof=5 |
| 4 | scenario switched to octo | PASS |  |
| 5 | eight arms spawned | PASS | 8 arms |
| 6 | all arms in surgical DOF range (4-5) | PASS | [5, 5, 5, 5, 5, 5, 5, 5] |
| 7 | calibration exposes full per-arm parameters | PASS | 8 arms, per-arm joints/link/limits/axes, sample reach=1.64 m |
| 8 | MoveTcp: all 8 arms under api control | PASS | ['api', 'api', 'api', 'api', 'api', 'api', 'api', 'api'] |
| 9 | MoveTcp: all 8 arms actually moved | PASS | tcp displacement m: 0.143, 0.170, 0.199, 0.214, 0.214, 0.215, 0.201, 0.186 |
| 10 | Gripper accepted on all 8 arms | PASS | open 0.2 -> 1.0 cycles |
| 11 | Home: all 8 arms at zero pose (<0.05 rad) | PASS | max |q| = 0.0252 rad |
| 12 | Resume: all 8 arms back to auto | PASS | ['auto', 'auto', 'auto', 'auto', 'auto', 'auto', 'auto', 'auto'] |
| 13 | global pause | PASS |  |
| 14 | progress frozen while paused | PASS | 0 == 0 |
| 15 | global resume | PASS |  |
| 16 | surgery completed by 8-arm cell | PASS | stitches 12/12 (paused=False, sofaFps=752.0) |
| 17 | screenshot captured | PASS | HTTP 200, image/png, 214952 bytes |

## 佐证

- 术野截图（12/12 完成）：`docs/images/octo_surgery.png`
- 同一实例随后通过 60 秒压力测试：8 场景 ×3 轮连续切换 **24/24 成功**，
  frame 持续递增（终帧 80350），无一次 503/停滞（详见
  `docs/CODEX_TEST_REPORT.md` R7 章节）
- 复现方式：`tools/launch_unity_detached.sh` 启动实例后运行
  `python3 tools/eight_arm_api_demo.py`
