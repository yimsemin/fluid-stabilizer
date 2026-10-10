"""DAgger loop around the Unity plant (experiment-plan A, findings F11).

    iteration 0   BC on the teacher dataset (already exported)  -> policy 0
    iteration k   serve policy k-1, Unity rolls it through the training drives
                  and relabels the states it visits (DaggerRolloutRunner), the new
                  rows are aggregated with all earlier ones, BC retrains from
                  scratch -> policy k, scored on train + validation drives.

Everything the policy is judged on (spill litres vs the rigid tank) comes from
Unity's PolicyEvalRunner, never from the BC loss.

    cd python
    .venv/bin/python dagger.py --dataset ../Logs/bc/dataset-<...>.csv \\
        --manifest datasets-f8-multi.tsv --out runs/dagger-f8 --iterations 4
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITY = "/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
PY = sys.executable


def serve(ckpt: str, port: int):
    log = open(ROOT / f"Logs/dagger-serve-{port}.log", "w")
    p = subprocess.Popen([PY, "serve_policy.py", "--checkpoint", ckpt, "--port", str(port)],
                         cwd=ROOT / "python", stdout=log, stderr=subprocess.STDOUT)
    time.sleep(5)
    return p


def unity(method: str, *args: str, log: str):
    r = subprocess.run([UNITY, "-batchmode", "-quit", "-nographics", "-projectPath", str(ROOT),
                        "-executeMethod", method, *args, "-logFile", str(ROOT / "Logs" / log)],
                       capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"{method} failed ({r.returncode}); see Logs/{log}")


def score(ckpt: str, port: int, manifest: str, split: str):
    """Total spill (L) of the served policy and of the rigid tank on one split."""
    srv = serve(ckpt, port)
    try:
        before = set(glob.glob(str(ROOT / "Logs/policy-eval-*.txt")))
        unity("FluidStabilizer.SimEditor.PolicyEvalRunner.Run", "-traceManifest", manifest,
              "-traceSplit", split, "-ppPort", str(port), log="dagger-peval.log")
        new = sorted(set(glob.glob(str(ROOT / "Logs/policy-eval-*.txt"))) - before)
        if not new:
            raise RuntimeError("no policy-eval report written")
        hold = pol = 0.0
        status = set()
        for line in open(new[-1]):
            m = re.match(r"\s+Rigid \(Hold\) \[analytic\]\s+([\d.]+)", line)
            if m:
                hold += float(m.group(1))
            m = re.match(r"\s+Learned policy \[([\w-]+)\]\s+([\d.]+)", line)
            if m:
                pol += float(m.group(2))
                status.add(m.group(1))
        return hold, pol, sorted(status)
    finally:
        srv.terminate()
        srv.wait()


def merge(paths: list[str], out: str):
    header, rows = [], []
    for i, p in enumerate(paths):
        for line in open(p):
            if line.startswith("#") or line[:1] == "o":
                if i == 0:
                    header.append(line)
                continue
            if line.strip():
                rows.append(line)
    with open(out, "w") as f:
        f.writelines(header)
        f.writelines(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dataset", required=True, help="iteration-0 BC dataset (teacher states)")
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--out", default="runs/dagger")
    ap.add_argument("--iterations", type=int, default=4)
    ap.add_argument("--epochs", type=int, default=150)
    ap.add_argument("--port", type=int, default=5650)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (ROOT / "Logs/dagger").mkdir(parents=True, exist_ok=True)
    datasets = [str(Path(args.dataset).resolve())]
    history = []

    for it in range(args.iterations + 1):
        agg = str(out / f"agg-{it}.csv")
        merge(datasets, agg)
        ckpt = str(out / f"it{it}" / "actor.pt")
        subprocess.run([PY, "bc_pretrain.py", "--dataset", agg, "--out", ckpt,
                        "--epochs", str(args.epochs), "--seed", str(args.seed)],
                       cwd=ROOT / "python", check=True, capture_output=True)
        row = {"iteration": it, "rows": sum(1 for l in open(agg) if l[:1] not in "#o\n")}
        for split in ("train", "validation"):
            hold, pol, status = score(ckpt, args.port, args.manifest, split)
            row[split] = {"hold": round(hold, 2), "policy": round(pol, 2), "status": status}
        history.append(row)
        print(json.dumps(row), flush=True)
        (out / "history.json").write_text(json.dumps(history, indent=1))
        if it == args.iterations:
            break
        # learner rollout -> relabelled states for the next aggregate
        srv = serve(ckpt, args.port)
        try:
            roll = str(ROOT / "Logs/dagger" / f"{out.name}-it{it + 1}.csv")
            unity("FluidStabilizer.SimEditor.DaggerRolloutRunner.Run", "-traceManifest", args.manifest,
                  "-ppPort", str(args.port), "-daOut", roll, log="dagger-rollout.log")
        finally:
            srv.terminate()
            srv.wait()
        datasets.append(roll)


if __name__ == "__main__":
    main()
