using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace UAssetManager.ModelStudio;

internal sealed record OutfitStudioInspection(IReadOnlyList<string> SourceBones, IReadOnlyList<string> TargetBones, IReadOnlySet<string> WeightedSourceBones);

internal sealed record OutfitStudioPreviewFace(int A, int B, int C, int Material, Point? UvA, Point? UvB, Point? UvC);
internal sealed record OutfitStudioPreviewMaterial(string Name, string? Texture, Color Color);
internal sealed record OutfitStudioMeshPreview(IReadOnlyList<Point3D> Vertices, IReadOnlyList<OutfitStudioPreviewFace> Faces, IReadOnlyList<OutfitStudioPreviewMaterial> Materials, IReadOnlyList<string> Bones, IReadOnlySet<string> WeightedSourceBones);

internal sealed record OutfitStudioRequest(string SourceFile, string TemplateFile, string GamePaks, string UnrealRoot, string OutputDirectory, IReadOnlyDictionary<string, string> SourceToTarget, string BridgeDirectory, IReadOnlyDictionary<int, string>? TextureOverrides = null, string? Id = null, IReadOnlyDictionary<string, double>? WeightScales = null, string? TargetPackage = null, double Scale = 1, double OffsetX = 0, double OffsetY = 0, double OffsetZ = 0, OutfitStudioProductionSettings? Production = null);
internal sealed record OutfitStudioFbxRequest(string SourceFile, string ReferenceFile, string SdkPath, string OutputDirectory, IReadOnlyDictionary<string, string> SourceToTarget, IReadOnlyDictionary<int, string> TextureOverrides, IReadOnlyDictionary<string, double> WeightScales, double Scale, double OffsetX, double OffsetY, double OffsetZ);

internal static class OutfitStudioService {
  internal static string RuntimeDirectory => Path.Combine(Path.GetDirectoryName(typeof(OutfitStudioService).Assembly.Location)!, "runtime");
  private sealed record FbxTool(string Executable, string SdkDirectory);
  private sealed record Material(string Name, string? Diffuse, double[]? Color);
  private sealed record FbxInfo(List<string> Bones, List<Material> Materials, int Vertices, int Triangles, IReadOnlySet<string> WeightedSourceBones, IReadOnlySet<string> HairWeightedBones);

  public static async Task<OutfitStudioInspection> InspectAsync(OutfitStudioMeshPreview source, string templateFile, string unrealRoot, CancellationToken ct) {
	var (_, target) = ReadTemplate(templateFile);
	return await InspectReferenceAsync(source, GetFbxTool(unrealRoot), target, ct);
  }

  public static Task<OutfitStudioInspection> InspectFbxAsync(OutfitStudioMeshPreview source, string referenceFile, string sdkPath, CancellationToken ct) {
	RequireReferenceFbx(referenceFile);
	return InspectReferenceAsync(source, GetFbxTool(sdkPath), referenceFile, ct);
  }

  private static async Task<OutfitStudioInspection> InspectReferenceAsync(OutfitStudioMeshPreview source, FbxTool tool, string target, CancellationToken ct) {
	var temporary = Directory.CreateTempSubdirectory("bns-outfit-inspect-").FullName;
	var table = Path.Combine(temporary, "reference.tsv");
	try {
	  var reference = await InspectModelAsync(tool, target, table, ct);
	  return Describe(source, reference.Bones);
	}
	finally {
	  File.Delete(table);
	  Directory.Delete(temporary);
	}
  }

  public static async Task<OutfitStudioInspection> InspectTargetAsync(OutfitStudioMeshPreview source, string targetPackage, string gamePaks, string bridgeDirectory, CancellationToken ct) {
	var temp = Directory.CreateTempSubdirectory("bns-outfit-target-").FullName;
	try {
	  var target = await DumpTargetAsync(Backend(bridgeDirectory), gamePaks, targetPackage, temp, null, ct);
	  return Describe(source, target.Bones);
	}
	finally {
	  DeleteTargetProbe(temp);
	}
  }

  private static OutfitStudioInspection Describe(OutfitStudioMeshPreview source, IReadOnlyList<string> targetBones) {
	return new OutfitStudioInspection(source.Bones, targetBones, source.WeightedSourceBones);
  }

  private static void DeleteTargetProbe(string directory) {
	foreach (var name in new[] { "mesh_dump.json", "reference_json.json", "shared_json.json", "target-info.json", "source.tsv", "target.tsv" })
	  File.Delete(Path.Combine(directory, name));
	var providerRoot = Path.Combine(directory, "empty-provider-root");
	if (Directory.Exists(providerRoot))
	  Directory.Delete(providerRoot);
	Directory.Delete(directory);
  }

