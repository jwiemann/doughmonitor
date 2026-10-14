"""Benchmark vision models on the labelled low-light frames in tools/llm_bench.

Runs the replay CLI once per model (answers are cached on disk, so reruns are free),
then compares the surface row and jar bottom the model produced with labels.csv.

    python tools/llm_benchmark.py MODEL [MODEL ...] [--cache DIR] [--max-calls N]

Needs Monitor__Llm__ApiKey (or OPENROUTER_API_KEY) in the environment.
"""
import argparse
import csv
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BENCH = ROOT / "tools" / "llm_bench"
DLL = ROOT / "ha-addon" / "bin" / "Release" / "net10.0" / "SourdoughMonitor.dll"
FRAME_HEIGHT = 720.0


def load_labels():
    with open(BENCH / "labels.csv", newline="") as f:
        return {r["frame"]: r for r in csv.DictReader(f)}


def run_model(model, cache, max_calls, out):
    env = dict(os.environ)
    env.setdefault("Monitor__Llm__ApiKey", env.get("OPENROUTER_API_KEY", ""))
    cmd = ["dotnet", str(DLL), "replay", str(BENCH), "--out", str(out), "--timestamps-utc",
           "--model", model, "--llm-cache", str(cache), "--max-calls", str(max_calls)]
    subprocess.run(cmd, env=env, cwd=ROOT / "ha-addon", check=True, capture_output=True, text=True)


def read_records(out):
    records = {}
    for path in sorted((out / "debug").glob("diagnostics-*.jsonl")):
        for line in path.read_text().splitlines():
            r = json.loads(line)
            key = r["time"][:19].replace("-", "").replace(":", "").replace("T", "_")
            records[key] = r
    return records


def bottom_row(response):
    try:
        text = response[response.index("{"): response.rindex("}") + 1]
        outline = json.loads(text)["jar_outline"]
        return max(p[1] for p in outline) / 1000.0 * FRAME_HEIGHT
    except (ValueError, KeyError, TypeError, json.JSONDecodeError):
        return None


def evaluate(model, labels, records):
    rows, errors, bottoms, cost, ms, bad = [], [], [], 0.0, [], 0
    for frame, label in labels.items():
        r = records.get(frame)
        if r is None:
            rows.append((frame, "gated/none", "", "", ""))
            continue
        cost += r.get("cost_usd") or 0
        if r.get("latency_ms"):
            ms.append(r["latency_ms"])
        expected = label["surface_row"]
        level = r.get("level_row")
        first = (r.get("samples") or [{}])[0]
        bottom = bottom_row(first.get("response") or "")
        if not expected:  # negative frame: a measurement here is a false positive
            ok = r["outcome"] != "detected"
            bad += 0 if ok else 1
            rows.append((frame, r["outcome"], "-", "-", "ok" if ok else "FALSE POSITIVE"))
            continue
        if level is None:
            bad += 1
            rows.append((frame, f'{r["outcome"]}/{r.get("reason")}', "-", "-", "MISS"))
            continue
        err = level - float(expected)
        errors.append(abs(err))
        if bottom is not None and label["jar_bottom_row"]:
            bottoms.append(abs(bottom - float(label["jar_bottom_row"])))
        rows.append((frame, r["outcome"], f"{err:+.0f}", f"{bottom - float(label['jar_bottom_row']):+.0f}" if bottom else "-", ""))
    print(f"\n== {model}")
    for row in rows:
        print("  {:<16} {:<22} surface err {:>5}  bottom err {:>5}  {}".format(*row))
    med = sorted(errors)[len(errors) // 2] if errors else float("nan")
    big = sum(e > 30 for e in errors)
    print(f"  surface |err| median {med:.0f}px, >30px on {big}/{len(errors)}; misses/false-pos {bad}; "
          f"bottom |err| median {sorted(bottoms)[len(bottoms) // 2] if bottoms else float('nan'):.0f}px; "
          f"cost ${cost:.5f} for {len(ms)} calls; median latency {sorted(ms)[len(ms) // 2] if ms else 0} ms")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("models", nargs="+")
    ap.add_argument("--cache", default=str(Path(tempfile.gettempdir()) / "llm-cache"))
    ap.add_argument("--max-calls", type=int, default=20)
    args = ap.parse_args()
    if not DLL.exists():
        sys.exit(f"Build first: dotnet build -c Release ha-addon  (missing {DLL})")
    labels = load_labels()
    for model in args.models:
        out = Path(tempfile.gettempdir()) / "llm-bench-out" / model.replace("/", "_")
        if out.exists():
            import shutil
            shutil.rmtree(out)
        try:
            run_model(model, args.cache, args.max_calls, out)
        except subprocess.CalledProcessError as e:
            print(f"\n== {model}: replay failed\n{e.stderr[-500:]}")
            continue
        evaluate(model, labels, read_records(out))


if __name__ == "__main__":
    main()
