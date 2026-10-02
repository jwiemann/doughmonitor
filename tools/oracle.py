"""Offline, non-causal jar/dough reference model and calibration loop.

Predictions are NOT ground truth. Accuracy is measured only against independently
reviewed references.json; agreement with the live detector is reported separately.
The expensive path combines scene-wide geometry, column-wise contour optimization,
and bidirectional temporal support. It never forces biological growth to be monotonic.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import html
import json
import math
import os
import re
import shutil
import subprocess
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CORPUS = ROOT / "corpus"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png"}
DEFAULT_PARAMETERS = {"threshold_scale": 1.0, "body_fraction": 0.7, "contrast": 8.0,
                      "temporal_minutes": 10.0}


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")
    os.replace(temp, path)


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def time_of(name):
    match = re.search(r"(\d{8})_(\d{6})(\d{3})?", Path(name).name)
    if not match:
        raise ValueError(f"No capture timestamp in {name}")
    time = datetime.strptime(match[1] + match[2], "%Y%m%d%H%M%S")
    return time.replace(microsecond=int(match[3] or 0) * 1000, tzinfo=timezone.utc)


def frames(corpus):
    manifest = load_json(corpus / "manifest.json")
    names = [row["file"] for row in manifest["frames"]]
    missing = [name for name in names if not (corpus / "snapshots" / name).is_file()]
    if missing:
        raise FileNotFoundError(f"Manifest frames missing locally: {missing[:5]}")
    return names


def image(corpus, name):
    bgr = cv2.imread(str(corpus / "snapshots" / name), cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError(f"Cannot decode {name}")
    return bgr, cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)


def spread(bgr):
    # Exactly the live detector's max-minus-min RGB metric, not HSV saturation.
    return bgr.max(axis=2).astype(np.int16) - bgr.min(axis=2).astype(np.int16)


def stats(gray):
    p10, median, p90 = np.percentile(gray, (10, 50, 90))
    return {"w": int(gray.shape[1]), "h": int(gray.shape[0]), "mean": float(gray.mean()),
            "median": float(median), "p10": float(p10), "p90": float(p90)}


def glow_blob(gray):
    mask = (gray > 235).astype(np.uint8)
    count, labels, components, centers = cv2.connectedComponentsWithStats(mask, 8)
    h, w = gray.shape
    best = None
    for x, y, bw, bh, area in components[1:]:
        if bw < w * .03 or bw > w * .7 or y + bh >= h - 12 or area < gray.size * .001:
            continue
        if best is None or area > best[4]:
            best = tuple(int(v) for v in (x, y, bw, bh, area))
    if best is None:
        return None
    x, y, bw, bh, area = best
    return {"x0": x, "x1": x + bw, "y0": y, "y1": y + bh, "area": area}


def regime(gray, glow):
    s = stats(gray)
    if s["p90"] < 25 or s["p90"] - s["p10"] < 30:
        return "unlit"
    if glow:
        return "backlit_day" if s["median"] >= 100 else "backlit_night"
    return "ambient" if s["median"] >= 100 else "dim_ambient"


def longest_run(on, minimum):
    starts = np.flatnonzero(np.diff(np.r_[False, on, False].astype(np.int8)) == 1)
    ends = np.flatnonzero(np.diff(np.r_[False, on, False].astype(np.int8)) == -1)
    if len(starts) == 0:
        return None
    index = int(np.argmax(ends - starts))
    if ends[index] - starts[index] < minimum:
        return None
    return int(starts[index]), int(ends[index] - 1)


def warm_extent(bgr, gray):
    h, w = gray.shape
    sp = spread(bgr)
    neutral = min(2.0, float(np.median(sp[:h // 4].mean(axis=0))))
    bright = float(np.median(gray[:h // 4]))
    warm = (sp > neutral + 6) & (gray < 225)
    mask = warm | (gray < bright - 18)
    band = mask[int(h * .35):int(h * .95)].mean(axis=1) >= .5
    run = longest_run(band, max(4, int(h * .05)))
    if not run:
        return None
    a, b = (int(h * .35) + v for v in run)
    columns = warm[a:b + 1].mean(axis=0) >= .55
    border = max(2, int(w * .06))
    columns[:border] = False
    columns[-border:] = False
    columns = cv2.morphologyEx(columns.astype(np.uint8).reshape(1, -1), cv2.MORPH_CLOSE,
                               np.ones((1, 5), np.uint8)).ravel().astype(bool)
    extent = longest_run(columns, max(12, int(w * .15)))
    if extent:
        return extent
    columns = mask[a:b + 1].mean(axis=0) >= .65
    columns[:border] = False
    columns[-border:] = False
    return longest_run(columns, max(12, int(w * .12)))


def base_row(gray, left, right, below):
    h, w = gray.shape
    inset = max(2, (right - left) // 10)
    prof = np.median(gray[:, left + inset:right - inset], axis=1).astype(np.float32)
    prof = cv2.blur(prof.reshape(-1, 1), (1, 5)).ravel()
    lo = max(below + max(8, h // 12), int(h * .6))
    hi = h - max(3, h // 100)
    if hi - lo < 10:
        return None
    gradient = np.abs(np.diff(prof))
    y = lo + int(np.argmax(gradient[lo:hi]))
    if gradient[y] < 2:
        return None
    return y


def rim_geometry(gray, glow):
    h, w = gray.shape
    start = glow["y1"] + max(3, int((h - glow["y1"]) * .15))
    left, right = max(0, glow["x0"] - int(w * .3)), min(w, glow["x1"] + int(w * .3))
    if h - start < 20:
        return None
    zone = gray[start:h - 2, left:right]
    mask = (zone > max(100, float(np.percentile(zone, 90)))).astype(np.uint8)
    kernel = np.ones((max(1, 2 * (h // 600) + 1), max(3, 2 * (w // 360) + 1)), np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    _, _, components, _ = cv2.connectedComponentsWithStats(mask, 8)
    center = (glow["x0"] + glow["x1"]) / 2
    candidates = []
    for x, y, bw, bh, area in components[1:]:
        x, y, bw, bh, area = map(int, (x + left, y + start, bw, bh, area))
        if bw >= w * .12 and bw > 3 * bh and x < center < x + bw:
            candidates.append((x, y, bw, bh, area))
    if not candidates:
        return None
    x, y, bw, bh, area = max(candidates, key=lambda item: (item[1] + item[3], item[4]))
    lo, hi = max(glow["y1"] + 8, y - max(3, h // 60)), min(h - 1, y + bh + max(3, h // 120))
    prof = np.median(gray[lo:hi, x + bw // 4:x + 3 * bw // 4], axis=1).astype(np.float32)
    prof = cv2.blur(prof.reshape(-1, 1), (1, max(3, 2 * (h // 288) + 1))).ravel()
    drop = prof[:-2] - prof[2:]
    if len(drop) == 0 or drop.max() < 8:
        return None
    bottom = lo + 1 + int(np.argmax(drop))
    padding = int(bw * .22)
    return {"left": max(2, x - padding), "right": min(w - 3, x + bw + padding),
            "top": max(2, h // 30), "bottom": bottom, "kind": "rim", "confidence": .75}


def edge_signature(gray):
    small = cv2.resize(gray, (160, 90), interpolation=cv2.INTER_AREA)
    dx = cv2.Sobel(small, cv2.CV_32F, 1, 0)
    dy = cv2.Sobel(small, cv2.CV_32F, 0, 1)
    magnitude = np.hypot(dx, dy)
    return magnitude / max(1e-6, float(magnitude.mean()))


def read_inventory(corpus):
    rows = list(csv.DictReader((corpus / "inventory.csv").open(encoding="utf-8", newline="")))
    if {r["file"] for r in rows} != set(frames(corpus)):
        raise ValueError("Inventory is stale: run segment after sync.")
    for row in rows:
        row["scene"] = int(row["scene"])
    return rows


def references(corpus, path=None):
    file = Path(path) if path else corpus / "references.json"
    return load_json(file)["frames"] if file.is_file() else []


def cmd_sync(args):
    source, corpus = Path(args.source), args.corpus.resolve()
    destination = corpus / "snapshots"
    destination.mkdir(parents=True, exist_ok=True)
    files = sorted(p for p in source.iterdir() if p.suffix.lower() in IMAGE_EXTENSIONS and p.is_file())
    if not files:
        raise ValueError(f"No archived images at {source}")
    rows, copied = [], 0
    for file in files:
        data = file.read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        target = destination / file.name
        if target.exists():
            if hashlib.sha256(target.read_bytes()).hexdigest() != sha:
                raise ValueError(f"Local/source conflict: {file.name}; neither copy was overwritten.")
        else:
            shutil.copy2(file, target)
            copied += 1
        rows.append({"file": file.name, "bytes": len(data), "sha256": sha})
    debug = source.parent / "debug"
    if debug.is_dir():
        for log in debug.glob("*.jsonl"):
            shutil.copy2(log, corpus / log.name)
    save_json(corpus / "manifest.json", {"source": str(source), "captured_utc": datetime.now(timezone.utc).isoformat(),
              "filename_timezone": "UTC on the gathered HA archive (diagnostics carry +00:00)",
              "frame_count": len(rows), "total_bytes": sum(r["bytes"] for r in rows),
              "first": rows[0]["file"], "last": rows[-1]["file"], "frames": rows})
    print(f"Local corpus: {len(rows)} images, {copied} newly copied; hashes and diagnostics saved.")


def cmd_segment(args):
    corpus = args.corpus
    rows, history = [], []
    scene, votes, previous_time = 0, 0, None
    for name in frames(corpus):
        bgr, gray = image(corpus, name)
        sig = edge_signature(gray)
        now = time_of(name)
        changed = False
        if history:
            typical = np.median(np.stack(history[-12:]), axis=0)
            correlation = np.corrcoef(sig.ravel(), typical.ravel())[0, 1]
            changed = not math.isfinite(correlation) or 1 - correlation > args.tau
        gap = previous_time is not None and (now - previous_time).total_seconds() > 1800
        votes = votes + 1 if changed else 0
        if votes >= 3 or gap:
            scene += 1
            history.clear()
            votes = 0
        history.append(sig)
        history = history[-12:]
        gl = glow_blob(gray)
        rows.append({"file": name, "time": now.isoformat(), "scene": scene, **stats(gray),
                     "regime": regime(gray, gl), "glow": bool(gl), "changed": changed})
        previous_time = now
    with (corpus / "inventory.csv").open("w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    groups = defaultdict(list)
    for row in rows:
        groups[row["scene"]].append(row)
    save_json(corpus / "scenes.json", {str(s): {"n": len(rs), "first": rs[0]["file"], "last": rs[-1]["file"],
              "regimes": dict(Counter(r["regime"] for r in rs))} for s, rs in groups.items()})
    print(f"Inventory: {len(rows)} frames, {len(groups)} structural scenes; lighting is classified independently.")


def cmd_geometry(args):
    corpus = args.corpus
    grouped = defaultdict(list)
    for row in read_inventory(corpus):
        grouped[row["scene"]].append(row)
    reviewed = {r["file"]: r for r in references(corpus, getattr(args, "references", None))
                if r.get("split") == "calibration" and r.get("jar")}
    result = {}
    for scene, rows in grouped.items():
        dominant_size = Counter((int(r["w"]), int(r["h"])) for r in rows).most_common(1)[0][0]
        seeds = [reviewed[r["file"]] for r in rows if r["file"] in reviewed]
        if len(rows) < 6 and not seeds:
            result[str(scene)] = None
            continue
        if seeds:
            bounds = {key: int(round(np.median([s["jar"][key] for s in seeds])))
                      for key in ("left", "right", "top", "bottom")}
            seed_width, seed_height = image(corpus, seeds[0]["file"])[1].shape[::-1]
            result[str(scene)] = {**bounds, "w": seed_width, "h": seed_height,
                                  "kind": "reviewed_scene", "confidence": .95,
                                  "reviewed_sources": [s["file"] for s in seeds]}
            continue
        candidates = []
        indices = np.linspace(0, len(rows) - 1, min(40, len(rows))).astype(int)
        for index in indices:
            if (int(rows[index]["w"]), int(rows[index]["h"])) != dominant_size:
                continue
            bgr, gray = image(corpus, rows[index]["file"])
            gl = glow_blob(gray)
            geometry = rim_geometry(gray, gl) if gl else None
            if geometry is None and rows[index]["regime"] == "ambient":
                extent = warm_extent(bgr, gray)
                if extent:
                    l, r = extent
                    bottom = base_row(gray, l, r, int(gray.shape[0] * .4))
                    if bottom:
                        geometry = {"left": l, "right": r, "top": max(2, gray.shape[0] // 30),
                                    "bottom": bottom, "kind": "ambient_body", "confidence": .6}
            if geometry:
                candidates.append(geometry)
        if not candidates:
            result[str(scene)] = None
            continue
        # The densest geometric cluster wins; illumination outliers cannot widen it.
        centers = np.array([[(g["left"] + g["right"]) / 2, g["right"] - g["left"], g["bottom"]]
                            for g in candidates])
        scale = max(20, centers[:, 1].max() * .12)
        distances = np.max(np.abs(centers[:, None] - centers[None, :]), axis=2)
        index = int(np.argmax((distances <= scale).sum(axis=1)))
        members = [g for g, distance in zip(candidates, distances[index]) if distance <= scale]
        result[str(scene)] = {key: int(round(np.median([m[key] for m in members])))
                              for key in ("left", "right", "top", "bottom")}
        result[str(scene)].update(kind="temporal_cluster", confidence=float(np.mean([m["confidence"] for m in members])),
                                  support_frames=len(members))
        result[str(scene)].update(w=dominant_size[0], h=dominant_size[1])
    save_json(corpus / "geometry.json", result)
    print("Scene-wide jar models:", {s: None if g is None else (g["left"], g["right"], g["bottom"], g["kind"])
                                     for s, g in result.items()})

def frame_geometry(model, gray):
    if model is None:
        return None
    height, width = gray.shape
    x_scale, y_scale = width / model["w"], height / model["h"]
    return {**model, "left": int(round(model["left"] * x_scale)),
            "right": int(round(model["right"] * x_scale)),
            "top": int(round(model["top"] * y_scale)),
            "bottom": min(height - 1, int(round(model["bottom"] * y_scale))),
            "w": width, "h": height}



def front_curve(bgr, gray, geometry, parameters, glow):
    h, w = gray.shape
    left, right, bottom = (geometry[k] for k in ("left", "right", "bottom"))
    if not (0 <= left < right < w and 20 < bottom < h):
        return None
    inset = max(2, (right - left) // 10)
    profile = np.median(gray[:, left + inset:right - inset], axis=1).astype(np.float32)
    sp = spread(bgr)
    warm = (sp > 11) & (gray < 225)
    coverage = warm[:, left + inset:right - inset].mean(axis=1)
    plateau = float(np.percentile(coverage[int(h * .65):max(int(h * .65) + 1, bottom - 20)], 90))
    warm_on = coverage >= .6 * plateau
    warm_anchor = next((y for y in range(int(h * .2), bottom - 30)
                        if plateau >= .5 and warm_on[y:y + 12].sum() >= 10), None)
    glass_start = geometry["top"] + (bottom - geometry["top"]) // 2
    start = glow["y1"] - max(2, h // 50) if glow else min(
        glass_start, warm_anchor - max(12, h // 10) if warm_anchor is not None else glass_start)
    start = max(geometry["top"], start)
    end = bottom - max(8, int((bottom - start) * .12))
    if end - start < 25:
        return None
    threshold, _ = cv2.threshold(np.clip(profile[start:end], 0, 255).astype(np.uint8), 0, 255,
                                  cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    threshold *= parameters["threshold_scale"]
    max_gap, gaps, anchor = max(2, h // 144), 0, None
    for y in range(end - 1, start - 1, -1):
        if profile[y] < threshold:
            gaps = 0
        else:
            gaps += 1
            if gaps > max_gap:
                anchor = y + gaps
                break
    if anchor is None or end - anchor < h * .08:
        return None
    ref = profile[max(start, anchor - max(10, h // 30)):max(start + 1, anchor - max(2, h // 100))]
    body = profile[max(anchor + 1, end - max(8, h // 20)):end]
    contrast = float(ref.mean() - body.mean()) if len(ref) and len(body) else 0.0
    if contrast < parameters["contrast"] or (glow is None and ref.mean() < 80):
        return None
    lo, hi = max(start, anchor - max(8, int(h * .065))), min(end, anchor + max(8, int(h * .065)))
    sm = cv2.blur(gray.astype(np.float32), (1, max(3, 2 * (h // 288) + 1)))
    observed = []
    for x in range(left + 4, right - 3):
        gaps, boundary = 0, None
        for y in range(hi - 1, lo - 1, -1):
            if sm[y, x] < threshold:
                gaps = 0
            else:
                gaps += 1
                if gaps > max_gap:
                    boundary = y + gaps
                    break
        if boundary is not None and lo + max_gap < boundary < hi - max_gap:
            below = sm[boundary:min(end, boundary + max(20, h // 15)), x]
            if float((below < threshold).mean()) >= parameters["body_fraction"]:
                observed.append((x, boundary))
    if len(observed) < max(8, (right - left) // 5):
        return None
    median = float(np.median([y for x, y in observed]))
    observed = [(x, y) for x, y in observed if abs(y - median) <= max(10, h * .03)]
    if len(observed) < max(8, (right - left) // 5):
        return None
    xs = np.arange(observed[0][0], observed[-1][0] + 1, max(1, w // 320))
    ys = np.arange(lo + max_gap, hi - max_gap)
    if len(xs) < 3 or len(ys) < 3:
        return None
    # Offline-only seam optimization. Body observations anchor it below any LED fade;
    # dynamic programming then penalizes discontinuities across the visible outline.
    raw = np.interp(xs, [x for x, y in observed], [y for x, y in observed])
    costs = np.abs(ys[:, None] - raw[None, :]) / max(3, h / 180)
    above = sm[np.maximum(0, ys - 4)[:, None], xs[None, :]]
    below = sm[np.minimum(h - 1, ys + 6)[:, None], xs[None, :]]
    costs -= np.clip((above - below) / max(8.0, contrast), -.25, .25)
    previous = costs[:, 0].copy()
    backtrack = np.empty((len(ys), len(xs)), dtype=np.int16)
    steps = range(-3, 4)
    for column in range(1, len(xs)):
        choices = np.full((7, len(ys)), np.inf, dtype=np.float32)
        for index, step in enumerate(steps):
            dest = np.arange(len(ys))
            origin = dest - step
            valid = (origin >= 0) & (origin < len(ys))
            choices[index, valid] = previous[origin[valid]] + .25 * abs(step)
        choice = np.argmin(choices, axis=0)
        backtrack[:, column] = np.arange(len(ys)) - (choice - 3)
        previous = costs[:, column] + choices[choice, np.arange(len(ys))]
    row = int(np.argmin(previous))
    path = np.empty(len(xs), dtype=np.float32)
    for column in range(len(xs) - 1, -1, -1):
        path[column] = ys[row]
        if column:
            row = int(backtrack[row, column])
    # Fit robustly in normalized coordinates; no artificial convexity or monotonic rise.
    nx = (xs - xs.mean()) / max(1, float(np.ptp(xs)) / 2)
    keep = np.ones(len(xs), dtype=bool)
    for iteration in range(3):
        coefficients = np.polyfit(nx[keep], path[keep], 2)
        fitted = np.polyval(coefficients, nx)
        residual = np.abs(path - fitted)
        keep = residual <= max(3, float(np.median(residual)) * 3)
        if keep.sum() < 3:
            return None
    all_x = np.arange(int(xs[0]), int(xs[-1]) + 1)
    all_nx = (all_x - xs.mean()) / max(1, float(np.ptp(xs)) / 2)
    curve_y = np.rint(np.polyval(coefficients, all_nx)).astype(int)
    center = (all_x >= all_x[0] + (all_x[-1] - all_x[0]) / 3) & (all_x <= all_x[-1] - (all_x[-1] - all_x[0]) / 3)
    level = int(round(float(np.median(curve_y[center]))))
    return {"level": level, "raw_level": level, "curve": [[int(x), int(y)] for x, y in zip(all_x, curve_y)],
            "contrast": contrast, "confidence": round(min(1.0, len(observed) / (right - left)), 3),
            "uncertainty_px": round(max(2.0, float(np.median(np.abs(path - fitted)))), 2)}


def cmd_infer(args):
    corpus = args.corpus
    parameters = {**DEFAULT_PARAMETERS, **(load_json(args.parameters) if getattr(args, "parameters", None) else {})}
    models = load_json(corpus / "geometry.json")
    rows = read_inventory(corpus)
    predictions = []
    for index, row in enumerate(rows):
        bgr, gray = image(corpus, row["file"])
        gl = glow_blob(gray)
        geometry = frame_geometry(models.get(str(row["scene"])), gray)
        prediction = {"file": row["file"], "time": row["time"], "scene": row["scene"],
                      "w": gray.shape[1], "h": gray.shape[0],
                      "regime": row["regime"], "jar": geometry, "glow": gl,
                      "level": None, "curve": None, "confidence": 0.0,
                      "reason": "no_geometry" if geometry is None else "insufficient_boundary_evidence"}
        if geometry and row["regime"] != "unlit":
            contour = front_curve(bgr, gray, geometry, parameters, gl)
            if contour:
                prediction.update(contour, reason="body_contour", method="offline_seam")
        if row["regime"] == "unlit":
            prediction["reason"] = "unlit"
        predictions.append(prediction)
        if (index + 1) % 400 == 0:
            print(f"Offline contours: {index + 1}/{len(rows)}", flush=True)
    # Bidirectional support is deliberately unavailable to the live detector. Do not
    # smooth across camera scenes or lighting regimes, and retain the unsmoothed value.
    raw = [p.copy() for p in predictions]
    support_seconds = parameters["temporal_minutes"] * 60
    for index, prediction in enumerate(predictions):
        if prediction["level"] is None:
            continue
        now = time_of(prediction["file"])
        neighbors = [other for other in raw[max(0, index - 12):index + 13]
                     if other["level"] is not None and other["scene"] == prediction["scene"]
                     and other["regime"] == prediction["regime"]
                     and other["w"] == prediction["w"] and other["h"] == prediction["h"]
                     and abs((time_of(other["file"]) - now).total_seconds()) <= support_seconds]
        level = int(round(float(np.median([p["level"] for p in neighbors]))))
        delta = level - prediction["level"]
        prediction["level"] = level
        prediction["curve"] = [[x, y + delta] for x, y in prediction["curve"]]
        prediction["temporal_samples"] = len(neighbors)
    path = corpus / "predictions.jsonl"
    temp = path.with_suffix(".tmp")
    with temp.open("w", encoding="utf-8") as file:
        for prediction in predictions:
            file.write(json.dumps(prediction, allow_nan=False) + "\n")
    os.replace(temp, path)
    save_json(corpus / "model_parameters.json", parameters)
    print("Offline outcomes:", dict(Counter(p["reason"] for p in predictions)), "(predictions, not labels)")


def prediction_rows(corpus):
    rows = [json.loads(line) for line in (corpus / "predictions.jsonl").read_text(encoding="utf-8").splitlines()]
    names = [row["file"] for row in rows]
    if len(names) != len(set(names)) or set(names) != set(frames(corpus)):
        raise ValueError("Predictions do not match the frozen manifest; re-run infer before evaluating.")
    return rows


def draw_prediction(bgr, prediction):
    drawn = bgr.copy()
    geometry = prediction.get("jar")
    if geometry:
        color = (0, 190, 0) if geometry["confidence"] >= .7 else (0, 165, 255)
        cv2.line(drawn, (geometry["left"], geometry["top"]), (geometry["left"], geometry["bottom"]), color, 2)
        cv2.line(drawn, (geometry["right"], geometry["top"]), (geometry["right"], geometry["bottom"]), color, 2)
        cv2.line(drawn, (geometry["left"], geometry["bottom"]), (geometry["right"], geometry["bottom"]), color, 1)
    curve = prediction.get("curve")
    if curve:
        cv2.polylines(drawn, [np.asarray(curve, dtype=np.int32)], False, (0, 0, 255), 2)
    text = f"{prediction['regime']} level={prediction.get('level')} confidence={prediction.get('confidence', 0):.2f}"
    cv2.rectangle(drawn, (0, 0), (drawn.shape[1] - 1, 36), (24, 24, 24), -1)
    cv2.putText(drawn, text, (8, 26), cv2.FONT_HERSHEY_SIMPLEX, .65, (255, 255, 255), 1)
    return drawn


def cmd_preview(args):
    corpus = args.corpus
    rows = prediction_rows(corpus)
    directory = corpus / "preview"
    directory.mkdir(exist_ok=True)
    by_group = defaultdict(list)
    for row in rows:
        by_group[(row["scene"], row["regime"], row["reason"])].append(row)
    selected = {r["file"] for r in references(corpus)}
    for group in by_group.values():
        for index in np.linspace(0, len(group) - 1, min(args.per, len(group))).astype(int):
            selected.add(group[index]["file"])
    cards = []
    for row in rows:
        if row["file"] not in selected:
            continue
        bgr, gray = image(corpus, row["file"])
        name = row["file"] + ".offline.jpg"
        if not cv2.imwrite(str(directory / name), draw_prediction(bgr, row)):
            raise OSError(f"Cannot write preview {name}")
        cards.append(f'<section><h3>{html.escape(row["file"])} — {html.escape(row["regime"])}</h3>'
                     f'<p>{html.escape(row["reason"])}; predicted row {row["level"]}</p>'
                     f'<a href="../snapshots/{row["file"]}">Raw full frame</a>'
                     f'<img src="{name}" loading="lazy" alt="Full frame with inferred jar and surface"></section>')
    (directory / "index.html").write_text('<!doctype html><meta charset="utf-8"><title>Dough detection review</title>'
        '<style>body{font:16px system-ui;background:#181818;color:#ddd;margin:24px}'
        'img{display:block;width:100%;height:auto;aspect-ratio:16/9;max-width:1280px;background:#080808}'
        'section{margin-bottom:32px}a{color:#8bf}</style><h1>Full-frame detection review</h1>'
        '<p>Red: inferred surface. Green: supported jar geometry. Orange: uncertain geometry. '
        'These are predictions, not ground truth; compare raw and annotated full frames.</p>' + ''.join(cards), encoding="utf-8")
    print(f"Review: {directory / 'index.html'} ({len(cards)} full-frame previews)")


def evaluate(rows_by_file, refs):
    groups = defaultdict(list)
    for ref in refs:
        row = rows_by_file.get(ref["file"])
        if row is None:
            continue
        predicted = row.get("level")
        detectable = ref.get("detectable", True)
        entry = {"file": ref["file"], "detected": predicted is not None, "source": ref["source"],
                 "split": ref["split"], "miss": detectable and predicted is None,
                 "false_positive": not detectable and predicted is not None}
        if detectable and predicted is not None:
            low, high = ref["surface_interval"]
            entry["error_outside_interval"] = max(0, low - predicted, predicted - high)
            entry["signed_midpoint_error"] = predicted - (low + high) / 2
        geometry = row.get("jar")
        if row.get("geometry_available", True) is False:
            entry["wrong_jar"] = None
        elif geometry and ref.get("jar"):
            tolerance = ref.get("jar_tolerance_px", 20)
            entry["wrong_jar"] = any(abs(geometry[k] - ref["jar"][k]) > tolerance
                                     for k in ("left", "right", "bottom"))
        elif detectable and predicted is not None:
            entry["wrong_jar"] = True
        groups[(ref["split"], ref["regime"])].append(entry)
    result = []
    for (split, light), entries in sorted(groups.items()):
        distances = [e["error_outside_interval"] for e in entries if "error_outside_interval" in e]
        errors = [abs(e["signed_midpoint_error"]) for e in entries if "signed_midpoint_error" in e]
        result.append({"split": split, "regime": light, "n": len(entries),
                       "misses": sum(e["miss"] for e in entries),
                       "false_positives": sum(e["false_positive"] for e in entries),
                       "wrong_jars": sum(e.get("wrong_jar") is True for e in entries),
                       "geometry_evaluated": sum(e.get("wrong_jar") is not None for e in entries),
                       "mean_error_outside_review_interval": float(np.mean(distances)) if distances else None,
                       "mean_midpoint_error": float(np.mean(errors)) if errors else None,
                       "frames": entries})
    return result


def replay_rows(path):
    result = {}
    with Path(path).open(encoding="utf-8", newline="") as file:
        reader = csv.DictReader(file)
        columns = set(reader.fieldnames or [])
        has_geometry = {"jar_left_px", "jar_right_px", "jar_column_kind"} <= columns
        for row in reader:
            name = row["file"].replace("\\", "/").rsplit("/", 1)[-1]
            level = float(row["dough_top_px"]) if row["dough_top_px"] and row["outcome"] == "detected" else None
            geometry = None
            if has_geometry and row["jar_left_px"] and row["jar_right_px"] and row["jar_bottom_px"]:
                geometry = {"left": int(row["jar_left_px"]), "right": int(row["jar_right_px"]),
                            "bottom": int(float(row["jar_bottom_px"]))}
            result[name] = {"level": level, "jar": geometry, "geometry_available": has_geometry,
                            "method": row["method"], "row": row}
    return result, has_geometry


def cmd_compare(args):
    corpus = args.corpus
    refs = references(corpus, getattr(args, "references", None))
    if not refs:
        raise ValueError("Accuracy requires independently reviewed references.json; predictions cannot substitute.")
    predicted = {row["file"]: row for row in prediction_rows(corpus)}
    live, has_geometry = replay_rows(args.replay)
    agreement = defaultdict(list)
    coverage = defaultdict(lambda: {"frames": 0, "detected": 0})
    for name, row in live.items():
        teacher = predicted.get(name)
        if teacher:
            group = coverage[teacher["regime"]]
            group["frames"] += 1
            group["detected"] += int(row["level"] is not None)
        if teacher and teacher["level"] is not None and row["level"] is not None:
            agreement[teacher["regime"]].append(row["level"] - teacher["level"])
    report = {"reference_note": "Accuracy below uses reviewed intervals, with source and split retained. "
              "Assistant visual intervals are approximate; only user-confirmed frames are user ground truth.",
              "offline_accuracy": evaluate(predicted, refs), "live_accuracy": evaluate(live, refs),
              "reference_count": len(refs),
              "unmatched_live_references": [r["file"] for r in refs if r["file"] not in live],
              "unmatched_offline_references": [r["file"] for r in refs if r["file"] not in predicted],
              "manifest_sha256": hashlib.sha256((corpus / "manifest.json").read_bytes()).hexdigest(),
              "replay_has_geometry": has_geometry,
              "raw_detection_coverage_not_accuracy": {
                  reg: {**counts, "coverage_percent": 100.0 * counts["detected"] / counts["frames"]}
                  for reg, counts in sorted(coverage.items())},
              "agreement_not_accuracy": {reg: {"n": len(err), "mean_absolute_disagreement_px": float(np.mean(np.abs(err))),
                   "signed_bias_px": float(np.mean(err)), "p90_disagreement_px": float(np.percentile(np.abs(err), 90))}
                   for reg, err in sorted(agreement.items())}}
    out = args.out or corpus / "evaluation.json"
    save_json(out, report)
    for model in ("offline_accuracy", "live_accuracy"):
        print(model)
        for group in report[model]:
            print({k: v for k, v in group.items() if k != "frames"})
    print("Agreement (NOT accuracy):", report["agreement_not_accuracy"])
    print(f"Evaluation written to {out}; geometry {'available' if has_geometry else 'NOT available in this legacy replay'}.")


def cmd_tune(args):
    corpus = args.corpus
    refs = references(corpus, args.references)
    train = [r for r in refs if r["split"] == "calibration" and r.get("detectable", True)]
    holdout = [r for r in refs if r["split"] == "validation" and r.get("detectable", True)]
    if not train or not holdout:
        raise ValueError("Tuning needs separate calibration and validation references; no random adjacent-frame split.")
    geometry = load_json(corpus / "geometry.json")
    inventory = {r["file"]: r for r in read_inventory(corpus)}
    cached = {r["file"]: image(corpus, r["file"]) for r in train + holdout}
    trials = []
    for factor in (.95, 1.0, 1.05):
        for body_fraction in (.6, .7, .8):
            parameters = {**DEFAULT_PARAMETERS, "threshold_scale": factor, "body_fraction": body_fraction}
            rows = {}
            for ref in train + holdout:
                name = ref["file"]
                bgr, gray = cached[name]
                geom = geometry.get(str(inventory[name]["scene"]))
                contour = front_curve(bgr, gray, geom, parameters, glow_blob(gray)) if geom else None
                rows[name] = {"level": contour["level"] if contour else None, "jar": geom}
            error = 0.0
            for ref in train:
                level = rows[ref["file"]]["level"]
                error += 100 if level is None else max(0, ref["surface_interval"][0] - level,
                                                      level - ref["surface_interval"][1])
            # A tie keeps the conservative defaults, not extra tuned complexity.
            tie = abs(factor - 1.0) + abs(body_fraction - .7)
            trials.append((error, tie, parameters, evaluate(rows, holdout)))
    best = min(trials, key=lambda t: (t[0], t[1]))
    save_json(corpus / "tuned_parameters.json", best[2])
    save_json(corpus / "tuning.json", {"selected": best[2], "calibration_loss": best[0],
              "holdout_accuracy": best[3], "trial_count": len(trials)})
    print(f"Tuned {len(trials)} offline candidates using calibration references only; validation retained separately.")
    print("Selected:", best[2], "; loss:", best[0])
    print("This selects the offline model only; live C# changes still require replay verification.")


def cmd_run(args):
    cmd_segment(args)
    cmd_geometry(args)
    cmd_infer(args)
    cmd_preview(args)
    output = args.out.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError(f"Replay output must be new/empty to avoid stale evidence: {output}")
    executable = shutil.which("dotnet")
    if not executable:
        raise FileNotFoundError("dotnet is not installed or not on PATH")
    command = [executable, "run", "--project", str(ROOT / "ha-addon"), "--no-build", "--", "replay",
               str((args.corpus / "snapshots").resolve()), "--out", str(output), "--timestamps-utc"]
    if not args.every_frame:
        command += ["--interval-minutes", str(args.interval_minutes)]
    subprocess.run(command, cwd=ROOT, check=True)
    args.replay = output / "readings.csv"
    args.out = output / "evaluation.json"
    cmd_compare(args)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    handlers = {"sync": cmd_sync, "segment": cmd_segment, "geometry": cmd_geometry, "infer": cmd_infer,
                "preview": cmd_preview, "compare": cmd_compare, "tune": cmd_tune, "run": cmd_run}
    for name, handler in handlers.items():
        p = sub.add_parser(name)
        p.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
        p.set_defaults(handler=handler)
        if name == "sync":
            p.add_argument("--source", type=Path, default=Path("//192.168.1.25/share/sourdough_monitor/snapshots"))
        if name in ("segment", "run"):
            p.add_argument("--tau", type=float, default=.45)
        if name in ("geometry", "compare", "tune", "run"):
            p.add_argument("--references", type=Path)
        if name in ("infer", "run"):
            p.add_argument("--parameters", type=Path)
        if name in ("preview", "run"):
            p.add_argument("--per", type=int, default=4)
        if name in ("compare", "run"):
            p.add_argument("--out", type=Path)
        if name == "compare":
            p.add_argument("--replay", type=Path, required=True)
        if name == "run":
            p.add_argument("--interval-minutes", type=float, default=10)
            p.add_argument("--every-frame", action="store_true")
    args = parser.parse_args()
    args.corpus = args.corpus.resolve()
    if args.command == "run" and args.out is None:
        parser.error("run requires --out pointing to a new replay directory")
    try:
        args.handler(args)
    except (ValueError, FileNotFoundError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Calibration error: {error}\n")


if __name__ == "__main__":
    main()
