using UnityEngine;

/// <summary>
/// 플레이어가 소유하거나 플레이어와 연관된 오브젝트에 구현.
/// Enemy AI의 어그로 대상 판별에 사용.
/// </summary>
public interface IPlayerRelated
{
    Transform GetTransform();
    bool IsAlive { get; }
}
