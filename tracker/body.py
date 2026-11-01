"""Optional hand and body tracking (MediaPipe Hand + Pose Landmarkers) and packing of their results for protocol v1."""
from pathlib import Path

from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision

from paths import MODELS


def make_hands():
    return vision.HandLandmarker.create_from_options(vision.HandLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(MODELS / "hand_landmarker.task")),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=2,
    ))


def make_pose():
    return vision.PoseLandmarker.create_from_options(vision.PoseLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(MODELS / "pose_landmarker_lite.task")),
        running_mode=vision.RunningMode.VIDEO,
        num_poses=1,
    ))


def _r(v):
    return round(float(v), 4)


def pack_hands(res):
    """{'left': {...}, 'right': {...}} keyed by the user's own hand.

    MediaPipe assumes a mirrored (selfie) image when it labels handedness. Our frames are not mirrored, so its
    'Left' is the user's right hand and vice versa; swap here.
    'world' is 21 metric landmarks (x right, y down, z away from the camera, origin at the hand centre);
    'image' is the same 21 points in normalised image coordinates.
    """
    out = {}
    for lms, world, handed in zip(res.hand_landmarks, res.hand_world_landmarks, res.handedness):
        label = handed[0].category_name
        side = "right" if label == "Left" else "left"
        score = float(handed[0].score)
        if side in out and out[side]["score"] >= score:
            continue
        out[side] = {
            "score": round(score, 3),
            "world": [[_r(p.x), _r(p.y), _r(p.z)] for p in world],
            "image": [[_r(p.x), _r(p.y)] for p in lms],
        }
    return out


def pack_pose(res):
    """33 pose landmarks: 'world' is [x, y, z, visibility] in metres with the hips as origin (same axes as hands)."""
    if not res.pose_world_landmarks:
        return None
    world = res.pose_world_landmarks[0]
    image = res.pose_landmarks[0]
    return {
        "world": [[_r(p.x), _r(p.y), _r(p.z), round(float(p.visibility or 0.0), 3)] for p in world],
        "image": [[_r(p.x), _r(p.y)] for p in image],
    }
