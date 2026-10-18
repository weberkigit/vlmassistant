using System.Text;

/// <summary>
/// The app's own cube state expressed in the standard facelet notation (U R F D L B, 9 stickers each, row-major as
/// looking at the face) that the solver and the face readers use. This is the same mapping the 2D overlay draws its
/// "actual cube" halves with, so anything compared against it - the debug overlay, the benchmark - agrees.
/// </summary>
public static class CubeGroundTruth
{
    public const string StdOrder = "URFDLB";

    /// <summary>The 54 facelets of a solved cube.</summary>
    public const string SolvedState = "UUUUUUUUURRRRRRRRRFFFFFFFFFDDDDDDDDDLLLLLLLLLBBBBBBBBB";

    // App faces are 0 Right, 1 Left, 2 Up, 3 Down, 4 Front, 5 Back; this is their index in StdOrder.
    public static readonly int[] StdFaceOfAppFace = { 1, 4, 0, 3, 2, 5 };

    /// <summary>Index (0..53) in the standard facelet string of an app sticker. R and L are column-mirrored.</summary>
    public static int FaceletIndex(int appFace, int row, int col)
    {
        int stdCol = (appFace == 0 || appFace == 1) ? 2 - col : col;
        return StdFaceOfAppFace[appFace] * 9 + row * 3 + stdCol;
    }

    /// <summary>The face letter whose center has this color (white U, red R, green F, yellow D, orange L, blue B).</summary>
    public static char Letter(FaceColor color)
    {
        int i = (int)color;
        return i >= 1 && i <= 6 ? "RLUDFB"[i - 1] : '?';
    }

    /// <summary>The 54-letter standard facelet string of the cube's current state.</summary>
    public static string ToFaceletString(RubiksCube cube)
    {
        var chars = new char[54];
        for (int f = 0; f < 6; f++)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    chars[FaceletIndex(f, r, c)] = Letter(cube.GetColor(f, r, c));
        return new string(chars);
    }

    /// <summary>The 9 letters of one standard face (index in StdOrder) out of a 54-letter string.</summary>
    public static string Face(string facelets, int stdFace) => facelets.Substring(stdFace * 9, 9);
}
