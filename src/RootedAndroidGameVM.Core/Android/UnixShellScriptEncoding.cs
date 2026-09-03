using System.Text;

namespace RootedAndroidGameVM.Core.Android;

public static class UnixShellScriptEncoding
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Encode(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Utf8WithoutBom.GetBytes(script.ReplaceLineEndings("\n"));
    }
}
