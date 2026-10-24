using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class ToolLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-tools-" + Guid.NewGuid().ToString("N"));

    public ToolLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private ToolLocation Resolve(string? configured = null, string? pathVariable = null, string? defaultPath = null) =>
        ToolLocator.Resolve(configured, "MEDIACENTER_X", "tool", Path.Combine(_root, "app"), pathVariable, defaultPath);

    [Fact]
    public void The_environment_variable_wins_even_if_the_file_is_missing()
    {
        Touch("app", "tool.exe");
        var found = Resolve(configured: @"Z:\nirgends\tool.exe");

        Assert.Equal(@"Z:\nirgends\tool.exe", found.Path);
        Assert.False(found.Found);
        Assert.Contains("MEDIACENTER_X", found.Source);
        Assert.Contains("nicht gefunden", found.Describe());
    }

    [Fact]
    public void A_tool_next_to_the_app_is_found_before_the_tools_folder_and_the_path()
    {
        var beside = Touch("app", "tool.exe");
        Touch("app", ToolLocator.ToolsFolder, "tool.exe");
        Touch("elsewhere", "tool.exe");

        var found = Resolve(pathVariable: Path.Combine(_root, "elsewhere"));

        Assert.Equal(beside, found.Path);
        Assert.True(found.Found);
    }

    [Fact]
    public void The_tools_folder_comes_before_the_path()
    {
        var inTools = Touch("app", ToolLocator.ToolsFolder, "tool.exe");
        Touch("elsewhere", "tool.exe");

        Assert.Equal(inTools, Resolve(pathVariable: Path.Combine(_root, "elsewhere")).Path);
    }

    [Fact]
    public void The_path_is_searched_in_order_and_quoted_entries_work()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var second = Touch("second", "tool.exe");
        var path = string.Join(Path.PathSeparator, Path.Combine(_root, "empty"), "", $"\"{Path.Combine(_root, "second")}\"");

        var found = Resolve(pathVariable: path);

        Assert.Equal(second, found.Path);
        Assert.Equal("PATH", found.Source);
    }

    [Fact]
    public void Without_a_hit_the_default_path_is_reported_as_missing_or_found()
    {
        var missing = Resolve(defaultPath: Path.Combine(_root, "nope", "tool.exe"));
        Assert.False(missing.Found);
        Assert.Equal(Path.Combine(_root, "nope", "tool.exe"), missing.Path);

        var present = Touch("default", "tool.exe");
        var found = Resolve(defaultPath: present);
        Assert.True(found.Found);
        Assert.Equal("Standardpfad", found.Source);
    }

    [Fact]
    public void Without_any_hit_and_default_only_the_name_comes_back_so_the_call_fails_loudly()
    {
        var found = Resolve(pathVariable: null);

        Assert.Equal("tool", found.Path);
        Assert.False(found.Found);
        Assert.Contains("gesucht", found.Describe());
    }

    [Fact]
    public void An_empty_variable_counts_as_not_set()
    {
        var beside = Touch("app", "tool.exe");

        Assert.Equal(beside, Resolve(configured: "").Path);
    }
}
