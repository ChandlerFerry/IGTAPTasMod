#!/usr/bin/env python3
"""Export a Unity serialized scene to readable JSON.

Designed for the IGTAP Unity 6 player data.  The export includes the complete
GameObject hierarchy, transforms, tile cells, and world-space 2D collider
geometry so route planning does not depend on what the live camera has loaded.
"""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter
from pathlib import Path
from typing import Any

import UnityPy


COMPOSITE_FIXED_SCALE = 100_000_000.0


def pointer_id(value: Any) -> int:
    if not isinstance(value, dict):
        return 0
    return int(value.get("m_PathID", value.get("path_id", 0)) or 0)


def vec(value: dict[str, Any], dimensions: int = 3) -> list[float]:
    keys = ("x", "y", "z", "w")[:dimensions]
    return [float(value.get(key, 0.0)) for key in keys]


def mat_identity() -> list[list[float]]:
    return [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]]


def mat_mul(a: list[list[float]], b: list[list[float]]) -> list[list[float]]:
    return [[sum(a[row][k] * b[k][col] for k in range(4)) for col in range(4)]
            for row in range(4)]


def local_matrix(transform: dict[str, Any]) -> list[list[float]]:
    position = vec(transform.get("m_LocalPosition", {}))
    scale = vec(transform.get("m_LocalScale", {"x": 1, "y": 1, "z": 1}))
    x, y, z, w = vec(transform.get("m_LocalRotation", {"w": 1}), 4)
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    rotation = [
        [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)],
        [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)],
        [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)],
    ]
    result = mat_identity()
    for row in range(3):
        for col in range(3):
            result[row][col] = rotation[row][col] * scale[col]
        result[row][3] = position[row]
    return result


def transform_point(matrix: list[list[float]], point: list[float]) -> list[float]:
    source = point + [0.0] * (3 - len(point)) + [1.0]
    return [round(sum(matrix[row][col] * source[col] for col in range(4)), 6)
            for row in range(3)]


