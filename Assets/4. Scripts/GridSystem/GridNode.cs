using UnityEngine;

//[System.Serializable] // 핵심: 이 속성이 있어야 유니티가 이 데이터를 씬에 저장합니다!
public class GridNode
{
    public bool IsOccupied;
    public int x = 0;
    public int y = 0;

    public GridNode(int x, int y)
    {
        this.x = x;
        this.y = y;
        this.IsOccupied = false;
    }
}
