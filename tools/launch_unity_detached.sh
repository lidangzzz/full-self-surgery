#!/bin/zsh
# Launch the Unity editor fully detached from this shell's session so it
# survives after the launching process exits (start_new_session via python).
/usr/bin/env python3 -c '
import os, subprocess, shutil

# A hard-killed editor leaves EditorInstance.json + Temp behind, which makes
# every subsequent launch block on a "dangling scene backups" modal dialog.
# Clear that state first (only when no editor is running).
running = subprocess.run(["pgrep", "-f",
    "Unity.app/Contents/MacOS/Unity -projectPath /Users/xinranwang/Documents/GitHub/full-self-surgery"],
    capture_output=True).returncode == 0
if not running:
    for p in ["Library/EditorInstance.json"]:
        if os.path.exists(p):
            os.remove(p)
            print("cleared stale", p)
    if os.path.isdir("Temp"):
        shutil.rmtree("Temp")
'
/usr/bin/env python3 -c '
import subprocess, sys
p = subprocess.Popen([
    "/Applications/Unity/Hub/Editor/6000.6.7f1-arm64/Unity.app/Contents/MacOS/Unity",
    "-projectPath", "/Users/xinranwang/Documents/GitHub/full-self-surgery",
    "-executeMethod", "SofaRobotAI.SceneSetup.LaunchDemo.Run",
    "-logFile", "/tmp/unity_octo_demo.log",
], start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
print("UNITY_PID", p.pid)
'
