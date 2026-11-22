using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Media;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace UAssetManager.ModelStudio;

public partial class OutfitStudioWindow : Window
{
    private enum TargetMode
    {
        Fbx,
        Preset,
        Custom,
        Template
    }

    private sealed record TargetChoice(string Name, TargetMode Mode, string Value = "");

    private static IEnumerable<TargetChoice> CreateTargetChoices()
    {
        yield return new TargetChoice(StudioStrings.Text("Window.TargetFbx", "参考 FBX"), TargetMode.Fbx);
        if (FindBridgeDirectory() is null)
            yield break;

        yield return new TargetChoice(StudioStrings.Text("Window.PresetJinFemale", "剑灵 / 60054 洪门道服 / 人女"), TargetMode.Preset, "/Game/Art/Character/Cloth/60054-홍문파도복/JinF/Mesh/60054_JinF");
        yield return new TargetChoice(StudioStrings.Text("Window.PresetLynFemale", "剑灵 / 60054 洪门道服 / 灵女"), TargetMode.Preset, "/Game/Art/Character/Cloth/60054-홍문파도복/LynF/Mesh/60054_LynF");
        yield return new TargetChoice(StudioStrings.Text("Window.TargetCustom", "剑灵 / 自定义资源路径…"), TargetMode.Custom);
        yield return new TargetChoice(StudioStrings.Text("Window.TargetTemplate", "剑灵 / 使用已有制作任务…"), TargetMode.Template);
    }

    private readonly string? _configDirectory;
    private OutfitStudioPreferences? _savedPreferences;
    private bool _initialized;
    private bool _applyingPreferences;
    private CancellationTokenSource? _operation;
    private string? _artifact;
    private bool _inspected;
    private bool UsesFbxTarget => TargetPicker.SelectedItem is TargetChoice { Mode: TargetMode.Fbx };
    private MappingRow[] _missingMappingRows = [];
    private OutfitStudioMeshPreview? _sourcePreview;
    private string? _optionsTemplate;
    private bool _closed;
    private Point _orbitStart;
    private Point3D _orbitCenter;
    private double _orbitRadius = 1;
    private double _orbitYaw;
    private double _orbitPitch;
    private double _orbitZoom;

    public ObservableCollection<string> TargetBones { get; } = new();
    public ObservableCollection<MappingRow> MappingRows { get; } = new();
    public ObservableCollection<MaterialRow> MaterialRows { get; } = new();

    public OutfitStudioWindow() : this(null)
    {
    }

    internal OutfitStudioWindow(string? configDirectory)
    {
        _configDirectory = configDirectory;
        InitializeComponent();
        Title = StudioStrings.Format("Window.Title", "模型制作工具 v{0}", ModelStudioPlugin.ReleaseVersion);
        PreviewAxes.AxisSelected += SnapPreviewAxis;
        MappingIssueFilter.TextChanged += MappingIssueFilter_Changed;
        DataContext = this;
        TargetPicker.ItemsSource = CreateTargetChoices().ToArray();
        TargetPicker.SelectedIndex = 0;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        _savedPreferences = OutfitStudioPreferences.Load(_configDirectory);
        ApplyPreferences(_savedPreferences);
        await DiscoverEnvironmentAsync(true);
    }

    private async void DetectEnvironment_Click(object sender, RoutedEventArgs e)
    {
        await DiscoverEnvironmentAsync(false);
    }

    private async Task DiscoverEnvironmentAsync(bool firstLoad)
    {
        var current = ReadPreferences();
        bool includeGame = !UsesFbxTarget;
        var message = includeGame ? StudioStrings.Text("Window.DetectEnvironment", "正在查找游戏和 UE4.25…") : StudioStrings.Text("Window.DetectFbxSdk", "正在查找 FBX SDK…");
        await RunOperationAsync(message, async token =>
        {
            var detected = await Task.Run(() =>
            {
                var toDetect = current;
                if (!firstLoad)
                {
                    if (includeGame && !OutfitStudioPreferences.IsGamePaks(current.GamePaks))
                        toDetect = toDetect with { GamePaks = string.Empty };
                    if (includeGame && !OutfitStudioPreferences.IsUnrealRoot(current.UnrealRoot))
                        toDetect = toDetect with { UnrealRoot = string.Empty };
                    if (!includeGame && OutfitStudioService.FindFbxSdk(current.GetFbxSdkPath()) is null)
                        toDetect = toDetect with { FbxSdkPath = string.Empty };
                }
                var found = OutfitStudioPreferences.Discover(toDetect, fillBlankOnly: true, includeGame);
                return found with
                {
                    GamePaks = found.GamePaks.Length == 0 ? current.GamePaks : found.GamePaks,
                    UnrealRoot = found.UnrealRoot.Length == 0 ? current.UnrealRoot : found.UnrealRoot,
                    FbxSdkPath = found.FbxSdkPath.Length == 0 ? current.FbxSdkPath : found.FbxSdkPath
                };
            }, token);
            token.ThrowIfCancellationRequested();
            ApplyPreferences(detected);
            var status = GetEnvironmentStatus();
            if (status is not null)
                EnvironmentSettings.IsExpanded = true;
            StatusText.Text = status ?? StudioStrings.Text("Window.LoadTargetHint", "选择源模型和目标服装后，点击「读取目标骨架」。");
        });
    }

    private OutfitStudioPreferences ReadPreferences()
    {
        return new OutfitStudioPreferences(GamePaksPath.Text.Trim(), UnrealPath.Text.Trim(), OutputPath.Text.Trim(), FbxSdkPath.Text.Trim());
    }

