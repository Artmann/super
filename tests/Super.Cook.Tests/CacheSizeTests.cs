using System.Globalization;

namespace Super.Cook.Tests;

/// <summary>Reading and writing the cook cache's limit, such as <c>4GB</c>, the same in every culture.</summary>
public sealed class CacheSizeTests
{
    [Theory]
    [InlineData("4GB", 4L << 30)]
    [InlineData("500MB", 500L << 20)]
    [InlineData("1.5GB", 1536L << 20)]
    [InlineData("2 gb", 2L << 30)]
    [InlineData("1KiB", 1024L)]
    [InlineData("3k", 3072L)]
    [InlineData("1TB", 1L << 40)]
    [InlineData("123", 123L)]
    [InlineData("123B", 123L)]
    [InlineData(" 8GB ", 8L << 30)]
    [InlineData("none", 0L)]
    [InlineData("None", 0L)]
    [InlineData("0", 0L)]
    public void Reads_a_size(string text, long bytes)
    {
        Assert.Equal(bytes, CacheSize.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("lots")]
    [InlineData("-1GB")]
    [InlineData("1,5GB")]
    [InlineData("4 PB")]
    [InlineData("99999999999TB")]
    [InlineData("9999999999999999999999TB")]
    [InlineData("79228162514264337593543950335")]
    public void Refuses_what_isnt_a_size_and_says_how_to_write_one(string text)
    {
        var error = Assert.Throws<FormatException>(() => CacheSize.Parse(text));

        Assert.Equal(
            $"\"{text}\" isn't a size. Give a number with KB, MB, GB or TB, such as 4GB, 500MB or 1.5GB, a number of bytes, or none for no limit.",
            error.Message);
    }

    [Fact]
    public void Reads_and_writes_the_same_in_a_culture_with_a_decimal_comma()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

            Assert.Equal(1536L << 20, CacheSize.Parse("1.5GB"));
            Assert.Throws<FormatException>(() => CacheSize.Parse("1,5GB"));
            Assert.Equal("1.5 GB", CacheSize.Format(1536L << 20));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(320L << 10, "320 KB")]
    [InlineData(1536L << 20, "1.5 GB")]
    [InlineData(4L << 30, "4.0 GB")]
    [InlineData(1_100_000_000L, "1.02 GB")]
    [InlineData(1_126L << 20, "1.1 GB")]
    [InlineData(12L << 30, "12 GB")]
    [InlineData(3L << 40, "3.0 TB")]
    public void Writes_a_size_for_people(long bytes, string text)
    {
        Assert.Equal(text, CacheSize.Format(bytes));
    }

    [Fact]
    public void Describes_a_trim_with_and_without_deletions()
    {
        Assert.Equal("Cache 3.5 GB of 4.0 GB: deleted 12 least recently used files, 1.1 GB.",
            new CacheTrim(3584L << 20, 4L << 30, 12, 1126L << 20, 0).Describe());
        Assert.Equal("Cache 3.5 GB of 4.0 GB: deleted 1 least recently used file, 10 KB.",
            new CacheTrim(3584L << 20, 4L << 30, 1, 10L << 10, 0).Describe());
        Assert.Equal("Cache 3.5 GB of 4.0 GB.", new CacheTrim(3584L << 20, 4L << 30, 0, 0, 0).Describe());
    }
}
