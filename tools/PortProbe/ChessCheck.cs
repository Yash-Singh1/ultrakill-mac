using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace ULTRAKILL.MacPort
{
    // Compiled only into the isolated verification helper.
    public sealed class ChessCheck : MonoBehaviour
    {
        async Task Deadline(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(15000)) != task)
                throw new TimeoutException("Chess UCI request timed out.");
            await task;
        }

        async void Start()
        {
            try
            {
                foreach (int elo in new[] { 500, 2000, 3200 })
                {
                    var engine = new UciChessEngine();
                    var process = (Process)typeof(UciChessEngine).GetField("engineProcess",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                    try
                    {
                        await Deadline(engine.InitializeUciModeAsync(false, elo));
                        string result = null;
                        await Deadline(engine.SendPlayerMoveAndGetEngineResponseAsync("e2e4", move => result = move, 50));
                        if (result == null || !Regex.IsMatch(result, @"^bestmove [a-h][1-8][a-h][1-8][qrbn]?( |$)"))
                            throw new Exception("Invalid engine move: " + result);
                        UnityEngine.Debug.Log("[MacChessCheck] elo=" + elo + " " + result);
                        // Retain a separate handle while the game's StopEngine closes its own.
                        using (var child = Process.GetProcessById(process.Id))
                        {
                            await Deadline(engine.StopEngine());
                            if (!child.WaitForExit(5000)) throw new Exception("Stockfish did not exit after quit.");
                        }
                    }
                    finally
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                        process.Dispose();
                    }
                }
                UnityEngine.Debug.Log("[MacChessCheck] PASS: game wrapper initialized, returned moves, restarted and quit all three native engines.");
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("[MacChessCheck] FAIL: " + error);
            }
            Application.Quit();
        }
    }
}
