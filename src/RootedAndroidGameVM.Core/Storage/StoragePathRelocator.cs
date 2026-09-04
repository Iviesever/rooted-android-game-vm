using System.Text.Json;
using System.Text.Json.Nodes;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Storage;

public sealed class StoragePathRelocator(IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public async Task RelocateAsync(InstallPaths source, InstallPaths target, CancellationToken cancellationToken = default)
    {
        foreach (var name in new[] { "install.json", "install-state.json" })
        {
            var path = Path.Combine(target.ProductRoot, name);
            if (!File.Exists(path)) continue;
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidDataException("安装元数据为空。");
            RewriteJson(node, source.ProductRoot, target.ProductRoot);
            await AtomicJsonFile.WriteAsync(path, node, cancellationToken).ConfigureAwait(false);
        }
        if (!Directory.Exists(target.AvdHome)) return;
        var files = VerifiedDirectoryCopy.ReadInventory(target.AvdHome).Files;
        foreach (var file in files.Where(file => file.RelativePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(target.AvdHome, file.RelativePath);
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            var changed = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf('=');
                if (separator < 0) continue;
                var value = lines[index][(separator + 1)..];
                var replacement = Remap(value, source.ProductRoot, target.ProductRoot);
                if (replacement == value) continue;
                lines[index] = lines[index][..(separator + 1)] + replacement;
                changed = true;
            }
            if (changed) await File.WriteAllLinesAsync(path, lines, cancellationToken).ConfigureAwait(false);
        }
        await RelocateBackingFilesAsync(source, target, files, cancellationToken).ConfigureAwait(false);
    }

    private async Task RelocateBackingFilesAsync(InstallPaths source, InstallPaths target, List<ResourceFile> files,
        CancellationToken cancellationToken)
    {
        var images = files.Where(file => file.RelativePath.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase) ||
                                         file.RelativePath.EndsWith(".img", StringComparison.OrdinalIgnoreCase)).ToList();
        if (images.Count == 0) return;
        var qemuImg = Path.Combine(target.SdkRoot, "emulator", "qemu-img.exe");
        if (!File.Exists(qemuImg)) throw new FileNotFoundException("无法检查虚拟磁盘的基础文件引用。", qemuImg);
        var rebasePlans = new List<(string Image, string Backing, string Format)>();
        foreach (var image in images)
        {
            var path = Path.Combine(target.AvdHome, image.RelativePath);
            var info = await _runner.RunAsync(new ProcessSpec(qemuImg, ["info", "--output=json", path],
                Path.GetDirectoryName(qemuImg)), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(info);
            using var document = JsonDocument.Parse(info.StandardOutput);
            if (!document.RootElement.TryGetProperty("backing-filename", out var backingProperty)) continue;
            var backing = backingProperty.GetString() ?? throw new InvalidDataException("虚拟磁盘基础文件路径为空。");
            if (!Path.IsPathFullyQualified(backing))
            {
                var relativeBacking = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, backing));
                PathBoundary.EnsureWithinRoot(target.ProductRoot, relativeBacking);
                if (!File.Exists(relativeBacking)) throw new FileNotFoundException("相对基础文件缺失。", relativeBacking);
                continue;
            }
            if (StoragePathPolicy.Contains(target.ProductRoot, backing)) continue;
            PathBoundary.EnsureWithinRoot(source.ProductRoot, backing);
            var replacement = Remap(backing, source.ProductRoot, target.ProductRoot);
            PathBoundary.EnsureWithinRoot(target.ProductRoot, replacement);
            var format = document.RootElement.GetProperty("backing-filename-format").GetString();
            if (format is not ("raw" or "qcow2")) throw new InvalidDataException("不支持的虚拟磁盘基础文件格式。");
            // Validate all old/new bases before rewriting any headers in a backing chain.
            var sourceHash = await Sha256Verifier.ComputeAsync(backing, cancellationToken).ConfigureAwait(false);
            var targetHash = await Sha256Verifier.ComputeAsync(replacement, cancellationToken).ConfigureAwait(false);
            if (sourceHash != targetHash) throw new InvalidDataException("基础磁盘副本不一致，已停止调整引用。");
            rebasePlans.Add((path, replacement, format));
        }
        foreach (var plan in rebasePlans)
        {
            var result = await _runner.RunAsync(new ProcessSpec(qemuImg,
                ["rebase", "-u", "-f", "qcow2", "-F", plan.Format, "-b", plan.Backing, plan.Image],
                Path.GetDirectoryName(qemuImg)), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result);
        }
    }

    private static void RewriteJson(JsonNode node, string source, string target)
    {
        if (node is JsonObject properties)
        {
            foreach (var (name, value) in properties.ToList())
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) properties[name] = Remap(text, source, target);
                else if (value is not null) RewriteJson(value, source, target);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[index] = Remap(text, source, target);
                else if (array[index] is { } child) RewriteJson(child, source, target);
            }
        }
    }

    private static string Remap(string value, string source, string target)
    {
        if (string.Equals(value, source, StringComparison.OrdinalIgnoreCase)) return target;
        if (value.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(source + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return target + value[source.Length..];
        return value;
    }

    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0)
            throw new IOException("虚拟磁盘引用检查或调整失败：" + result.StandardError + result.StandardOutput);
    }
}
