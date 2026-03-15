using UnityEngine;

public static class VectorExtensions
{
    // »ç¿ë ¿¹: Vector3 dir = (target.position - transform.position).XZVector().normalized;
    public static Vector3 XZVector(this Vector3 v)
    {
        return new Vector3(v.x, 0, v.z);
    }
}