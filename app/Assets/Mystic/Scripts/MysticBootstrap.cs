using UnityEngine;

namespace Mystic
{
    /// <summary>Spawns the receiver, loader and driver in whatever scene is playing, so no scene wiring is needed.</summary>
    public static class MysticBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (Object.FindFirstObjectByType<TrackerReceiver>() != null) return;

            // 60 fps like VSeeFace: the tracker runs at ~30 Hz, so rendering hundreds of frames a second only burns
            // CPU/GPU (it was running at ~430 fps uncapped in the editor).
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            // Keep tracking and physics running when another window (OBS, a game) has focus.
            Application.runInBackground = true;
            // No single frame may advance physics by more than 50 ms, so a hitch can't fling the hair.
            Time.maximumDeltaTime = 0.05f;

            var go = new GameObject("Mystic");
            var loader = go.AddComponent<AvatarLoader>();
            var receiver = go.AddComponent<TrackerReceiver>();
            var driver = go.AddComponent<AvatarDriver>();
            driver.receiver = receiver;
            driver.loader = loader;
            // AddComponent already ran OnEnable before the fields were set; bind explicitly.
            driver.enabled = false;
            driver.enabled = true;

            var view = go.AddComponent<ViewSetup>();
            view.loader = loader;
            var look = go.AddComponent<LookController>();
            look.loader = loader;
            var profile = go.AddComponent<AvatarProfile>();
            profile.loader = loader;
            profile.driver = driver;
            profile.look = look;
            var panel = go.AddComponent<SettingsPanel>();
            panel.driver = driver;
            panel.view = view;
            panel.look = look;
            panel.profile = profile;
            var debug = go.AddComponent<HairDebug>();
            debug.loader = loader;
            debug.driver = driver;
            panel.debug = debug;
            var lipSync = go.AddComponent<LipSync>();
            driver.lipSync = lipSync;
            panel.lipSync = lipSync;
            var arms = go.AddComponent<ArmDriver>();
            arms.receiver = receiver;
            arms.loader = loader;
            arms.driver = driver;
            panel.arms = arms;

            var vmc = go.AddComponent<VmcProtocol>();
            vmc.loader = loader;
            vmc.driver = driver;
            panel.vmc = vmc;
            var spout = go.AddComponent<SpoutOutput>();
            panel.spout = spout;

            var phone = go.AddComponent<VtsPhoneSource>();
            phone.receiver = receiver;
            phone.driver = driver;
            panel.phone = phone;

            var launcher = go.AddComponent<TrackerLauncher>();
            launcher.receiver = receiver;
            panel.launcher = launcher;
            var window = go.AddComponent<NativeWindow>();
            panel.window = window;

            var store = go.AddComponent<SettingsStore>();
            store.driver = driver;
            store.look = look;
            store.view = view;
            store.lipSync = lipSync;
            store.arms = arms;
            store.debug = debug;
            store.receiver = receiver;
            store.vmc = vmc;
            store.spout = spout;
            store.phone = phone;
            store.window = window;
            store.launcher = launcher;
            panel.store = store;
        }
    }
}
