namespace UnityEngine {
 public class DefaultExecutionOrder:System.Attribute { public DefaultExecutionOrder(int order){} }
 public class Object { public static implicit operator bool(Object o)=>o!=null; }
 public class MonoBehaviour:Object{}
 public class GameObject { public T AddComponent<T>() where T:new()=>new T(); }
 public static class Application { public static int targetFrameRate; }
 public static class QualitySettings {public static int vSyncCount=1;}
 public enum KeyCode {F9}
 public static class Input {public static bool press; public static bool GetKeyDown(KeyCode key){var r=press;press=false;return r;}}
 public static class Debug {public static void Log(string s){} }
 public struct Vector3 { public int value; }
 public class Collider:Object {public int calls;public Vector3 ClosestPoint(Vector3 v){calls++;return v;} }
 public class MeshCollider:Collider {public bool convex;}
}
namespace UnityEngine.Profiling {public static class Profiler {public static void BeginSample(string s){}public static void EndSample(){}}}
public class WallCheck {public UnityEngine.Collider currentCollider;public UnityEngine.Vector3 cached;public int calls;public UnityEngine.Vector3 GetPointOfCollision(){calls++;return cached;}}

namespace Unity.Profiling {public struct ProfilerMarker { public ProfilerMarker(string name){}public void Begin(){}public void End(){} }}
