"""Debug overlay for the tracker preview: landmark regions, live per-region blendshape bars, head axes, mood guess.

The mood is a hand-tuned heuristic over the ARKit blendshapes, not a trained classifier (MediaPipe has no emotion
output). It is meant for sanity-checking tracking, and is a starting point for a learned model later.
"""
import cv2
import numpy as np
from mediapipe.tasks.python import vision

FC = vision.FaceLandmarksConnections

# (name, connections, BGR colour)
REGIONS = [
    ("face", FC.FACE_LANDMARKS_FACE_OVAL, (255, 255, 255)),
    ("brows", FC.FACE_LANDMARKS_LEFT_EYEBROW + FC.FACE_LANDMARKS_RIGHT_EYEBROW, (0, 215, 255)),
    ("eyes", FC.FACE_LANDMARKS_LEFT_EYE + FC.FACE_LANDMARKS_RIGHT_EYE, (255, 200, 0)),
    ("irises", FC.FACE_LANDMARKS_LEFT_IRIS + FC.FACE_LANDMARKS_RIGHT_IRIS, (0, 255, 0)),
    ("mouth", FC.FACE_LANDMARKS_LIPS, (255, 0, 255)),
    ("nose", FC.FACE_LANDMARKS_NOSE, (170, 170, 170)),
]

# Blendshapes grouped by the part of the face they describe.
GROUPS = [
    ("BROWS", ["browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight"]),
    ("EYES", ["eyeBlinkLeft", "eyeBlinkRight", "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight"]),
    ("GAZE", ["eyeLookInLeft", "eyeLookOutLeft", "eyeLookInRight", "eyeLookOutRight",
              "eyeLookUpLeft", "eyeLookUpRight", "eyeLookDownLeft", "eyeLookDownRight"]),
    ("CHEEKS / NOSE", ["cheekPuff", "cheekSquintLeft", "cheekSquintRight", "noseSneerLeft", "noseSneerRight"]),
    ("MOUTH", ["jawOpen", "jawLeft", "jawRight", "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft",
               "mouthFrownRight", "mouthPucker", "mouthFunnel", "mouthPressLeft", "mouthPressRight"]),
]

PANEL_W = 270
FONT = cv2.FONT_HERSHEY_SIMPLEX


def _avg(bs, *names):
    return sum(bs.get(n, 0.0) for n in names) / len(names)


def estimate_mood(bs):
    """Returns (label, scores). Scores are 0..1 and independent; label is the strongest above a threshold."""
    smile = _avg(bs, "mouthSmileLeft", "mouthSmileRight")
    cheek = _avg(bs, "cheekSquintLeft", "cheekSquintRight")
    wide = _avg(bs, "eyeWideLeft", "eyeWideRight")
    brow_up = _avg(bs, "browInnerUp", "browOuterUpLeft", "browOuterUpRight")
    jaw = bs.get("jawOpen", 0.0)
    brow_down = _avg(bs, "browDownLeft", "browDownRight")
    sneer = _avg(bs, "noseSneerLeft", "noseSneerRight")
    press = _avg(bs, "mouthPressLeft", "mouthPressRight")
    frown = _avg(bs, "mouthFrownLeft", "mouthFrownRight")
    inner = bs.get("browInnerUp", 0.0)

    clamp = lambda v: float(min(1.0, max(0.0, v)))
    scores = {
        "happy": clamp(smile * 1.6 + cheek * 0.4),
        "surprised": clamp(wide * 1.0 + brow_up * 0.9 + jaw * 0.6 - brow_down),
        "angry": clamp(brow_down * 1.4 + sneer * 0.7 + press * 0.5 + frown * 0.3 - smile),
        "sad": clamp(inner * 0.9 + frown * 1.0 - smile - brow_down * 0.5),
    }
    label, top = max(scores.items(), key=lambda kv: kv[1])
    return (label if top >= 0.3 else "neutral"), scores


def _short(name):
    return name.replace("Left", "L").replace("Right", "R")


def _bar(img, x, y, w, h, value, colour):
    cv2.rectangle(img, (x, y), (x + w, y + h), (60, 60, 60), 1)
    fill = int(w * max(0.0, min(1.0, value)))
    if fill > 0:
        cv2.rectangle(img, (x, y), (x + fill, y + h), colour, -1)


def _draw_axes(img, matrix, origin, length=60):
    """Draws the head's X (red), Y (green) and Z (blue) axes from the facial transformation matrix."""
    r = np.asarray(matrix)[:3, :3]
    for i, colour in enumerate(((0, 0, 255), (0, 255, 0), (255, 0, 0))):
        end = (int(origin[0] + length * r[0, i]), int(origin[1] - length * r[1, i]))
        cv2.line(img, origin, end, colour, 2, cv2.LINE_AA)


