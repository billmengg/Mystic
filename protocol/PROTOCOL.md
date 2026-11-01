# Mystic tracking protocol v1

Tracker → renderer, UDP, one JSON object per datagram, default `127.0.0.1:39570` (not 39539, which is the VMC protocol default).
JSON is for debuggability; swap for a binary layout once the field set settles.

```json
{
  "v": 1,
  "src": "mediapipe",
  "t": 12.345,
  "seq": 1042,
  "valid": true,
  "head": { "pos": [x, y, z], "quat": [x, y, z, w] },
  "bs": { "eyeBlinkLeft": 0.02, "jawOpen": 0.4, "...": 0.0 }
}
```

- `t`: seconds on the tracker's monotonic clock, captured at frame grab.
- `valid`: false when no face was found; `head` and `bs` are then omitted.
- `bs`: the 52 ARKit blendshape names (MediaPipe emits the same set; `_neutral` is dropped).
  An iPhone/ARKit source uses identical names, so the renderer does not care which produced them.
- `head`: MediaPipe facial transformation matrix, right-handed, +Y up, camera looks down -Z,
  units are centimetres. The renderer converts to its own handedness (Unity: negate x and the
  quaternion's y,z).

## Optional fields (tracker started with `--hands` and/or `--pose`)

Both are additive; a renderer that does not know them just ignores them.

```json
"hands": {
  "left":  { "score": 0.96, "world": [[x, y, z] x21], "image": [[x, y] x21] },
  "right": { "...": "same shape" }
},
"pose": { "world": [[x, y, z, visibility] x33], "image": [[x, y] x33] }
```

- `hands` is keyed by the user's own hand and only contains hands that were detected this frame. MediaPipe assumes a
  mirrored image when it labels handedness; the tracker's frames are not mirrored, so it swaps the labels before sending.
- `world` is metric (metres). Axes: x right, y down, z away from the camera (same as the image). Hands are centred on the
  hand; pose is centred on the hips. Landmark indices are MediaPipe's (hand: 0 wrist, 4 thumb tip, 8 index tip, ...;
  pose: 11/12 shoulders, 13/14 elbows, 15/16 wrists).
- `image` is normalised image coordinates (x right, y down, 0-1), useful for where on screen a hand is.
- To convert a direction to Unity space the same way as the head: (x, y, z) -> (-x, -y, -z).
