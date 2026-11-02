using System.IO;
using System.Text.Json;

namespace ScoutR;

/// <summary>Layout remembered between runs: last folders, column order/widths, sort, tree width.</summary>
public sealed class AppSettings
{
    public PaneSettings A { get; set; } = new();
    public PaneSettings B { get; set; } = new();
    public double TreeWidth { get; set; }
    /// <summary>Standard iProperties the user chose not to show (Fields menu of the iProperties panel).</summary>
    public List<string> HiddenIProps { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScoutR", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Broken file: start with defaults.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not fatal.
        }
    }
}

public sealed class PaneSettings
{
    public string? Folder { get; set; }
    /// <summary>Columns in display order: which column (0 Name, 1 Date, 2 Type, 3 Size) and its width.</summary>
    public List<ColumnSetting> Columns { get; set; } = new();
    public int SortBy { get; set; }
    public bool SortDescending { get; set; }
}

public sealed class ColumnSetting
{
    public int Key { get; set; }
    public double Width { get; set; }
}
