using System.Collections.Generic;

/// <summary>
/// The 18 turns of a cube (a quarter turn each way and a half turn of each of the six faces) as permutations of the
/// 54 facelets in standard notation (U R F D L B, nine each, row-major as looking at the face). The tables come out of
/// the geometry: every facelet gets the position and outward direction of its sticker on a cube with its center in the
/// origin, and a turn moves the stickers of one layer around that face's axis. Works on partly known states ('?').
/// </summary>
public static class CubeMoves
{
    public const string Faces = "URFDLB";

    /// <summary>"U", "U'", "U2", "R", ... in the order of <see cref="Permutation"/>.</summary>
    public static readonly string[] Names;

    // TargetOf[move][source facelet] = the facelet that sticker ends up on.
    private static readonly int[][] TargetOf;

    private struct Vec3 { public int X, Y, Z; public Vec3(int x, int y, int z) { X = x; Y = y; Z = z; } }

    private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static int Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Neg(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);

    // Where the sticker (face, row, col) sits and which way it looks. x right, y up, z towards the viewer at F.
    private static void Place(int face, int row, int col, out Vec3 position, out Vec3 normal)
    {
        switch (face)
        {
            case 0: normal = new Vec3(0, 1, 0);  position = new Vec3(-1 + col, 1, -1 + row); break;   // U, B at the top
            case 1: normal = new Vec3(1, 0, 0);  position = new Vec3(1, 1 - row, 1 - col); break;     // R
            case 2: normal = new Vec3(0, 0, 1);  position = new Vec3(-1 + col, 1 - row, 1); break;    // F
            case 3: normal = new Vec3(0, -1, 0); position = new Vec3(-1 + col, -1, 1 - row); break;   // D, F at the top
            case 4: normal = new Vec3(-1, 0, 0); position = new Vec3(-1, 1 - row, -1 + col); break;   // L
            default: normal = new Vec3(0, 0, -1); position = new Vec3(1 - col, 1 - row, -1); break;   // B
        }
    }

    // A quarter turn clockwise as seen from outside the face: -90 degrees about the face's outward axis k,
    // v' = -(k x v) + k (k . v).
    private static Vec3 TurnClockwise(Vec3 axis, Vec3 v)
    {
        Vec3 c = Cross(axis, v);
        int d = Dot(axis, v);
        return new Vec3(-c.X + axis.X * d, -c.Y + axis.Y * d, -c.Z + axis.Z * d);
    }

    private static int IndexOf(Vec3 position, Vec3 normal)
    {
        for (int f = 0; f < 6; f++)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    Place(f, r, c, out Vec3 p, out Vec3 n);
                    if (p.X == position.X && p.Y == position.Y && p.Z == position.Z && n.X == normal.X && n.Y == normal.Y && n.Z == normal.Z)
                        return f * 9 + r * 3 + c;
                }
        return -1;
    }

    static CubeMoves()
    {
        var names = new List<string>();
        var tables = new List<int[]>();
        for (int face = 0; face < 6; face++)
        {
            Place(face, 1, 1, out _, out Vec3 axis);
            int[] quarter = new int[54];
            for (int f = 0; f < 6; f++)
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        int index = f * 9 + r * 3 + c;
                        Place(f, r, c, out Vec3 p, out Vec3 n);
                        if (Dot(p, axis) == 1) { p = TurnClockwise(axis, p); n = TurnClockwise(axis, n); }
                        quarter[index] = IndexOf(p, n);
                    }

            int[] half = new int[54], counter = new int[54];
            for (int i = 0; i < 54; i++) { half[i] = quarter[quarter[i]]; counter[i] = quarter[half[i]]; }

            char letter = Faces[face];
            names.Add(letter.ToString()); tables.Add(quarter);
            names.Add(letter + "'"); tables.Add(counter);
            names.Add(letter + "2"); tables.Add(half);
        }
        Names = names.ToArray();
        TargetOf = tables.ToArray();
    }

    public static int Count => Names.Length;

    /// <summary>
    /// The solver's moves as they have to be made on the cube on screen. The cube in the scene is the mirror image of a
    /// standard cube, so a turn the solver calls clockwise is a counterclockwise turn as seen on screen, and the other
    /// way round (R becomes R', R' becomes R; half turns stay). In the result a letter alone means: turn that face
    /// clockwise as you look at it - the same as the keyboard shortcut of that letter in the app.
    /// </summary>
    public static string ToScreenNotation(string solverMoves)
    {
        if (string.IsNullOrWhiteSpace(solverMoves)) return "";
        var screen = new List<string>();
        foreach (string token in solverMoves.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.EndsWith("2")) screen.Add(token);
            else if (token.EndsWith("'")) screen.Add(token.Substring(0, token.Length - 1));
            else screen.Add(token + "'");
        }
        return string.Join(" ", screen);
    }

    /// <summary>The state after the move: the sticker on facelet i goes to facelet Target(move, i).</summary>
    public static string Apply(string state54, int move)
    {
        var result = new char[54];
        int[] target = TargetOf[move];
        for (int i = 0; i < 54; i++) result[target[i]] = state54[i];
        return new string(result);
    }

    public static int Target(int move, int facelet) => TargetOf[move][facelet];
}
