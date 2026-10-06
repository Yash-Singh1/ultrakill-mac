using Unity.Profiling;
namespace ULTRAKILL.MacPort
{
    public static class FrameScopes
    {
        static readonly ProfilerMarker Portal = new ProfilerMarker("MacFrame.PortalRender");
        static readonly ProfilerMarker PostProcess = new ProfilerMarker("MacFrame.PostProcess");
        static readonly ProfilerMarker WallCheck = new ProfilerMarker("MacFrame.WallCheck");
        static readonly ProfilerMarker Pacing = new ProfilerMarker("MacPacing.Wait");
        static FrameScopes() { }
        // Register marker handles before creating the recorders, including when VSync is initially on.
        public static void Initialize() { }
        public static void BeginPortal() { Portal.Begin(); }
        public static void EndPortal() { Portal.End(); }
        public static void BeginPostProcess() { PostProcess.Begin(); }
        public static void EndPostProcess() { PostProcess.End(); }
        public static void BeginWallCheck() { WallCheck.Begin(); }
        public static void EndWallCheck() { WallCheck.End(); }
        public static void BeginPacing() { Pacing.Begin(); }
        public static void EndPacing() { Pacing.End(); }
    }
}
