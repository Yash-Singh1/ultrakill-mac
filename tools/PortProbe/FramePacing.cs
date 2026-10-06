using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;

namespace ULTRAKILL.MacPort
{
    [DefaultExecutionOrder(-32000)]
    public sealed class FramePacing : MonoBehaviour
    {
        public static bool Precise { get; private set; } = true;
        public static int RequestedRate { get; private set; } = -1;
        static FramePacing instance;
        readonly FrameDeadline scheduler = new FrameDeadline();
        public static void Install(GameObject root)
        {
            if (!instance) { RequestedRate = Application.targetFrameRate; instance = root.AddComponent<FramePacing>(); }
        }
        // Called after the game's graphics option setter, including its Unlimited setting.
        public static void FrameLimitChanged()
        {
            RequestedRate = Application.targetFrameRate;
            if (instance) instance.scheduler.Reset();
            Apply();
        }
        public static void VSyncChanged()
        {
            if (instance) instance.scheduler.Reset();
            Apply();
        }
        public static void BenchmarkUncapped()
        {
            RequestedRate = -1;
            QualitySettings.vSyncCount = 0;
            if (instance) instance.scheduler.Reset();
            Apply();
        }
        static void Apply()
        {
            Application.targetFrameRate = Precise && QualitySettings.vSyncCount == 0 ? -1 : RequestedRate;
        }
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Precise = !Precise; scheduler.Reset(); Apply();
                UnityEngine.Debug.Log("[MacPacing] " + (Precise ? "Absolute deadline limiter" : "Unity limiter") + "; requested FPS " + RequestedRate);
            }
            if (!Precise || QualitySettings.vSyncCount != 0 || RequestedRate <= 0) { scheduler.Reset(); return; }
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double target = scheduler.Advance(now, RequestedRate);
            if (target <= now) return;
            FrameScopes.BeginPacing();
            try
            {
                while (true)
                {
                    double remaining = target - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    if (remaining <= 0) break;
                    if (remaining > 0.002) Thread.Sleep(Math.Max(1, (int)(remaining * 1000) - 1));
                    else if (remaining > 0.00015) Thread.Sleep(0);
                    else Thread.SpinWait(32);
                }
            }
            finally { FrameScopes.EndPacing(); }
        }
        void OnDestroy() { if (instance == this) { Application.targetFrameRate = RequestedRate; instance = null; } }
    }
    public static class SlideContact
    {
        public static Vector3 Find(WallCheck wall, Vector3 position)
        {
            var collider = wall.currentCollider;
            if (collider is MeshCollider mesh && !mesh.convex) return wall.GetPointOfCollision();
            return collider.ClosestPoint(position);
        }
    }
}
