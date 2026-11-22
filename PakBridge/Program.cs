using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI.Pak.Compression;
using UAssetAPI.Pak.Encryption.Aes;
using UAssetAPI.Pak.Objects;
using UAssetAPI.Pak.Pak;
using UAssetAPI.Pak.Versions;

namespace UAssetManager.ModelStudio.PakBridge;

internal static class Program
{
    private const string MountPoint = "BNSR/Content/";
    private const int MaximumCompressionBlockSize = 1024 * 1024;

    private sealed record PakPlan(bool RequestOodle, IReadOnlyDictionary<string, string> Files);

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("用法: BnsOutfitPak <plan.json> <dest.pak>。通过标准输入传入一行 AES 密钥。");
            return 2;
        }

        try
        {
            var destination = Path.GetFullPath(args[1]);
            if (!Path.GetExtension(destination).Equals(".pak", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("输出必须是 .pak 文件。");

            var plan = ReadPlan(args[0], destination);
            var key = ReadKey();
            var oodle = plan.RequestOodle && InitializeLocalOodle();
            var compressedCount = WriteAndVerify(plan, destination, key, oodle);
            var method = compressedCount > 0 ? "oodle" : "none";
            Console.WriteLine($"PAK_WRITTEN entries={plan.Files.Count} compression={method} compressed={compressedCount}");
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"PAK 写入失败（{error.GetType().Name}）。");
            return 1;
        }
    }

    private static PakPlan ReadPlan(string path, string destination)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("compression", out var compression) || compression.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("打包清单缺少 compression。");

        var compressionName = compression.GetString();
        if (compressionName is not ("oodle" or "none"))
            throw new InvalidDataException("compression 必须是 oodle 或 none。");

        if (!root.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("打包清单缺少 files。");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateObject())
        {
            var virtualPath = ValidateVirtualPath(entry.Name);
            var sourcePath = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
            if (sourcePath is null || !Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
                throw new InvalidDataException($"条目「{virtualPath}」必须指向一个存在的绝对文件路径。");

            var source = Path.GetFullPath(sourcePath);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("输出 PAK 不能覆盖输入文件。");

            if (!files.TryAdd(virtualPath, source))
                throw new InvalidDataException($"打包清单含重复条目「{virtualPath}」。");
        }
        if (files.Count == 0)
            throw new InvalidDataException("打包清单没有文件。");

        return new PakPlan(compressionName == "oodle", files);
    }

    private static string ValidateVirtualPath(string path)
    {
        if (!path.StartsWith(MountPoint, StringComparison.OrdinalIgnoreCase) || path.Contains(".placeholder", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("打包条目必须位于 BNSR/Content/，且不能包含占位文件。");

        var relative = path[MountPoint.Length..];
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(['\\', ':', '<', '>', '"', '|', '?', '*']) >= 0 || segment.Any(char.IsControl))
            {
                throw new InvalidDataException("打包条目包含无效的虚拟路径。");
            }
        }
        return MountPoint + relative;
    }

    private static string ReadKey()
    {
        if (!Console.IsInputRedirected)
            throw new InvalidDataException("请通过重定向的标准输入传入 AES 密钥。");

        var key = Console.ReadLine()?.Trim();
        if (key?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
            key = key[2..];
        if (key is null || key.Length != 64 || !key.All(Uri.IsHexDigit))
            throw new InvalidDataException("标准输入必须提供一行 32 字节十六进制 AES 密钥。");

        return "0x" + key;
    }

    private static bool InitializeLocalOodle()
    {
        var codec = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_DLL_NAME);
        if (!File.Exists(codec))
            return false;

        try
        {
            OodleHelper.Initialize(codec);
            return OodleHelper.Instance is not null;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or FileLoadException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int WriteAndVerify(PakPlan plan, string destination, string key, bool oodle)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".outfit-{Guid.NewGuid():N}.pak");
        try
        {
            using (var pak = new PakFileReader(key))
            {
                foreach (var entry in plan.Files)
                {
                    var method = CompressionFor(entry, oodle);
                    pak.AddFile(entry.Value, entry.Key, method);
                }

                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream);
                pak.Write(writer, MountPoint);
            }
            var compressed = VerifyPak(plan, temporary, key, oodle);
            File.Move(temporary, destination, true);
            return compressed;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static CompressionMethod CompressionFor(KeyValuePair<string, string> entry, bool oodle)
    {
        var extension = Path.GetExtension(entry.Key).ToLowerInvariant();
        if (oodle && extension is ".uexp" or ".ubulk" && new FileInfo(entry.Value).Length > 0)
            return CompressionMethod.Oodle;

        return CompressionMethod.None;
    }

    private static int VerifyPak(PakPlan plan, string file, string key, bool oodle)
    {
        using var pak = new PakFileReader(file, new VersionContainer { Game = EGame.GAME_BladeAndSoul });
        pak.AesKey = new FAesKey(key);
        pak.Mount(StringComparer.OrdinalIgnoreCase);
        if (!pak.IsEncrypted || pak.Info.Version != EPakFileVersion.PakFile_Version_FrozenIndex
            || !string.Equals(pak.MountPoint, MountPoint, StringComparison.OrdinalIgnoreCase) || pak.FileCount != plan.Files.Count)
        {
            throw new InvalidDataException("PAK 的加密索引、版本、挂载路径或文件数量与计划不符。");
        }

        var compressed = 0;
        foreach (var entry in plan.Files)
        {
            if (!pak.Files.TryGetValue(entry.Key, out var gameFile) || gameFile is not FPakEntry pakEntry || !pakEntry.IsEncrypted)
                throw new InvalidDataException($"打包条目「{entry.Key}」缺失或未加密。");

            if (pakEntry.CompressionMethod != CompressionFor(entry, oodle))
                throw new InvalidDataException($"打包条目「{entry.Key}」的实际压缩格式与计划不符。");

            if (pakEntry.CompressionMethod == CompressionMethod.Oodle && (pakEntry.CompressionBlockSize == 0 || pakEntry.CompressionBlockSize > MaximumCompressionBlockSize))
                throw new InvalidDataException($"打包条目「{entry.Key}」的压缩块大小无效。");

            using var source = File.OpenRead(entry.Value);
            if (!SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(pakEntry.Read())))
                throw new InvalidDataException($"打包条目「{entry.Key}」与源文件内容不符。");

            if (pakEntry.CompressionMethod == CompressionMethod.Oodle)
                compressed++;
        }
        return compressed;
    }
}
