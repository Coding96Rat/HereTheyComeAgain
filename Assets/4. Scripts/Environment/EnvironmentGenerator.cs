using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class EnvironmentGenerator : MonoBehaviour
{
    [Header("References")]
    public GameObject wallPrefab;
    public Transform environmentParent;

    [Header("Safe Zone Settings")]
    public float safeZoneRadius = 50f;

    [Header("Room Shape Settings")]
    public int numberOfRooms = 40;

    public int minRoomWidth = 6;
    public int maxRoomWidth = 15;

    public int minRoomDepth = 6;
    public int maxRoomDepth = 15;

    public int minOverlap = 4;
    public int roomSpacing = 3;

    [Header("Door Settings")]
    public int minDoorSize = 3;
    public int maxDoorSize = 4;
    public int doorClearance = 3;

    [Header("Wall Settings")]
    public float wallHeight = 3f;

    private bool[,] _occupiedGrid;

    public void GenerateEnvironment()
    {
        GridSystem gridSystem = FindFirstObjectByType<GridSystem>();

        if (gridSystem == null || wallPrefab == null || environmentParent == null)
        {
            Debug.LogError("Environment Generator: 인스펙터 세팅을 확인해주세요.");
            return;
        }

        ClearEnvironment();

        int cols = gridSystem.Columns;
        int rows = gridSystem.Rows;
        _occupiedGrid = new bool[cols, rows];

        int spawnedRooms = 0;
        Vector3 groundCenter = GetGridCenter(gridSystem);

        for (int i = 0; i < numberOfRooms * 5; i++)
        {
            if (spawnedRooms >= numberOfRooms) break;

            int startX = Random.Range(0, cols);
            int startZ = Random.Range(0, rows);

            HashSet<Vector2Int> roomCells = GenerateRoomShape(startX, startZ, cols, rows);

            if (!IsValidRoom(roomCells, gridSystem, groundCenter)) continue;

            foreach (var cell in roomCells)
            {
                _occupiedGrid[cell.x, cell.y] = true;
            }

            GameObject roomObject = new GameObject($"Room_{spawnedRooms + 1}");
            roomObject.transform.SetParent(environmentParent);

            BuildRoomWalls(roomCells, gridSystem, roomObject.transform);
            spawnedRooms++;
        }

        Debug.Log($"[Map Gen] 맵 생성 완료. 핵심 요충지(방) 개수: {spawnedRooms}");
    }

    public void ClearEnvironment()
    {
        for (int i = environmentParent.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(environmentParent.GetChild(i).gameObject);
        }
    }

    private Vector3 GetGridCenter(GridSystem grid)
    {
        float centerX = grid.LeftBottomLocation.x + (grid.Columns * grid.CellSize) / 2f;
        float centerZ = grid.LeftBottomLocation.z + (grid.Rows * grid.CellSize) / 2f;
        return new Vector3(centerX, 0, centerZ);
    }

    private HashSet<Vector2Int> GenerateRoomShape(int startX, int startZ, int maxCol, int maxRow)
    {
        HashSet<Vector2Int> cells = new HashSet<Vector2Int>();

        int w1 = Random.Range(minRoomWidth, maxRoomWidth);
        int h1 = Random.Range(minRoomDepth, maxRoomDepth);
        AddRectToRoom(cells, startX, startZ, w1, h1);

        if (Random.value < 0.7f) AddCompositeRect(cells, startX, startZ, w1, h1);
        if (Random.value < 0.3f) AddCompositeRect(cells, startX, startZ, w1, h1);

        int minX = int.MaxValue, maxX = int.MinValue;
        int minZ = int.MaxValue, maxZ = int.MinValue;

        foreach (var cell in cells)
        {
            if (cell.x < minX) minX = cell.x;
            if (cell.x > maxX) maxX = cell.x;
            if (cell.y < minZ) minZ = cell.y;
            if (cell.y > maxZ) maxZ = cell.y;
        }

        int shiftX = 0, shiftZ = 0;
        if (minX < 0) shiftX = -minX;
        else if (maxX >= maxCol) shiftX = (maxCol - 1) - maxX;

        if (minZ < 0) shiftZ = -minZ;
        else if (maxZ >= maxRow) shiftZ = (maxRow - 1) - maxZ;

        if (shiftX != 0 || shiftZ != 0)
        {
            HashSet<Vector2Int> shiftedCells = new HashSet<Vector2Int>();
            foreach (var cell in cells)
            {
                shiftedCells.Add(new Vector2Int(cell.x + shiftX, cell.y + shiftZ));
            }
            return shiftedCells;
        }

        return cells;
    }

    private void AddCompositeRect(HashSet<Vector2Int> cells, int startX, int startZ, int w1, int h1)
    {
        int w2 = Random.Range(minRoomWidth, maxRoomWidth);
        int h2 = Random.Range(minRoomDepth, maxRoomDepth);

        int minX = -w2 + minOverlap;
        int maxX = w1 - minOverlap + 1;
        if (minX >= maxX) maxX = minX + 1;
        int offsetX = Random.Range(minX, maxX);

        int minZ = -h2 + minOverlap;
        int maxZ = h1 - minOverlap + 1;
        if (minZ >= maxZ) maxZ = minZ + 1;
        int offsetZ = Random.Range(minZ, maxZ);

        AddRectToRoom(cells, startX + offsetX, startZ + offsetZ, w2, h2);
    }

    private void AddRectToRoom(HashSet<Vector2Int> cells, int xStart, int zStart, int width, int height)
    {
        for (int x = xStart; x < xStart + width; x++)
        {
            for (int z = zStart; z < zStart + height; z++)
            {
                cells.Add(new Vector2Int(x, z));
            }
        }
    }

    private bool IsCorner(Vector2Int cell, HashSet<Vector2Int> boundaryCells)
    {
        bool u = boundaryCells.Contains(cell + Vector2Int.up);
        bool d = boundaryCells.Contains(cell + Vector2Int.down);
        bool l = boundaryCells.Contains(cell + Vector2Int.left);
        bool r = boundaryCells.Contains(cell + Vector2Int.right);

        if (u && l) return true;
        if (u && r) return true;
        if (d && l) return true;
        if (d && r) return true;

        return false;
    }

    private bool IsValidRoom(HashSet<Vector2Int> cells, GridSystem gridSystem, Vector3 groundCenter)
    {
        if (cells.Count == 0) return false;

        foreach (var cell in cells)
        {
            Vector3 worldPos = gridSystem.GetWorldPosition(cell.x, cell.y);
            if (Vector3.Distance(worldPos, groundCenter) < safeZoneRadius) return false;

            for (int dx = -roomSpacing; dx <= roomSpacing; dx++)
            {
                for (int dz = -roomSpacing; dz <= roomSpacing; dz++)
                {
                    int nx = cell.x + dx;
                    int nz = cell.y + dz;
                    if (nx >= 0 && nx < gridSystem.Columns && nz >= 0 && nz < gridSystem.Rows)
                    {
                        if (_occupiedGrid[nx, nz]) return false;
                    }
                }
            }
        }
        return true;
    }

    private void BuildRoomWalls(HashSet<Vector2Int> roomCells, GridSystem gridSystem, Transform roomTransform)
    {
        HashSet<Vector2Int> boundaryCells = new HashSet<Vector2Int>();

        foreach (var cell in roomCells)
        {
            bool isBoundary = false;
            Vector2Int[] dirs = {
                Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right,
                new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
            };

            foreach (var dir in dirs)
            {
                if (!roomCells.Contains(cell + dir))
                {
                    isBoundary = true;
                    break;
                }
            }
            if (isBoundary) boundaryCells.Add(cell);
        }

        Vector2Int[] straightDirs = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

        if (boundaryCells.Count > 0)
        {
            List<Vector2Int> validDoors = new List<Vector2Int>();

            foreach (var cell in boundaryCells)
            {
                Vector2Int outDir = Vector2Int.zero;
                foreach (var dir in straightDirs)
                {
                    if (!roomCells.Contains(cell + dir))
                    {
                        outDir = dir;
                        break;
                    }
                }

                if (outDir != Vector2Int.zero)
                {
                    bool isClear = true;
                    for (int i = 1; i <= doorClearance; i++)
                    {
                        Vector2Int checkPos = cell + outDir * i;
                        if (checkPos.x < 0 || checkPos.x >= gridSystem.Columns || checkPos.y < 0 || checkPos.y >= gridSystem.Rows ||
                            _occupiedGrid[checkPos.x, checkPos.y])
                        {
                            isClear = false;
                            break;
                        }
                    }

                    if (isClear)
                    {
                        for (int i = 1; i <= 2; i++)
                        {
                            if (!roomCells.Contains(cell - outDir * i))
                            {
                                isClear = false;
                                break;
                            }
                        }
                    }
                    if (isClear) validDoors.Add(cell);
                }
            }

            Vector2Int startDoorCell;
            if (validDoors.Count > 0)
                startDoorCell = validDoors[Random.Range(0, validDoors.Count)];
            else
                startDoorCell = boundaryCells.ToList()[Random.Range(0, boundaryCells.Count)];

            int targetDoorSize = Random.Range(minDoorSize, maxDoorSize + 1);

            bool startIsCorner = IsCorner(startDoorCell, boundaryCells);
            if (startIsCorner) targetDoorSize += 2;

            Queue<Vector2Int> q = new Queue<Vector2Int>();
            HashSet<Vector2Int> doorCells = new HashSet<Vector2Int>();

            q.Enqueue(startDoorCell);
            doorCells.Add(startDoorCell);

            bool skewCornerFound = false;

            while (q.Count > 0 && doorCells.Count < targetDoorSize)
            {
                Vector2Int curr = q.Dequeue();
                foreach (var d in straightDirs)
                {
                    Vector2Int next = curr + d;
                    if (boundaryCells.Contains(next) && !doorCells.Contains(next))
                    {
                        if (!startIsCorner && !skewCornerFound && IsCorner(next, boundaryCells))
                        {
                            targetDoorSize += 1;
                            skewCornerFound = true;
                        }

                        doorCells.Add(next);
                        q.Enqueue(next);

                        if (doorCells.Count >= targetDoorSize) break;
                    }
                }
            }

            foreach (var door in doorCells)
            {
                boundaryCells.Remove(door);
            }
        }

        InstantiateMergedWalls(boundaryCells, gridSystem, roomTransform);
    }

    private void InstantiateMergedWalls(HashSet<Vector2Int> cells, GridSystem gridSystem, Transform parentTransform)
    {
        HashSet<Vector2Int> wallCells = new HashSet<Vector2Int>(cells);
        List<RectInt> mergedWalls = new List<RectInt>();
        List<Vector2Int> sortedBounds = wallCells.ToList();

        sortedBounds.Sort((a, b) => {
            if (a.y != b.y) return a.y.CompareTo(b.y);
            return a.x.CompareTo(b.x);
        });

        foreach (var cell in sortedBounds)
        {
            if (!wallCells.Contains(cell)) continue;

            int spanX = 1;
            while (wallCells.Contains(new Vector2Int(cell.x + spanX, cell.y))) { spanX++; }

            if (spanX > 1)
            {
                for (int i = 0; i < spanX; i++) wallCells.Remove(new Vector2Int(cell.x + i, cell.y));
                mergedWalls.Add(new RectInt(cell.x, cell.y, spanX, 1));
            }
            else
            {
                int spanY = 1;
                while (wallCells.Contains(new Vector2Int(cell.x, cell.y + spanY))) { spanY++; }
                for (int i = 0; i < spanY; i++) wallCells.Remove(new Vector2Int(cell.x, cell.y + i));
                mergedWalls.Add(new RectInt(cell.x, cell.y, 1, spanY));
            }
        }

        float cellSize = gridSystem.CellSize;
        float wallHalfHeight = wallHeight / 2f;

        foreach (var rect in mergedWalls)
        {
            Vector3 startPos = gridSystem.GetWorldPosition(rect.x, rect.y);
            Vector3 endPos = gridSystem.GetWorldPosition(rect.x + rect.width, rect.y + rect.height);

            Vector3 centerPos = (startPos + endPos) / 2f;
            centerPos.y = wallHalfHeight;

            GameObject wall = Instantiate(wallPrefab, centerPos, Quaternion.identity, parentTransform);
            wall.transform.localScale = new Vector3(rect.width * cellSize, wallHeight, rect.height * cellSize);
        }
    }

    private void OnDrawGizmosSelected()
    {
        GridSystem gridSystem = FindFirstObjectByType<GridSystem>();

        if (gridSystem != null)
        {
            Vector3 center = GetGridCenter(gridSystem);
            Gizmos.color = new Color(0, 1, 0, 0.8f);
            Gizmos.DrawWireSphere(center, safeZoneRadius);
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(center, 1f);
        }
    }
}