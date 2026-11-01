# Building the Mystic installer

1. **Package the tracker** (only needed when `tracker/` changes):
   ```powershell
   cd tracker
   .\.venv\Scripts\python.exe -m PyInstaller tracker.py --name tracker --onedir --noconfirm --collect-all mediapipe `
       --distpath ..\build\dist --workpath ..\build\pyi-work --specpath ..\build
   New-Item -ItemType Directory -Force ..\build\dist\tracker\models | Out-Null
   Copy-Item ..\models\*.task ..\build\dist\tracker\models\
   ..\build\dist\tracker\tracker.exe --selftest   # should print "selftest ok"
   ```
2. **Build the app** in Unity: menu **Mystic > Build Windows App**. Output: `build\Mystic\Mystic.exe` with the tracker in
   `build\Mystic\Tracker`. Personal models in `StreamingAssets\Models` are removed from the build; users add their own
   with "Add model from file", which stores them in `Documents\Mystic\Models`.
3. **Build the installer**:
   ```powershell
   & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\Mystic.iss
   ```
   Output: `build\installer\MysticSetup-0.5.0.exe`.
