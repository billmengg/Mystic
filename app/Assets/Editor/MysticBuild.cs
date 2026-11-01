using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Mystic > Build Windows App: builds Mystic.exe into build/Mystic and puts the packaged tracker next to it
/// (build/dist/tracker, made with PyInstaller from tracker/). Personal models in StreamingAssets/Models are not
/// shipped; users add theirs with "Add model from file", which stores them in Documents/Mystic/Models.
/// </summary>
public static class MysticBuild
{
    // Shaders that are only looked up by name at runtime (UniVRM import, Mystic/Toon, Spout); a build drops them
    // unless they're "always included", and models would then render magenta. These only use multi_compile
    // keywords, so always-including them costs no more variants than referencing them would.
    static readonly string[] RuntimeShaders =
    {
        "Mystic/Toon",
        "VRM10/Universal Render Pipeline/MToon10",
        "UniGLTF/UniUnlit",
        "Hidden/UniGLTF/StandardMapImporter",
        "Hidden/Klak/Spout/Blit",
    };

    // URP Lit (UniGLTF's shader for plain glTF PBR materials) has so many shader_feature keywords that always-including
    // it means compiling ~600k variants. Instead keep-alive materials in Resources pull in just the keyword combinations
    // UniGLTF's URP importer can set (UrpBaseShaderContext / UrpLitContext).
    const string LitShader = "Universal Render Pipeline/Lit";
    static readonly string[] NotAlwaysIncluded = { LitShader, "Universal Render Pipeline/Unlit" };
    const string KeepDir = "Assets/Mystic/Resources/MysticShaderKeep";

    [MenuItem("Mystic/Build Windows App")]
    static void Build()
    {
        var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        var tracker = Path.Combine(repo, "build", "dist", "tracker");
        if (!File.Exists(Path.Combine(tracker, "tracker.exe")))
        {
            EditorUtility.DisplayDialog("Mystic build",
                $"The packaged tracker is missing:\n{tracker}\\tracker.exe\n\nBuild it first with PyInstaller (see installer/README).", "OK");
            return;
        }

        IncludeShaders();

        PlayerSettings.productName = "Mystic";
        PlayerSettings.companyName = "Mystic";
        PlayerSettings.bundleVersion = "0.5.0";
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;

        var outDir = Path.Combine(repo, "build", "Mystic");
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outDir, "Mystic.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[Mystic build] Failed: {report.summary.result}, {report.summary.totalErrors} errors. See the Console.");
            return;
        }

        // Personal / commissioned models must not ship inside the app.
        var bundledModels = Path.Combine(outDir, "Mystic_Data", "StreamingAssets", "Models");
        if (Directory.Exists(bundledModels)) Directory.Delete(bundledModels, true);
        foreach (var dir in Directory.GetDirectories(outDir, "*_DoNotShip")) Directory.Delete(dir, true);

        var trackerOut = Path.Combine(outDir, "Tracker");
        if (Directory.Exists(trackerOut)) Directory.Delete(trackerOut, true);
        CopyDirectory(tracker, trackerOut);

        Debug.Log($"[Mystic build] Done: {outDir}\\Mystic.exe ({report.summary.totalSize / (1024 * 1024)} MB player + tracker). " +
                  "Next: compile installer/Mystic.iss with Inno Setup.");
        EditorUtility.RevealInFinder(Path.Combine(outDir, "Mystic.exe"));
    }

    static void IncludeShaders()
    {
        var settings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(settings);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        foreach (var name in NotAlwaysIncluded)
        {
            var shader = Shader.Find(name);
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                if (shader == null || list.GetArrayElementAtIndex(i).objectReferenceValue != shader) continue;
                list.DeleteArrayElementAtIndex(i);
                Debug.Log($"[Mystic build] No longer always including {name}");
            }
        }
        foreach (var name in RuntimeShaders)
        {
            var shader = Shader.Find(name);
            if (shader == null)
            {
                Debug.LogWarning($"[Mystic build] Shader not found, not included: {name}");
                continue;
            }
            bool present = false;
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
            if (present) continue;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            Debug.Log($"[Mystic build] Always including shader {name}");
        }
        so.ApplyModifiedProperties();
        KeepLitVariants();
        AssetDatabase.SaveAssets();
    }

    static void KeepLitVariants()
    {
        var lit = Shader.Find(LitShader);
        if (lit == null)
        {
            Debug.LogWarning($"[Mystic build] Shader not found: {LitShader}");
            return;
        }
        if (!Directory.Exists(KeepDir))
        {
            Directory.CreateDirectory(KeepDir);
            AssetDatabase.Refresh();
        }
        // surface: opaque / alpha clip / transparent / transparent premultiplied; then optional maps.
        string[][] surfaces =
        {
            new string[0],
            new[] { "_ALPHATEST_ON" },
            new[] { "_SURFACE_TYPE_TRANSPARENT" },
            new[] { "_SURFACE_TYPE_TRANSPARENT", "_ALPHAPREMULTIPLY_ON" },
        };
        string[] maps = { "_NORMALMAP", "_EMISSION", "_OCCLUSIONMAP", "_METALLICSPECGLOSSMAP" };
        int made = 0;
        for (int s = 0; s < surfaces.Length; s++)
        for (int m = 0; m < 1 << maps.Length; m++)
        {
            var path = $"{KeepDir}/Lit_{s}_{m}.mat";
            if (File.Exists(path)) continue;
            var mat = new Material(lit);
            foreach (var k in surfaces[s]) mat.EnableKeyword(k);
            for (int b = 0; b < maps.Length; b++)
                if ((m & (1 << b)) != 0) mat.EnableKeyword(maps[b]);
            AssetDatabase.CreateAsset(mat, path);
            made++;
        }
        if (made > 0) Debug.Log($"[Mystic build] Created {made} keep-alive materials for {LitShader} in {KeepDir}");
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
