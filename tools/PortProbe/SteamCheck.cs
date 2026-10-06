using System;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace ULTRAKILL.MacPort
{
    // Explicit read-only verification. No achievement, statistics or cloud writes.
    public sealed class SteamCheck : MonoBehaviour
    {
        private bool requested;
        private bool received;
        private int callbackErrors;
        private float deadline;
        void Start()
        {
            deadline = Time.realtimeSinceStartup + 15;
            SteamUserStats.OnUserStatsReceived += StatsReceived;
            Dispatch.OnException += CallbackError;
        }
        void StatsReceived(SteamId id, Result result) { received = result == Result.OK; }
        void CallbackError(Exception error) { callbackErrors++; Debug.LogError("[SteamCheck] Callback error: " + error.GetType().Name); }
        void Update()
        {
            if (!requested && SteamClient.IsValid)
            {
                requested = SteamUserStats.RequestCurrentStats();
                deadline = Time.realtimeSinceStartup + 5;
            }
            if (Time.realtimeSinceStartup < deadline) return;
            var assembly = typeof(SteamClient).Assembly;
            int callbackSize = Marshal.SizeOf(assembly.GetType("Steamworks.Dispatch+CallbackMsg_t"));
            int leaderboardSize = Marshal.SizeOf(assembly.GetType("Steamworks.Data.LeaderboardEntry_t"));
            long ugcOffset = Marshal.OffsetOf(assembly.GetType("Steamworks.Data.LeaderboardEntry_t"), "UGC").ToInt64();
            bool valid = SteamClient.IsValid;
            bool loggedOn = valid && SteamClient.IsLoggedOn;
            Debug.Log("[SteamCheck] " + (valid && loggedOn && received && callbackErrors == 0 && callbackSize == 20 && leaderboardSize == 28 && ugcOffset == 20 ? "PASS" : "FAIL")
                + " valid=" + valid + " loggedOn=" + loggedOn + " statsReceived=" + received + " callbackErrors=" + callbackErrors
                + " callbackSize=" + callbackSize + " leaderboardSize=" + leaderboardSize + " ugcOffset=" + ugcOffset);
            Destroy(this);
        }
        void OnDestroy()
        {
            SteamUserStats.OnUserStatsReceived -= StatsReceived;
            Dispatch.OnException -= CallbackError;
        }
    }
}
