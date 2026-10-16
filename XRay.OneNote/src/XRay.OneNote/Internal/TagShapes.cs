namespace XRay.OneNote.Internal;

/// <summary>The MS-ONE note tag shapes, grouped by what they look like; shared by the PDF and HTML writers.</summary>
internal static class TagShapes
{
    public static readonly HashSet<int> Checkbox = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 28, 30, 32, 48, 50, 52, 69, 71, 73, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99];

    public static readonly HashSet<int> Star = [4, 5, 6, 13, 34, 40, 54, 61, 75];

    public static readonly HashSet<int> Flag = [14, 89, 90, 91, 92, 93, 97, 98, 99];

    public static readonly HashSet<int> Question = [15, 111];

    public static readonly HashSet<int> ArrowRight = [10, 11, 12, 16, 59, 80];

    public static readonly HashSet<int> ArrowLeft = [38, 58, 79];

    public static readonly HashSet<int> ArrowUp = [45, 66, 86];

    public static readonly HashSet<int> ArrowDown = [37, 57, 78];

    public static readonly HashSet<int> Exclamation = [7, 8, 9, 17, 115];

    public static readonly HashSet<int> Contact = [18, 94, 95, 96, 114, 115, 116, 118];

    public static readonly HashSet<int> Music = [121];

    public static readonly HashSet<int> Calendar = [19, 120, 133, 139];

    public static readonly HashSet<int> Time = [20, 110, 117];

    public static readonly HashSet<int> Checkmark = [35, 55, 76];

    public static readonly HashSet<int> Green = [1, 4, 7, 10, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 96, 99, 103];

    public static readonly HashSet<int> Blue = [3, 6, 9, 12, 14, 16, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 94, 97, 102, 118, 121, 124, 125, 126, 133];

    public static readonly HashSet<int> Red = [17, 98, 100, 113, 119, 137, 138, 140, 142];

}
