using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ESPresense.Utils;

public static class ArrayExtensions
{
    public static T[] EnsureLength<T>(this T[] array, int length, T defaultValue = default!) where T : struct
    {
        array ??= Array.Empty<T>();
        if (array.Length >= length) return array;

        int oldLength = array.Length;
        Array.Resize(ref array, length);
        for (int i = oldLength; i < array.Length; i++)
            array[i] = defaultValue;

        return array;
    }
}
