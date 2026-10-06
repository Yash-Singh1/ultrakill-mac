namespace UnityEngine {
 public class Object {public static implicit operator bool(Object value)=>value!=null;}
 public class MonoBehaviour:Object{public bool enabled=true;}
 public class GameObject {public T AddComponent<T>() where T:new()=>new T();}
 public static class Application {public static string dataPath="";}
 public static class Debug {public static void LogWarning(string message)=>System.Console.Error.WriteLine(message);}
}
namespace UnityEngine.LowLevel {
 public struct PlayerLoopSystem {public System.Type type;public PlayerLoopSystem[] subSystemList;public System.Action updateDelegate;public System.IntPtr updateFunction,loopConditionFunction;}
 public static class PlayerLoop {public static PlayerLoopSystem root;public static PlayerLoopSystem GetCurrentPlayerLoop()=>root;public static void SetPlayerLoop(PlayerLoopSystem loop)=>root=loop;}
}
namespace UnityEngine.SceneManagement {
 public struct Scene {}public enum LoadSceneMode {Single,Additive}
 public static class SceneManager {public static event System.Action<Scene,LoadSceneMode> sceneLoaded;public static void Change()=>sceneLoaded?.Invoke(new Scene(),LoadSceneMode.Single);}
}
public static class SceneHelper {public static string CurrentScene="Timing fixture";}
