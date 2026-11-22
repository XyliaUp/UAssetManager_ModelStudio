using Microsoft.Win32;
using System.IO;
using System.Security;
using System.Text.Json;

namespace UAssetManager.ModelStudio;

internal sealed record OutfitStudioPreferences(string GamePaks, string UnrealRoot, string OutputDirectory, string FbxSdkPath = "")
{
    private const string SettingsFile = "ModelStudio.preferences.json";
    private const string LegacySettingsFile = "BnsOutfitStudio.preferences.json";
    private const string WeGameProductId = "2002085";

    public static OutfitStudioPreferences Load(string? configDirectory)
    {
        var defaults = new OutfitStudioPreferences(string.Empty, string.Empty, DefaultOutputDirectory());
        if (string.IsNullOrWhiteSpace(configDirectory))
            return defaults;

        try
        {
            var path = Path.Combine(configDirectory, SettingsFile);
            if (!File.Exists(path))
                path = Path.Combine(configDirectory, LegacySettingsFile);
            if (!File.Exists(path))
                return defaults;

            var saved = JsonSerializer.Deserialize<OutfitStudioPreferences>(File.ReadAllText(path));
            if (saved is null)
                return defaults;

            var output = string.IsNullOrWhiteSpace(saved.OutputDirectory) ? defaults.OutputDirectory : saved.OutputDirectory;
            return new OutfitStudioPreferences(saved.GamePaks ?? string.Empty, saved.UnrealRoot ?? string.Empty, output, saved.FbxSdkPath ?? string.Empty);
        }
        catch (Exception error) when (IsExpectedPathError(error) || error is JsonException)
        {
            return defaults;
        }
    }

