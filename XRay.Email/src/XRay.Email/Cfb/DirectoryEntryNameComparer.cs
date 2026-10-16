using System.Globalization;

namespace XRay.Email.Cfb;

public static class DirectoryEntryNameComparer
{
    public static int Compare(string left, string right)
    {
        var leftLength = GetDirectoryEntryNameLength(left);
        var rightLength = GetDirectoryEntryNameLength(right);
        if (leftLength != rightLength)
        {
            return leftLength < rightLength ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftChar = ToSimpleUppercase(left[i]);
            var rightChar = ToSimpleUppercase(right[i]);
            if (leftChar != rightChar)
            {
                return leftChar < rightChar ? -1 : 1;
            }
        }

        return 0;
    }

    internal static int GetDirectoryEntryNameLength(string name) => EncodingHelper.GetUtf16ByteCount(name) + 2;

    private static char ToSimpleUppercase(char value) => char.ToUpper(value, CultureInfo.InvariantCulture);
}
