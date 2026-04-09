/// <summary>
/// 건물 설치 가능 여부를 나타내는 결과값.
/// </summary>
public enum PlacementResult
{
    /// <summary>설치 가능.</summary>
    Valid,

    /// <summary>지형이 있어 설치 불가.</summary>
    TerrainBlocked,

    /// <summary>이미 점유된 셀이 있어 설치 불가.</summary>
    Blocked,

    /// <summary>그리드 범위 밖.</summary>
    OutOfBounds,

    /// <summary>플레이어에서 너무 멀어 설치 불가.</summary>
    TooFar
}
