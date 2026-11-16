using System.Text.Json.Nodes;
using Rendition.Vulkan;

namespace Super.Tests;

public sealed class LayerManifestsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layer-manifests-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _manifests;
    private readonly string _libraries;
    private readonly string _copies;

    public LayerManifestsTests()
    {
        _manifests = Path.Combine(_folder, "share", "vulkan", "explicit_layer.d");
        _libraries = Path.Combine(_folder, "lib");
        _copies = Path.Combine(_folder, "copies");
        Directory.CreateDirectory(_manifests);
        Directory.CreateDirectory(_libraries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_library_named_with_no_folder_becomes_a_path_into_the_library_folder()
    {
        File.WriteAllText(Path.Combine(_libraries, "libVkLayer_khronos_validation.dylib"), "");
        WriteManifest("validation.json", "libVkLayer_khronos_validation.dylib");

        LayerManifests.WithFullPaths(_manifests, _libraries, _copies);

        Assert.Equal(Path.Combine(_libraries, "libVkLayer_khronos_validation.dylib"), LibraryIn("validation.json"));
    }

    [Fact]
    public void A_library_named_with_no_folder_that_isnt_there_is_left_as_it_was()
    {
        WriteManifest("other.json", "libVkLayer_other.dylib");

        LayerManifests.WithFullPaths(_manifests, _libraries, _copies);

        Assert.Equal("libVkLayer_other.dylib", LibraryIn("other.json"));
    }

    [Fact]
    public void A_path_relative_to_the_manifest_becomes_a_full_path_so_the_copy_still_finds_it()
    {
        WriteManifest("sdk.json", "../../../lib/libVkLayer_khronos_validation.dylib");

        LayerManifests.WithFullPaths(_manifests, _libraries, _copies);

        Assert.Equal(Path.Combine(_libraries, "libVkLayer_khronos_validation.dylib"), LibraryIn("sdk.json"));
    }

    [Fact]
    public void Every_layer_of_a_manifest_that_lists_several_is_fixed()
    {
        File.WriteAllText(Path.Combine(_libraries, "libA.dylib"), "");
        File.WriteAllText(Path.Combine(_libraries, "libB.dylib"), "");
        File.WriteAllText(
            Path.Combine(_manifests, "both.json"),
            """{ "file_format_version": "1.2.0", "layers": [ { "library_path": "libA.dylib" }, { "library_path": "libB.dylib" } ] }""");

        LayerManifests.WithFullPaths(_manifests, _libraries, _copies);

        var layers = JsonNode.Parse(File.ReadAllText(Path.Combine(_copies, "both.json")))!["layers"]!.AsArray();
        Assert.Equal(
            [Path.Combine(_libraries, "libA.dylib"), Path.Combine(_libraries, "libB.dylib")],
            layers.Select(layer => layer!["library_path"]!.GetValue<string>()));
    }

    private void WriteManifest(string name, string library) =>
        File.WriteAllText(
            Path.Combine(_manifests, name),
            $$"""{ "file_format_version": "1.2.0", "layer": { "name": "VK_LAYER_TEST", "library_path": "{{library}}" } }""");

    private string LibraryIn(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_copies, name)))!["layer"]!["library_path"]!.GetValue<string>();
}
