/// <summary>
/// 건물 설치 가능 여부를 나타내는 결과값.
/// </summary>
public enum PlacementResult
{
    /// <summary>지형 변경 없이 바로 설치 가능.</summary>
    Valid,

    /// <summary>침범 비율 이하의 지형이 있어, 평탄화 후 설치 가능.</summary>
    ValidWithFlattening,

    /// <summary>지형이 너무 높아 설치 불가.</summary>
    TerrainTooHigh,

    /// <summary>이미 점유된 셀이 있어 설치 불가.</summary>
    Blocked,

    /// <summary>그리드 범위 밖.</summary>
    OutOfBounds
}
