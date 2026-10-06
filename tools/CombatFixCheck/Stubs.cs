namespace ULTRAKILL.Enemy {public interface ITarget {}}
namespace UnityEngine {
 public class Object {public bool alive=true;public static implicit operator bool(Object o)=>o!=null&&o.alive;}
 public struct Vector3 {public float value;public float magnitude=>System.Math.Abs(value);public static Vector3 operator -(Vector3 a,Vector3 b)=>new(){value=a.value-b.value};public static Vector3 operator /(Vector3 a,float b)=>new(){value=a.value/b};}
 public struct Bounds {public Vector3 center,extents;}
 public struct Ray {public Ray(Vector3 o,Vector3 d){}}
 public struct RaycastHit {public Vector3 point;}
 public class Collider:Object {public int closestQueries,rays,boundsQueries;public bool hit;public Bounds bounds;public Vector3 point;public Vector3 ClosestPoint(Vector3 p){closestQueries++;return p;}public Vector3 ClosestPointOnBounds(Vector3 p){boundsQueries++;return point;}public bool Raycast(Ray r,out RaycastHit h,float d){rays++;h=new(){point=point};return hit;}}
 public class MeshCollider:Collider {public bool convex;}
}