def bounds(paths: list[list[list[float]]]) -> dict[str, list[float]] | None:
    points = [point for path in paths for point in path]
    if not points:
        return None
    dimensions = len(points[0])
    return {
        "min": [min(point[i] for point in points) for i in range(dimensions)],
        "max": [max(point[i] for point in points) for i in range(dimensions)],
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("scene", type=Path, help="Unity serialized scene, e.g. level2")
    parser.add_argument("output", type=Path, help="Output JSON path")
    args = parser.parse_args()

    environment = UnityPy.load(str(args.scene))
    objects = {obj.path_id: obj for obj in environment.objects}
    trees: dict[int, dict[str, Any]] = {}
    failures: list[dict[str, Any]] = []
    for obj in environment.objects:
        try:
            trees[obj.path_id] = obj.read_typetree()
        except Exception as exc:  # Some script types lack an available type tree.
            failures.append({"pathId": obj.path_id, "type": obj.type.name, "error": str(exc)})

    game_objects: dict[int, dict[str, Any]] = {
        path_id: tree for path_id, tree in trees.items()
        if objects[path_id].type.name == "GameObject"
    }
    transforms: dict[int, dict[str, Any]] = {
        path_id: tree for path_id, tree in trees.items()
        if objects[path_id].type.name in ("Transform", "RectTransform")
    }
    transform_for_go = {
        pointer_id(tree.get("m_GameObject")): path_id for path_id, tree in transforms.items()
    }

    world_cache: dict[int, list[list[float]]] = {}

    def world_matrix(transform_id: int, visiting: set[int] | None = None) -> list[list[float]]:
        if transform_id in world_cache:
            return world_cache[transform_id]
        if transform_id not in transforms:
            return mat_identity()
        visiting = set() if visiting is None else visiting
        if transform_id in visiting:
            return mat_identity()
        visiting.add(transform_id)
        tree = transforms[transform_id]
        parent_id = pointer_id(tree.get("m_Father"))
        result = mat_mul(world_matrix(parent_id, visiting), local_matrix(tree))
        world_cache[transform_id] = result
        return result

    path_cache: dict[int, str] = {}
    active_cache: dict[int, bool] = {}

    def hierarchy_path(go_id: int, visiting: set[int] | None = None) -> str:
        if go_id in path_cache:
            return path_cache[go_id]
        go = game_objects.get(go_id, {})
        name = str(go.get("m_Name", f"GameObject#{go_id}"))
        transform = transforms.get(transform_for_go.get(go_id, 0), {})
        parent_transform_id = pointer_id(transform.get("m_Father"))
        parent_go_id = pointer_id(transforms.get(parent_transform_id, {}).get("m_GameObject"))
        visiting = set() if visiting is None else visiting
        if not parent_go_id or go_id in visiting:
            result = name
        else:
            visiting.add(go_id)
            result = hierarchy_path(parent_go_id, visiting) + "/" + name
        path_cache[go_id] = result
        return result

    def active_in_hierarchy(go_id: int, visiting: set[int] | None = None) -> bool:
        if go_id in active_cache:
            return active_cache[go_id]
        go = game_objects.get(go_id, {})
        if not bool(go.get("m_IsActive", True)):
            active_cache[go_id] = False
            return False
        transform = transforms.get(transform_for_go.get(go_id, 0), {})
        parent_transform_id = pointer_id(transform.get("m_Father"))
        parent_go_id = pointer_id(transforms.get(parent_transform_id, {}).get("m_GameObject"))
        visiting = set() if visiting is None else visiting
        if not parent_go_id or go_id in visiting:
            result = True
        else:
            visiting.add(go_id)
            result = active_in_hierarchy(parent_go_id, visiting)
        active_cache[go_id] = result
        return result

    hierarchy = []
    for go_id, go in game_objects.items():
        transform_id = transform_for_go.get(go_id, 0)
        transform = transforms.get(transform_id, {})
        matrix = world_matrix(transform_id)
        component_ids = [pointer_id(item.get("component", item)) for item in go.get("m_Component", [])]
        hierarchy.append({
            "pathId": go_id,
            "name": go.get("m_Name", ""),
            "path": hierarchy_path(go_id),
            "active": bool(go.get("m_IsActive", True)),
            "activeInHierarchy": active_in_hierarchy(go_id),
            "layer": int(go.get("m_Layer", 0)),
            "tagIndex": int(go.get("m_Tag", 0)),
            "transformPathId": transform_id,
            "parentTransformPathId": pointer_id(transform.get("m_Father")),
            "localPosition": vec(transform.get("m_LocalPosition", {})),
            "localRotation": vec(transform.get("m_LocalRotation", {"w": 1}), 4),
            "localScale": vec(transform.get("m_LocalScale", {"x": 1, "y": 1, "z": 1})),
            "worldPosition": transform_point(matrix, [0, 0, 0]),
            "components": [{"pathId": cid, "type": objects[cid].type.name if cid in objects else "External"}
                           for cid in component_ids],
        })

    collider_types = {"BoxCollider2D", "PolygonCollider2D", "CompositeCollider2D", "TilemapCollider2D"}
    colliders = []
    for path_id, tree in trees.items():
        type_name = objects[path_id].type.name
        if type_name not in collider_types:
            continue
        go_id = pointer_id(tree.get("m_GameObject"))
        transform_id = transform_for_go.get(go_id, 0)
        matrix = world_matrix(transform_id)
        offset = vec(tree.get("m_Offset", {}), 2)
        local_paths: list[list[list[float]]] = []
        source = "none"
        if type_name == "BoxCollider2D":
            size = vec(tree.get("m_Size", {}), 2)
            half_x, half_y = size[0] / 2, size[1] / 2
            local_paths = [[[offset[0] - half_x, offset[1] - half_y],
                            [offset[0] + half_x, offset[1] - half_y],
                            [offset[0] + half_x, offset[1] + half_y],
                            [offset[0] - half_x, offset[1] + half_y]]]
            source = "box"
        elif type_name == "PolygonCollider2D":
            raw_paths = tree.get("m_Points", {}).get("m_Paths", [])
            local_paths = [[[float(point["x"]) + offset[0], float(point["y"]) + offset[1]]
                            for point in path] for path in raw_paths]
            source = "polygon"
        elif type_name == "CompositeCollider2D":
            raw_paths = tree.get("m_CompositePaths", [])
            if isinstance(raw_paths, dict):
                raw_paths = raw_paths.get("m_Paths", [])
                local_paths = [[[(float(point["x"]) + offset[0]),
                                 (float(point["y"]) + offset[1])]
                                for point in path] for path in raw_paths]
                source = "composite"
            elif not raw_paths:
                raw_paths = [path for group in tree.get("m_ColliderPaths", [])
                             for path in group.get("m_ColliderPaths", [])]
                local_paths = [[[(float(point["X"]) / COMPOSITE_FIXED_SCALE) + offset[0],
                                 (float(point["Y"]) / COMPOSITE_FIXED_SCALE) + offset[1]]
                                for point in path] for path in raw_paths]
                source = "composite-fixed-1e8"
        world_paths = [[transform_point(matrix, point)[:2] for point in path] for path in local_paths]
        colliders.append({
            "pathId": path_id,
            "type": type_name,
            "gameObjectPathId": go_id,
            "name": game_objects.get(go_id, {}).get("m_Name", ""),
            "path": hierarchy_path(go_id),
            "layer": int(game_objects.get(go_id, {}).get("m_Layer", 0)),
            "enabled": bool(tree.get("m_Enabled", True)),
            "activeInHierarchy": active_in_hierarchy(go_id),
            "isTrigger": bool(tree.get("m_IsTrigger", False)),
            "compositeOperation": int(tree.get("m_CompositeOperation", 0)),
            "geometrySource": source,
            "localPaths": local_paths,
            "worldPaths": world_paths,
            "worldBounds": bounds(world_paths),
        })

    tilemaps = []
    for path_id, tree in trees.items():
        if objects[path_id].type.name != "Tilemap":
            continue
        go_id = pointer_id(tree.get("m_GameObject"))
        cells = []
        for position, tile in tree.get("m_Tiles", []):
            cells.append({"position": [int(position.get(k, 0)) for k in ("x", "y", "z")],
                          "tileIndex": int(tile.get("m_TileIndex", 0)),
                          "spriteIndex": int(tile.get("m_TileSpriteIndex", 0))})
        tilemaps.append({
            "pathId": path_id,
            "gameObjectPathId": go_id,
            "name": game_objects.get(go_id, {}).get("m_Name", ""),
            "path": hierarchy_path(go_id),
            "enabled": bool(tree.get("m_Enabled", True)),
            "origin": vec(tree.get("m_Origin", {})),
            "size": vec(tree.get("m_Size", {})),
            "tileAnchor": vec(tree.get("m_TileAnchor", {})),
            "cellCount": len(cells),
            "cells": cells,
        })

    type_counts = Counter(obj.type.name for obj in environment.objects)
    result = {
        "source": str(args.scene.resolve()),
        "unityVersion": environment.assets[0].unity_version,
        "objectCount": len(environment.objects),
        "typeCounts": dict(sorted(type_counts.items())),
        "hierarchy": sorted(hierarchy, key=lambda item: item["path"]),
        "colliders": sorted(colliders, key=lambda item: (item["path"], item["type"])),
        "tilemaps": sorted(tilemaps, key=lambda item: item["path"]),
        "typeTreeFailures": failures,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"Exported {len(hierarchy)} GameObjects, {len(colliders)} colliders, "
          f"and {sum(item['cellCount'] for item in tilemaps)} tile cells to {args.output}")


if __name__ == "__main__":
    main()
