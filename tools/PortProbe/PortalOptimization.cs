using System.IO;
using UnityEngine;

namespace ULTRAKILL.MacPort
{
    // Enabled in ordinary builds. Isolated benchmark helpers opt in explicitly.
    public sealed class PortalOptimization : MonoBehaviour
    {
        public static bool Installed { get; private set; }
        public static string Caption => "Portal optimization enabled";
        public static void Install(GameObject root)
        {
#if !PORTAL_OPTIMIZATION
            // Optional experiment helpers enable this only in marked bundles.
            DirectoryInfo bundle=new DirectoryInfo(Application.dataPath);
            while(bundle!=null && !bundle.Name.EndsWith(".app",System.StringComparison.OrdinalIgnoreCase)) bundle=bundle.Parent;
            if(bundle==null || !File.Exists(Path.Combine(bundle.FullName,"Contents/Resources/portal-cache-test.json"))) return;
#endif
            Installed=true;
            PortalVisibilityCache.Enabled=true;
            PortalVisibilityCache.Invalidate();
            root.AddComponent<PortalOptimization>();
            Debug.Log("[MacPortals] Portal visibility cache enabled.");
        }
        void OnDestroy()
        {
            Installed=false;
            PortalVisibilityCache.Enabled=false;
            PortalVisibilityCache.Invalidate();
        }
    }
}
