using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using ULTRAKILL.Portal;

namespace ULTRAKILL.MacPort
{
    // Explicit test-only component. The launcher provides a muted copied profile.
    public sealed class BloodTransitionCheck : MonoBehaviour
    {
        IEnumerator Start()
        {
            while(SceneHelper.CurrentScene!="Level 8-3" || SceneManager.GetActiveScene().name=="Bootstrap") yield return null;
            yield return new WaitForSecondsRealtime(12);
            var manager=MonoSingleton<BloodsplatterManager>.Instance;
            var parents=new List<BloodstainParent>();
            for(int i=0;i<16;i++)
            {
                var obj=new GameObject("Blood teardown fixture "+i);
                obj.transform.position=new Vector3(i,0,0);
                parents.Add(obj.AddComponent<BloodstainParent>());
            }
            yield return null;
            foreach(var parent in parents)for(int i=0;i<16;i++)parent.CreateChild(parent.transform.position+Vector3.right*i*.01f,Vector3.up,false,false);
            Debug.Log("[MacBloodTransition] Created 256 stains on 16 parents. Native blood buffer alive="+manager.props.IsCreated);
            var childrenField=typeof(BloodstainParent).GetField("children",BindingFlags.Instance|BindingFlags.NonPublic);
            var liveIndices=((IEnumerable<int>)childrenField.GetValue(parents[0])).ToArray();
            parents[0].ClearChildren();
            if(((IEnumerable<int>)childrenField.GetValue(parents[0])).Any() || liveIndices.Any(i=>Unity.Mathematics.math.lengthsq(manager.props[i].norm)!=0))throw new Exception("Live stain cleanup changed");
            Debug.Log("[MacBloodTransition] PASS: live cleanup removed 16 stains.");
            if(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_BLOOD_TRANSITION")=="disposed")
            {
                MonoSingleton<PortalManagerV2>.Instance.Particles.CompleteJobs();
                // The last CreateChild has already completed the mesh job.
                var copy=manager.props.ToArray();manager.props.Dispose();
                Debug.Log("[MacBloodTransition] Reproducing late parent cleanup after buffer disposal.");
                // Higher indices reproduce the real SIGSEGV rather than Mono's
                // special null-reference handling for addresses close to zero.
                foreach(var parent in Enumerable.Reverse(parents))parent.ClearChildren();
                if(parents.Any(parent=>((IEnumerable<int>)childrenField.GetValue(parent)).Any()))throw new Exception("Disposed cleanup retained child indices");
                manager.props=new NativeArray<BloodsplatterManager.InstanceProperties>(copy,Allocator.Persistent);
                Debug.Log("[MacBloodTransition] PASS: late parent cleanup survived disposed buffer.");
                Application.Quit();yield break;
            }
            yield return new WaitForSecondsRealtime(2);
            Debug.Log("[MacBloodTransition] Loading Level 8-4 with stained parents still present.");
            SceneHelper.LoadScene("Level 8-4",true);
            while(SceneHelper.CurrentScene!="Level 8-4" || SceneManager.GetActiveScene().name=="Bootstrap")yield return null;
            yield return new WaitForSecondsRealtime(12);
            var nextManager=MonoSingleton<BloodsplatterManager>.Instance;
            if(manager || !nextManager || !nextManager.props.IsCreated)throw new Exception("Scene transition did not finish blood-manager teardown and startup");
            Debug.Log("[MacBloodTransition] 8-3 -> 8-4 survived. Loading Main Menu.");
            SceneHelper.LoadScene("Main Menu",true);
            while(SceneHelper.CurrentScene!="Main Menu")yield return null;
            yield return new WaitForSecondsRealtime(8);
            Debug.Log("[MacBloodTransition] PASS: 8-3 -> 8-4 -> Main Menu survived.");
            Application.Quit();
        }
    }
}
