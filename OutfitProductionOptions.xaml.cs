using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace UAssetManager.ModelStudio;

internal sealed record OutfitStudioProductionSettings(bool HideFace, bool HideHair, bool HideHeadgear, string Compression, JsonObject HairPhysics, IReadOnlyList<string>? OtherHidePaths = null);

public partial class OutfitProductionOptions : UserControl {
    private sealed record HairNumber(string Key, string LabelKey, string LabelFallback, double Default, double Min, double Max) {
        public string Label => StudioStrings.Text(LabelKey, LabelFallback);
    }

    private const string Face = "/Character/Face/";
    private const string Hair = "/Character/Hair/";
    private static readonly string[] Headgear = ["/Character/Acc/04-머리장식/", "/Character/Acc/04-문파_머리장식/", "/Character/Acc/06-머리장식/"];
    private static readonly HairNumber[] Numbers = [
        new("gravity_cm_s2", "Production.HairGravity", "重力 (cm/s²)", -300, -2000, 0),
        new("radius_cm", "Production.HairRadius", "发链半径 (cm)", .8, .1, 10),
        new("head_radius_cm", "Production.HeadCollisionRadius", "头部碰撞半径 (cm)", 4, 0, 30),
        new("torso_radius_cm", "Production.TorsoCollisionRadius", "胸背碰撞半径 (cm)", 6, .1, 30),
        new("torso_length_cm", "Production.TorsoCollisionLength", "胸背碰撞长度 (cm)", 16, 0, 100),
        new("torso_offset_z_cm", "Production.TorsoCollisionOffset", "胸背碰撞高度偏移 (cm)", -6, -100, 100),
        new("mass_kg", "Production.HairMass", "质量 (kg)", .04, .001, 5),
        new("linear_damping", "Production.LinearDamping", "线性阻尼", 2, 0, 50),
        new("angular_damping", "Production.AngularDamping", "角阻尼", 5, 0, 50),
        new("swing_degrees", "Production.SwingLimit", "摆动限位 (°)", 25, 1, 80),
        new("twist_degrees", "Production.TwistLimit", "扭转限位 (°)", 15, 0, 80),
    ];

    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.Ordinal);

    public OutfitProductionOptions() {
        InitializeComponent();
        foreach (var setting in Numbers) {
            var row = HairNumbers.RowDefinitions.Count;
            HairNumbers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var input = new TextBox { Margin = new Thickness(5, 0, 0, 4) };
            var title = setting.Label;
            AutomationProperties.SetName(input, title);
            var labelText = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
            var label = new Label { Content = labelText, Target = input, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);
            HairNumbers.Children.Add(label);
            HairNumbers.Children.Add(input);
            _inputs.Add(setting.Key, input);
        }
        ApplyTemplate(default);
    }

    internal void LoadTemplate(string? templatePath) {
        if (string.IsNullOrWhiteSpace(templatePath)) {
            ApplyTemplate(default);
            return;
        }
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(templatePath));
            ApplyTemplate(document.RootElement);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException) {
            ApplyTemplate(default);
        }
    }

    internal OutfitStudioProductionSettings Read(IReadOnlyCollection<string> targetBones) {
        var paths = Split(OtherPaths.Text);
        if (paths.Length > 32)
            throw new InvalidDataException(StudioStrings.Text("Production.TooManyHidePaths", "自定义隐藏路径最多 32 条。"));
        foreach (var path in paths) {
            if (!IsHidePath(path))
                throw new InvalidDataException(StudioStrings.Format("Production.InvalidHidePath", "无效的自定义隐藏路径：{0}", path));
        }
        var selected = ((ComboBoxItem?)CompressionOption.SelectedItem)?.Tag as string ?? "oodle";
        return new OutfitStudioProductionSettings(HideFaceOption.IsChecked == true, HideHairOption.IsChecked == true, HideHeadgearOption.IsChecked == true, selected, ReadHair(targetBones), paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private JsonObject ReadHair(IReadOnlyCollection<string> targetBones) {
        if (HairEnabled.IsChecked != true)
            return new JsonObject { ["enabled"] = false };
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in targetBones)
            names.TryAdd(name, name);
        var selected = Split(HairBones.Text);
        if (selected.Length < 2 || selected.Length > 32 || selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new InvalidDataException(StudioStrings.Text("Production.InvalidHairChain", "头发物理需要按顺序填写 2–32 根互不重复的目标骨骼。"));
        var chain = selected.Select(name => TargetBone(names, name, StudioStrings.Text("Production.HairChainBone", "发链骨骼"))).ToArray();
        var anchor = TargetBone(names, HairAnchor.Text.Trim(), StudioStrings.Text("Production.HeadAnchor", "头部锚点"));
        var torso = string.IsNullOrWhiteSpace(HairTorso.Text) ? "" : TargetBone(names, HairTorso.Text.Trim(), StudioStrings.Text("Production.TorsoBone", "胸背碰撞骨"));
        var occupied = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase);
        if (!occupied.Add(anchor) || (torso.Length > 0 && !occupied.Add(torso)))
            throw new InvalidDataException(StudioStrings.Text("Production.DuplicateHairPhysicsBones", "发链、头部锚点和胸背碰撞骨不能重复。"));
        var hair = new JsonObject {
            ["enabled"] = true,
            ["bones"] = new JsonArray(chain.Select(bone => (JsonNode?)JsonValue.Create(bone)).ToArray()),
            ["anchor"] = anchor,
            ["torso"] = torso,
        };
        foreach (var setting in Numbers) {
            var text = _inputs[setting.Key].Text;
            var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            if (!parsed)
                parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            if (!parsed || !double.IsFinite(value) || value < setting.Min || value > setting.Max)
                throw new InvalidDataException(StudioStrings.Format("Production.HairNumberOutOfRange", "{0}必须在 {1} 至 {2} 之间。", setting.Label, Format(setting.Min), Format(setting.Max)));
            hair[setting.Key] = value;
        }
        return hair;
    }

    private void ApplyTemplate(JsonElement template) {
        var visibility = Property(template, "visibility");
        var fromVisibility = visibility.ValueKind == JsonValueKind.Object;
        var paths = Property(visibility, "enabled").ValueKind == JsonValueKind.True
            ? TextList(Property(visibility, "hide_paths"))
            : [];
        var known = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        HideFaceOption.IsChecked = !fromVisibility || known.Remove(Face);
        HideHairOption.IsChecked = !fromVisibility || known.Remove(Hair);
        var fullHeadgear = Headgear.All(known.Contains);
        HideHeadgearOption.IsChecked = !fromVisibility || fullHeadgear;
        if (fullHeadgear)
            known.ExceptWith(Headgear);
        OtherPaths.Text = string.Join(Environment.NewLine, paths.Where(known.Contains).Distinct(StringComparer.OrdinalIgnoreCase));
        var compression = Text(Property(Property(template, "compression"), "method"));
        CompressionOption.SelectedIndex = compression == "none" ? 1 : 0;
        var hair = Property(template, "hair_physics");
        HairEnabled.IsChecked = Property(hair, "enabled").ValueKind == JsonValueKind.True;
        HairBones.Text = string.Join(Environment.NewLine, TextList(Property(hair, "bones")));
        HairAnchor.Text = Text(Property(hair, "anchor")) ?? "bip01head";
        HairTorso.Text = Text(Property(hair, "torso")) ?? "bip01spine2";
        foreach (var setting in Numbers) {
            var number = Property(hair, setting.Key);
            var value = number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : setting.Default;
            _inputs[setting.Key].Text = Format(value);
        }
    }

    private static bool IsHidePath(string path) {
        if (path.Length <= 12 || !path.StartsWith("/Character/", StringComparison.Ordinal) || !path.EndsWith('/') || path.Contains('\\') || path.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
            return false;
        return path[11..^1].Split('/').All(part => part.Length > 0 && part is not ("." or "..") && part.Trim() == part && !part.Any(char.IsControl));
    }

    private static string TargetBone(Dictionary<string, string> names, string name, string label) {
        return names.TryGetValue(name, out var bone) ? bone : throw new InvalidDataException(StudioStrings.Format("Production.BoneNotInTarget", "{0}不在目标骨架中：{1}", label, name));
    }

    private static string[] Split(string text) {
        return text.Split(['\r', '\n', ',', '，', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static JsonElement Property(JsonElement value, string name) {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    }

    private static string? Text(JsonElement value) {
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string[] TextList(JsonElement value) {
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(Text).OfType<string>().Where(text => !string.IsNullOrWhiteSpace(text)).ToArray()
            : [];
    }

    private static string Format(double value) {
        return value.ToString("G", CultureInfo.InvariantCulture);
    }
}
