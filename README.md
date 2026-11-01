# Mystic

A customizable VTuber app for VRM avatars: webcam face, arm and hand tracking (MediaPipe) or iPhone tracking through
the VTube Studio app, Perfect Sync blendshapes, hair physics, mic lip sync, a 2D mode, and transparent OBS output over
Spout2.

**Download:** [latest release](https://github.com/billmengg/Mystic/releases/latest) (Windows 10/11, 64-bit).

## Layout

| Folder | What it is |
|---|---|
| `app/` | Unity 6000.0.28f1 (URP) project. Scripts in `app/Assets/Mystic/Scripts`, build menu in `app/Assets/Editor`. |
| `tracker/` | Python 3.11 webcam tracker (MediaPipe) that streams to the app over UDP. |
| `models/` | MediaPipe model files used by the tracker (Apache 2.0, from Google). |
| `protocol/` | The tracker-to-app UDP protocol. |
| `installer/` | Inno Setup scripts and the release script. See `installer/README.md`. |

## Running from source

1. Tracker: Python 3.11 from python.org (not the Microsoft Store build, which can't open the camera).
   ```powershell
   cd tracker
   py -3.11 -m venv .venv
   .\.venv\Scripts\pip install -r requirements.txt
   ```
2. Open `app/` in Unity Hub with Unity 6000.0.28f1 and press Play. The app starts the tracker itself.
3. Add an avatar with **Model > Add model from file**. Avatars are never committed to this repository.

## License

MIT, see `LICENSE`. Third-party components keep their own licenses (UniVRM/MToon MIT, see
`app/Assets/Mystic/Shaders/MysticToon/LICENSE-UniVRM.txt`; release builds include all notices in `Licenses/`).
