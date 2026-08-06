using System.Linq;

namespace Acm.Api.Services;

/// <summary>输出比对：忽略行尾空白和文件尾换行后逐字符比对。</summary>
public static class OutputComparer
{
    public static bool EqualsIgnoreTrailingWhitespace(string expected, string actual)
    {
        static string Normalize(string s) =>
            // 先去末尾换行（含空行），再逐行清尾部空白
            string.Join("\n",
                s.TrimEnd('\n')
                 .Split('\n')
                 .Select(l => l.TrimEnd(' ', '\t', '\r')));
        return Normalize(expected) == Normalize(actual);
    }
}