HAND_CONNS = vision.HandLandmarksConnections.HAND_CONNECTIONS
# Body only: skip the face points (0-10) the pose model also reports.
POSE_CONNS = [c for c in vision.PoseLandmarksConnections.POSE_LANDMARKS if c.start >= 11 and c.end >= 11]


def _draw_hands(img, hand_result):
    h, w = img.shape[:2]
    for lms in hand_result.hand_landmarks:
        pts = [(int(p.x * w), int(p.y * h)) for p in lms]
        for c in HAND_CONNS:
            cv2.line(img, pts[c.start], pts[c.end], (0, 255, 0), 2, cv2.LINE_AA)
        for p in pts:
            cv2.circle(img, p, 3, (0, 0, 255), -1, cv2.LINE_AA)


def _draw_pose(img, pose_result):
    h, w = img.shape[:2]
    for lms in pose_result.pose_landmarks:
        for c in POSE_CONNS:
            a, b = lms[c.start], lms[c.end]
            if min(a.visibility or 0, b.visibility or 0) < 0.4:
                continue
            cv2.line(img, (int(a.x * w), int(a.y * h)), (int(b.x * w), int(b.y * h)), (0, 140, 255), 3, cv2.LINE_AA)


def render(frame, result, hand_result=None, pose_result=None, mirror=True):
    """Returns a new image: the (optionally mirrored) camera frame with landmarks, plus a side panel."""
    h, w = frame.shape[:2]
    cam = frame.copy()
    if pose_result is not None:
        _draw_pose(cam, pose_result)
    if hand_result is not None:
        _draw_hands(cam, hand_result)

    bs = None
    if result.face_landmarks and result.face_blendshapes:
        lms = result.face_landmarks[0]
        pts = np.array([(int(l.x * w), int(l.y * h)) for l in lms], dtype=np.int32)
        for _, conns, colour in REGIONS:
            for c in conns:
                cv2.line(cam, tuple(pts[c.start]), tuple(pts[c.end]), colour, 1, cv2.LINE_AA)
        if result.facial_transformation_matrixes:
            _draw_axes(cam, result.facial_transformation_matrixes[0], tuple(pts[1]))  # landmark 1 = nose tip
        bs = {c.category_name: c.score for c in result.face_blendshapes[0]}

    if mirror:
        cam = cv2.flip(cam, 1)  # after drawing, so the overlay is mirrored with the picture; text is added below

    # Legend.
    for i, (name, _, colour) in enumerate(REGIONS):
        y = 14 + i * 14
        cv2.rectangle(cam, (8, y - 8), (18, y + 2), colour, -1)
        cv2.putText(cam, name, (24, y + 1), FONT, 0.4, (255, 255, 255), 1, cv2.LINE_AA)

    panel = np.full((h, PANEL_W, 3), 24, dtype=np.uint8)
    if bs is None:
        cv2.putText(cam, "NO FACE IN VIEW", (w // 2 - 90, h // 2), FONT, 0.7, (0, 0, 255), 2, cv2.LINE_AA)
        return np.hstack([cam, panel])

    # Per-region blendshape bars.
    y = 12
    for title, names in GROUPS:
        cv2.putText(panel, title, (6, y), FONT, 0.38, (0, 215, 255), 1, cv2.LINE_AA)
        y += 4
        for n in names:
            y += 11
            v = bs.get(n, 0.0)
            cv2.putText(panel, _short(n), (6, y), FONT, 0.32, (220, 220, 220), 1, cv2.LINE_AA)
            _bar(panel, 120, y - 8, 100, 8, v, (0, 200, 0) if v < 0.7 else (0, 140, 255))
            cv2.putText(panel, f"{v:.2f}", (226, y), FONT, 0.32, (220, 220, 220), 1, cv2.LINE_AA)
        y += 13

    # Mood guess.
    label, scores = estimate_mood(bs)
    box_h = 20 + 14 * len(scores)
    overlay = cam.copy()
    cv2.rectangle(overlay, (0, h - box_h - 6), (210, h), (0, 0, 0), -1)
    cam = cv2.addWeighted(overlay, 0.55, cam, 0.45, 0)
    cv2.putText(cam, f"mood: {label}", (8, h - box_h + 10), FONT, 0.6, (255, 255, 255), 2, cv2.LINE_AA)
    for i, (name, v) in enumerate(scores.items()):
        yy = h - box_h + 30 + i * 14
        cv2.putText(cam, name, (8, yy), FONT, 0.38, (255, 255, 255), 1, cv2.LINE_AA)
        _bar(cam, 80, yy - 8, 110, 8, v, (255, 200, 0))

    return np.hstack([cam, panel])
