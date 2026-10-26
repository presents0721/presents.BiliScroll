using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace presents.BiliScroll;

/// <summary>
/// B站 wbi 签名（来源：bilibili-API-collect 文档）。
/// 步骤：取 nav 接口的 wbi_img.img_url/sub_url 末段（去掉 .png）得 img_key/sub_key；
/// 拼 raw = img_key + sub_key，按 MixinKeyEncTab 重排取前 32 位得 mixin_key；
/// 参数加 wts → 按 key 升序 → 过滤值里的 !'()* → urlencode → query；
/// w_rid = md5(query + mixin_key)。
/// </summary>
public static class WbiSigner
{
    /// <summary>mixin_key 重排表（官方 64 位定长表，务必照抄）。</summary>
    static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    };

    public static string GetMixinKey(string imgKey, string subKey)
    {
        string raw = imgKey + subKey;
        var sb = new StringBuilder(32);
        foreach (int index in MixinKeyEncTab)
        {
            if (index < raw.Length)
                sb.Append(raw[index]);
            if (sb.Length == 32)
                break;
        }
        return sb.ToString();
    }

    /// <summary>把 img_url / sub_url 末段（形如 https://.../xxxx.png）取出 key。</summary>
    public static string ExtractKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        string last = url.Trim().Split('/').Last();
        int dot = last.LastIndexOf('.');
        return dot > 0 ? last.Substring(0, dot) : last;
    }

    /// <summary>给参数集签名，返回已排序并带 wts / w_rid 的 query 串（不含前导 ?）。</summary>
    public static string Sign(IReadOnlyDictionary<string, string> parameters, string imgKey, string subKey)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in parameters)
        {
            if (kv.Value == null)
                continue;
            sorted[kv.Key] = kv.Value;
        }
        sorted["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var queryParts = sorted.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(FilterValue(kv.Value)));
        string query = string.Join("&", queryParts);

        string wRid = Md5Hex(query + GetMixinKey(imgKey, subKey));
        return query + "&w_rid=" + wRid;
    }

    /// <summary>过滤 value 中 wbi 规定需剔除的字符 !'()* </summary>
    static string FilterValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '!' or '\'' or '(' or ')' or '*')
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Md5Hex(string input)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
