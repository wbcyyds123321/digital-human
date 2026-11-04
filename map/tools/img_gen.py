"""GPT-Image client for the packyapi relay (OpenAI-compatible /v1/images/generations).

Parallel, idempotent (skips existing files), retries transient errors, records a manifest.

Usage:
    python img_gen.py jobs.json --out-dir ref --workers 3 [--force] [--model m1,m2]
    python img_gen.py --list-models

jobs.json: [{"name": "...", "prompt": "...", "size": "1536x1024", "quality": "high", "n": 1}, ...]

Env:
    PACKY_KEY     required
    PACKY_BASE    default https://www.packyapi.ai/v1
    PACKY_MODELS  comma list, default gpt-image-2.5-flare,gpt-image-2.5-sunburst,gpt-image-2
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request

BASE = os.environ.get("PACKY_BASE", "https://www.packyapi.ai/v1").rstrip("/")
KEY = os.environ.get("PACKY_KEY", "").strip()
DEFAULT_MODELS = os.environ.get(
    "PACKY_MODELS", "gpt-image-2.5-flare,gpt-image-2.5-sunburst,gpt-image-2").split(",")
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/128.0 Safari/537.36")

_print_lock = threading.Lock()


def log(*a):
    with _print_lock:
        print(*a, flush=True)


def api(path: str, payload=None, timeout=600, method=None):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        BASE + path, data=data, method=method or ("POST" if data else "GET"),
        headers={"Authorization": "Bearer " + KEY, "Content-Type": "application/json",
                 "Accept": "application/json", "User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8", errors="replace"))


def list_models():
    body = api("/models", timeout=60)
    ids = sorted({m.get("id") for m in body.get("data", []) if m.get("id")})
    print("TOTAL", len(ids))
    for i in ids:
        print(i)


def generate_one(model, prompt, size, quality, n=1):
    payload = {"model": model, "prompt": prompt, "size": size, "quality": quality, "n": n}
    for attempt in range(3):
        try:
            return api("/images/generations", payload, timeout=900), None
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", errors="replace")[:1500]
            if e.code in (429, 500, 502, 503, 504, 524) and attempt < 2:
                time.sleep(8 * (attempt + 1))
                continue
            return None, {"code": e.code, "raw": raw}
        except Exception as e:  # noqa: BLE001
            if attempt < 2:
                time.sleep(5 * (attempt + 1))
                continue
            return None, {"code": -1, "raw": repr(e)}
    return None, {"code": -1, "raw": "retries exhausted"}


def save_image(img, path):
    if img.get("b64_json"):
        raw = base64.b64decode(img["b64_json"])
    elif img.get("url"):
        req = urllib.request.Request(img["url"], headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=300) as r:
            raw = r.read()
    else:
        raise RuntimeError("no image payload: " + json.dumps(img)[:300])
    tmp = path + ".part"
    with open(tmp, "wb") as fh:
        fh.write(raw)
    os.replace(tmp, path)
    return raw


def run_job(job, out_dir, models, force):
    name = job["name"]
    ext = job.get("ext", "png")
    path = os.path.join(out_dir, name + "." + ext)
    if os.path.exists(path) and not force:
        return {"name": name, "path": path, "skipped": True}
    prompt = job["prompt"]
    size = job.get("size", "1536x1024")
    quality = job.get("quality", "high")
    n = job.get("n", 1)
    if n > 1:
        # one file per returned image
        prefix = name
        name = name  # kept for manifest readability
    last_err = None
    for model in models:
        t0 = time.time()
        body, err = generate_one(model, prompt, size, quality, n)
        if body:
            entries = []
            for i, img in enumerate(body.get("data") or []):
                p = path if len(body.get("data") or []) == 1 else os.path.join(
                    out_dir, f"{prefix}_{i + 1}.{ext}")
                try:
                    raw = save_image(img, p)
                except Exception as e:  # noqa: BLE001
                    last_err = {"save": repr(e)}
                    entries = []
                    break
                entries.append({"name": name if i == 0 else f"{prefix}_{i + 1}",
                                "path": p, "bytes": len(raw), "model": model,
                                "seconds": round(time.time() - t0, 1),
                                "size": size, "quality": quality,
                                "usage": body.get("usage"), "prompt": prompt,
                                "prompt_sha1": __import__("hashlib").sha1(prompt.encode()).hexdigest()[:12]})
            if entries:
                for e in entries:
                    log("OK", e["name"], model, "->", e["path"], e["bytes"], "bytes",
                        e["seconds"], "s", e.get("usage"))
                return entries
        last_err = err
        log("FAIL", name, model, json.dumps(err, ensure_ascii=False)[:400])
        if (err or {}).get("code") in (401, 403):
            break
    return [{"name": name, "error": True, "detail": last_err}]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jobs", nargs="?")
    ap.add_argument("--out-dir", default="ref")
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--model", default=None, help="comma separated model fallback order")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--list-models", action="store_true")
    ap.add_argument("--only", default=None, help="comma separated job names")
    args = ap.parse_args()

    if not KEY:
        print("PACKY_KEY is not set", file=sys.stderr)
        return 2
    if args.list_models:
        list_models()
        return 0
    if not args.jobs:
        print("jobs.json required", file=sys.stderr)
        return 2

    models = [m.strip() for m in (args.model.split(",") if args.model else DEFAULT_MODELS) if m.strip()]
    jobs = json.load(open(args.jobs, encoding="utf-8"))
    if args.only:
        wanted = {s.strip() for s in args.only.split(",")}
        jobs = [j for j in jobs if j["name"] in wanted]
    os.makedirs(args.out_dir, exist_ok=True)
    manifest_path = os.path.join(args.out_dir, "_manifest.json")
    old = []
    if os.path.exists(manifest_path):
        try:
            old = json.load(open(manifest_path, encoding="utf-8"))
        except Exception:  # noqa: BLE001
            old = []

    results = []
    from concurrent.futures import ThreadPoolExecutor
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
        futs = {ex.submit(run_job, j, args.out_dir, models, args.force): j["name"] for j in jobs}
        for fut in futs:
            try:
                results.extend(fut.result())
            except Exception as e:  # noqa: BLE001
                results.append({"name": futs[fut], "error": True, "detail": repr(e)})

    by_name = {r["name"]: r for r in old if not r.get("skipped") or True}
    for r in results:
        by_name[r["name"]] = r
    merged = list(by_name.values())
    json.dump(merged, open(manifest_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    ok = sum(1 for r in results if not r.get("error"))
    print(f"DONE ok={ok} fail={len(results) - ok} manifest={manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
