using System.Text;

namespace QLaunch;

internal static class Pinyin
{
    // GB2312 一级字库按拼音排序，每个字母块由该块第一个字起头。
    // 边界码在运行时由锚点字自身算出，避免手抄错位表导致整表偏移。
    private static readonly (string Letter, char Anchor)[] Blocks =
    [
        ("A", '阿'), ("B", '芭'), ("C", '擦'), ("D", '搭'), ("E", '蛾'), ("F", '发'),
        ("G", '噶'), ("H", '哈'), ("J", '击'), ("K", '喀'), ("L", '垃'), ("M", '妈'),
        ("N", '拿'), ("O", '欧'), ("P", '啪'), ("Q", '期'), ("R", '然'), ("S", '撒'),
        ("T", '塌'), ("W", '挖'), ("X", '昔'), ("Y", '压'), ("Z", '匝')
    ];

    private static readonly Encoding? Gb2312 = ResolveGb2312();
    private static readonly short[] Boundaries = BuildBoundaries();

    private static Encoding? ResolveGb2312()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB2312");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static short[] BuildBoundaries()
    {
        if (Gb2312 is null)
        {
            return [];
        }
        short[] codes = new short[Blocks.Length];
        for (int i = 0; i < Blocks.Length; i++)
        {
            byte[] b = Gb2312.GetBytes(Blocks[i].Anchor.ToString());
            codes[i] = b.Length >= 2 ? (short)((b[0] << 8) | b[1]) : (short)0;
        }
        // 锚点必须随字母序递增，一旦出现非递增说明字表或编码有问题，直接放弃拼音匹配
        for (int i = 1; i < codes.Length; i++)
        {
            if (codes[i] <= codes[i - 1])
            {
                return [];
            }
        }
        return codes;
    }

    /// <summary>
    /// 取名称的拼音首字母串，ASCII 字符原样保留，用于「微信→wx」这类缩写检索。
    /// </summary>
    /// <param name="text">待转换的应用或文件名。</param>
    /// <returns>大写首字母串；无法判定拼音的字符直接跳过。</returns>
    public static string GetInitials(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                sb.Append(char.ToUpperInvariant(c));
                continue;
            }
            string letter = FirstLetterOf(c);
            if (letter.Length > 0)
            {
                sb.Append(letter);
            }
        }
        return sb.ToString();
    }

    private static string FirstLetterOf(char c)
    {
        if (Gb2312 is null || Boundaries.Length == 0)
        {
            return "";
        }
        byte[] bytes = Gb2312.GetBytes(c.ToString());
        if (bytes.Length < 2)
        {
            return "";
        }
        short code = (short)((bytes[0] << 8) | bytes[1]);
        if (code < Boundaries[0])
        {
            return "";
        }
        for (int i = Boundaries.Length - 1; i >= 0; i--)
        {
            if (code >= Boundaries[i])
            {
                return Blocks[i].Letter;
            }
        }
        return "";
    }
}
