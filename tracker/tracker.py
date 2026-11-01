"""Mystic face tracker: webcam -> MediaPipe Face Landmarker -> UDP (protocol v1)."""
import argparse
import json
import socket
import time
from pathlib import Path

import cv2
import mediapipe as mp
import numpy as np
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision

import annotate
from paths import MODELS
import body

MODEL = MODELS / "face_landmarker.task"


def matrix_to_quat(m):
    """3x3 rotation matrix -> (x, y, z, w)."""
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        return [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    if m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        return [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    if m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        return [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
    return [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--width", type=int, default=640)
    ap.add_argument("--height", type=int, default=480)
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=39570)  # 39539 is the VMC protocol default
    ap.add_argument("--preview", action="store_true", help="show debug window (q to quit)")
    ap.add_argument("--hands", action="store_true", help="also track hands and fingers (costs some fps)")
    ap.add_argument("--pose", action="store_true", help="also track shoulders, elbows and wrists (costs some fps)")
    ap.add_argument("--selftest", action="store_true", help="load every model, run it once without a camera, exit")
    args = ap.parse_args()

    options = vision.FaceLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(MODEL)),
        running_mode=vision.RunningMode.VIDEO,
        num_faces=1,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    )
    if args.selftest:
        # Checks a packaged build can find and run its models (no camera needed).
        blank = mp.Image(mp.ImageFormat.SRGB, np.zeros((480, 640, 3), dtype=np.uint8))
        with vision.FaceLandmarker.create_from_options(options) as lm:
            lm.detect_for_video(blank, 1)
        for make in (body.make_hands, body.make_pose):
            m = make()
            m.detect_for_video(blank, 1)
            m.close()
        print("selftest ok: face, hands and pose models load and run")
        return

    cap = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW)
    cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    cap.set(cv2.CAP_PROP_FPS, args.fps)
    if not cap.isOpened():
        raise SystemExit(f"cannot open camera {args.camera}")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (args.host, args.port)
    seq, n, t_fps = 0, 0, time.monotonic()
    annotated = True  # preview overlay; 'a' toggles it
    last_ts = -1

    hands_lm = body.make_hands() if args.hands else None
    pose_lm = body.make_pose() if args.pose else None

    with vision.FaceLandmarker.create_from_options(options) as lm:
        while True:
            ok, frame = cap.read()
            if not ok:
                continue
            t = time.monotonic()
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            # MediaPipe needs strictly increasing millisecond timestamps; buffered camera frames can land in the
            # same millisecond, so force each one to be at least 1 ms after the last.
            ts = max(int(t * 1000), last_ts + 1)
            last_ts = ts
            image = mp.Image(mp.ImageFormat.SRGB, rgb)
            res = lm.detect_for_video(image, ts)
            hand_res = hands_lm.detect_for_video(image, ts) if hands_lm else None
            pose_res = pose_lm.detect_for_video(image, ts) if pose_lm else None

            pkt = {"v": 1, "src": "mediapipe", "t": round(t, 4), "seq": seq, "valid": False}
            if hand_res is not None:
                pkt["hands"] = body.pack_hands(hand_res)
            if pose_res is not None:
                pose = body.pack_pose(pose_res)
                if pose:
                    pkt["pose"] = pose
            if res.face_blendshapes and res.facial_transformation_matrixes:
                m = np.asarray(res.facial_transformation_matrixes[0])
                pkt["valid"] = True
                pkt["head"] = {
                    "pos": [round(float(v), 4) for v in m[:3, 3]],
                    "quat": [round(float(v), 5) for v in matrix_to_quat(m[:3, :3])],
                }
                pkt["bs"] = {
                    c.category_name: round(c.score, 4)
                    for c in res.face_blendshapes[0]
                    if c.category_name != "_neutral"
                }
            sock.sendto(json.dumps(pkt, separators=(",", ":")).encode(), addr)
            seq += 1

            n += 1
            if t - t_fps >= 2.0:
                print(f"{n / (t - t_fps):.1f} fps  valid={pkt['valid']}")
                n, t_fps = 0, t
            if args.preview:
                view = annotate.render(frame, res, hand_res, pose_res) if annotated else frame
                cv2.imshow("mystic tracker", view)
                key = cv2.waitKey(1) & 0xFF
                if key == ord("q"):
                    break
                if key == ord("a"):
                    annotated = not annotated
    cap.release()
    for extra in (hands_lm, pose_lm):
        if extra is not None:
            extra.close()


if __name__ == "__main__":
    main()
