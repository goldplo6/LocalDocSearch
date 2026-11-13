using System.Text;

namespace LocalDocSearch.Fixtures;

public static class EncodingSamples
{
    public static void CreateAll(string directory)
    {
        Directory.CreateDirectory(directory);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var chinese = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        const string text = "编号,备注\r\n1,\"西瓜\r\n果汁\"\r\n2,苹果\r\n";
        File.WriteAllText(Path.Combine(directory, "UTF8无BOM.csv"), text, new UTF8Encoding(false, true));
        File.WriteAllText(Path.Combine(directory, "UTF8带BOM.csv"), text, new UTF8Encoding(true, true));
        var original = chinese.GetBytes(text);
        File.WriteAllBytes(Path.Combine(directory, "GB18030原始.csv"), original);
        File.WriteAllText(Path.Combine(directory, "已转为UTF8.csv"), chinese.GetString(original), new UTF8Encoding(true, true));
    }
}
