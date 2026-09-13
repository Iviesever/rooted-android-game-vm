using System.IO.Compression;
using System.Text;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record ApkMetadata(string Package, string VersionName, long VersionCode, string[] Abis)
{
    public static ApkMetadata Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("AndroidManifest.xml") ?? throw new IOException("APK 缺少清单。");
        if (entry.Length > 8 * 1024 * 1024) throw new IOException("APK 清单过大。");
        using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var data = buffer.ToArray(); var strings = new List<string>(); string? package = null; var name = ""; long version = -1;
        uint U(int i) => BitConverter.ToUInt32(data, i); ushort S(int i) => BitConverter.ToUInt16(data, i);
        for (var offset = 8; offset + 8 <= data.Length;)
        {
            var type = S(offset); var header = S(offset + 2); var size = checked((int)U(offset + 4));
            if (size < 8 || offset + size > data.Length) throw new IOException("APK 二进制清单损坏。");
            if (type == 1)
            {
                var count = checked((int)U(offset + 8)); if (count > 100000) throw new IOException("字符串数量过大。");
                var utf8 = (U(offset + 16) & 256) != 0; var start = offset + checked((int)U(offset + 20));
                int Len8(ref int p) { var a = data[p++]; return (a & 128) == 0 ? a : ((a & 127) << 8) | data[p++]; }
                foreach (var index in Enumerable.Range(0, count))
                {
                    var p = start + checked((int)U(offset + header + index * 4));
                    if (utf8) { Len8(ref p); var len = Len8(ref p); strings.Add(Encoding.UTF8.GetString(data, p, len)); }
                    else { var len = S(p); p += 2; if ((len & 32768) != 0) throw new IOException("不支持的超长清单字符串。"); strings.Add(Encoding.Unicode.GetString(data, p, len * 2)); }
                }
            }
            else if (type == 0x102 && strings[(int)U(offset + 20)] == "manifest")
            {
                var first = offset + 16 + S(offset + 24); var count = S(offset + 28); var stride = S(offset + 26);
                if (stride < 20) throw new IOException("清单属性损坏。");
                for (var i = 0; i < count; i++)
                {
                    var p = first + stride * i; var key = strings[(int)U(p + 4)]; var raw = U(p + 8); var value = U(p + 16);
                    var text = raw != uint.MaxValue ? strings[(int)raw] : data[p + 15] == 3 ? strings[(int)value] : value.ToString();
                    if (key == "package") package = text;
                    if (key == "versionName") name = text;
                    if (key == "versionCode") version = value;
                }
            }
            offset += size;
        }
        if (package is null || version < 0) throw new IOException("无法读取 APK 包名及版本。");
        return new(package, name, version, zip.Entries.Where(e => e.FullName.StartsWith("lib/") && e.FullName.EndsWith(".so"))
            .Select(e => e.FullName.Split('/')[1]).Distinct().Order().ToArray());
    }
}
