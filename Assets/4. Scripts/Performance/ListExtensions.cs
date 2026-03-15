using System.Collections.Generic;

public static class ListExtensions
{
    public static void FastRemoveAt<T>(this List<T> list, int index)
    {
        int lastIndex = list.Count - 1;
        list[index] = list[lastIndex]; // 삭제할 자리에 마지막 요소를 덮어씌움
        list.RemoveAt(lastIndex);      // 마지막 요소 삭제
    }
}
