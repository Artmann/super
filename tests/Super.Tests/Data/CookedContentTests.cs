using Rendition;
using Super.Testing;
using Super.Tests.Ui;

namespace Super.Tests;

public class CookedContentTests
{
    public record Item(string Id, int Power);

    [Fact]
    public void Naming_a_missing_asset_explains()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ContentException>(() => content.Model("models/fox.glb"));

        Assert.Equal("models/fox.glb isn't in the cooked content. Check the name, including its case and extension.", problem.Message);
    }

    [Fact]
    public void Wrong_case_is_missing()
    {
        var files = new MemoryContent();
        files.AddModel("fox.glb");
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Model("Fox.glb"));

        Assert.StartsWith("Fox.glb isn't in the cooked content.", problem.Message);
        content.Model("fox.glb");
    }

    [Fact]
    public void Unlisted_part_lists_the_parts()
    {
        var files = new MemoryContent();
        files.AddModel("firs.glb#young");
        files.AddModel("firs.glb#tall");
        files.AddModel("firsts.glb#other");
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Model("firs.glb#tal"));

        Assert.Equal(
            "firs.glb#tal isn't in the cooked content. Check the name, including its case and extension. firs.glb has the parts: tall, young.",
            problem.Message);
    }

    [Fact]
    public void Wrong_kind_explains()
    {
        var files = new MemoryContent { ["items/axe.json"] = "{ \"id\": \"axe\", \"power\": 1 }" };
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Model("items/axe.json"));

        Assert.Equal("items/axe.json is a data, not a model.", problem.Message);
    }

    [Fact]
    public void Image_size_comes_from_the_cooked_header()
    {
        var content = new Content(new MemoryContent().Add("ui/a.png", ImageTests.Png(3, 2)));

        var image = content.Image("ui/a.png");

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void Normal_map_kind_comes_from_cook_settings()
    {
        var files = new MemoryContent { ["cook.json"] = "{ \"rules\": [{ \"match\": \"*-normal.png\", \"texture\": { \"normalMap\": true } }] }" };
        files.Add("a-normal.png", ImageTests.Png(2, 2)).Add("a.png", ImageTests.Png(2, 2));
        var content = new Content(files);

        var normal = SceneRenderer.LoadTexture((TextureItem)content.Items[content.Texture("a-normal.png").Index - 1]);
        var color = SceneRenderer.LoadTexture((TextureItem)content.Items[content.Texture("a.png").Index - 1]);

        Assert.Equal(TextureKind.Linear, normal.Kind);
        Assert.Equal(TextureKind.Color, color.Kind);
    }

    [Fact]
    public void CookedFolder_without_manifest_is_empty()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var files = new CookedFolder(folder);

            Assert.Null(files.KindOf("items/axe.json"));
            Assert.Null(files.List("items"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CookedFolder_reads_what_FolderStore_wrote()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string cooked = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "items"));
            File.WriteAllText(Path.Combine(root, "items", "axe.json"), "{ \"id\": \"axe\", \"power\": 3 }");
            CookedTemp.Cook(root, cooked);

            var items = new Content(new CookedFolder(cooked)).LoadAll<Item>("items");

            Assert.Equal(3, items["axe"].Power);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cooked, recursive: true);
        }
    }
}
