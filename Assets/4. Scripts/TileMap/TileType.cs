/// <summary>타일 종류. FlowField cost와 직접 대응.</summary>
public enum TileType
{
    Walkable = 1,   // cost 1 — 평지
    Slow     = 5,   // cost 5 — 느린 지형 (늪 등)
    Blocked  = 255  // cost 255 — 절대 진입 불가 (벽)
}
