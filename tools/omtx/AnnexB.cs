namespace Omtx;

/// <summary>
/// Annex B access-unit helpers for H.264 and HEVC: NAL scanning, keyframe detection, and
/// keeping parameter sets in front of every keyframe (spec section 1).
/// </summary>
internal sealed class AnnexB
{
    private readonly bool hevc;
    private byte[] paramSets = Array.Empty<byte>();

    public AnnexB(bool hevc) { this.hevc = hevc; }

    public static IEnumerable<(int type, int start, int length)> Nals(byte[] buf, int len, bool hevc)
    {
        int i = 0, nalStart = -1;
        while (i + 3 <= len)
        {
            int sc = 0;
            if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1) sc = 3;
            else if (i + 4 <= len && buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1) sc = 4;
            if (sc > 0)
            {
                if (nalStart >= 0) yield return (Type(buf[nalStart], hevc), nalStart, Trim(buf, nalStart, i) - nalStart);
                nalStart = i + sc;
                i += sc;
            }
            else i++;
        }
        if (nalStart >= 0 && nalStart < len) yield return (Type(buf[nalStart], hevc), nalStart, len - nalStart);
    }

    static int Trim(byte[] buf, int start, int end)
    {
        while (end > start && buf[end - 1] == 0) end--; // trailing zero bytes belong to the next start code
        return end;
    }

    static int Type(byte b, bool hevc) => hevc ? (b >> 1) & 0x3F : b & 0x1F;

    public static bool IsParamSet(int type, bool hevc) => hevc ? type >= 32 && type <= 34 : type == 7 || type == 8;
    public static bool IsKeyNal(int type, bool hevc) => hevc ? type >= 16 && type <= 21 : type == 5;

    public static bool IsKeyframe(byte[] buf, int len, bool hevc)
    {
        foreach (var n in Nals(buf, len, hevc)) if (IsKeyNal(n.type, hevc)) return true;
        return false;
    }

    /// <summary>
    /// Returns the access unit to send. Remembers the latest parameter sets, and when a keyframe
    /// arrives without them, puts the remembered ones in front.
    /// </summary>
    public byte[] Process(byte[] buf, int len, out bool keyframe)
    {
        keyframe = false;
        bool hasParams = false;
        var ps = new List<(int start, int length)>();
        foreach (var n in Nals(buf, len, hevc))
        {
            if (IsKeyNal(n.type, hevc)) keyframe = true;
            if (IsParamSet(n.type, hevc)) { hasParams = true; ps.Add((n.start, n.length)); }
        }
        if (hasParams)
        {
            using var ms = new MemoryStream();
            foreach (var p in ps) { ms.Write(StartCode); ms.Write(buf, p.start, p.length); }
            paramSets = ms.ToArray();
        }
        if (keyframe && !hasParams && paramSets.Length > 0)
        {
            var outBuf = new byte[paramSets.Length + len];
            Buffer.BlockCopy(paramSets, 0, outBuf, 0, paramSets.Length);
            Buffer.BlockCopy(buf, 0, outBuf, paramSets.Length, len);
            return outBuf;
        }
        var copy = new byte[len];
        Buffer.BlockCopy(buf, 0, copy, 0, len);
        return copy;
    }

    static readonly byte[] StartCode = { 0, 0, 0, 1 };
}
