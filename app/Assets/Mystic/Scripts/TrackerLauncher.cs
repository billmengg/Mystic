using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mystic
{
    /// <summary>
    /// Starts the webcam tracker with Mystic, restarts it if it stops, and restarts it with new arguments when the
    /// camera or arm/hand options change. Built app: Tracker/tracker.exe next to Mystic.exe. Editor: the repo's
    /// tracker/.venv Python running tracker.py.
    /// </summary>
    public class TrackerLauncher : MonoBehaviour
    {
        public TrackerReceiver receiver;

        [Tooltip("Run the webcam tracker together with Mystic.")]
        public bool autoStart = true;
        public int cameraIndex;
        public bool withPose;
        public bool withHands;
        [Tooltip("Show the tracker's camera window with the tracking overlay.")]
        public bool showPreview;

        Process _proc;
        string _appliedArgs;
        float _nextStart;
        int _failures;
        volatile string _lastLine;

        public string Status { get; private set; } = "";

        public bool Running
        {
            get
            {
                try { return _proc != null && !_proc.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public static string[] CameraNames
        {
            get
            {
                var devices = WebCamTexture.devices;
                var names = new string[devices.Length];
                for (int i = 0; i < devices.Length; i++) names[i] = devices[i].name;
                return names;
            }
        }

        void Update()
        {
            string args = BuildArgs();
            if (Running && (!autoStart || args != _appliedArgs)) StopTracker();
            if (autoStart && !Running && Time.unscaledTime >= _nextStart) StartTracker(args);

            string last = _lastLine;
            if (Running) Status = string.IsNullOrEmpty(last) ? "Running" : $"Running ({last})";
            else if (autoStart && !string.IsNullOrEmpty(last)) Status = $"Stopped: {last}. Retrying...";
            else if (!autoStart) Status = "Off";
        }

        void OnDisable() => StopTracker();
        void OnApplicationQuit() => StopTracker();

        public void Restart()
        {
            StopTracker();
            _failures = 0;
            _nextStart = 0f;
        }

        string BuildArgs()
        {
            int port = receiver != null ? receiver.port : 39570;
            return $"--camera {cameraIndex} --port {port}" + (withPose ? " --pose" : "") + (withHands ? " --hands" : "")
                   + (showPreview ? " --preview" : "");
        }

        static bool Resolve(out string exe, out string scriptArg)
        {
            var appRoot = Path.GetDirectoryName(Application.dataPath); // <app> in a build, the Unity project in the editor
            if (!Application.isEditor)
            {
                exe = Path.Combine(appRoot, "Tracker", "tracker.exe");
                scriptArg = "";
                return File.Exists(exe);
            }
            var repo = Path.GetDirectoryName(appRoot);
            exe = Path.Combine(repo, "tracker", ".venv", "Scripts", "python.exe");
            scriptArg = $"\"{Path.Combine(repo, "tracker", "tracker.py")}\" ";
            return File.Exists(exe);
        }

        void StartTracker(string args)
        {
            // Back off after repeated failures (e.g. camera in use), up to 30 s between attempts.
            _nextStart = Time.unscaledTime + Mathf.Min(30f, 3f * (_failures + 1));
            if (!Resolve(out var exe, out var scriptArg))
            {
                Status = $"Tracker not found at {exe}";
                _failures++;
                return;
            }
            var psi = new ProcessStartInfo(exe, scriptArg + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            try
            {
                _lastLine = null;
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (_, e) => Remember(e.Data);
                _proc.ErrorDataReceived += (_, e) => Remember(e.Data);
                _proc.Exited += (_, __) => _failures++;
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _appliedArgs = args;
                Debug.Log($"[Mystic] Started tracker: {exe} {scriptArg}{args}");
            }
            catch (Exception e)
            {
                Status = $"Could not start the tracker: {e.Message}";
                _proc = null;
                _failures++;
            }
        }

        // Keeps the tracker's last meaningful line (fps, "cannot open camera ...") for the status display, skipping
        // MediaPipe's start-up log noise.
        void Remember(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("W0000") || line.StartsWith("I0000") || line.StartsWith("INFO:") || line.StartsWith("WARNING:"))
                return;
            _lastLine = line.Trim();
            if (line.Contains("fps")) _failures = 0; // it's running fine
        }

        void StopTracker()
        {
            try
            {
                if (Running) _proc.Kill();
            }
            catch (Exception) { /* already gone */ }
            _proc?.Dispose();
            _proc = null;
            _appliedArgs = null;
        }
    }
}
