using Super.Testing;

namespace Super.Tests.Audio;

public class SoundContentTests
{
    private static byte[] Wav(float first = 0.25f) => TestWav.Make(48000, 1, [first, 0.5f, -0.5f], asFloat: false);

    private static string Forest(string every = "[4, 12]") => $$"""
        {
          "layers": [
            { "file": "wind.wav", "volume": 0.6 },
            { "file": "birds.wav", "volume": 0.3 }
          ],
          "scattered": [
            { "files": ["crow.wav", "owl.wav"], "every": {{every}}, "volume": [0.4, 0.8], "pitchVariation": 0.1 }
          ]
        }
        """;

    private static MemoryContent ForestFiles(string forest)
    {
        var files = new MemoryContent();
        files["sounds/forest.ambience.json"] = forest;
        foreach (string name in new[] { "wind", "birds", "crow", "owl" })
        {
            files.Add($"sounds/{name}.wav", Wav());
        }

        return files;
    }

    [Fact]
    public void SameSoundPathGivesSameRef()
    {
        var files = new MemoryContent().Add("sounds/pop.wav", Wav());
        var content = new Content(files);

        SoundRef first = content.Sound("sounds/pop.wav");
        SoundRef second = content.Sound("sounds/pop.wav");

        Assert.Equal(first, second);
        Assert.Equal(3.0, content.Clip(first).Seconds * 48000, 3);
    }

    [Fact]
    public void DifferentSoundPathsGiveDifferentRefs()
    {
        var files = new MemoryContent().Add("sounds/a.wav", Wav()).Add("sounds/b.wav", Wav(0.75f));
        var content = new Content(files);

        Assert.NotEqual(content.Sound("sounds/a.wav"), content.Sound("sounds/b.wav"));
    }

    [Fact]
    public void MissingSoundFileNamesThePath()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ContentException>(() => content.Sound("sounds/missing.wav"));

        Assert.Contains("sounds/missing.wav", problem.Message);
    }

    [Fact]
    public void AmbienceReadsLayersAndScattered()
    {
        var content = new Content(ForestFiles(Forest()));

        AmbienceDefinition forest = content.AmbienceOf(content.Ambience("sounds/forest.ambience.json"));

        Assert.Equal(2, forest.Layers.Length);
        Assert.Equal(0.6f, forest.Layers[0].Volume);
        Assert.Equal(0.3f, forest.Layers[1].Volume);
        Assert.Equal("sounds/wind.wav", forest.Layers[0].Clip.Path);
        Scattered scattered = Assert.Single(forest.Scattered);
        Assert.Equal(["sounds/crow.wav", "sounds/owl.wav"], scattered.Clips.Select(clip => clip.Path));
        Assert.Equal(4f, scattered.MinSeconds);
        Assert.Equal(12f, scattered.MaxSeconds);
        Assert.Equal(0.4f, scattered.MinVolume);
        Assert.Equal(0.8f, scattered.MaxVolume);
        Assert.Equal(0.1f, scattered.PitchVariation);
    }

    [Fact]
    public void AmbienceDefaultsVolumeToOneAndPitchVariationToZero()
    {
        var files = new MemoryContent().Add("sounds/wind.wav", Wav());
        files["sounds/a.ambience.json"] = """
            {
              "layers": [ { "file": "wind.wav" } ],
              "scattered": [ { "files": ["wind.wav"], "every": [1, 2], "volume": [0.5, 1] } ]
            }
            """;
        var content = new Content(files);

        AmbienceDefinition ambience = content.AmbienceOf(content.Ambience("sounds/a.ambience.json"));

        Assert.Equal(1f, ambience.Layers[0].Volume);
        Assert.Equal(0f, ambience.Scattered[0].PitchVariation);
    }

    [Fact]
    public void SameAmbiencePathGivesSameRef()
    {
        var content = new Content(ForestFiles(Forest()));

        Assert.Equal(content.Ambience("sounds/forest.ambience.json"), content.Ambience("sounds/forest.ambience.json"));
    }

    [Fact]
    public void AmbiencePathsAreRelativeToTheFile()
    {
        var files = new MemoryContent().Add("sounds/ambient/rain.wav", Wav());
        files["sounds/forest.ambience.json"] = """{ "layers": [ { "file": "ambient/rain.wav", "volume": 1 } ] }""";
        var content = new Content(files);

        AmbienceDefinition forest = content.AmbienceOf(content.Ambience("sounds/forest.ambience.json"));

        Assert.Equal("sounds/ambient/rain.wav", forest.Layers[0].Clip.Path);
        Assert.Empty(forest.Scattered);
    }

    [Fact]
    public void AmbienceEveryWithMinAboveMaxExplains()
    {
        var content = new Content(ForestFiles(Forest("[12, 4]")));

        var problem = Assert.Throws<ContentException>(() => content.Ambience("sounds/forest.ambience.json"));

        Assert.Contains("forest.ambience.json", problem.Message);
        Assert.Contains("every", problem.Message);
    }

    [Fact]
    public void AmbienceMissingLayerFileNamesBothFiles()
    {
        var files = ForestFiles(Forest());
        files.Remove("sounds/birds.wav");
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Ambience("sounds/forest.ambience.json"));

        Assert.Contains("sounds/forest.ambience.json", problem.Message);
        Assert.Contains("sounds/birds.wav", problem.Message);
    }

    [Fact]
    public void AmbienceWithBadJsonNamesTheFile()
    {
        var files = new MemoryContent();
        files["sounds/bad.ambience.json"] = "{ not json";
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Ambience("sounds/bad.ambience.json"));

        Assert.Contains("sounds/bad.ambience.json", problem.Message);
    }
}
