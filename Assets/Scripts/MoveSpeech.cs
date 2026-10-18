using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Writes the next moves of a solution as plain words, from the user's point of view: which face (by its center color),
/// where it is in the picture (in front, on top, on the right ...) and which way to turn it. The words are made here, in
/// code, so a language model - or the text to speech - never has to read "R' U2" and guess what it means.
/// </summary>
public static class MoveSpeech
{
    /// <summary>A face that points towards the camera: how squarely, and where it sits relative to the cube's center.</summary>
    public struct ViewFace
    {
        public int Face;        // index in "URFDLB"
        public float Facing;    // 1 = straight at the camera, 0 = edge-on
        public float OffsetX;   // pixels right of the cube's center on screen
        public float OffsetY;   // pixels above the cube's center on screen
    }

    private const string Faces = "URFDLB";

    // "the red-centered face"
    private static string Name(int face) => CubeVlmMemory.ColorName(Faces[face]) + "-centered face";

    // Where the face is for the user: "" if the view is unknown (no position is given then), null if the face points away
    // from the camera. view is ordered best-facing first.
    private static string Place(int face, IList<ViewFace> view)
    {
        if (view == null) return "";
        for (int i = 0; i < view.Count; i++)
        {
            if (view[i].Face != face) continue;
            if (i == 0) return view[i].Facing >= 0.85f ? "in front of you" : "turned most towards you";

            string side = System.Math.Abs(view[i].OffsetY) > System.Math.Abs(view[i].OffsetX)
                ? (view[i].OffsetY > 0 ? "on top" : "at the bottom")
                : (view[i].OffsetX > 0 ? "on the right" : "on the left");
            return view[i].Facing < 0.5f ? side + ", seen at a steep angle" : side;
        }
        return null;
    }

    /// <summary>One move in words, e.g. "turn the red-centered face on the right clockwise".</summary>
    public static string DescribeMove(string move, IList<ViewFace> view)
    {
        if (string.IsNullOrEmpty(move)) return "";
        int face = Faces.IndexOf(move[0]);
        if (face < 0) return "";

        string name = Name(face);
        bool half = move.EndsWith("2");
        string way = half ? "a half turn" : move.EndsWith("'") ? "counterclockwise" : "clockwise";
        string place = Place(face, view);

        // A face on the far side can't be reached on the picture: bring it round first.
        if (place == null)
            return half ? $"bring the {name} to the front and give it a half turn"
                        : $"bring the {name} to the front and turn it {way}";
        return place.Length == 0 ? $"turn the {name} {way}" : $"turn the {name} ({place}) {way}";
    }

    /// <summary>
    /// The next one or two moves of a solution as one sentence, for example "Turn the red-centered face (on the right)
    /// clockwise, then turn the white-centered face (on top) a half turn." The moves are in the direction as seen on
    /// screen (see CubeMoves.ToScreenNotation).
    /// </summary>
    public static string Describe(IList<string> screenMoves, int count, IList<ViewFace> view)
    {
        if (screenMoves == null || screenMoves.Count == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < screenMoves.Count && parts.Count < count; i++)
        {
            string part = DescribeMove(screenMoves[i], view);
            if (part.Length > 0) parts.Add(part);
        }
        if (parts.Count == 0) return "";

        string sentence = string.Join(", then ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence.Substring(1) + ".";
    }
}
