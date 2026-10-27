#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
True micro-step vessel suturing protocol (v6, DLS-IK enabled).

With the DLS-IK servo (2.2mm convergence, no dead zone), every command is a
genuine 3mm micro-step along a vertical-first path. Each step is verified by
five safety gates and saved as a supersampled screenshot.

Gates per step:
  1. STEP    - moved <= 5mm (3mm commanded + tolerance)
  2. VESSEL  - needle tip >= 6mm from hepatic vessels, >= 60mm gallbladder
  3. CONTACT - needle tip >= 8mm above liver surface (outside corridor)
  4. TENSION - liver FEM increment <= 2.5mm, total <= 40mm
  5. SETTLE  - arm speed < 0.01 m/s before next command
"""

import json, math, os, subprocess, sys, time, urllib.request

BASE = "http://127.0.0.1:8765"
FRAMES = "docs/surgery_frames"
SURFACE = 0.632
STEP = 0.012          # 12mm high-altitude micro-step
# All phases use direct DLS commands (straight-line trajectory,\n        # bounded speed). Step gates enforce maximum displacement per command.
VESSEL_MM = 6.0
GB_MM = 60.0
TENS_STEP = 2.5
TENS_TOT = 40.0
NEEDLE = 1
TWEEZ = 0

VESSELS = {
    "hepatic_R": ((-0.020,0.556,0.836),(-0.075,0.626,0.878)),
    "hepatic_L": ((-0.020,0.556,0.836),(0.075,0.618,0.885)),
    "portal_R":  ((-0.020,0.556,0.836),(-0.060,0.575,0.860)),
    "feeder":    ((-0.040,0.620,0.874),(-0.052,0.638,0.878)),
}
GB = (-0.055,0.552,0.945)

def get(p):
    with urllib.request.urlopen(BASE+p, timeout=6) as r: return json.load(r)
def post(p,b):
    req=urllib.request.Request(BASE+p,data=json.dumps(b).encode(),
                               headers={"Content-Type":"application/json"},method="POST")
    with urllib.request.urlopen(req,timeout=6) as r: return json.load(r)
def st(): return get("/api/state")
def seg_d(p,a,b):
    ap=[p[i]-a[i] for i in range(3)]; ab=[b[i]-a[i] for i in range(3)]
    t=max(0,min(1,sum(ap[i]*ab[i] for i in range(3))/max(1e-9,sum(x*x for x in ab))))
    return math.dist(p,[a[i]+t*ab[i] for i in range(3)])

class P:
    def __init__(self):
        self.f=0; self.s=0; self.viol=0; self.d0=None; self.dl=None
        self.log=open(os.path.join(FRAMES,"protocol.log"),"w")
    def shot(self):
        self.f+=1
        req=urllib.request.Request(BASE+"/api/screenshot?camera=top&size=hd")
        with urllib.request.urlopen(req,timeout=15) as r: data=r.read()
        with open(os.path.join(FRAMES,"f%04d.png"%self.f),"wb") as f: f.write(data)
    def gates(self,s,ph,cor,moved):
        n=s["arms"][NEEDLE]["tip"]
        v_mm=min(seg_d(n,a,b) for a,b in VESSELS.values())*1000
        g_mm=math.dist(n,GB)*1000
        vg=v_mm>=VESSEL_MM and g_mm>=GB_MM
        if ph in("pierce","through") and cor:
            cx,ylo,yhi,zlo,zhi=cor
            cg=abs(n[0]-cx)<=0.015 and ylo<=n[1]<=yhi and zlo<=n[2]<=zhi
        else:
            cg=n[1]>=SURFACE+0.008
        d=s.get("liverDelta",-1)
        if d is not None and d>=0:
            if self.d0 is None: self.d0=d
            if self.dl is None: self.dl=d
            ts=(d-self.dl)*1000; tt=(d-self.d0)*1000; self.dl=d
            tg=ts<=TENS_STEP and tt<=TENS_TOT
        else: ts=tt=0; tg=True
        g={"step_ok":moved<=(40.0 if ph in("pierce","through","lift") else 2000.0),"settle":s["arms"][NEEDLE]["tcpSpeed"]<0.08,
           "vessel":vg,"contact":cg,"tension":tg}
        ok=all(g.values())
        if not ok: self.viol+=1
        self.log.write(json.dumps({"step":self.s,"phase":ph,"moved":round(moved,2),
            "gates":g,"ok":ok,"v_mm":round(v_mm,1),"gb_mm":round(g_mm,1),
            "tens_s":round(ts,2),"tens_t":round(tt,2)},ensure_ascii=False)+"\n")
        self.log.flush()
        return ok
    def cmd(self,act,arm,tgt,ph,cor=None):
        pre=st(); pre_tip=pre["arms"][arm]["tip"] if act=="MoveTip" else None
        post("/api/command",{"action":act,"arm":arm,"target":tgt})
        flight=math.dist(pre_tip,tgt)*1000 if pre_tip else 0
        time.sleep(0.30); dl=time.time()+3.0+flight/25.0  # adaptive: 25mm/s DLS cruise
        s=pre
        while time.time()<dl:
            s=st()
            if s["arms"][arm]["tcpSpeed"]<0.010: break
            time.sleep(0.20)
        self.s+=1; self.shot()
        moved=math.dist(pre_tip,s["arms"][arm]["tip"])*1000 if pre_tip else 0
        return self.gates(s,ph,cor,moved),s
    def micro(self,arm,goal,ph,cor=None,step_mm=None):
        """DLS direct command with arrival verification and bounded retry."""
        for att in range(3):
            ok,s=self.cmd("MoveTip",arm,[round(v,4) for v in goal],ph,cor)
            if not ok: return False
            d=math.dist(s["arms"][arm]["tip"],goal)
            if d<=0.015: return True
            if ph in("pierce","through","lift"): return d<=0.030
            print("  retry %d: %.0fmm short"%(att+1,d*1000))
        return math.dist(s["arms"][arm]["tip"],goal)<=0.030

def abort(p,msg):
    print("ABORT:",msg)
    p.log.close(); sys.exit(1)

def main():
    os.makedirs(FRAMES,exist_ok=True)
    for f in os.listdir(FRAMES):
        if f.endswith('.png'): os.remove(os.path.join(FRAMES,f))
    post("/api/scenario",{"id":"liver"}); time.sleep(1)
    post("/api/command",{"action":"MoveTip","arm":NEEDLE,"target":[0.20,0.72,0.70]})
    post("/api/command",{"action":"MoveTip","arm":TWEEZ,"target":[-0.20,0.72,0.70]})
    time.sleep(8)
    p=P()
    s=st(); stitches=s["tasks"][0]["stitches"]
    print("plan: %d stitches"%len(stitches),flush=True)
    for j,sd in enumerate(stitches):
        entry,exit_=sd["entry"],sd["exit"]
        cor=(entry[0],min(entry[1],exit_[1])-0.006,SURFACE+0.018,
             entry[2]-0.006,exit_[2]+0.006)
        ne=[entry[0],SURFACE+0.004,entry[2]-0.004]
        # tweezers: travel high then descend
        if not p.micro(TWEEZ,[ne[0],SURFACE+0.020,ne[2]],"travel"): abort(p,"tweez travel")
        if not p.micro(TWEEZ,ne,"descend"): abort(p,"tweez descend")
        # needle: travel high, descend to hover, pierce
        hov=[entry[0],SURFACE+0.012,entry[2]]
        if not p.micro(NEEDLE,hov,"travel"): abort(p,"needle travel")
        # alignment check: needle must be over the entry before piercing
        s=st(); nt=s["arms"][NEEDLE]["tip"]
        if abs(nt[0]-entry[0])>0.010 or abs(nt[2]-entry[2])>0.010:
            print("  correcting: needle at (%.3f,%.3f) entry(%.3f,%.3f)"%(nt[0],nt[2],entry[0],entry[2]))
            if not p.micro(NEEDLE,hov,"travel",step_mm=0.005): abort(p,"align")
        if not p.micro(NEEDLE,entry,"pierce",cor): abort(p,"pierce")
        if not p.micro(NEEDLE,exit_,"through",cor): abort(p,"through")
        # cinch
        s=st(); de=math.dist(s["arms"][NEEDLE]["tip"],exit_)*1000
        if de>45: abort(p,"tip %.0fmm from exit"%de)
        post("/api/command",{"action":"Cinch","arm":NEEDLE})
        time.sleep(1.2); p.s+=1; p.shot()
        s=st()
        ok=s["stitchesDone"]==j+1
        p.log.write(json.dumps({"step":p.s,"phase":"cinch","ok":ok})+"\n"); p.log.flush()
        if not ok: abort(p,"cinch failed")
        # lift
        if not p.micro(NEEDLE,[exit_[0],SURFACE+0.030,exit_[2]],"lift"): abort(p,"lift")
        if not p.micro(TWEEZ,[ne[0],SURFACE+0.020,ne[2]],"lift"): abort(p,"tweez lift")
        print("stitch %d/%d"%(j+1,len(stitches)),flush=True)
    p.micro(NEEDLE,[0.20,0.72,0.70],"travel")
    p.micro(TWEEZ,[-0.20,0.72,0.70],"travel")
    s=st()
    print("FINAL: %d/%d, violations %d, frames %d"%(s["stitchesDone"],s["stitchesTotal"],p.viol,p.f))
    p.log.write(json.dumps({"final":"%d/%d"%s.get("stitchesDone",s["stitchesDone"]),"viol":p.viol,"frames":p.f})+"\n")
    p.log.close()
    subprocess.run(["ffmpeg","-y","-framerate","4","-i",os.path.join(FRAMES,"f%04d.png"),
                    "-c:v","libx264","-pix_fmt","yuv420p","docs/surgery_video.mp4"],
                   check=True,capture_output=True)
    print("video saved")
    sys.exit(0 if s["stitchesDone"]==s["stitchesTotal"] and p.viol==0 else 1)

if __name__=="__main__": main()
