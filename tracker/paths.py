"""Where the MediaPipe model files live: repo/models in development, next to tracker.exe when packaged."""
import sys
from pathlib import Path

if getattr(sys, "frozen", False):  # PyInstaller build
    MODELS = Path(sys.executable).resolve().parent / "models"
else:
    MODELS = Path(__file__).resolve().parent.parent / "models"