  public static async Task<OutfitStudioMeshPreview> LoadPreviewAsync(string sourceFile, string sdkPath, CancellationToken ct) {
	RequireSource(sourceFile);
	var tool = GetFbxTool(sdkPath);
	var temporary = Directory.CreateTempSubdirectory("bns-outfit-preview-").FullName;
	var table = Path.Combine(temporary, "mesh.tsv");
	try {
	  await RunAsync(tool.Executable, ["preview", Path.GetFullPath(sourceFile), table], Path.GetDirectoryName(tool.Executable)!, null, ct, tool.SdkDirectory);
	  var vertices = new List<Point3D>();
	  var faces = new List<OutfitStudioPreviewFace>();
	  var materials = new List<OutfitStudioPreviewMaterial>();
	  var bones = new List<string>();
	  IReadOnlySet<string>? weightedSourceBones = null;
	  using var reader = File.OpenText(table);
	  while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
		var cols = line.Split('\t');
		switch (cols[0]) {
		  case "P" when cols.Length == 4:
			vertices.Add(new Point3D(ParsePosition(cols[1]), ParsePosition(cols[3]), -ParsePosition(cols[2])));
			break;
		  case "B" when cols.Length == 2 && cols[1].Length > 0:
			bones.Add(cols[1]);
			break;
		  case "S" when weightedSourceBones is null:
			weightedSourceBones = ReadWeightedBones(cols, bones);
			break;
		  case "M" when cols.Length == 7:
			var color = Color.FromScRgb(ToChannel(cols[6]), ToChannel(cols[3]), ToChannel(cols[4]), ToChannel(cols[5]));
			materials.Add(new OutfitStudioPreviewMaterial(cols[1], FindDiffuse(sourceFile, cols[2]), color));
			break;
		  case "F" when cols.Length == 11:
			faces.Add(new OutfitStudioPreviewFace(Index(cols[1]), Index(cols[2]), Index(cols[3]), Index(cols[4]), Uv(cols[5], cols[6]), Uv(cols[7], cols[8]), Uv(cols[9], cols[10])));
			break;
		  default:
			throw new InvalidDataException(StudioStrings.Text("Service.PreviewData"));
		}
	  }
	  if (bones.Count == 0 || materials.Count == 0)
		throw new InvalidDataException(StudioStrings.Text("Service.MissingBonesMaterials"));
	  if (weightedSourceBones is null)
		throw new InvalidDataException(StudioStrings.Text("Service.InvalidSkinReport"));
	  if (vertices.Count < 3 || faces.Count == 0 || faces.Any(f => !ValidIndex(f.A, vertices.Count) || !ValidIndex(f.B, vertices.Count) || !ValidIndex(f.C, vertices.Count) || f.Material < -1 || f.Material >= materials.Count))
		throw new InvalidDataException(StudioStrings.Text("Service.MissingTriangles"));
	  return new OutfitStudioMeshPreview(vertices, faces, materials, bones, weightedSourceBones);
	}
	finally {
	  File.Delete(table);
	  Directory.Delete(temporary);
	}
  }

  private static IReadOnlySet<string> ReadWeightedBones(string[] columns, List<string> bones) {
	var names = columns.Skip(1).ToHashSet(StringComparer.Ordinal);
	if (names.Count != columns.Length - 1 || !names.IsSubsetOf(bones))
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidSkinReport"));
	return names;
  }

  private static int Index(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

  private static bool ValidIndex(int index, int count) => (uint)index < (uint)count;

  private static float ToChannel(string value) => (float)Math.Clamp(ParsePosition(value), 0, 1);

  private static Point? Uv(string x, string y) => x == "-" || y == "-" ? null : new Point(ParsePosition(x), 1 - ParsePosition(y));

  private static double ParsePosition(string value) {
	double coordinate = double.Parse(value, CultureInfo.InvariantCulture);
	return double.IsFinite(coordinate) ? coordinate : throw new InvalidDataException(StudioStrings.Text("Service.InvalidCoordinate"));
  }

  public static async Task<string> ExportFbxAsync(OutfitStudioFbxRequest request, IProgress<string> progress, CancellationToken ct) {
	RequireSource(request.SourceFile);
	RequireReferenceFbx(request.ReferenceFile);
	var tool = GetFbxTool(request.SdkPath);
	var name = Path.GetFileNameWithoutExtension(request.SourceFile);
	var output = Path.Combine(Path.GetFullPath(request.OutputDirectory), name[..Math.Min(name.Length, 48)] + "_" + Guid.NewGuid().ToString("N")[..12]);
	var fbx = Path.Combine(output, "model.fbx");
	var temporary = Directory.CreateTempSubdirectory("model-studio-fbx-").FullName;
	var completed = false;
	try {
	  var source = await InspectModelAsync(tool, request.SourceFile, Path.Combine(temporary, "source.tsv"), ct);
	  var reference = await InspectModelAsync(tool, request.ReferenceFile, Path.Combine(temporary, "reference.tsv"), ct);
	  ValidateTextureOverrides(source, request.TextureOverrides);
	  var mappingFile = Path.Combine(temporary, "mapping.tsv");
	  await WriteMappingAsync(source, reference.Bones, request.SourceToTarget, request.WeightScales, mappingFile, ct);
	  Directory.CreateDirectory(output);
	  var materials = PrepareTextures(request.SourceFile, "", request.TextureOverrides, new JsonObject(), source.Materials, output, out _, progress);
	  var textureFile = Path.Combine(temporary, "textures.tsv");
	  var links = materials.OfType<JsonObject>().Where(slot => slot["texture"] is not null).Select(slot => $"{slot["index"]}\t{slot["texture"]!.GetValue<string>()}");
	  await File.WriteAllLinesAsync(textureFile, links, new UTF8Encoding(false), ct);
	  var adapted = await RebindAsync(tool, source, reference.Bones, request.SourceFile, request.ReferenceFile, mappingFile, fbx, Path.Combine(temporary, "adapted.tsv"), [request.Scale, request.OffsetX, request.OffsetY, request.OffsetZ], progress, ct, textureFile);
	  for (var i = 0; i < adapted.Materials.Count; i++) {
		var expected = materials[i]?["texture"]?.GetValue<string>();
		if (expected is not null && (adapted.Materials[i].Diffuse is not { Length: > 0 } actual || !PathsEqual(expected, ResolveFrom(fbx, actual))))
		  throw new InvalidDataException(StudioStrings.Format("Service.FbxTextureMismatch", "", i));
	  }
	  completed = true;
	  return fbx;
	}
	finally {
	  DeleteExportWorkDirectory(temporary);
	  if (!completed && Directory.Exists(output)) {
		File.Delete(fbx);
		DeleteExportWorkDirectory(Path.Combine(output, "textures"));
		Directory.Delete(output);
	  }
	}
  }

  private static void DeleteExportWorkDirectory(string path) {
	if (!Directory.Exists(path))
	  return;
	foreach (var file in Directory.EnumerateFiles(path))
	  File.Delete(file);
	Directory.Delete(path);
  }

  public static async Task<string> BuildAsync(OutfitStudioRequest request, IProgress<string> progress, CancellationToken ct) {
	RequireSource(request.SourceFile);
	bool useTarget = !string.IsNullOrWhiteSpace(request.TargetPackage);
	var (job, reference) = useTarget ? (new JsonObject(), "") : ReadTemplate(request.TemplateFile);
	var tool = GetBnsFbxTool(request.UnrealRoot);
	var runner = Path.Combine(Path.GetFullPath(request.BridgeDirectory), "pipeline.py");
	var backend = Backend(request.BridgeDirectory);
	var game = Path.GetFullPath(request.GamePaks);
	if (!Directory.Exists(game))
	  throw new DirectoryNotFoundException(StudioStrings.Format("Service.GamePaksError", "", game));
	var root = Path.GetFullPath(request.OutputDirectory);
	var relative = Path.GetRelativePath(game, root);
	if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
	  throw new InvalidDataException(StudioStrings.Text("Service.OutputInPaks"));
	var id = string.IsNullOrWhiteSpace(request.Id) ? job["id"]?.GetValue<string>() ?? "StudioOutfit" : request.Id.Trim();
	if (!Regex.IsMatch(id, @"\A[A-Za-z][A-Za-z0-9_]{0,47}\z", RegexOptions.CultureInvariant))
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidId"));
	var output = Path.Combine(root, id + "_" + Guid.NewGuid().ToString("N")[..12]);
	Directory.CreateDirectory(output);
	var source = await InspectModelAsync(tool, request.SourceFile, Path.Combine(output, "source.tsv"), ct);
	IReadOnlyList<string> targetNames;
	if (useTarget) {
	  progress.Report(StudioStrings.Text("Service.ReadClient"));
	  var target = await DumpTargetAsync(backend, game, request.TargetPackage!, output, progress, ct);
	  job = target.Job;
	  reference = target.Rig;
	  targetNames = target.Bones;
	}
	else {
	  var donor = await InspectModelAsync(tool, reference, Path.Combine(output, "reference.tsv"), ct);
	  targetNames = donor.Bones;
	}
	ValidateTextureOverrides(source, request.TextureOverrides);
	var mappingFile = Path.Combine(output, "mapping.tsv");
	var mapping = await WriteMappingAsync(source, targetNames, request.SourceToTarget, request.WeightScales, mappingFile, ct);
	var fbx = Path.Combine(output, "model.fbx");
	var adapted = await RebindAsync(tool, source, targetNames, request.SourceFile, reference, mappingFile, fbx, Path.Combine(output, "adapted.tsv"), [request.Scale, request.OffsetX, request.OffsetY, request.OffsetZ], progress, ct);
	ApplyProduction(job, request.Production);
	ValidateHairWeights(job, adapted.HairWeightedBones);
	var materials = PrepareTextures(request.SourceFile, request.TemplateFile, request.TextureOverrides, job, source.Materials, output, out var textureHashes, progress);
	job["studio_fit"] = new JsonObject { ["scale"] = request.Scale, ["x_cm"] = request.OffsetX, ["y_cm"] = request.OffsetY, ["z_cm"] = request.OffsetZ };
	job["id"] = id;
	job["asset_root"] = "/Game/BNSBridge/" + id;
	job["runtime_abp"] = "/Game/BNSBridge/" + id + "/ABP_Outfit";
	job["runtime_root"] = "/Game/BNSBridge/Runtime_v1";
	job["fbx"] = fbx;
	job["output"] = output;
	job["game_paks"] = game;
	job["ue_root"] = Path.GetFullPath(request.UnrealRoot);
	job["materials"] = materials;
	job["texture_hashes"] = textureHashes;
	job["mapping"] = JsonSerializer.SerializeToNode(mapping);
	job["max_influences"] = 8;
	job.Remove("source_blend");
	job.Remove("blender_version");
	job.Remove("verification");
	var hashes = new JsonObject { ["fbx"] = Fingerprint(fbx) };
	foreach (var key in new[] { "reference_json", "shared_json", "mesh_dump", "original_reference_json", "animation_probe" }) {
	  bool required = key is "reference_json" or "shared_json" or "mesh_dump";
	  if (string.IsNullOrWhiteSpace(job[key]?.GetValue<string>()) && !required)
		continue;
	  var input = useTarget ? RequiredString(job, key) : ResolveFrom(request.TemplateFile, RequiredString(job, key));
	  var copy = Path.Combine(output, key + ".json");
	  if (!PathsEqual(input, copy))
		File.Copy(input, copy);
	  job[key] = copy;
	  hashes[key] = Fingerprint(copy);
	}
	job["input_hashes"] = hashes;
	var jobFile = Path.Combine(output, "job.json");
	await File.WriteAllTextAsync(jobFile, job.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
	var python = Environment.GetEnvironmentVariable("BNS_STUDIO_PYTHON") ?? "python.exe";
	var writer = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.BridgeDirectory))!, "PakBridge", "BnsOutfitPak.exe");
	if (!File.Exists(writer))
	  throw new FileNotFoundException(StudioStrings.Text("Service.MissingPakWriter"), writer);
	progress.Report(StudioStrings.Text("Service.RunCook"));
	await RunAsync(python, ["-X", "utf8", runner, jobFile, "--pak-writer", "UAssetManager"], output, progress, ct, pakWriter: writer);
	var manifestPath = Path.Combine(output, "release", "release-manifest.json");
	var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, ct))?.AsObject() ?? throw new InvalidDataException(StudioStrings.Text("Service.MissingManifest"));
	var pak = Path.GetFullPath(RequiredString(manifest, "pak"));
	if (manifest["pak_writer"]?.GetValue<string>() != "UAssetManager" || !PathsEqual(Path.GetDirectoryName(pak)!, Path.GetDirectoryName(manifestPath)!) || !File.Exists(pak) || !string.Equals(Fingerprint(pak), RequiredString(manifest, "sha256"), StringComparison.OrdinalIgnoreCase))
	  throw new InvalidDataException(StudioStrings.Text("Service.VerifyFailed"));
	return pak;
  }

  private static void ValidateTextureOverrides(FbxInfo source, IReadOnlyDictionary<int, string>? overrides) {
	if (overrides?.Keys.Any(i => i < 0 || i >= source.Materials.Count) == true)
	  throw new InvalidDataException(StudioStrings.Text("Service.UnknownSlot"));
  }

  private static async Task<Dictionary<string, string>> WriteMappingAsync(FbxInfo source, IReadOnlyList<string> targetNames, IReadOnlyDictionary<string, string> sourceToTarget, IReadOnlyDictionary<string, double>? weightScales, string mappingFile, CancellationToken ct) {
	var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
	var sourceBones = source.Bones.ToHashSet(StringComparer.Ordinal);
	var targetBones = targetNames.ToHashSet(StringComparer.Ordinal);
	foreach (var (from, to) in sourceToTarget) {
	  if (!sourceBones.Contains(from) || !targetBones.Contains(to))
		throw new InvalidDataException(StudioStrings.Format("Service.InvalidMapping", "", from, to));
	  mapping.Add(from, to);
	}
	var missingMappings = source.Bones.Where(name => source.WeightedSourceBones.Contains(name) && !mapping.ContainsKey(name)).ToArray();
	if (missingMappings.Length > 0)
	  throw new InvalidDataException(StudioStrings.Format("Service.UnmappedWeightedBones", "", string.Join(", ", missingMappings)));
	if (mapping.Count == 0)
	  throw new InvalidDataException(StudioStrings.Text("Service.NoMapping"));
	if (weightScales?.Any(pair => !mapping.ContainsKey(pair.Key) || !double.IsFinite(pair.Value) || pair.Value <= 0) == true)
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidWeight"));
	var rows = mapping.Select(pair => pair.Key + "\t" + pair.Value + "\t" + (weightScales?.GetValueOrDefault(pair.Key, 1.0) ?? 1.0).ToString("R", CultureInfo.InvariantCulture));
	await File.WriteAllLinesAsync(mappingFile, rows, new UTF8Encoding(false), ct);
	return mapping;
  }

  private static async Task<FbxInfo> RebindAsync(FbxTool tool, FbxInfo source, IReadOnlyList<string> targetNames, string sourceFile, string reference, string mappingFile, string fbx, string adaptedTable, double[] fit, IProgress<string> progress, CancellationToken ct, string? textureFile = null) {
	if (fit[0] <= 0 || fit.Any(value => !double.IsFinite(value)))
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidTransform"));
	progress.Report(StudioStrings.Text("Service.RemapProgress"));
	var factors = fit.Select(x => x.ToString("R", CultureInfo.InvariantCulture));
	List<string> remap = ["remap", Path.GetFullPath(sourceFile), Path.GetFullPath(reference), mappingFile, fbx, ..factors];
	if (textureFile is not null)
	  remap.Add(textureFile);
	await RunAsync(tool.Executable, remap, Path.GetDirectoryName(tool.Executable)!, progress, ct, tool.SdkDirectory);
	var adapted = await InspectModelAsync(tool, fbx, adaptedTable, ct);
	if (adapted.Vertices != source.Vertices || adapted.Triangles != source.Triangles || !adapted.Bones.SequenceEqual(targetNames, StringComparer.Ordinal) || !adapted.Materials.Select(row => row.Name).SequenceEqual(source.Materials.Select(row => row.Name), StringComparer.Ordinal))
	  throw new InvalidDataException(StudioStrings.Text("Service.RemapMismatch"));
	return adapted;
  }

  private static void ValidateHairWeights(JsonObject job, IReadOnlySet<string> weightedBones) {
	if (job["hair_physics"] is not JsonObject hair || hair["enabled"]?.GetValue<bool>() != true)
	  return;

	var bones = hair["bones"]?.AsArray() ?? throw new InvalidDataException(StudioStrings.Text("Service.HairMissingChain"));
	var missing = bones.Select(bone => bone!.GetValue<string>()).Where(name => !weightedBones.Contains(name)).ToArray();
	if (missing.Length > 0)
	  throw new InvalidDataException(StudioStrings.Format("Service.HairMissingWeights", "", string.Join(", ", missing)));
  }

  private static void ApplyProduction(JsonObject job, OutfitStudioProductionSettings? settings) {
	if (settings is null)
	  return;

	var hidden = new List<string>();
	if (settings.HideFace)
	  hidden.Add("/Character/Face/");
	if (settings.HideHair)
	  hidden.Add("/Character/Hair/");
	if (settings.HideHeadgear) {
	  hidden.Add("/Character/Acc/04-머리장식/");
	  hidden.Add("/Character/Acc/04-문파_머리장식/");
	  hidden.Add("/Character/Acc/06-머리장식/");
	}
	if (settings.OtherHidePaths is not null)
	  hidden.AddRange(settings.OtherHidePaths);
	job["visibility"] = new JsonObject { ["enabled"] = hidden.Count > 0, ["hide_paths"] = JsonSerializer.SerializeToNode(hidden.Distinct(StringComparer.OrdinalIgnoreCase)) };
	job["compression"] = new JsonObject { ["method"] = settings.Compression };
	job["hair_physics"] = settings.HairPhysics.DeepClone();
  }

  private static JsonArray PrepareTextures(string sourceFile, string templateFile, IReadOnlyDictionary<int, string>? overrides, JsonObject template, List<Material> slots, string output, out JsonObject textureHashes, IProgress<string> progress) {
	var materials = new JsonArray();
	textureHashes = new JsonObject();
	var previousMaterials = template["materials"]?.AsArray();
	var templateByName = previousMaterials?.OfType<JsonObject>().GroupBy(m => m["slot"]?.GetValue<string>() ?? "", StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
	var textures = new Dictionary<string, (int Slot, string Path)>(StringComparer.Ordinal);
	for (int i = 0; i < slots.Count; i++) {
	  var material = slots[i];
	  int canonical = i;
	  string? frozen = null;
	  var overridePath = overrides?.GetValueOrDefault(i);
	  var path = string.IsNullOrWhiteSpace(overridePath) ? FindDiffuse(sourceFile, material.Diffuse) : Path.GetFullPath(overridePath);
	  var previous = templateByName?.GetValueOrDefault(material.Name);
	  if (path == null && previous?["texture"]?.GetValue<string>() is string sameSourceTexture && PathsEqual(sourceFile, ResolveFrom(templateFile, RequiredString(template, "fbx"))))
		path = ResolveFrom(templateFile, sameSourceTexture);
	  if (path != null) {
		string extension = Path.GetExtension(path).ToLowerInvariant();
		if (!File.Exists(path) || extension is not (".png" or ".tga" or ".bmp" or ".jpg" or ".jpeg" or ".exr" or ".psd"))
		  throw new InvalidDataException(StudioStrings.Format("Service.InvalidTexture", "", i, path));
		string hash = Fingerprint(path);
		if (!textures.TryGetValue(hash, out var entry)) {
		  var directory = Path.Combine(output, "textures");
		  Directory.CreateDirectory(directory);
		  frozen = Path.Combine(directory, $"T_{i:00}{extension}");
		  File.Copy(path, frozen);
		  textures.Add(hash, (i, frozen));
		  textureHashes[frozen] = hash;
		}
		else {
		  canonical = entry.Slot;
		  frozen = entry.Path;
		}
	  }
	  else {
		progress.Report(StudioStrings.Format("Service.FlatMaterial", "", i, material.Name));
	  }
	  var color = material.Color is { } sourceColor ? JsonSerializer.SerializeToNode(sourceColor) : previous?["color"]?.DeepClone() ?? new JsonArray(1, 1, 1, 1);
	  materials.Add(new JsonObject { ["index"] = i, ["slot"] = material.Name, ["texture"] = frozen, ["texture_index"] = canonical, ["color"] = color });
	}
	return materials;
  }

  private static string? FindDiffuse(string source, string? path) {
	if (string.IsNullOrWhiteSpace(path))
	  return null;
	var resolved = ResolveFrom(source, path);
	if (File.Exists(resolved))
	  return resolved;
	var adjacent = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, Path.GetFileName(resolved));
	return File.Exists(adjacent) ? adjacent : null;
  }

  private static async Task<FbxInfo> InspectModelAsync(FbxTool tool, string fbx, string table, CancellationToken ct) {
	await RunAsync(tool.Executable, ["inspect", Path.GetFullPath(fbx), table], Path.GetDirectoryName(tool.Executable)!, null, ct, tool.SdkDirectory);
	var bones = new List<string>();
	var materials = new List<Material>();
	var vertices = 0;
	var triangles = 0;
	var hairWeightedBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	IReadOnlySet<string>? weightedSourceBones = null;
	foreach (var line in await File.ReadAllLinesAsync(table, ct)) {
	  var cols = line.Split('\t');
	  switch (cols[0]) {
		case "B" when cols.Length == 2 && cols[1].Length > 0:
		  bones.Add(cols[1]);
		  break;
		case "M" when cols.Length >= 2:
		  materials.Add(new Material(cols[1], cols.Length > 2 ? cols[2] : null, cols.Length == 7 ? cols[3..].Select(ParsePosition).ToArray() : null));
		  break;
		case "V" when cols.Length == 2:
		  vertices = int.Parse(cols[1], CultureInfo.InvariantCulture);
		  break;
		case "T" when cols.Length == 2:
		  triangles = int.Parse(cols[1], CultureInfo.InvariantCulture);
		  break;
		case "W" when cols.Length == 2:
		  hairWeightedBones.Add(cols[1]);
		  break;
		case "S" when weightedSourceBones is null:
		  weightedSourceBones = ReadWeightedBones(cols, bones);
		  break;
		case "S":
		  throw new InvalidDataException(StudioStrings.Text("Service.InvalidSkinReport"));
	  }
	}
	if (bones.Count == 0 || vertices <= 0 || triangles <= 0 || materials.Count == 0)
	  throw new InvalidDataException(StudioStrings.Format("Service.MissingMesh", "", fbx));
	if (weightedSourceBones is null)
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidSkinReport"));
	return new FbxInfo(bones, materials, vertices, triangles, weightedSourceBones, hairWeightedBones);
  }

  private sealed record TargetRecipe(JsonObject Job, string Rig, IReadOnlyList<string> Bones);

  private static async Task<TargetRecipe> DumpTargetAsync(string backend, string gamePaks, string targetPackage, string output, IProgress<string>? progress, CancellationToken ct) {
	var package = targetPackage.Trim();
	if (package.StartsWith("/Game/", StringComparison.Ordinal)) {
	  var objectDot = package.LastIndexOf('.');
	  var slash = package.LastIndexOf('/');
	  if (objectDot > slash && string.Equals(package[(slash + 1)..objectDot], package[(objectDot + 1)..], StringComparison.OrdinalIgnoreCase))
		package = package[..objectDot];
	}
	if (!package.StartsWith("/Game/", StringComparison.Ordinal))
	  throw new InvalidDataException(StudioStrings.Text("Service.InvalidTargetPath"));
	await RunAsync(backend, ["--dump-target", Path.GetFullPath(gamePaks), package, output], Path.GetDirectoryName(backend)!, progress, ct);
	var info = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "target-info.json"), ct))!.AsObject();
	var reference = Path.Combine(output, "reference_json.json");
	var skeleton = JsonNode.Parse(await File.ReadAllTextAsync(reference, ct))![0]!["ReferenceSkeleton"]!;
	var names = skeleton["FinalRefBoneInfo"]!.AsArray();
	var poses = skeleton["FinalRefBonePose"]!.AsArray();
	if (names.Count == 0 || names.Count != poses.Count)
	  throw new InvalidDataException(StudioStrings.Text("Service.MissingPose"));
	var bones = new List<string>(names.Count);
	var rows = new List<string>(names.Count);
	for (int i = 0; i < names.Count; i++) {
	  var name = names[i]!["Name"]!.GetValue<string>();
	  var parent = names[i]!["ParentIndex"]!.GetValue<int>();
	  var pose = poses[i]!;
	  if (name.IndexOfAny(['\t', '\r', '\n']) >= 0 || parent < -1 || parent >= i)
		throw new InvalidDataException(StudioStrings.Text("Service.InvalidHierarchy"));
	  bones.Add(name);
	  var values = Coordinates(pose["Translation"], "XYZ").Concat(Coordinates(pose["Rotation"], "XYZW")).Concat(Coordinates(pose["Scale3D"], "XYZ"));
	  rows.Add(name + "\t" + parent.ToString(CultureInfo.InvariantCulture) + "\t" + string.Join('\t', values));
	}
	var rig = Path.Combine(output, "target.tsv");
	await File.WriteAllLinesAsync(rig, rows, new UTF8Encoding(false), ct);
	var job = new JsonObject {
	  ["schema"] = 1,
	  ["target_mesh"] = RequiredString(info, "target_mesh"),
	  ["material_template"] = RequiredString(info, "material_template"),
	  ["material_overrides"] = new JsonArray(),
	  ["reference_bones"] = bones.Count,
	  ["reference_json"] = reference,
	  ["shared_json"] = Path.Combine(output, "shared_json.json"),
	  ["mesh_dump"] = Path.Combine(output, "mesh_dump.json"),
	  ["neck"] = new JsonObject { ["enabled"] = false, ["ratio"] = 1.0, ["root"] = "bip01neck", ["bones"] = new JsonArray("bip01neck1", "bip01head") },
	  ["visibility"] = new JsonObject { ["enabled"] = true, ["hide_paths"] = new JsonArray("/Character/Face/", "/Character/Hair/", "/Character/Acc/04-머리장식/", "/Character/Acc/04-문파_머리장식/", "/Character/Acc/06-머리장식/") },
	  ["hair_physics"] = new JsonObject { ["enabled"] = false },
	  ["compression"] = new JsonObject { ["method"] = "oodle" },
	};
	return new TargetRecipe(job, rig, bones);
  }

  private static IEnumerable<string> Coordinates(JsonNode? vector, string axes) {
	if (vector == null)
	  throw new InvalidDataException(StudioStrings.Text("Service.MissingTransform"));
	foreach (var axis in axes) {
	  var component = vector[axis.ToString()]!.GetValue<double>();
	  if (!double.IsFinite(component))
		throw new InvalidDataException(StudioStrings.Text("Service.InvalidBoneTransform"));
	  yield return component.ToString("R", CultureInfo.InvariantCulture);
	}
  }

  private static string Backend(string bridgeDirectory) {
	var root = Path.GetFullPath(bridgeDirectory);
	var executable = Path.Combine(root, "runtime/mod-package/BnsAssetBridge.exe");
	if (!File.Exists(Path.Combine(root, "pipeline.py")) || !File.Exists(executable))
	  throw new InvalidDataException(StudioStrings.Text("Service.MissingRuntime"));
	return executable;
  }

  private static (JsonObject Job, string Reference) ReadTemplate(string file) {
	var job = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new InvalidDataException(StudioStrings.Text("Service.InvalidTemplate"));
	if (job["schema"]?.GetValue<int>() != 1)
	  throw new InvalidDataException(StudioStrings.Text("Service.TemplateVersion"));
	var reference = ResolveFrom(file, RequiredString(job, "fbx"));
	if (!Path.GetExtension(reference).Equals(".fbx", StringComparison.OrdinalIgnoreCase) || !File.Exists(reference))
	  throw new InvalidDataException(StudioStrings.Format("Service.MissingReference", "", reference));
	return (job, reference);
  }

  private static string RequiredString(JsonObject data, string key) {
	var value = data[key]?.GetValue<string>();
	return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException(StudioStrings.Format("Service.MissingField", "", key));
  }

  private static void RequireSource(string path) {
	if (string.IsNullOrWhiteSpace(path) || Path.GetExtension(path).ToLowerInvariant() is not (".fbx" or ".pmx") || !File.Exists(path))
	  throw new InvalidDataException(StudioStrings.Format("Service.MissingSource", "", path));
  }

  private static FbxTool GetBnsFbxTool(string unrealRoot) {
	var ueVersionPath = Path.Combine(Path.GetFullPath(unrealRoot), "Engine/Build/Build.version");
	var version = JsonNode.Parse(File.ReadAllText(ueVersionPath))?.AsObject() ?? throw new InvalidDataException(StudioStrings.Text("Service.InvalidEngineVersion"));
	if (version["MajorVersion"]?.GetValue<int>() != 4 || version["MinorVersion"]?.GetValue<int>() != 25)
	  throw new InvalidDataException(StudioStrings.Text("Service.SelectEngine"));
	return GetFbxTool(UnrealSdkDirectory(unrealRoot));
  }

  internal static bool IsFbxRuntimeAvailable => File.Exists(Path.Combine(RuntimeDirectory, "FbxBridge", "BnsOutfitFbx.exe"));

  internal static string? FindFbxSdk(string? path) {
	if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
	  return null;
	if (File.Exists(Path.Combine(path, "libfbxsdk.dll")))
	  return path;
	var sdk = UnrealSdkDirectory(path);
	return File.Exists(Path.Combine(sdk, "libfbxsdk.dll")) ? sdk : null;
  }

  private static FbxTool GetFbxTool(string sdkPath) {
	var helper = Path.Combine(RuntimeDirectory, "FbxBridge", "BnsOutfitFbx.exe");
	if (!File.Exists(helper))
	  throw new FileNotFoundException(StudioStrings.Text("Service.MissingFbxBridge"), helper);
	var sdk = FindFbxSdk(sdkPath) ?? throw new FileNotFoundException(StudioStrings.Text("Service.MissingLocalFbxSdk"));
	return new FbxTool(helper, sdk);
  }

  private static void RequireReferenceFbx(string path) {
	if (!File.Exists(path) || !Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
	  throw new InvalidDataException(StudioStrings.Format("Service.MissingReferenceFbx", "", path));
  }

  private static string UnrealSdkDirectory(string unrealRoot) => Path.Combine(unrealRoot, "Engine/Source/ThirdParty/FBX/2018.1.1/lib/vs2015/x64/release");

  private static string ResolveFrom(string document, string relative) => Path.GetFullPath(relative, Path.GetDirectoryName(Path.GetFullPath(document))!);
  private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

  private static string Fingerprint(string path) {
	using var stream = File.OpenRead(path);
	return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }

  private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string directory, IProgress<string>? progress, CancellationToken ct, string? sdkDirectory = null, string? pakWriter = null) {
	ct.ThrowIfCancellationRequested();
	var info = new ProcessStartInfo(executable) {
	  CreateNoWindow = true,
	  UseShellExecute = false,
	  WorkingDirectory = directory,
	  RedirectStandardOutput = true,
	  RedirectStandardError = true,
	  StandardOutputEncoding = Encoding.UTF8,
	  StandardErrorEncoding = Encoding.UTF8,
	};
	if (sdkDirectory != null)
	  info.Environment["PATH"] = sdkDirectory + ";" + Environment.GetEnvironmentVariable("PATH");
	if (pakWriter != null)
	  info.Environment["BNS_STUDIO_PAK_WRITER"] = pakWriter;
	foreach (var argument in arguments)
	  info.ArgumentList.Add(argument);
	using var process = new Process { StartInfo = info };
	string? error = null;
	process.OutputDataReceived += (_, e) => {
	  if (!string.IsNullOrWhiteSpace(e.Data))
		progress?.Report(e.Data);
	};
	process.ErrorDataReceived += (_, e) => {
	  if (!string.IsNullOrWhiteSpace(e.Data)) {
		error = e.Data;
		progress?.Report(e.Data);
	  }
	};
	if (!process.Start())
	  throw new InvalidOperationException(StudioStrings.Format("Service.StartFailed", "", executable));
	process.BeginOutputReadLine();
	process.BeginErrorReadLine();
	using var cancellation = ct.Register(() => {
	  try {
		if (!process.HasExited)
		  process.Kill(entireProcessTree: true);
	  }
	  catch (InvalidOperationException) {
	  }
	});
	await process.WaitForExitAsync();
	ct.ThrowIfCancellationRequested();
	if (process.ExitCode != 0)
	  throw new InvalidOperationException(StudioStrings.Format("Service.ProcessExit", "", Path.GetFileName(executable), process.ExitCode, error));
  }
}
