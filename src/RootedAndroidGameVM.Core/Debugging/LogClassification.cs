using System.Text.RegularExpressions;
namespace RootedAndroidGameVM.Core.Debugging;

public static class LogClassification
{
    public static bool IsLossMarker(string line) => Regex.IsMatch(line,
        @"(logcat.*(dropped|lost|truncat)|chatty\s*:.*(identical|expire)|dropped\s+\d+\s+(log\s+)?(lines|messages)|unexpected EOF)", RegexOptions.IgnoreCase);
    public static string Category(string line) => Regex.IsMatch(line, @"ANR in |am_anr") ? "anr" :
        Regex.IsMatch(line, @"FATAL EXCEPTION|Fatal signal|am_crash") ? "crash" :
        Regex.IsMatch(line, @"LuaException|Lua.*(error|exception)", RegexOptions.IgnoreCase) ? "lua" :
        Regex.IsMatch(line, @"Unity.*(error|exception)", RegexOptions.IgnoreCase) ? "unity" : "app";
}
