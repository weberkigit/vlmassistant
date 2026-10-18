using UnityEngine;



public class RubiksCube
{
    // 0 Right, 1 Left, 2 Up, 3 Down, 4 Front, 5 Back
    private FaceColor[,,] faces = new FaceColor[6, 3, 3];

    public RubiksCube()
    {
        Reset();
    }

    public void Reset()
    {
        for (int f = 0; f < 6; f++)
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            faces[f, r, c] = (FaceColor)(f + 1);
    }

    // ======================================================
    // PUBLIC MOVE API
    // axis: 0 = X, 1 = Y, 2 = Z
    // layer: 0 or 2 (0 = min side, 2 = max side)
    // ======================================================
    public void Rotate(int axis, int layer, bool cw)
    {
        switch (axis, layer, cw)
        {
            case (0, 2, true):  R(); break;
            case (0, 2, false): RPrime(); break;

            case (0, 0, true):  L(); break;
            case (0, 0, false): LPrime(); break;

            case (1, 2, true):  U(); break;
            case (1, 2, false): UPrime(); break;

            case (1, 0, true):  D(); break;
            case (1, 0, false): DPrime(); break;

            case (2, 2, true):  F(); break;
            case (2, 2, false): FPrime(); break;

            case (2, 0, true):  B(); break;
            case (2, 0, false): BPrime(); break;
        }
    }

    // ======================================================
    // FACE ROTATION
    // ======================================================

    void RotateFaceCW(int f)
    {
        FaceColor[,] t = new FaceColor[3, 3];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            t[c, 2 - r] = faces[f, r, c];
        CopyFace(t, f);
    }

    void RotateFaceCCW(int f)
    {
        FaceColor[,] t = new FaceColor[3, 3];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            t[2 - c, r] = faces[f, r, c];
        CopyFace(t, f);
    }

    void CopyFace(FaceColor[,] src, int f)
    {
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            faces[f, r, c] = src[r, c];
    }

    // ======================================================
    // BASIC HELPERS
    // ======================================================

    FaceColor[] GetRow(int f, int r)
        => new FaceColor[] { faces[f, r, 0], faces[f, r, 1], faces[f, r, 2] };

    FaceColor[] GetCol(int f, int c)
        => new FaceColor[] { faces[f, 0, c], faces[f, 1, c], faces[f, 2, c] };

    void SetRow(int f, int r, FaceColor[] v)
    {
        for (int i = 0; i < 3; i++)
            faces[f, r, i] = v[i];
    }

    void SetCol(int f, int c, FaceColor[] v)
    {
        for (int i = 0; i < 3; i++)
            faces[f, i, c] = v[i];
    }

    FaceColor[] Rev(FaceColor[] a)
        => new FaceColor[] { a[2], a[1], a[0] };

    void R()
    {
        RotateFaceCW(0);
        var u = GetCol(2, 2);
        var f = GetCol(4, 2);
        var d = GetCol(3, 2);
        var b = GetCol(5, 0);
        SetCol(4, 2, u);        
        SetCol(3, 2, f);      
        SetCol(5, 0, Rev(d));   
        SetCol(2, 2, Rev(b));   
    }

    void RPrime()
    {
        RotateFaceCCW(0);
        var u = GetCol(2, 2);
        var f = GetCol(4, 2);
        var d = GetCol(3, 2);
        var b = GetCol(5, 0);
        SetCol(2, 2, f);        
        SetCol(3, 2, Rev(b));   
        SetCol(4, 2, d);        
        SetCol(5, 0, Rev(u));  
    }

    void L()
    {
        RotateFaceCW(1);  
        var u = GetCol(2, 0);
        var f = GetCol(4, 0);
        var d = GetCol(3, 0);
        var b = GetCol(5, 2);
        SetCol(2, 0, f);        
        SetCol(3, 0, Rev(b));   
        SetCol(4, 0, d);        
        SetCol(5, 2, Rev(u));   
    }

    void LPrime()
    {
        RotateFaceCCW(1);   
        var u = GetCol(2, 0);
        var f = GetCol(4, 0);
        var d = GetCol(3, 0);
        var b = GetCol(5, 2);
        SetCol(4, 0, u);       
        SetCol(3, 0, f);        
        SetCol(5, 2, Rev(d));  
        SetCol(2, 0, Rev(b));   
    }

    void U()
    {
        RotateFaceCCW(2);
        var f = GetRow(4, 0);
        var r = GetRow(0, 0);
        var b = GetRow(5, 0);
        var l = GetRow(1, 0);
        SetRow(0, 0, Rev(f));   
        SetRow(5, 0, Rev(r));     
        SetRow(1, 0, Rev(b));    
        SetRow(4, 0, Rev(l));      
    }

