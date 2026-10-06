using System;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
namespace ULTRAKILL.MacPort
{
    // Keep the main/render threads and the short portal jobs from competing
    // with a worker for every efficiency core on Apple silicon.
    [DefaultExecutionOrder(-31900)]
    public sealed class FraudWorkers : MonoBehaviour
    {
        int original, budget;
        string previous;
        void Awake()
        {
            original = JobsUtility.JobWorkerCount;
            int cores = JobTuning.PerformanceCores();
            budget = cores > 2 ? Math.Min(original, cores - 2) : original;
        }
        void Update()
        {
            string scene = SceneHelper.CurrentScene ?? "";
            if (scene == previous) return;
            previous = scene;
            int desired = scene.StartsWith("Level 8-", StringComparison.Ordinal) ? budget : original;
            desired = Math.Min(desired, JobsUtility.JobWorkerMaximumCount);
            if (desired == JobsUtility.JobWorkerCount) return;
            JobsUtility.JobWorkerCount = desired;
            Debug.Log("[MacWorkers] Scene=" + scene + " workers=" + desired + " original=" + original);
        }
        void OnDestroy() { JobsUtility.JobWorkerCount = Math.Min(original, JobsUtility.JobWorkerMaximumCount); }
    }
}
