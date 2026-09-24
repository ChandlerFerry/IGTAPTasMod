#!/usr/bin/env python3
"""Create a compact course map from export-unity-scene.py output."""

from __future__ import annotations

import argparse
import html
import json
from collections import Counter
from pathlib import Path
from typing import Any


def overlaps(a: dict[str, list[float]], b: dict[str, list[float]]) -> bool:
    return not (a["max"][0] < b["min"][0] or a["min"][0] > b["max"][0]
                or a["max"][1] < b["min"][1] or a["min"][1] > b["max"][1])


def path_bounds(points: list[list[float]]) -> dict[str, list[float]]:
    return {"min": [min(p[0] for p in points), min(p[1] for p in points)],
            "max": [max(p[0] for p in points), max(p[1] for p in points)]}


def classify(path: str, layer: int, trigger: bool) -> str:
    lower = path.lower()
    if "ballkiller" in lower:
        return "killer"
    if "spike" in lower or layer == 6:
        return "spike"
    if lower.endswith("/realend"):
        return "finish"
    if lower.endswith("/start"):
        return "start"
    if "checkpoint" in lower:
        return "checkpoint"
    if trigger:
        return "trigger"
    return "solid"


def write_svg(path: Path, viewport: dict[str, list[float]], features: list[dict[str, Any]]) -> None:
    min_x, min_y = viewport["min"][:2]
    max_x, max_y = viewport["max"][:2]
    width, height = max_x - min_x, max_y - min_y
    colors = {"solid": "#708090", "spike": "#dc3545", "killer": "#7b1fa2",
              "checkpoint": "#00a67d", "finish": "#ffd166", "start": "#4dabf7",
              "trigger": "#8795a1"}
    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{min_x} {-max_y} {width} {height}">',
        '<rect width="100%" height="100%" fill="#10141c"/>',
        '<g transform="scale(1,-1)" stroke-linejoin="round">',
    ]
    for feature in features:
        if feature["outsideViewport"]:
            continue
        points = " ".join(f"{point[0]:.3f},{point[1]:.3f}" for point in feature["points"])
        color = colors.get(feature["kind"], "#adb5bd")
        opacity = "0.82" if feature["kind"] not in ("trigger", "checkpoint") else "0.35"
        label = html.escape(feature["path"], quote=True)
        lines.append(f'<polygon points="{points}" fill="{color}" fill-opacity="{opacity}" '
                     f'stroke="{color}" stroke-width="3"><title>{label}</title></polygon>')
    lines.extend(["</g>", "</svg>"])
    path.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("scene_json", type=Path)
    parser.add_argument("course", help='Course root name, e.g. "course 2"')
    parser.add_argument("output_json", type=Path)
    parser.add_argument("--svg", type=Path)
    args = parser.parse_args()

    scene = json.loads(args.scene_json.read_text(encoding="utf-8"))
    prefix = args.course.casefold() + "/"
    viewport_collider = next(c for c in scene["colliders"]
                             if c["path"].casefold() == prefix + "clones/activeclonearea")
    viewport = viewport_collider["worldBounds"]
    features = []
    for collider in scene["colliders"]:
        lower = collider["path"].casefold()
        belongs_to_course = lower.startswith(prefix)
        is_global_geometry = lower.startswith("grid/")
        if not (belongs_to_course or is_global_geometry):
            continue
        if not collider["enabled"] or not collider["activeInHierarchy"]:
            continue
        for index, points in enumerate(collider["worldPaths"]):
            if len(points) < 3:
                continue
            feature_bounds = path_bounds(points)
            outside = not overlaps(feature_bounds, viewport)
            if is_global_geometry and outside:
                continue
            features.append({
                "kind": classify(collider["path"], collider["layer"], collider["isTrigger"]),
                "path": collider["path"],
                "colliderType": collider["type"],
                "colliderPathId": collider["pathId"],
                "pathIndex": index,
                "layer": collider["layer"],
                "isTrigger": collider["isTrigger"],
                "outsideViewport": outside,
                "bounds": feature_bounds,
                "points": points,
            })

    result = {
        "source": str(args.scene_json.resolve()),
        "unityVersion": scene["unityVersion"],
        "course": args.course,
        "viewport": viewport,
        "summary": dict(sorted(Counter(item["kind"] for item in features).items())),
        "features": features,
    }
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(result, indent=2), encoding="utf-8")
    if args.svg:
        args.svg.parent.mkdir(parents=True, exist_ok=True)
        write_svg(args.svg, viewport, features)
    print(f"Exported {len(features)} course features: {result['summary']}")


if __name__ == "__main__":
    main()