    public void Save(string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            return;

        var directory = Path.GetFullPath(configDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SettingsFile);
        var staged = Path.Combine(directory, $".outfit-studio-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this);
                stream.Flush(true);
            }
            File.Move(staged, path, true);
        }
        finally
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception error) when (IsExpectedPathError(error))
            {
            }
        }
    }

    public static OutfitStudioPreferences Discover(OutfitStudioPreferences current, bool fillBlankOnly, bool includeGame)
    {
        if (!includeGame)
        {
            var fbxSdk = DiscoverPath(current.FbxSdkPath, fillBlankOnly, path => OutfitStudioService.FindFbxSdk(path) is not null,
                () => [Environment.GetEnvironmentVariable("MODEL_STUDIO_FBX_SDK")], () => [current.UnrealRoot], EnvironmentUnrealRoots, LauncherUnrealRoots, DefaultUnrealRoots);
            return current with { FbxSdkPath = fbxSdk };
        }

        var gamePaks = DiscoverPath(current.GamePaks, fillBlankOnly, IsGamePaks, EnvironmentGamePaks, WeGameClassicPaks);
        var unrealRoot = DiscoverPath(current.UnrealRoot, fillBlankOnly, IsUnrealRoot, EnvironmentUnrealRoots, LauncherUnrealRoots, DefaultUnrealRoots);
        return current with { GamePaks = gamePaks, UnrealRoot = unrealRoot };
    }

    public string GetFbxSdkPath() => FbxSdkPath.Length > 0 ? FbxSdkPath : UnrealRoot;

    public static bool IsGamePaks(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        try
        {
            return Directory.EnumerateFiles(path, "*.pak", SearchOption.TopDirectoryOnly).Any(IsOfficialArchive);
        }
        catch (Exception error) when (IsExpectedPathError(error))
        {
            return false;
        }
    }

    public static bool IsUnrealRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var engine = Path.Combine(path, "Engine");
            var fbx = Path.Combine(engine, "Source", "ThirdParty", "FBX", "2018.1.1");
            var library = Path.Combine(fbx, "lib", "vs2015", "x64", "release");
            if (!File.Exists(Path.Combine(engine, "Binaries", "Win64", "UE4Editor-Cmd.exe"))
                || !File.Exists(Path.Combine(engine, "Binaries", "DotNET", "UnrealBuildTool.exe"))
                || !File.Exists(Path.Combine(fbx, "include", "fbxsdk.h"))
                || !File.Exists(Path.Combine(library, "libfbxsdk.dll"))
                || !File.Exists(Path.Combine(library, "libfbxsdk.lib")))
            {
                return false;
            }

            using var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(engine, "Build", "Build.version")));
            var root = version.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("MajorVersion", out var major) && major.ValueKind == JsonValueKind.Number && major.TryGetInt32(out var majorVersion)
                && root.TryGetProperty("MinorVersion", out var minor) && minor.ValueKind == JsonValueKind.Number && minor.TryGetInt32(out var minorVersion)
                && majorVersion == 4 && minorVersion == 25;
        }
        catch (Exception error) when (IsExpectedPathError(error) || error is JsonException)
        {
            return false;
        }
    }

    private static bool IsOfficialArchive(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith("Pak_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Pak0-", StringComparison.OrdinalIgnoreCase));
    }

    private static string DiscoverPath(string current, bool fillBlankOnly, Func<string?, bool> validate, params Func<IEnumerable<string?>>[] sources)
    {
        if (fillBlankOnly && !string.IsNullOrWhiteSpace(current))
            return current;

        foreach (var source in sources)
        {
            var candidates = source().Select(NormalizePath).Where(validate).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (candidates.Length == 1)
                return candidates[0]!;
            if (candidates.Length == 2)
                return current;
        }
        return current;
    }

    private static IEnumerable<string?> EnvironmentGamePaks()
    {
        foreach (var name in new[] { "BNS_GAME_PAKS", "BNS_PAKS", "BNS_GAME_ROOT" })
        {
            foreach (var path in GamePakPaths(Environment.GetEnvironmentVariable(name)))
                yield return path;
        }
    }

    private static IEnumerable<string?> WeGameClassicPaks()
    {
        var appKey = $@"SOFTWARE\Tencent\WeGame\Apps\{WeGameProductId}";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                foreach (var name in new[] { "InstallPath", "InstallDir", "GamePath" })
                {
                    foreach (var path in GamePakPaths(ReadRegistry(hive, view, appKey, name)))
                        yield return path;
                }
            }
        }

        foreach (var root in FixedDriveRoots())
        {
            var installed = Path.Combine(root, "WeGameApps", "rail_apps");
            foreach (var game in ChildDirectories(installed, $"*({WeGameProductId})"))
            {
                foreach (var path in GamePakPaths(game))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> GamePakPaths(string? candidate)
    {
        var root = NormalizePath(candidate);
        if (root is null)
            yield break;

        yield return root;
        yield return Path.Combine(root, "Content", "Paks");
        yield return Path.Combine(root, "BNSR", "Content", "Paks");
        yield return Path.Combine(root, "BNSNEO", "BNSR", "Content", "Paks");
        yield return Path.Combine(root, "Game", "BNSNEO", "BNSR", "Content", "Paks");
    }

    private static IEnumerable<string?> EnvironmentUnrealRoots()
    {
        foreach (var name in new[] { "UE425_ROOT", "UE_ROOT", "UNREAL_ENGINE_ROOT" })
            yield return Environment.GetEnvironmentVariable(name);
    }

    private static IEnumerable<string?> LauncherUnrealRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0)
                continue;

            var manifest = Path.Combine(root, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            foreach (var directory in ReadLauncherInstallations(manifest))
                yield return directory;
        }
    }

    private static IReadOnlyList<string> ReadLauncherInstallations(string manifest)
    {
        try
        {
            if (!File.Exists(manifest))
                return [];

            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("InstallationList", out var installations) || installations.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var directories = new List<string>();
            foreach (var installation in installations.EnumerateArray())
            {
                if (installation.ValueKind != JsonValueKind.Object || !installation.TryGetProperty("AppName", out var appName) || appName.ValueKind != JsonValueKind.String)
                    continue;

                if (!string.Equals(appName.GetString(), "UE_4.25", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (installation.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String && location.GetString() is { } directory)
                    directories.Add(directory);
            }
            return directories;
        }
        catch (Exception error) when (IsExpectedPathError(error) || error is JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string?> DefaultUnrealRoots()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            yield return ReadRegistry(RegistryHive.LocalMachine, view, @"SOFTWARE\EpicGames\Unreal Engine\4.25", "InstalledDirectory");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
            yield return Path.Combine(programFiles, "Epic Games", "UE_4.25");

        foreach (var root in FixedDriveRoots())
        {
            yield return Path.Combine(root, "Program Files", "Epic Games", "UE_4.25");
            yield return Path.Combine(root, "Epic Games", "UE_4.25");
        }
    }

    private static IReadOnlyList<string> FixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).Select(drive => drive.Name).ToArray();
        }
        catch (Exception error) when (IsExpectedPathError(error))
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ChildDirectories(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly) : [];
        }
        catch (Exception error) when (IsExpectedPathError(error))
        {
            return [];
        }
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception error) when (IsExpectedPathError(error))
        {
            return null;
        }
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var candidate = value.Trim().Trim('"');
            return Path.IsPathFullyQualified(candidate) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) : null;
        }
        catch (Exception error) when (IsExpectedPathError(error))
        {
            return null;
        }
    }

    private static string DefaultOutputDirectory()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (baseDirectory.Length == 0)
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (baseDirectory.Length == 0)
            baseDirectory = Path.GetTempPath();

        return Path.Combine(baseDirectory, "BnsOutfitStudio", "输出");
    }

    private static bool IsExpectedPathError(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or ArgumentException or SecurityException or NotSupportedException;
    }
}
