using System;
using System.Diagnostics;
using System.Globalization;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using ULTRAKILL.Portal;
namespace ULTRAKILL.MacPort
{
    public static class BurstCheck
    {
        public static unsafe void Run()
        {
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_BURST_CHECK") != "1") return;
            bool available = BurstCompiler.IsEnabled;
            if (!available) throw new InvalidOperationException("Native Burst library was not loaded.");
            float3* points = stackalloc float3[4];
            var view = float4x4.identity;
            var projection = float4x4.PerspectiveFov(math.radians(80), 1.6f, .1f, 1000);
            for (int sample = 0; sample < 512; sample++)
            {
                float depth = .5f + sample * .13f;
                float x = (sample % 17 - 8) * .5f;
                points[0] = new float3(x - 1, -1, -depth); points[1] = new float3(x + 1, -1, -depth);
                points[2] = new float3(x + 1, 1, -depth); points[3] = new float3(x - 1, 1, -depth);
                BurstCompiler.Options.EnableBurstCompilation = false;
                bool managed = PortalRenderV2.CalculateCullingData(points, 4, in view, in projection, 3456 * 2234, 1, out var a, out var nearA);
                BurstCompiler.Options.EnableBurstCompilation = true;
                bool native = PortalRenderV2.CalculateCullingData(points, 4, in view, in projection, 3456 * 2234, 1, out var b, out var nearB);
                if (managed != native || math.cmax(math.abs(a - b)) > 0.00005f || math.abs(nearA - nearB) > 0.00005f)
                    throw new InvalidOperationException("Native/managed portal culling mismatch at " + sample);
            }
            foreach (bool native in new[] { false, true, true, false })
            {
                BurstCompiler.Options.EnableBurstCompilation = native;
                float checksum = 0;
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 100000; i++)
                {
                    PortalRenderV2.CalculateCullingData(points, 4, in view, in projection, 3456 * 2234, 1, out var bounds, out var near);
                    checksum += bounds.x + near;
                }
                watch.Stop();
                UnityEngine.Debug.Log("[MacBurstCheck] native=" + BurstCompiler.IsEnabled + " calls=100000 ms=" + watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " checksum=" + checksum.ToString("R", CultureInfo.InvariantCulture));
            }
            BurstCompiler.Options.EnableBurstCompilation = true;
            UnityEngine.Debug.Log("[MacBurstCheck] PASS: 512 original portal-culling inputs agree within 0.00005; library loaded.");
            Application.Quit();
        }
    }
}
