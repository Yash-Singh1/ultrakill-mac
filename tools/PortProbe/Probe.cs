using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ULTRAKILL.MacPort
{
    // Explicit test mode only. The launcher runs it with a separate save profile.
    public static class Probe
    {
        private static bool installed;
        private static int stage;
        private static float next;
        private static bool enabled;
        private static bool loggedScene;
        public static void Install()
        {
            if (installed) return;
            installed = true;
            FrameScopes.Initialize();
            Debug.Log("[MacRuntime] Native Burst enabled=" + Unity.Burst.BurstCompiler.IsEnabled);
            FpsDisplay.Install();
            var diagnostics = new GameObject("Mac frame diagnostics");
            Object.DontDestroyOnLoad(diagnostics);
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_TEST_MUTE") == "1")
            {
                AudioListener.pause = true;
                AudioListener.volume = 0;
                diagnostics.AddComponent<TestAudioMute>();
                Debug.Log("[MacTest] Audio muted for this process.");
            }
            FramePacing.Install(diagnostics);
            diagnostics.AddComponent<FraudWorkers>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_CONTROL") != null)
                JobTuning.Install(diagnostics);
#if FRAUD_TEST_TOOLS || PORTAL_OPTIMIZATION
            PortalOptimization.Install(diagnostics);
#endif
#if FRAUD_TEST_TOOLS
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_BLOOD_TRANSITION") != null)
                diagnostics.AddComponent<BloodTransitionCheck>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_SHOCKWAVE_CHECK") == "1")
                diagnostics.AddComponent<ShockwaveCheck>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_VISUAL") == "1")
                diagnostics.AddComponent<FraudVisualCheck>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_BENCH") == "1")
                diagnostics.AddComponent<FraudBenchmark>();
            BurstCheck.Run();
#endif
            JobTuning.Benchmark();
#if CHESS_TEST_TOOLS
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_CHESS_CHECK") == "1")
                diagnostics.AddComponent<ChessCheck>();
#endif
            PhaseDiagnostics.Install(diagnostics);
            diagnostics.AddComponent<FrameDiagnostics>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_STEAM_CHECK") == "1")
                diagnostics.AddComponent<SteamCheck>();
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_TRANSITION_PROBE") != "1") return;
            enabled = true;
            Debug.Log("[MacProbe] Installed scene transition probe.");
        }
        public static void Tick()
        {
            if (!enabled) return;
            if (Time.unscaledTime < next) return;
            string active = SceneManager.GetActiveScene().name;
            if (!loggedScene)
            {
                Debug.Log("[MacProbe] Active scene " + active + "; logical scene " + SceneHelper.CurrentScene);
                loggedScene = true;
            }
            if (stage == 0 && SceneHelper.CurrentScene == "Tutorial" && Object.FindObjectsOfType<EnemyIdentifier>().Length > 0)
            {
                stage = 1;
                next = Time.unscaledTime + 8;
            }
            else if (stage == 1)
            {
                var enemies = Object.FindObjectsOfType<EnemyIdentifier>();
                Debug.Log("[MacProbe] Killing " + enemies.Length + " tutorial enemies to create blood particles.");
                foreach (var enemy in enemies)
                {
                    var gore = MonoSingleton<BloodsplatterManager>.Instance.GetGore(GoreType.Small, enemy);
                    gore.transform.SetParent(enemy.transform.parent, true);
                    gore.transform.position = enemy.transform.position;
                    gore.SetActive(true);
                    var blood = gore.GetComponent<Bloodsplatter>();
                    blood.GetReady();
                    var main = blood.part.main;
                    main.startLifetime = 40f;
                    blood.part.Emit(32);
                    enemy.Death();
                }
                var portalParticles = Object.FindObjectsOfType<ULTRAKILL.Portal.PortalAwareParticleSystem>();
                foreach (var particles in portalParticles) particles._system.Emit(32);
                Debug.Log("[MacProbe] Emitted particles in " + portalParticles.Length + " portal-aware systems.");
                stage = 2;
                next = Time.unscaledTime + 8;
            }
            else if (stage == 2)
            {
                int count = 0;
                foreach (var particles in Object.FindObjectsOfType<ParticleSystem>()) count += particles.particleCount;
                Debug.Log("[MacProbe] Tutorial particle count " + count + "; transitioning to Level 0-1.");
                SceneHelper.LoadScene("Level 0-1", true);
                stage = 3;
                next = Time.unscaledTime + 12;
            }
            else if (stage == 3 && SceneHelper.CurrentScene == "Level 0-1")
            {
                Debug.Log("[MacProbe] Tutorial -> Level 0-1 succeeded; transitioning to Level 0-2.");
                SceneHelper.LoadScene("Level 0-2", true);
                stage = 4;
                next = Time.unscaledTime + 12;
            }
            else if (stage == 4 && SceneHelper.CurrentScene == "Level 0-2")
            {
                Debug.Log("[MacProbe] Level 0-1 -> Level 0-2 succeeded; transitioning to Main Menu.");
                SceneHelper.LoadScene("Main Menu", true);
                stage = 5;
                next = Time.unscaledTime + 10;
            }
            else if (stage == 5 && SceneHelper.CurrentScene == "Main Menu")
            {
                Debug.Log("[MacProbe] PASS: particle effects and three scene transitions survived.");
                enabled = false;
            }
        }
    }
    public sealed class TestAudioMute : MonoBehaviour
    {
        void LateUpdate() { AudioListener.pause = true; AudioListener.volume = 0; }
    }
}
