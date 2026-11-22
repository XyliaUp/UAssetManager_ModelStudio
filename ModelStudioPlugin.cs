using System.IO;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Markup;
using UAssetAPI.Pak.Utils;

namespace UAssetManager.ModelStudio;

public sealed class ModelStudioPlugin : IUAssetPlugin
{
    public string Name => StudioStrings.ForLanguage(CurrentLanguage, "Plugin.Name", "模型制作工具");
    public string Version => ReleaseVersion;
    public string Description => StudioStrings.ForLanguage(CurrentLanguage, "Plugin.Description", "预览 FBX 和 PMX，将蒙皮模型换绑后导出为 FBX");
    public string Author => "Xylia";

    internal static string ReleaseVersion { get; } = typeof(ModelStudioPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ModelStudioPlugin).Assembly.GetName().Version?.ToString(3)
        ?? string.Empty;

    private OutfitStudioWindow? _window;
    private IPluginHost? _host;
    private string _language = "zh-CN";

    private string CurrentLanguage => _host?.GetLanguage() ?? _language;

    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Initialize(host, host.GetLanguage());
    }

    public void Initialize(IPluginHost host, string language)
    {
        ArgumentNullException.ThrowIfNull(host);
        _language = language;
        _host = host;
    }

    public void Execute()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Execute);
            return;
        }

        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Activate();
            return;
        }

        var language = CurrentLanguage;
        StudioStrings.UseLanguage(language);
        var window = new OutfitStudioWindow(GetConfigDirectory())
        {
            Language = XmlLanguage.GetLanguage(StudioStrings.Language),
        };
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }

    private string? GetConfigDirectory()
    {
        try
        {
            return _host?.GetConfigDirectory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public void Cleanup()
    {
        _host = null;
        var window = _window;
        if (window is null)
            return;

        if (window.Dispatcher.CheckAccess())
            window.Close();
        else
            window.Dispatcher.Invoke(window.Close);
    }
}