    void UPrime()
    {
        RotateFaceCW(2);
        var f = GetRow(4, 0);
        var r = GetRow(0, 0);
        var b = GetRow(5, 0);
        var l = GetRow(1, 0);
        SetRow(1, 0, Rev(f));     
        SetRow(4, 0, Rev(r));      
        SetRow(0, 0, Rev(b));    
        SetRow(5, 0, Rev(l));       
    }

    void D()
    {
        RotateFaceCCW(3);
        var f = GetRow(4, 2);
        var r = GetRow(0, 2);
        var b = GetRow(5, 2);
        var l = GetRow(1, 2);
        SetRow(1, 2, Rev(f));    
        SetRow(4, 2, Rev(r));    
        SetRow(0, 2, Rev(b));      
        SetRow(5, 2, Rev(l));    
    }

    void DPrime()
    {
        RotateFaceCW(3);
        var f = GetRow(4, 2);
        var r = GetRow(0, 2);
        var b = GetRow(5, 2);
        var l = GetRow(1, 2);
        SetRow(0, 2, Rev(f));     
        SetRow(5, 2, Rev(r));      
        SetRow(1, 2, Rev(b));      
        SetRow(4, 2, Rev(l));     
    }

    void F()
    {
        RotateFaceCW(4);
        var u = GetRow(2, 2);
        var r = GetCol(0, 2);
        var d = GetRow(3, 0);
        var l = GetCol(1, 0);
        SetCol(0, 2, u);        // U→R
        SetRow(3, 0, Rev(r));   // R→D (rev)
        SetCol(1, 0, d);        // D→L
        SetRow(2, 2, Rev(l));   // L→U (rev)
    }

    void FPrime()
    {
        RotateFaceCCW(4);
        var u = GetRow(2, 2);
        var r = GetCol(0, 2);
        var d = GetRow(3, 0);
        var l = GetCol(1, 0);
        SetRow(2, 2, r);      
        SetCol(0, 2, Rev(d));   
        SetRow(3, 0, l);        
        SetCol(1, 0, Rev(u));   
    }

    void B()
    {
        RotateFaceCW(5);
        var u = GetRow(2, 0);
        var r = GetCol(0, 0);
        var d = GetRow(3, 2);
        var l = GetCol(1, 2);
        SetCol(1, 2, Rev(u));   // U -> L (rev)
        SetRow(3, 2, l);        // L -> D
        SetCol(0, 0, Rev(d));   // D -> R (rev)
        SetRow(2, 0, r);        // R -> U
    }

    void BPrime()
    {
        RotateFaceCCW(5);
        var u = GetRow(2, 0);
        var r = GetCol(0, 0);
        var d = GetRow(3, 2);
        var l = GetCol(1, 2);
        SetRow(2, 0, Rev(l));   // L -> U (rev)
        SetCol(1, 2, d);        // D -> L
        SetRow(3, 2, Rev(r));   // R -> D (rev)
        SetCol(0, 0, u);        // U -> R
    }

    public FaceColor GetColor(int face, int row, int col)
    => faces[face, row, col];

    /// <summary>All 54 stickers, to put the cube back later with Restore.</summary>
    public FaceColor[] Snapshot()
    {
        var copy = new FaceColor[54];
        int i = 0;
        for (int f = 0; f < 6; f++)
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            copy[i++] = faces[f, r, c];
        return copy;
    }

    public void Restore(FaceColor[] snapshot)
    {
        if (snapshot == null || snapshot.Length != 54) return;
        int i = 0;
        for (int f = 0; f < 6; f++)
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            faces[f, r, c] = snapshot[i++];
    }

    public string GetStateString()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        sb.AppendLine("========== CUBE STATE ==========");

        sb.AppendLine("        U");
        sb.AppendLine(FaceToString(2));

        sb.AppendLine("L   F   R   B");

        for (int i = 0; i < 3; i++)
        {
            sb.AppendLine(
                $"{RowString(1, i)}   {RowString(4, i)}   {RowString(0, i)}   {RowString(5, i)}"
            );
        }

        sb.AppendLine(FaceToString(3));
        sb.AppendLine("        D");

        return sb.ToString();
    }
    string FaceToString(int f)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        for (int r = 0; r < 3; r++)
        {
            sb.AppendLine(RowString(f, r));
        }

        return sb.ToString();
    }
    string RowString(int f, int r)
    {
        return $"{faces[f, r, 0]} {faces[f, r, 1]} {faces[f, r, 2]}";
    }
}