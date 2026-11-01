# Runs the tracker with the project's own venv, so PATH doesn't matter.
& "$PSScriptRoot\.venv\Scripts\python.exe" "$PSScriptRoot\tracker.py" --preview @args
