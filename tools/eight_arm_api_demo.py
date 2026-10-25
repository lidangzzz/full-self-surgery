#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Eight-arm, full-API surgery demo (external AI agent point of view).

Drives the running SofaRobotAI instance through EVERY documented HTTP API
surface and uses all eight arms of the "octo" scenario to complete one
full surgery (12 stitches, one batch per arm):

  GET  /api/state          fresh-state check (timestamp/frame must advance)
  GET  /api/scenarios      catalogue >= 50 SOFA-implementable scenarios
  GET  /api/calibration    full per-arm geometry/drive/joint calibration
  POST /api/scenario       switch to the eight-arm replantation scenario
  POST /api/command        MoveTcp + Gripper + Home + Resume on all 8 arms
  POST /api/control        global pause/resume mid-surgery
  GET  /api/screenshot     PNG capture of the operated scene

Exit code 0 = all checks passed. A Markdown report is written to
docs/EIGHT_ARM_API_DEMO_REPORT.md.
"""

import json
import math
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:8765"
REPORT_PATH = "docs/EIGHT_ARM_API_DEMO_REPORT.md"

results = []


def http(method, path, body=None, timeout=10):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read(), dict(resp.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)


def get_json(path, timeout=10):
    code, raw, _ = http("GET", path, timeout=timeout)
    if code != 200:
        raise RuntimeError(f"GET {path} -> HTTP {code}: {raw[:300].decode('utf-8', 'replace')}")
    return json.loads(raw)


def post(path, body, timeout=10):
    code, raw, _ = http("POST", path, body, timeout)
    if code != 200 or json.loads(raw).get("ok") is not True:
        raise RuntimeError(f"POST {path} {body} -> HTTP {code}: {raw[:300].decode('utf-8', 'replace')}")
    return json.loads(raw)


def state():
    return get_json("/api/state")


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
    return ok


def wait_until(predicate, timeout, poll=0.4, desc=""):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = state()
        if predicate(last):
            return True, last
        time.sleep(poll)
    return False, last


def main():
    ok_all = True

    # -- 1. fresh state -------------------------------------------------- #
    s1 = state()
    time.sleep(1.2)
    s2 = state()
    fresh = (s2["frame"] > s1["frame"]) and (s2["timestamp"] != s1["timestamp"])
    ok_all &= check("state readable & fresh (frame+timestamp advance)", fresh,
                    f"frame {s1['frame']}->{s2['frame']}, sofaFps={s2.get('sofaFps')}")

    # -- 2. scenario catalogue ------------------------------------------- #
    scenarios = get_json("/api/scenarios")["scenarios"]
    octo = next((s for s in scenarios if s["id"] == "octo"), None)
    ok_all &= check("catalogue >= 50 scenarios", len(scenarios) >= 50, f"{len(scenarios)} entries")
    ok_all &= check("octo eight-arm scenario present", octo is not None,
                    f"{octo['title']} armCount={octo['armCount']} dof={octo['dof']}" if octo else "missing")

    # -- 3. switch to octo ------------------------------------------------ #
    post("/api/scenario", {"id": "octo"})
    reached, s = wait_until(lambda st: st["scenario"] == "octo", 20, desc="scenario=octo")
    ok_all &= check("scenario switched to octo", reached and s.get("scenario") == "octo")
    arms = s["arms"]
    ok_all &= check("eight arms spawned", len(arms) == 8, f"{len(arms)} arms")
    ok_all &= check("all arms in surgical DOF range (4-5)",
                    all(a["dof"] in (4, 5) for a in arms),
                    str([a["dof"] for a in arms]))

    # -- 4. calibration completeness -------------------------------------- #
    cal = get_json("/api/calibration")
    cals = cal["arms"]
    cal_ok = len(cals) == 8
    for c in cals:
        cal_ok &= (
            len(c.get("basePosition", [])) == 3
            and len(c.get("facing", [])) == 3
            and c.get("reach", 0) > 0
            and len(c.get("joints", [])) == c["dof"]
            and all(len(j.get("axisLocal", [])) == 3 and len(j.get("limitDeg", [])) == 2
                    and j.get("linkLength", 0) > 0 for j in c["joints"])
        )
    ok_all &= check("calibration exposes full per-arm parameters", cal_ok,
                    f"{len(cals)} arms, per-arm joints/link/limits/axes"
                    + (f", sample reach={cals[0]['reach']:.2f} m" if cals else ""))

    # -- 5. MoveTcp: every arm enters api control and moves --------------- #
    s = state()
    moved = {}
    for i, a in enumerate(s["arms"]):
        tcp = a["tcp"]
        # small, safe displacement toward the table centre + lift: well inside
        # the 4-DOF workspace, avoiding neighbouring arms
        toward = (-tcp[0], 0.0, 0.90 - tcp[2])
        n = math.sqrt(toward[0] ** 2 + toward[2] ** 2) or 1.0
        target = [tcp[0] + toward[0] / n * 0.18, tcp[1] + 0.08, tcp[2] + toward[2] / n * 0.18]
        post("/api/command", {"action": "MoveTcp", "arm": i, "target": target})
        moved[i] = (a["tcp"], target)
    ok, s = wait_until(lambda st: all(a["control"] == "api" for a in st["arms"]), 8)
    ok_all &= check("MoveTcp: all 8 arms under api control", ok,
                    str([a["control"] for a in s["arms"]]))

    time.sleep(3.0)  # let the servos travel
    s = state()
    displ = []
    for i, a in enumerate(s["arms"]):
        d0 = moved[i][0]
        displ.append(math.dist(d0, a["tcp"]))
    ok_all &= check("MoveTcp: all 8 arms actually moved", min(displ) > 0.03,
                    "tcp displacement m: " + ", ".join(f"{d:.3f}" for d in displ))

    # -- 6. Gripper on every arm ------------------------------------------ #
    for i in range(8):
        post("/api/command", {"action": "Gripper", "arm": i, "open": 0.2})
        post("/api/command", {"action": "Gripper", "arm": i, "open": 1.0})
    ok_all &= check("Gripper accepted on all 8 arms", True, "open 0.2 -> 1.0 cycles")

    # -- 7. Home: all joints return to zero -------------------------------- #
    for i in range(8):
        post("/api/command", {"action": "Home", "arm": i})
    ok, s = wait_until(
        lambda st: all(max((abs(q) for q in a["joints"]), default=0) < 0.05 for a in st["arms"]),
        12)
    maxq = max(max((abs(q) for q in a["joints"]), default=0) for a in s["arms"])
    ok_all &= check("Home: all 8 arms at zero pose (<0.05 rad)", ok, f"max |q| = {maxq:.4f} rad")

    # -- 8. Resume: autonomy handed back on every arm ----------------------- #
    for i in range(8):
        post("/api/command", {"action": "Resume", "arm": i})
    ok, s = wait_until(lambda st: all(a["control"] == "auto" for a in st["arms"]), 8)
    ok_all &= check("Resume: all 8 arms back to auto", ok,
                    str([a["control"] for a in s["arms"]]))

    # -- 9. global pause/resume mid-surgery -------------------------------- #
    post("/api/control", {"action": "pause"})
    ok, s = wait_until(lambda st: st["paused"] is True, 5)
    ok_all &= check("global pause", ok)
    frozen = s["stitchesDone"]
    time.sleep(2.0)
    s = state()
    ok_all &= check("progress frozen while paused", s["stitchesDone"] == frozen,
                    f"{frozen} == {s['stitchesDone']}")
    post("/api/control", {"action": "resume"})
    ok, s = wait_until(lambda st: st["paused"] is False, 5)
    ok_all &= check("global resume", ok)

    # -- 10. autonomy completes the full surgery ---------------------------- #
    total = s["stitchesTotal"]
    ok, s = wait_until(lambda st: st["stitchesDone"] >= st["stitchesTotal"] > 0, 900, poll=1.0)
    ok_all &= check("surgery completed by 8-arm cell", ok,
                    f"stitches {s['stitchesDone']}/{s['stitchesTotal']}"
                    + (f" (paused={s['paused']}, sofaFps={s.get('sofaFps')})" if ok else ""))

    # -- 11. screenshot ----------------------------------------------------- #
    code, raw, headers = http("GET", "/api/screenshot", timeout=12)
    shot_ok = code == 200 and headers.get("Content-Type") == "image/png" and len(raw) > 30_000
    ok_all &= check("screenshot captured", shot_ok,
                    f"HTTP {code}, {headers.get('Content-Type')}, {len(raw)} bytes")
    if shot_ok:
        with open("/tmp/octo_surgery.png", "wb") as f:
            f.write(raw)

    # -- report -------------------------------------------------------------- #
    passed = sum(1 for _, okk, _ in results if okk)
    lines = [
        "# 八臂全 API 手术演示报告",
        "",
        f"- 时间：{time.strftime('%Y-%m-%d %H:%M:%S')}",
        f"- API：`{BASE}`  场景：`octo`（八臂多指离断血管重建，8×5 DOF，12 针）",
        f"- 结果：**{passed}/{len(results)} PASS**",
        "",
        "| # | 检查项 | 结果 | 证据 |",
        "|---|---|---|---|",
    ]
    for i, (name, okk, detail) in enumerate(results, 1):
        lines.append(f"| {i} | {name} | {'PASS' if okk else 'FAIL'} | {detail} |")
    lines.append("")
    with open(REPORT_PATH, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print(f"\n{passed}/{len(results)} checks passed; report -> {REPORT_PATH}")
    sys.exit(0 if ok_all else 1)


if __name__ == "__main__":
    main()
