using UnityEngine;

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
