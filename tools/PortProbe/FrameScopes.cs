using Unity.Profiling;
using UnityEngine;
namespace ULTRAKILL.MacPort
{
    public static class FrameScopes
    {
        static readonly ProfilerMarker Portal = new ProfilerMarker("MacFrame.PortalRender");
        static readonly ProfilerMarker PostProcess = new ProfilerMarker("MacFrame.PostProcess");
        static readonly ProfilerMarker WallCheck = new ProfilerMarker("MacFrame.WallCheck");
        static readonly ProfilerMarker Pacing = new ProfilerMarker("MacPacing.Wait");
        static bool trackingPortal, initialized;
        static int portalCameraPasses, completedPasses, completedFrame = -2;
        static int auxiliaryCameraPasses, completedAuxiliaryPasses;
        static Camera portalCamera;
        public static int PortalCameraPasses => Time.frameCount - completedFrame <= 1 ? completedPasses : 0;
        public static int PortalAuxiliaryPasses => Time.frameCount - completedFrame <= 1 ? completedAuxiliaryPasses : 0;
        static FrameScopes() { }
        // Register marker handles before creating the recorders, including when VSync is initially on.
        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            Camera.onPostRender += CountPortalCamera;
        }
        static void CountPortalCamera(Camera camera)
        {
            if (!trackingPortal) return;
            if (camera == portalCamera) portalCameraPasses++;
            else auxiliaryCameraPasses++;
        }
        public static void BeginPortal()
        {
            portalCamera = MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance.portalCamera;
            trackingPortal = true; portalCameraPasses = auxiliaryCameraPasses = 0; Portal.Begin();
        }
        public static void EndPortal()
        {
            Portal.End(); trackingPortal = false;
            completedPasses = portalCameraPasses; completedFrame = Time.frameCount;
            completedAuxiliaryPasses = auxiliaryCameraPasses;
        }
        public static void BeginPostProcess() { PostProcess.Begin(); }
        public static void EndPostProcess() { PostProcess.End(); }
        public static void BeginWallCheck() { WallCheck.Begin(); }
        public static void EndWallCheck() { WallCheck.End(); }
        public static void BeginPacing() { Pacing.Begin(); }
        public static void EndPacing() { Pacing.End(); }
    }
}
