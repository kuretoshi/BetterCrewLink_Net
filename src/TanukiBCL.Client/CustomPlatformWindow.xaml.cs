using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TanukiBCL.Client;

public partial class CustomPlatformWindow : Window
{
    private readonly GameLaunchPlatform? original;
    private readonly IReadOnlyCollection<string> takenKeys;

    internal CustomPlatformWindow(GameLaunchPlatform? original = null,
        IReadOnlyCollection<string>? takenKeys = null)
    {
        this.original = original;
        this.takenKeys = takenKeys ?? [];
        InitializeComponent();
        DeleteButton.IsEnabled = original is not null;
        if (original is null) return;
        NameBox.Text = original.Name;
        if (original.LaunchType == "URI") UriTypeRadio.IsChecked = true;
        else ExeTypeRadio.IsChecked = true;
        RunPathBox.Text = original.LaunchType == "URI" ? original.RunPath :
            Path.Combine(original.RunPath, original.Execute[0]);
        ArgumentsBox.Text = string.Join(' ', original.Execute.Skip(1));
        AdvancedCheck.IsChecked = original.Execute.Length > 1;
        UpdateRunTypeControls();
    }

    internal GameLaunchPlatform? ResultPlatform { get; private set; }
    internal bool DeleteRequested { get; private set; }

    private void LaunchType_Changed(object sender, RoutedEventArgs e)
    {
        if (RunPathBox is null) return;
        RunPathBox.Text = string.Empty;
        ArgumentsBox.Text = string.Empty;
        AdvancedCheck.IsChecked = false;
        UpdateRunTypeControls();
    }

    private void UpdateRunTypeControls()
    {
        if (RunPathBox is null) return;
        var uri = UriTypeRadio.IsChecked == true;
        RunPathLabel.Text = uri ? "URI" : "実行ファイル";
        RunPathBox.IsReadOnly = !uri;
        BrowseButton.Visibility = uri ? Visibility.Collapsed : Visibility.Visible;
        AdvancedCheck.Visibility = uri ? Visibility.Collapsed : Visibility.Visible;
        ArgumentsPanel.Visibility = !uri && AdvancedCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AdvancedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ArgumentsBox is null) return;
        if (AdvancedCheck.IsChecked != true) ArgumentsBox.Text = string.Empty;
        UpdateRunTypeControls();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Among Usの実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) RunPathBox.Text = picker.FileName;
    }

    private GameLaunchPlatform? BuildPlatform()
    {
        var name = NameBox.Text.Trim();
        var uri = UriTypeRadio.IsChecked == true;
        var path = RunPathBox.Text.Trim();
        if (name.Length == 0 || path.Length == 0) return null;
        if (uri) return new GameLaunchPlatform(name, name, "URI", path, [""]);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var execute = new List<string> { Path.GetFileName(path) };
        if (AdvancedCheck.IsChecked == true && ArgumentsBox.Text.Length > 0)
            execute.AddRange(ArgumentsBox.Text.Split(' '));
        return new GameLaunchPlatform(name, name, "EXE", directory, execute.ToArray());
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var platform = BuildPlatform();
        if (platform?.IsValid != true || platform.LaunchType == "EXE" &&
            !File.Exists(Path.Combine(platform.RunPath, platform.Execute[0])))
        {
            ValidationText.Text = "名前と有効な実行ファイルまたはURIを指定してください。";
            return;
        }
        if (takenKeys.Any(key => key.Equals(platform.Key, StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "その名前は既に起動先に使用されています。";
            return;
        }
        ResultPlatform = platform;
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (original is null) return;
        DeleteRequested = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    internal static void Verify()
    {
        var window = new CustomPlatformWindow();
        window.NameBox.Text = "Custom URI";
        window.UriTypeRadio.IsChecked = true;
        window.RunPathBox.Text = "steam://rungameid/945360";
        var uri = window.BuildPlatform();
        if (uri is null || !uri.IsValid || uri.LaunchType != "URI")
            throw new InvalidOperationException("Custom URI form did not produce a platform");
        window.Close();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path missing");
        var existing = new GameLaunchPlatform("Custom EXE", "Custom EXE", "EXE",
            Path.GetDirectoryName(executable)!, [Path.GetFileName(executable), "--demo"]);
        var edit = new CustomPlatformWindow(existing);
        var edited = edit.BuildPlatform();
        if (edited is null || !edited.IsValid || !edited.Execute.SequenceEqual(existing.Execute) ||
            edit.DeleteButton.IsEnabled != true || edit.ArgumentsPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Custom EXE edit form did not restore arguments or deletion");
        edit.Close();
        Console.WriteLine("[PASS] Custom platform URI and EXE edit forms validate and restore arguments");
    }
}
