using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.Views;

public sealed record ProcessEntry(string ProcessName, string DisplayName, string Detail);

public partial class AddMappingDialog : Window
{
    private readonly ObservableCollection<ProcessEntry> _visible = [];
    private List<ProcessEntry> _all = [];

    public AddMappingDialog(IEnumerable<SonarConfig> presets)
    {
        InitializeComponent();

        PresetBox.ItemsSource = presets.ToList();
        if (PresetBox.Items.Count > 0)
            PresetBox.SelectedIndex = 0;

        ProcessList.ItemsSource = _visible;
        LoadProcesses();
    }

    /// <summary>The mapping the user built, or null when cancelled.</summary>
    public GameMapping? Result { get; private set; }

    private void LoadProcesses()
    {
        var entries = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var title = SafeMainWindowTitle(process);

                // Prefer the entry that has a window title — that is the one a user recognises.
                if (entries.TryGetValue(name, out var existing) && (string.IsNullOrWhiteSpace(title) || !existing.Detail.StartsWith(name + ".exe", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var display = string.IsNullOrWhiteSpace(title) ? name : title;
                entries[name] = new ProcessEntry(name, display, $"{name}.exe");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process exited, or is not accessible from this session.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Windowed apps first — games and launchers live there — then everything else alphabetically.
        _all = entries.Values
            .OrderByDescending(e => !string.Equals(e.DisplayName, e.ProcessName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    private static string SafeMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        _visible.Clear();

        foreach (var entry in _all)
        {
            if (filter.Length == 0 ||
                entry.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                entry.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _visible.Add(entry);
            }
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshProcesses(object sender, RoutedEventArgs e) => LoadProcesses();

    private void OnProcessSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessEntry entry)
            NameBox.Text = entry.DisplayName;

        UpdateAddButton();
    }

    private void OnProcessDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessEntry && AddButton.IsEnabled)
            OnAdd(sender, e);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the game executable",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var processName = Path.GetFileNameWithoutExtension(dialog.FileName);
        var display = TryGetProductName(dialog.FileName) ?? processName;

        var entry = new ProcessEntry(processName, display, dialog.FileName);

        // Surface the browsed exe as the selected row so the flow matches picking from the list.
        _all.Insert(0, entry);
        FilterBox.Text = "";
        ApplyFilter();
        ProcessList.SelectedItem = _visible.FirstOrDefault(x => x == entry);
        NameBox.Text = display;
        UpdateAddButton();
    }

    private static string? TryGetProductName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = info.ProductName ?? info.FileDescription;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnFormChanged(object sender, RoutedEventArgs e) => UpdateAddButton();

    private void UpdateAddButton()
    {
        AddButton.IsEnabled =
            ProcessList.SelectedItem is ProcessEntry &&
            !string.IsNullOrWhiteSpace(NameBox.Text) &&
            PresetBox.SelectedItem is SonarConfig;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessEntry entry || PresetBox.SelectedItem is not SonarConfig preset)
            return;

        Result = new GameMapping
        {
            ProcessName = GameMapping.NormalizeProcessName(entry.ProcessName),
            DisplayName = NameBox.Text.Trim(),
            PresetId = preset.Id,
            PresetName = preset.Name,
            Enabled = true,
        };

        DialogResult = true;
    }
}
