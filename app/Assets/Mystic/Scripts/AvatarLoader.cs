using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniVRM10;
using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Loads a .vrm at runtime (VRM 0.x is migrated to 1.0). Startup path priority: --vrm command-line argument,
    /// the last model you picked, the Vrm Path field, then the first .vrm in StreamingAssets/Models.
    /// </summary>
    public class AvatarLoader : MonoBehaviour
    {
        const string LastModelKey = "Mystic.LastModel";

        public string vrmPath;

        public Vrm10Instance Avatar { get; private set; }
        public string CurrentPath { get; private set; }
        public bool Loading { get; private set; }
        public event Action<Vrm10Instance> Loaded;

        // Models shipped inside the app (read-only once installed) and the user's own folder (where "Add model"
        // copies to). In the editor both are StreamingAssets/Models.
        public static string BundledModelsDirectory => Path.Combine(Application.streamingAssetsPath, "Models");

        public static string ModelsDirectory => Application.isEditor
            ? BundledModelsDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mystic", "Models");

        async void Start()
        {
            var path = ResolveStartupPath();
            if (path == null)
            {
                Debug.LogWarning("[Mystic] No VRM found. Put a .vrm in Assets/StreamingAssets/Models, " +
                                 "set Vrm Path on the Mystic object, or pass --vrm <path>.");
                return;
            }
            await Load(path);
        }

        public static List<string> ListModels()
        {
            var files = new List<string>();
            foreach (var dir in new[] { ModelsDirectory, BundledModelsDirectory }.Distinct())
                if (Directory.Exists(dir))
                    files.AddRange(Directory.GetFiles(dir, "*.vrm").Where(f => f.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)));
            return files.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string ResolveStartupPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--vrm") return args[i + 1];

            var last = PlayerPrefs.GetString(LastModelKey, "");
            if (!string.IsNullOrEmpty(last) && File.Exists(last)) return last;

            if (!string.IsNullOrEmpty(vrmPath) && File.Exists(vrmPath)) return vrmPath;

            return ListModels().FirstOrDefault();
        }

        /// <summary>Copies a .vrm (and its .mystic.json settings, if next to it) into the Models folder and loads it.</summary>
        public async Task AddModel(string sourcePath)
        {
            try
            {
                var dir = ModelsDirectory;
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, Path.GetFileName(sourcePath));
                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, dest, true);
                    var sidecar = AvatarProfile.SidecarFor(sourcePath);
                    if (File.Exists(sidecar)) File.Copy(sidecar, AvatarProfile.SidecarFor(dest), true);
                    Debug.Log($"[Mystic] Added {Path.GetFileName(sourcePath)} to the Models folder");
                }
                await Load(dest);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mystic] Could not add {sourcePath}: {e.Message}");
            }
        }

        public async Task Load(string path)
        {
            if (Loading) return;
            Loading = true;
            try
            {
                var vrm = await Vrm10.LoadPathAsync(
                    path,
                    canLoadVrm0X: true,
                    controlRigGenerationOption: ControlRigGenerationOption.None, // we drive the real bones directly
                    showMeshes: true);
                if (vrm == null)
                {
                    Debug.LogError($"[Mystic] Failed to load {path}");
                    return;
                }

                if (Avatar != null) Destroy(Avatar.gameObject);
                Avatar = vrm;
                CurrentPath = path;
                Avatar.transform.SetParent(transform, false);
                PlayerPrefs.SetString(LastModelKey, path);
                Debug.Log($"[Mystic] Loaded {Path.GetFileName(path)}");
                Loaded?.Invoke(Avatar);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mystic] Failed to load {path}: {e}");
            }
            finally
            {
                Loading = false;
            }
        }
    }
}
