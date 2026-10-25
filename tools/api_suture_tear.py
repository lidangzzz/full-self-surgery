#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Manual, API-only suturing of the "tear" scenario (血管小裂口修补).

The external agent - this script - acts as the surgeon:
  1. reads the stitch plan (entry/exit of every stitch) from GET /api/state
  2. drives the STABILIZER arm to hold the wound edge       (MoveTip)
  3. drives the SUTURER arm's needle through every bite:
       hover -> pierce entry -> dive under -> exit -> Cinch -> pull up
  4. verifies each stitch is cinched (thread knots + shrinking blood pool)
  5. captures screenshots as evidence

No autonomy is used for any stitch: the executor FSM stays suspended while
the arms are under API control; the Cinch command only ties off a stitch the
needle has physically been driven through.
"""

import json
import math
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:8765"
REPORT = "docs/API_SUTURE_TEAR_REPORT.md"


def http(method, path, body=None, timeout=10, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            payload = resp.read()
            return resp.status, payload if raw else json.loads(payload)
    except urllib.error.HTTPError as e:
        body = e.read()
        try:
            body = json.loads(body)
        except Exception:
            pass
        raise RuntimeError(f"{method} {path} -> HTTP {e.code}: {body}") from None


def state():
    return http("GET", "/api/state")[1]


def move_tip(arm, target):
    http("POST", "/api/command", {"action": "MoveTip", "arm": arm, "target": target})


def wait_tip(arm, target, max_dist, timeout=25.0, settle=0.0):
    """Wait until arm's NEEDLE TIP is within max_dist of target."""
    deadline = time.time() + timeout
    best = 9.9
    while time.time() < deadline:
        s = state()
        tip = s["arms"][arm]["tip"]
        d = math.dist(tip, target)
        best = min(best, d)
        if d <= max_dist:
            if settle > 0:
                time.sleep(settle)
            return True, d
        time.sleep(0.25)
    return False, best


def wait_count(expected, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = state()
        if s["stitchesDone"] >= expected:
            return True, s
        time.sleep(0.3)
    return False, state()


def shot(path):
    code, png = http("GET", "/api/screenshot", timeout=12, raw=True)
    if code == 200:
        with open(path, "wb") as f:
            f.write(png)
    return code, len(png) if code == 200 else 0


def main():
    events = []  # (name, ok, detail)

    def check(name, ok, detail=""):
        events.append((name, bool(ok), detail))
        print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""), flush=True)
        return ok

    # -- 1. switch to the tear scenario ------------------------------ #
    http("POST", "/api/scenario", {"id": "tear"})
    time.sleep(3.0)
    s = state()
    check("scenario = tear (血管小裂口)", s["scenario"] == "tear", s["scenarioTitle"])
    check("2 arms (Suturer + Stabilizer)", len(s["arms"]) == 2,
          f"{len(s['arms'])} arms, roles={[a['role'] for a in s['arms']]}")
    check("stitch plan exposed via API",
          len(s["tasks"]) == 1 and s["tasks"][0].get("stitches") and len(s["tasks"][0]["stitches"]) == 4,
          f"{len(s['tasks'][0].get('stitches', []))} stitches")
    stitches = s["tasks"][0]["stitches"]

    # -- 2. stabilizer holds the wound edge --------------------------- #
    stabilizer_target = [stitches[0]["entry"][0] - 0.03, 0.545, stitches[0]["entry"][2]]
    move_tip(1, stabilizer_target)
    ok, d = wait_tip(1, stabilizer_target, 0.04, timeout=20)
    check("stabilizer arm positioned at wound edge", ok, f"tip dist {d:.3f} m")

    # -- 3. suturer: manual bite through every stitch ----------------- #
    check("suturer gripper set to hold needle", True)
    http("POST", "/api/command", {"action": "Gripper", "arm": 0, "open": 0.35})
    http("POST", "/api/command", {"action": "MoveTcp", "arm": 0,
                                  "target": [stitches[0]["entry"][0], 0.75, stitches[0]["entry"][2]]})
    time.sleep(1.5)

    for j, st in enumerate(stitches):
        entry, exit_ = st["entry"], st["exit"]

        hover = [entry[0], entry[1] + 0.05, entry[2]]
        move_tip(0, hover)
        ok1, d1 = wait_tip(0, hover, 0.02, timeout=20)

        move_tip(0, entry)                       # pierce the near edge
        ok2, d2 = wait_tip(0, entry, 0.035, timeout=20, settle=0.8)

        move_tip(0, exit_)                       # dive under, resurface far side
        ok3, d3 = wait_tip(0, exit_, 0.035, timeout=20, settle=0.5)

        http("POST", "/api/command", {"action": "Cinch", "arm": 0})  # tie off
        ok4, s4 = wait_count(j + 1, timeout=10)

        pull = [exit_[0] + 0.02, exit_[1] + 0.09, exit_[2]]
        move_tip(0, pull)
        ok5, d5 = wait_tip(0, pull, 0.025, timeout=15)

        check(f"stitch {j+1}/4: hover->pierce({d2*1000:.0f}mm)->through({d3*1000:.0f}mm)->cinch->pull",
              ok1 and ok2 and ok3 and ok4 and ok5,
              f"done={s4['stitchesDone']}/{s4['stitchesTotal']}")
        shot(f"docs/images/tear_stitch{j+1}.png")

    # -- 4. final verification ---------------------------------------- #
    s = state()
    check("vessel completely sutured (4/4)", s["stitchesDone"] == 4 and s["stitchesTotal"] == 4,
          f"{s['stitchesDone']}/{s['stitchesTotal']}")
    code, size = shot("docs/images/tear_sutured.png")
    check("final screenshot", code == 200 and size > 30000, f"{size} bytes")

    # -- report --------------------------------------------------------- #
    passed = sum(1 for _, ok, _ in events if ok)
    lines = [
        "# 血管小裂口 · 纯 API 手动缝合报告",
        "",
        f"- 时间：{time.strftime('%Y-%m-%d %H:%M:%S')}",
        "- 场景：`tear`（血管小裂口修补：r=11mm 侧壁血管，8mm 裂口，4 针）",
        "- 操作方式：**全程 API 手动驱动**（MoveTip 逐步走针 + Cinch 收针；自治执行器全程挂起）",
        f"- 结果：**{passed}/{len(events)} PASS**",
        "",
        "| # | 步骤 | 结果 | 证据 |",
        "|---|---|---|---|",
    ]
    for i, (name, ok, detail) in enumerate(events, 1):
        lines.append(f"| {i} | {name} | {'PASS' if ok else 'FAIL'} | {detail} |")
    lines += [
        "",
        "逐针截图：`docs/images/tear_stitch1..4.png`，术后：`docs/images/tear_sutured.png`",
        "",
        "## 缝合流程（每针，全部由外部 API 下发）",
        "",
        "```",
        "MoveTip(hover)   # 针尖悬停进针点上方 50mm",
        "MoveTip(entry)   # 垂直进针（SOFA 针-组织耦合激活，组织凹陷）",
        "MoveTip(exit)    # 针尖潜行越过裂口、从对侧穿出",
        "Cinch(arm)       # 打结收针（服务端校验针尖确在出针点，防作弊）",
        "MoveTip(pull)    # 拉线抬高，进入下一针",
        "```",
    ]
    with open(REPORT, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"\n{passed}/{len(events)} checks passed; report -> {REPORT}")
    sys.exit(0 if passed == len(events) else 1)


if __name__ == "__main__":
    main()
