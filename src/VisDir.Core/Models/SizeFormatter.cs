using System.Text;

namespace VisDir.Core;

public static class SizeFormatter
{
    /// <summary>Windows-style units: GB means GiB, matching Explorer.</summary>
    public static string Format(ulong bytes, int decimals = 1)
    {
        const ulong KB = 1024;
        const ulong MB = KB * 1024;
        const ulong GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{Trim(bytes / (double)GB, decimals)} GB",
            >= MB => $"{Trim(bytes / (double)MB, decimals)} MB",
            >= KB => $"{Trim(bytes / (double)KB, decimals)} KB",
            _ => bytes == 1 ? "1 byte" : $"{bytes} bytes",
        };
    }

    public static string FormatShort(ulong bytes)
    {
        const ulong KB = 1024;
        const ulong MB = KB * 1024;
        const ulong GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:0.#} GB",
            >= MB => $"{bytes / (double)MB:0} MB",
            >= KB => $"{bytes / (double)KB:0} KB",
            _ => $"{bytes} B",
        };
    }

    private static string Trim(double value, int decimals) =>
        value.ToString(decimals switch
        {
            0 => "0.#",
            1 => "0.#",
            2 => "0.##",
            _ => "0.###",
        }, System.Globalization.CultureInfo.InvariantCulture);

    public static StringBuilder AppendPercent(StringBuilder sb, double fraction) =>
        sb.Append((fraction * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('%');
}