    private void ApplyPreferences(OutfitStudioPreferences preferences)
    {
        var previous = ReadPreferences();
        _applyingPreferences = true;
        try
        {
            GamePaksPath.Text = preferences.GamePaks;
            UnrealPath.Text = preferences.UnrealRoot;
            OutputPath.Text = preferences.OutputDirectory;
            FbxSdkPath.Text = preferences.FbxSdkPath;
        }
        finally
        {
            _applyingPreferences = false;
        }
        if (!string.Equals(previous.GamePaks, preferences.GamePaks, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.UnrealRoot, preferences.UnrealRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.FbxSdkPath, preferences.FbxSdkPath, StringComparison.OrdinalIgnoreCase))
        {
            ResetInspection();
        }
        RefreshEnvironmentStatus();
    }

    private void EnvironmentInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || !_initialized || _applyingPreferences)
            return;

        if (!ReferenceEquals(sender, OutputPath))
        {
            ResetInspection();
        }
        EnvironmentStatus.Text = StudioStrings.Text("Window.EnvironmentChanged", "路径已更改，保存或检查时会验证。");
        SetOperationState(_operation is not null);
    }

    private void RefreshEnvironmentStatus()
    {
        var ready = UsesFbxTarget ? StudioStrings.Text("Window.FbxEnvironmentReady", "FBX 工具已就绪。") : StudioStrings.Text("Window.EnvironmentReady", "游戏和 UE4.25 已就绪。");
        EnvironmentStatus.Text = GetEnvironmentStatus() ?? ready;
    }

    private string? GetEnvironmentStatus()
    {
        var preferences = ReadPreferences();
        if (UsesFbxTarget)
        {
            if (!OutfitStudioService.IsFbxRuntimeAvailable)
                return StudioStrings.Text("Service.MissingFbxBridge");

            return OutfitStudioService.FindFbxSdk(preferences.GetFbxSdkPath()) is null ? StudioStrings.Text("Service.MissingLocalFbxSdk") : null;
        }

        if (FindBridgeDirectory() is null)
            return StudioStrings.Text("Window.EnvironmentMissingBridge", "制作组件不完整，请重新安装插件。");

        bool game = OutfitStudioPreferences.IsGamePaks(preferences.GamePaks);
        bool unreal = OutfitStudioPreferences.IsUnrealRoot(preferences.UnrealRoot);
        if (!game && !unreal)
            return StudioStrings.Text("Window.EnvironmentMissingBoth", "请设置游戏 Paks 和 UE4.25。");

        if (!game)
            return StudioStrings.Text("Window.EnvironmentMissingGame", "请设置游戏 Paks 目录。");

        return unreal ? null : StudioStrings.Text("Window.EnvironmentMissingUnreal", "请设置 UE4.25 安装目录。");
    }

    private static string? FindBridgeDirectory()
    {
        var profile = Environment.GetEnvironmentVariable("BNS_STUDIO_PROFILE_DIR");
        foreach (var root in new[] { profile, OutfitStudioService.RuntimeDirectory })
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                continue;

            var bridge = Path.Combine(root, "bns_skeleton_bridge");
            if (File.Exists(Path.Combine(bridge, "pipeline.py")) && File.Exists(Path.Combine(bridge, "runtime", "mod-package", "BnsAssetBridge.exe"))
                && File.Exists(Path.Combine(root, "PakBridge", "BnsOutfitPak.exe")))
            {
                return bridge;
            }
        }
        return null;
    }

    private void SaveEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var preferences = ReadPreferences();
        if (!TryValidateEnvironment(preferences, requireGame: true, requireOutput: true))
            return;

        StatusText.Text = SavePreferences(preferences) ? StudioStrings.Text("Window.EnvironmentSaved", "已保存制作环境，下次打开会自动使用。") : StudioStrings.Text("Window.EnvironmentSaveFailed", "本次可继续使用，设置未能保存。");
        RefreshEnvironmentStatus();
    }

    private bool SavePreferences(OutfitStudioPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(_configDirectory))
            return false;

        if (preferences == _savedPreferences)
            return true;

        try
        {
            preferences.Save(_configDirectory);
            _savedPreferences = preferences;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryValidateEnvironment(OutfitStudioPreferences preferences, bool requireGame, bool requireOutput)
    {
        if (UsesFbxTarget)
        {
            if (!OutfitStudioService.IsFbxRuntimeAvailable)
                return ShowEnvironmentError(FbxSdkPath, StudioStrings.Text("Service.MissingFbxBridge"));
            if (OutfitStudioService.FindFbxSdk(preferences.GetFbxSdkPath()) is null)
                return ShowEnvironmentError(FbxSdkPath, StudioStrings.Text("Service.MissingLocalFbxSdk"));
        }
        else
        {
            if (requireGame && !OutfitStudioPreferences.IsGamePaks(preferences.GamePaks))
                return ShowEnvironmentError(GamePaksPath, StudioStrings.Text("Window.EnvironmentInvalidGame", "请选择包含原版 .pak 文件的游戏 Paks 目录。"));
            if (!OutfitStudioPreferences.IsUnrealRoot(preferences.UnrealRoot))
                return ShowEnvironmentError(UnrealPath, StudioStrings.Text("Window.EnvironmentInvalidUnreal", "请选择包含编辑器和 FBX SDK 的 UE4.25 安装目录。"));
        }

        if (requireOutput && !IsOutputDirectory(preferences, protectGamePaks: !UsesFbxTarget))
            return ShowEnvironmentError(OutputPath, UsesFbxTarget ? StudioStrings.Text("Window.InvalidFbxOutput", "请选择绝对输出目录。") : StudioStrings.Text("Window.EnvironmentInvalidOutput", "请选择游戏 Paks 目录以外的绝对输出路径。"));

        return true;
    }

    private bool ShowEnvironmentError(TextBox input, string message)
    {
        EnvironmentSettings.IsExpanded = true;
        RefreshEnvironmentStatus();
        StatusText.Text = message;
        input.BringIntoView();
        input.Focus();
        return false;
    }

    private static bool IsOutputDirectory(OutfitStudioPreferences preferences, bool protectGamePaks)
    {
        try
        {
            if (!Path.IsPathFullyQualified(preferences.OutputDirectory) || File.Exists(preferences.OutputDirectory))
                return false;

            if (!protectGamePaks)
                return true;

            var game = Path.GetFullPath(preferences.GamePaks);
            var output = Path.GetFullPath(preferences.OutputDirectory);
            var relative = Path.GetRelativePath(game, output);
            return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private bool RememberEnvironment(OutfitStudioPreferences preferences)
    {
        if ((!UsesFbxTarget && !OutfitStudioPreferences.IsGamePaks(preferences.GamePaks)) || !IsOutputDirectory(preferences, protectGamePaks: !UsesFbxTarget))
            return true;

        return SavePreferences(preferences);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "Source":
                var source = PickFile(SourcePath.Text, StudioStrings.Text("Window.SourceFileFilter", "蒙皮模型 (*.fbx;*.pmx)|*.fbx;*.pmx"));
                if (source is not null)
                    await LoadSourceAsync(source);
                break;
            case "Template":
                SelectFile(TemplatePath, StudioStrings.Text("Window.JobFileFilter", "制作任务 (*.json)|*.json"));
                break;
            case "ReferenceFbx":
                SelectFile(ReferenceFbxPath, StudioStrings.Text("Window.ReferenceFbxFilter", "参考 FBX (*.fbx)|*.fbx"));
                break;
            case "FbxSdk":
                SelectDirectory(FbxSdkPath);
                break;
            case "GamePaks":
                SelectDirectory(GamePaksPath);
                break;
            case "Unreal":
                SelectDirectory(UnrealPath);
                break;
            case "Output":
                SelectDirectory(OutputPath);
                break;
        }
    }

    private void SelectFile(TextBox box, string filter)
    {
        var path = PickFile(box.Text, filter);
        if (path is not null)
            box.Text = path;
    }

    private string? PickFile(string currentPath, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (File.Exists(currentPath))
            dialog.InitialDirectory = Path.GetDirectoryName(currentPath);

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task LoadSourceAsync(string source)
    {
        var preferences = ReadPreferences();
        var hadModel = _sourcePreview is not null;
        await RunOperationAsync(StudioStrings.Text("Window.LoadingModel", "正在检查模型并加载三维预览…"), async token =>
        {
            try
            {
                var sdkPath = UsesFbxTarget ? preferences.GetFbxSdkPath() : preferences.UnrealRoot;
                var preview = await OutfitStudioService.LoadPreviewAsync(source, sdkPath, token);
                var model = await Task.Run(() => OutfitStudioPreviewRenderer.Create(preview, null, token), token);
                token.ThrowIfCancellationRequested();

                SourcePath.Text = source;
                _sourcePreview = preview;
                ResetInspection();
                MaterialRows.Clear();
                for (var slot = 0; slot < preview.Materials.Count; slot++)
                {
                    var material = preview.Materials[slot];
                    MaterialRows.Add(new MaterialRow(slot, material.Name, material.Texture));
                }
                ModelSummary.Text = StudioStrings.Format("Window.ModelSummary", "顶点 {0:N0} 个，三角形 {1:N0} 个，源骨骼 {2:N0} 个，材质槽 {3:N0} 个。", preview.Vertices.Count, preview.Faces.Count, preview.Bones.Count, preview.Materials.Count);
                ApplyPreview(model, frameModel: true);
                WorkTabs.SelectedItem = PreviewTab;
                StatusText.Text = StudioStrings.Text("Window.SourceLoaded", "源模型已加载。选择目标服装后，点击「读取目标骨架」。");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                var reason = hadModel ? StudioStrings.Text("Window.SourceLoadKept", "已保留上一个可用模型和预览。") : StudioStrings.Text("Window.SourceLoadUnchanged", "没有更改当前模型。");
                StatusText.Text = StudioStrings.Format("Window.SourceLoadFailed", "源模型加载失败。{0}", reason);
                MessageBox.Show(this, $"{Path.GetFileName(source)}\n\n{error.Message}\n\n{reason}", StudioStrings.Text("Window.SourceLoadFailedTitle", "无法加载模型"), MessageBoxButton.OK, MessageBoxImage.Warning);
                if (GetEnvironmentStatus() is not null)
                    EnvironmentSettings.IsExpanded = true;
            }
        });
    }

    private void ApplyPreview(Model3DGroup model, bool frameModel)
    {
        PreviewModel.Children.Clear();
        PreviewModel.Children.Add(model);
        if (TryReadTransform(out var scale, out var offset))
            UpdatePreviewTransform(scale, offset);
        if (frameModel)
            FrameNewModel();
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewAxes.Visibility = Visibility.Visible;
    }

    private void SelectDirectory(TextBox box)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true)
            box.Text = dialog.FolderName;
    }

    private void InspectionInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        ResetInspection();

        StatusText.Text = StudioStrings.Text("Window.LoadTargetHint", "选择源模型和目标服装后，点击「读取目标骨架」。");
        SetOperationState(_operation is not null);
    }

    private void TargetPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetPicker.SelectedItem is not TargetChoice choice)
            return;

        PackageInput.Visibility = choice.Mode == TargetMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        TemplateInput.Visibility = choice.Mode == TargetMode.Template ? Visibility.Visible : Visibility.Collapsed;
        bool useFbx = choice.Mode == TargetMode.Fbx;
        FbxTargetInput.Visibility = useFbx ? Visibility.Visible : Visibility.Collapsed;
        FbxEnvironmentSettings.Visibility = useFbx ? Visibility.Visible : Visibility.Collapsed;
        BnsEnvironmentSettings.Visibility = useFbx ? Visibility.Collapsed : Visibility.Visible;
        BnsProductionSettings.Visibility = useFbx ? Visibility.Collapsed : Visibility.Visible;
        BuildButton.Content = useFbx ? StudioStrings.Text("Window.ExportFbx", "导出 FBX") : StudioStrings.Text("Window.BuildPak", "制作 PAK");
        if (!IsLoaded)
            return;

        ResetInspection();
        RefreshEnvironmentStatus();
        StatusText.Text = StudioStrings.Text("Window.TargetSelectedHint", "选好目标后，点击「读取目标骨架」。");
        SetOperationState(_operation is not null);
    }

    private bool TryGetTarget(out string target, out string template, out string referenceFbx)
    {
        target = string.Empty;
        template = string.Empty;
        referenceFbx = string.Empty;
        if (TargetPicker.SelectedItem is not TargetChoice choice)
            return false;

        switch (choice.Mode)
        {
            case TargetMode.Fbx:
                referenceFbx = ReferenceFbxPath.Text.Trim();
                break;
            case TargetMode.Preset:
                target = choice.Value;
                break;
            case TargetMode.Custom:
                target = TargetPath.Text.Trim();
                break;
            case TargetMode.Template:
                template = TemplatePath.Text.Trim();
                break;
        }
        return target.Length > 0 || template.Length > 0 || referenceFbx.Length > 0;
    }

    private void ResetInspection()
    {
        _inspected = false;
        foreach (var row in MappingRows)
            row.PropertyChanged -= MappingTarget_Changed;

        MappingRows.Clear();
        TargetBones.Clear();
        RefreshMappingPreflight();
        MappingIssueFilter.Clear();
        InspectionSummary.Text = StudioStrings.Text("Window.MappingHint", "读取目标骨架后，可在这里设置骨骼映射。");
    }

    private bool TryReadSource(out string source)
    {
        source = SourcePath.Text.Trim();
        if (_sourcePreview is not null && File.Exists(source))
            return true;

        StatusText.Text = StudioStrings.Text("Window.SourceInvalid", "请选择可读取的 FBX 或 PMX 模型。");
        SourcePath.Focus();
        return false;
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSource(out _))
            return;

        if (!TryGetTarget(out var target, out var template, out var referenceFbx))
        {
            StatusText.Text = StudioStrings.Text("Window.TargetMissing", "请选择目标服装，或填写自定义路径。");
            return;
        }

        bool useFbx = UsesFbxTarget;
        bool usePackage = !useFbx && template.Length == 0;
        if (usePackage && !target.StartsWith("/Game/", StringComparison.Ordinal))
        {
            StatusText.Text = StudioStrings.Text("Window.TargetInvalid", "目标服装路径须以 /Game/ 开头。");
            TargetPath.Focus();
            return;
        }
        if (!useFbx && !usePackage && !File.Exists(template))
        {
            StatusText.Text = StudioStrings.Text("Window.TemplateMissing", "请选择已有的制作任务文件。");
            TemplatePath.Focus();
            return;
        }

        var preferences = ReadPreferences();
        if (!TryValidateEnvironment(preferences, requireGame: usePackage, requireOutput: false))
            return;

        var bridge = FindBridgeDirectory();
        if (usePackage && bridge is null)
        {
            StatusText.Text = StudioStrings.Text("Window.EnvironmentMissingBridge", "制作组件不完整，请重新安装插件。");
            return;
        }

        ResetInspection();
        await RunOperationAsync(StudioStrings.Text("Window.LoadingTargetRig", "正在读取目标骨架…"), async cancellationToken =>
        {
            var inspection = useFbx
                ? await OutfitStudioService.InspectFbxAsync(_sourcePreview!, referenceFbx, preferences.GetFbxSdkPath(), cancellationToken)
                : usePackage
                    ? await OutfitStudioService.InspectTargetAsync(_sourcePreview!, target, preferences.GamePaks, bridge!, cancellationToken)
                    : await OutfitStudioService.InspectAsync(_sourcePreview!, template, preferences.UnrealRoot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyInspection(inspection, template);
            bool saved = RememberEnvironment(preferences);
            StatusText.Text = saved ? StudioStrings.Text("Window.TargetRigLoaded", "目标骨架已读取。请检查骨骼映射。") : StudioStrings.Text("Window.TargetRigLoadedSaveFailed", "目标骨架已读取。制作环境未能保存。");
            WorkTabs.SelectedItem = MappingTab;
            RefreshEnvironmentStatus();
        });
    }

    private void ApplyInspection(OutfitStudioInspection inspection, string template)
    {
        using var document = template.Length == 0 ? null : JsonDocument.Parse(File.ReadAllText(template));
        var templateMapping = document is not null && document.RootElement.TryGetProperty("mapping", out var entry) && entry.ValueKind == JsonValueKind.Object
            ? entry
            : default;

        var sourceNames = inspection.SourceBones.Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var targetNames = inspection.TargetBones.Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var targetSet = targetNames.ToHashSet(StringComparer.Ordinal);
        TargetBones.Add(string.Empty);
        foreach (var name in targetNames)
            TargetBones.Add(name);

        foreach (var name in sourceNames)
        {
            var suggested = targetSet.Contains(name) ? name : string.Empty;
            if (templateMapping.ValueKind == JsonValueKind.Object && templateMapping.TryGetProperty(name, out var mapped) && mapped.ValueKind == JsonValueKind.String)
            {
                var target = mapped.GetString();
                if (target is not null && targetSet.Contains(target))
                    suggested = target;
            }
            var row = new MappingRow(name, suggested, inspection.WeightedSourceBones.Contains(name));
            row.PropertyChanged += MappingTarget_Changed;
            MappingRows.Add(row);
        }

        if (!UsesFbxTarget && !string.Equals(_optionsTemplate, template, StringComparison.OrdinalIgnoreCase))
        {
            ProductionOptions.LoadTemplate(template);
            _optionsTemplate = template;
        }
        _inspected = true;
        InspectionSummary.Text = StudioStrings.Format("Window.MappingSummary", "源骨骼 {0:N0} 个，目标骨骼 {1:N0} 个。双击编辑目标骨骼或权重倍率。", sourceNames.Length, targetNames.Length);
        RefreshMappingPreflight();
    }

    private void MappingTarget_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingRow.Target))
            RefreshMappingPreflight();
    }

    private void RefreshMappingPreflight()
    {
        _missingMappingRows = MappingRows.Where(row => row.IsWeighted && string.IsNullOrEmpty(row.Target)).ToArray();
        MappingPreflight.Visibility = _inspected ? Visibility.Visible : Visibility.Collapsed;
        MappingPreflightSummary.Text = _missingMappingRows.Length == 0
            ? StudioStrings.Text("Window.MappingPreflightComplete", "所有有权重的源骨骼均已映射。")
            : StudioStrings.Format("Window.MappingPreflightMissing", "有 {0:N0} 个有权重的源骨骼未映射。点击骨名定位。", _missingMappingRows.Length);
        MappingIssues.Visibility = _missingMappingRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshMappingIssues();
        UpdateBuildAvailability(_operation is not null);
    }

    private void MappingIssueFilter_Changed(object sender, TextChangedEventArgs e)
    {
        RefreshMappingIssues();
    }

    private void RefreshMappingIssues()
    {
        var filter = MappingIssueFilter.Text.Trim();
        var rows = filter.Length == 0
            ? _missingMappingRows
            : _missingMappingRows.Where(row => row.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        MappingIssueList.ItemsSource = rows;
        MappingIssueList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MappingIssueNoResults.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JumpToMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MappingRow row } && CommitMappingEdit())
            SelectMappingTarget(row);
    }

    private void SelectMappingTarget(MappingRow row)
    {
        WorkTabs.SelectedItem = MappingTab;
        MappingGrid.SelectedItem = row;
        MappingGrid.ScrollIntoView(row, MappingTargetColumn);
        MappingGrid.Focus();
        MappingGrid.CurrentCell = new DataGridCellInfo(row, MappingTargetColumn);
    }

    private void TransformInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || PreviewModel.Children.Count == 0)
            return;

        if (TryReadTransform(out var scale, out var offset))
        {
            UpdatePreviewTransform(scale, offset);
            FitCameraToModel();
        }
    }

    private bool TryReadTransform(out double scale, out Vector3D offset)
    {
        offset = default;
        if (!TryReadNumber(ScaleInput, out scale) || scale <= 0)
            return false;

        if (!TryReadNumber(OffsetXInput, out var x) || !TryReadNumber(OffsetYInput, out var y) || !TryReadNumber(OffsetZInput, out var z))
            return false;

        offset = new Vector3D(x, y, z);
        return true;
    }

    private static bool TryReadNumber(TextBox box, out double value)
    {
        return double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    }

    private void UpdatePreviewTransform(double scale, Vector3D offset)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
        transform.Children.Add(new TranslateTransform3D(offset.X, offset.Z, -offset.Y));
        PreviewModel.Transform = transform;
    }

    private void PreviewSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewModel.Children.Count == 0)
            return;

        _orbitStart = e.GetPosition(PreviewSurface);
        e.Handled = PreviewSurface.CaptureMouse();
    }

    private void PreviewSurface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!PreviewSurface.IsMouseCaptured)
            return;

        PreviewSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!PreviewSurface.IsMouseCaptured)
            return;

        var point = e.GetPosition(PreviewSurface);
        var movement = point - _orbitStart;
        _orbitStart = point;
        _orbitYaw -= movement.X * 0.012;
        _orbitPitch = Math.Clamp(_orbitPitch + movement.Y * 0.012, -Math.PI / 2, Math.PI / 2);
        UpdateCamera();
    }

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewModel.Children.Count == 0)
            return;

        _orbitZoom = Math.Clamp(_orbitZoom * Math.Pow(0.84, e.Delta / 120.0), 0.23, 5);
        UpdateCamera();
        e.Handled = true;
    }

    private void SnapPreviewAxis(Vector3D axis)
    {
        if (PreviewModel.Children.Count == 0)
            return;

        _orbitPitch = Math.Asin(axis.Y);
        _orbitYaw = axis.Y switch
        {
            > 0 => Math.PI,
            < 0 => 0,
            _ => Math.Atan2(-axis.Z, axis.X),
        };
        UpdateCamera();
    }

    private void FrameNewModel()
    {
        _orbitYaw = -Math.PI * 0.18;
        _orbitPitch = Math.PI * 0.045;
        _orbitZoom = 1;
        FitCameraToModel();
    }

    private void FitCameraToModel()
    {
        var bounds = PreviewModel.Bounds;
        if (bounds.IsEmpty)
            return;

        _orbitCenter = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        _orbitRadius = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var horizontal = Math.Cos(_orbitPitch);
        var height = Math.Sin(_orbitPitch);
        var yawCos = Math.Cos(_orbitYaw);
        var yawSin = Math.Sin(_orbitYaw);
        var direction = new Vector3D(horizontal * yawCos, height, -horizontal * yawSin);
        var up = new Vector3D(-height * yawCos, horizontal, height * yawSin);
        var aspect = ModelViewport.ActualHeight > 1 ? Math.Max(1, ModelViewport.ActualWidth / ModelViewport.ActualHeight) : 1;
        var horizontalHalfAngle = PreviewCamera.FieldOfView * Math.PI / 360;
        var fitAngle = Math.Atan(Math.Tan(horizontalHalfAngle) / aspect);
        var distance = _orbitRadius * 1.12 * _orbitZoom / Math.Sin(fitAngle);
        PreviewCamera.Position = _orbitCenter + direction * distance;
        PreviewCamera.LookDirection = direction * -distance;
        PreviewCamera.UpDirection = up;
        PreviewAxes.UpdateOrientation(PreviewCamera.LookDirection, up);
        PreviewCamera.NearPlaneDistance = _orbitRadius * 0.003;
        PreviewCamera.FarPlaneDistance = distance + _orbitRadius * 3;
    }

    private void ModelViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewModel?.Children.Count > 0)
            UpdateCamera();
    }

    private void MaterialSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshTextureActions();
    }

    private void RefreshTextureActions()
    {
        if (TextureButton is null || ResetTextureButton is null || MaterialGrid is null)
            return;

        var row = MaterialGrid.SelectedItem as MaterialRow;
        TextureButton.IsEnabled = _operation is null && row is not null;
        ResetTextureButton.IsEnabled = _operation is null && row?.Texture.Length > 0;
    }

    private async void Texture_Click(object sender, RoutedEventArgs e)
    {
        if (MaterialGrid.SelectedItem is not MaterialRow row)
            return;

        var path = PickFile(row.TextureDisplay, StudioStrings.Text("Window.TextureFileFilter", "漫反射贴图 (*.png;*.jpg;*.jpeg;*.bmp;*.tga)|*.png;*.jpg;*.jpeg;*.bmp;*.tga"));
        if (path is not null)
            await UpdateTextureAsync(row, path);
    }

    private async void ResetTexture_Click(object sender, RoutedEventArgs e)
    {
        if (MaterialGrid.SelectedItem is MaterialRow row)
            await UpdateTextureAsync(row, string.Empty);
    }

    private async Task UpdateTextureAsync(MaterialRow row, string texture)
    {
        if (_sourcePreview is not { } preview)
            return;

        var overrides = MaterialRows.Where(m => m != row && m.Texture.Length > 0).ToDictionary(m => m.Slot, m => m.Texture);
        if (texture.Length > 0)
            overrides.Add(row.Slot, texture);

        await RunOperationAsync(StudioStrings.Text("Window.UpdatingTexture", "正在更新预览贴图…"), async token =>
        {
            var model = await Task.Run(() =>
            {
                if (texture.Length > 0)
                    OutfitStudioPreviewRenderer.ValidateOverride(texture);
                return OutfitStudioPreviewRenderer.Create(preview, overrides, token);
            }, token);
            token.ThrowIfCancellationRequested();
            row.Texture = texture;
            ApplyPreview(model, frameModel: false);
            WorkTabs.SelectedItem = PreviewTab;
            StatusText.Text = texture.Length > 0 ? StudioStrings.Text("Window.TextureUpdated", "已更新贴图。") : StudioStrings.Text("Window.TextureRestored", "已还原源贴图。");
        });
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!_inspected)
            return;

        if (!CommitMappingEdit()
            || !MaterialGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !MaterialGrid.CommitEdit(DataGridEditingUnit.Row, true))
            return;

        if (_missingMappingRows.Length > 0)
        {
            StatusText.Text = StudioStrings.Text("Window.BuildNeedsMappings", "请先为所有有权重的源骨骼选择目标。");
            SelectMappingTarget(_missingMappingRows[0]);
            return;
        }

        if (!TryMakeRequest(out var request, out var fbxRequest))
            return;

        bool saved = SavePreferences(ReadPreferences());
        _artifact = null;
        ProgressLog.Clear();
        if (!saved)
            ProgressLog.AppendText(StudioStrings.Text("Window.BuildSettingsSaveFailed", "设置未能保存，本次制作可以继续。") + Environment.NewLine);
        WorkTabs.SelectedItem = ProgressTab;
        var message = fbxRequest is not null ? StudioStrings.Text("Window.ExportingFbx", "正在导出 FBX…") : StudioStrings.Text("Window.BuildingPak", "正在制作 PAK…");
        await RunOperationAsync(message, async cancellationToken =>
        {
            var progress = new Progress<string>(message =>
            {
                if (_closed)
                    return;

                ProgressLog.AppendText(message + Environment.NewLine);
                ProgressLog.ScrollToEnd();
            });
            var artifact = fbxRequest is not null
                ? await OutfitStudioService.ExportFbxAsync(fbxRequest, progress, cancellationToken)
                : await OutfitStudioService.BuildAsync(request!, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _artifact = artifact;
            StatusText.Text = StudioStrings.Format("Window.BuiltPak", "已生成 {0}", Path.GetFileName(_artifact));
        });
    }

    private bool CommitMappingEdit()
    {
        return MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true) && MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void ImportMapping_Click(object sender, RoutedEventArgs e)
    {
        if (!_inspected || !CommitMappingEdit())
            return;

        var dialog = new OpenFileDialog { Filter = StudioStrings.Text("Window.MappingFileFilter", "骨骼映射 TSV (*.tsv)|*.tsv"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var sources = MappingRows.Select(row => row.Source).ToHashSet(StringComparer.Ordinal);
            var targets = TargetBones.Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            var entries = new Dictionary<string, (string Target, double Weight)>(StringComparer.Ordinal);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(dialog.FileName))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var columns = line.Split('\t');
                if (columns.Length is < 2 or > 3 || !sources.Contains(columns[0]) || !targets.Contains(columns[1]))
                    throw new InvalidDataException(StudioStrings.Format("Window.MappingInvalidRow", "第 {0} 行应包含当前源骨骼、目标骨骼及可选权重倍率，以制表符分隔。", lineNumber));

                var weight = 1.0;
                if (columns.Length == 3 && (!double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out weight) || !double.IsFinite(weight) || weight <= 0))
                    throw new InvalidDataException(StudioStrings.Format("Window.MappingInvalidWeight", "第 {0} 行的权重倍率必须大于零。", lineNumber));

                if (!entries.TryAdd(columns[0], (columns[1], weight)))
                    throw new InvalidDataException(StudioStrings.Format("Window.MappingDuplicateBone", "第 {0} 行的源骨骼已出现过。", lineNumber));
            }
            if (entries.Count == 0)
                throw new InvalidDataException(StudioStrings.Text("Window.MappingEmpty", "映射文件没有有效条目。"));

            for (var index = 0; index < MappingRows.Count; index++)
            {
                var previous = MappingRows[index];
                var row = new MappingRow(previous.Source, string.Empty, previous.IsWeighted);
                if (entries.TryGetValue(row.Source, out var entry))
                {
                    row.Target = entry.Target;
                    row.WeightScale = entry.Weight.ToString("R", CultureInfo.CurrentCulture);
                }
                previous.PropertyChanged -= MappingTarget_Changed;
                row.PropertyChanged += MappingTarget_Changed;
                MappingRows[index] = row;
            }
            RefreshMappingPreflight();
            StatusText.Text = StudioStrings.Format("Window.MappingImported", "已导入 {0:N0} 条骨骼映射。", entries.Count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = error.Message;
        }
    }

    private void ExportMapping_Click(object sender, RoutedEventArgs e)
    {
        if (!_inspected || !CommitMappingEdit() || !TryReadMapping(out var mapping, out var weights))
            return;

        var dialog = new SaveFileDialog { Filter = StudioStrings.Text("Window.MappingFileFilter", "骨骼映射 TSV (*.tsv)|*.tsv"), DefaultExt = ".tsv", AddExtension = true, FileName = "mapping.tsv" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var entries = mapping.Select(pair => $"{pair.Key}\t{pair.Value}\t{weights.GetValueOrDefault(pair.Key, 1).ToString("R", CultureInfo.InvariantCulture)}");
            File.WriteAllLines(dialog.FileName, entries);
            StatusText.Text = StudioStrings.Format("Window.MappingExported", "已导出 {0:N0} 条骨骼映射。", mapping.Count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = error.Message;
        }
    }

    private bool TryMakeRequest(out OutfitStudioRequest? request, out OutfitStudioFbxRequest? fbxRequest)
    {
        request = null;
        fbxRequest = null;
        var preferences = ReadPreferences();
        if (!TryValidateEnvironment(preferences, requireGame: true, requireOutput: true))
            return false;

        if (!TryReadSource(out var source) || !TryGetTarget(out var target, out var template, out var referenceFbx))
            return false;

        if (!TryReadMapping(out var mapping, out var weightScales))
            return false;

        if (!TryReadTransform(out var scale, out var offset))
        {
            AdvancedSettings.IsExpanded = true;
            StatusText.Text = StudioStrings.Text("Window.InvalidTransform", "请输入正数倍率和有效的 XYZ 偏移。");
            return false;
        }
        var textures = new Dictionary<int, string>();
        foreach (var row in MaterialRows)
        {
            var texture = row.Texture.Trim();
            if (texture.Length == 0)
                continue;

            if (!File.Exists(texture))
            {
                StatusText.Text = StudioStrings.Format("Window.TextureInvalid", "请选择材质槽 {0} 对应的贴图文件。", row.Slot);
                MaterialGrid.SelectedItem = row;
                MaterialGrid.ScrollIntoView(row);
                return false;
            }
            textures.Add(row.Slot, texture);
        }

        if (UsesFbxTarget)
        {
            fbxRequest = new OutfitStudioFbxRequest(source, referenceFbx, preferences.GetFbxSdkPath(), preferences.OutputDirectory, mapping, textures, weightScales, scale, offset.X, offset.Y, offset.Z);
            return true;
        }

        var bridge = FindBridgeDirectory();
        if (bridge is null)
        {
            StatusText.Text = StudioStrings.Text("Window.EnvironmentMissingBridge", "制作组件不完整，请重新安装插件。");
            return false;
        }

        var id = OutfitId.Text.Trim();
        if (id.Length != 0 && (id[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z') || id.Length > 48
            || id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')))
        {
            AdvancedSettings.IsExpanded = true;
            StatusText.Text = StudioStrings.Text("Window.InvalidBuildName", "制作名称须以英文字母开头，最多 48 位，只能使用英文字母、数字和下划线。");
            OutfitId.Focus();
            return false;
        }
        if (id.Length == 0 && target.Length > 0)
            id = MakeOutfitId(source, target);

        OutfitStudioProductionSettings production;
        try
        {
            production = ProductionOptions.Read(TargetBones);
        }
        catch (InvalidDataException error)
        {
            AdvancedSettings.IsExpanded = true;
            ProductionOptions.BringIntoView();
            StatusText.Text = error.Message;
            return false;
        }

        request = new OutfitStudioRequest(source, template, preferences.GamePaks, preferences.UnrealRoot, preferences.OutputDirectory, mapping, bridge, textures.Count == 0 ? null : textures, id.Length == 0 ? null : id, weightScales.Count == 0 ? null : weightScales, target.Length == 0 ? null : target, scale, offset.X, offset.Y, offset.Z, production);
        return true;
    }

    private static string MakeOutfitId(string source, string target)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var safeName = new string(name.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').Take(28).ToArray()).Trim('_');
        var package = target.Trim();
        int slash = package.LastIndexOf('/');
        int dot = package.LastIndexOf('.');
        if (dot > slash && string.Equals(package[(slash + 1)..dot], package[(dot + 1)..], StringComparison.OrdinalIgnoreCase))
            package = package[..dot];

        if (safeName.Length == 0)
            safeName = new string(Path.GetFileName(package).Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(28).ToArray());

        if (safeName.Length == 0)
            safeName = "Model";

        var identity = Path.GetFullPath(source).ToUpperInvariant() + "\n" + package.ToUpperInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..10];
        return $"Outfit_{safeName}_{suffix}";
    }

    private bool TryReadMapping(out Dictionary<string, string> mapping, out Dictionary<string, double> weightScales)
    {
        mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        weightScales = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in MappingRows)
        {
            if (!double.TryParse(row.WeightScale, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) || !double.IsFinite(scale) || scale <= 0)
            {
                StatusText.Text = StudioStrings.Format("Window.BoneWeightInvalid", "骨骼「{0}」的权重倍率必须大于零。", row.Source);
                WorkTabs.SelectedItem = MappingTab;
                MappingGrid.SelectedItem = row;
                MappingGrid.ScrollIntoView(row);
                return false;
            }
            if (scale != 1)
            {
                if (string.IsNullOrEmpty(row.Target))
                {
                    StatusText.Text = StudioStrings.Format("Window.BoneWeightWithoutTarget", "请为调整了权重倍率的骨骼「{0}」选择目标。", row.Source);
                    WorkTabs.SelectedItem = MappingTab;
                    MappingGrid.SelectedItem = row;
                    MappingGrid.ScrollIntoView(row);
                    return false;
                }
                weightScales.Add(row.Source, scale);
            }
            if (!string.IsNullOrEmpty(row.Target))
                mapping.Add(row.Source, row.Target);
        }
        if (mapping.Count == 0)
        {
            StatusText.Text = StudioStrings.Text("Window.MappingMissing", "至少为一个源骨骼选择目标骨骼。");
            return false;
        }

        return true;
    }

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_operation is not null)
            return;

        using var source = new CancellationTokenSource();
        _operation = source;
        SetOperationState(true);
        StatusText.Text = message;
        try
        {
            await action(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (!_closed)
                StatusText.Text = StudioStrings.Text("Window.Cancelled", "已取消。");
        }
        catch (Exception error)
        {
            if (!_closed)
                StatusText.Text = error.Message;
        }
        finally
        {
            _operation = null;
            if (!_closed)
                SetOperationState(false);
        }
    }

    private void SetOperationState(bool busy)
    {
        InputPanel.IsEnabled = !busy;
        MappingGrid.IsEnabled = !busy;
        MappingPreflight.IsEnabled = !busy;
        MappingActions.IsEnabled = !busy && _inspected;
        MaterialGrid.IsEnabled = !busy;
        RefreshTextureActions();
        bool canBuild = !busy && _inspected;
        bool canOpenOutput = !busy && _artifact is not null;
        TaskActions.Visibility = busy || canBuild || canOpenOutput ? Visibility.Visible : Visibility.Collapsed;
        BuildButton.Visibility = canBuild ? Visibility.Visible : Visibility.Collapsed;
        UpdateBuildAvailability(busy);
        InspectButton.IsEnabled = !busy && _sourcePreview is not null && TryGetTarget(out _, out _, out _);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy && _operation?.IsCancellationRequested != true;
        OpenOutputButton.Visibility = canOpenOutput ? Visibility.Visible : Visibility.Collapsed;
        OpenOutputButton.IsEnabled = canOpenOutput;
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBuildAvailability(bool busy)
    {
        bool missing = _missingMappingRows.Length > 0;
        BuildButton.IsEnabled = !busy && _inspected && !missing;
        BuildButton.ToolTip = missing ? StudioStrings.Text("Window.BuildNeedsMappings", "请先为所有有权重的源骨骼选择目标。") : null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = StudioStrings.Text("Window.Cancelling", "正在取消…");
        _operation?.Cancel();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var directory = _artifact is null ? null : Path.GetDirectoryName(_artifact);
        if (directory is null || !Directory.Exists(directory))
        {
            StatusText.Text = StudioStrings.Text("Window.MissingOutput", "找不到产物目录。");
            _artifact = null;
            SetOperationState(false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Win32Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _operation?.Cancel();
        base.OnClosed(e);
    }

    public sealed class MappingRow(string source, string target, bool isWeighted) : INotifyPropertyChanged
    {
        private string _target = target;
        public string Source { get; } = source;
        public bool IsWeighted { get; } = isWeighted;
        public string Target
        {
            get => _target;
            set
            {
                if (_target == value)
                    return;

                _target = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Target)));
            }
        }
        public string WeightScale { get; set; } = "1";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class MaterialRow(int slot, string name, string? sourceTexture) : INotifyPropertyChanged
    {
        private string _texture = string.Empty;
        public int Slot { get; } = slot;
        public string Name { get; } = name;
        public string TextureDisplay => _texture.Length > 0 ? _texture : sourceTexture ?? string.Empty;
        public string Texture
        {
            get => _texture;
            set
            {
                if (_texture == value)
                    return;

                _texture = value;
                NotifyChanged();
                NotifyChanged(nameof(TextureDisplay));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
