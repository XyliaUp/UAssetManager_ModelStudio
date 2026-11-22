using System.Diagnostics;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using static PakValidation;

internal static class PakWriter {
  public const string Name = "UAssetManager";

  public static Entry[] Write(PackageJob job, string destination, IReadOnlyDictionary<string, string> files) {
    var helper = Environment.GetEnvironmentVariable("BNS_STUDIO_PAK_WRITER");
    if (string.IsNullOrWhiteSpace(helper) || !Path.IsPathFullyQualified(helper) || !File.Exists(helper))
      throw new InvalidDataException("UAssetManager PAK writer has not been installed.");
    var directory = Path.GetDirectoryName(destination)!;
    var id = Guid.NewGuid().ToString("N");
    var plan = Path.Combine(directory, $"{id}.json");
    var candidate = Path.Combine(directory, $"{id}.pak");
    try {
      File.WriteAllText(plan, JsonConvert.SerializeObject(new { compression = job.Compression, files }));
      var start = new ProcessStartInfo(helper) {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
      };
      start.ArgumentList.Add(plan);
      start.ArgumentList.Add(candidate);
      using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the UAssetManager PAK writer.");
      var key = Convert.ToHexString(BaseGame.GetKey().Key);
      process.StandardInput.WriteLine(key);
      process.StandardInput.Close();
      process.WaitForExit();
      if (process.ExitCode != 0 || !File.Exists(candidate))
        throw new InvalidDataException("UAssetManager PAK writer failed (exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");
      var entries = Verify(candidate, files, job.Compression == "oodle", allowZlib: false, requireOodle: false);
      File.Move(candidate, destination, true);
      return entries;
    }
    finally {
      File.Delete(plan);
      File.Delete(candidate);
    }
  }
}
