using System;
using System.IO;
using System.Linq;
using System.Text;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class CompressedLogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stardew-log-test-" + Guid.NewGuid().ToString("N"));
    public CompressedLogStoreTests() => Directory.CreateDirectory(_dir);
    private string PathFor(int day) => Path.Combine(_dir, $"day_{day:000}.jsonl");
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void MembersRemainReadableAcrossIndependentAppendsAndUnicode()
    {
        string expected = "";
        for (int i = 0; i < 50; i++)
        {
            string line = $"{{\"seq\":{i},\"text\":\"สวัสดี AI1 🌾 中文\"}}\n";
            CompressedLogStore.Append(PathFor(1), line);
            expected += line;
            Assert.Equal(expected, CompressedLogStore.Read(PathFor(1)));
        }
        Assert.False(File.Exists(PathFor(1)));
        Assert.Equal(new[] { 1 }, CompressedLogStore.Days(_dir));
    }

    [Fact]
    public void MigrationPreservesBytesAndAllowsFurtherWrites()
    {
        string original = string.Concat(Enumerable.Repeat("{\"text\":\"บันทึกครบถ้วน\"}\r\n", 2000));
        File.WriteAllText(PathFor(2), original, new UTF8Encoding(true));
        CompressedLogStore.Migrate(PathFor(2));
        CompressedLogStore.Migrate(PathFor(2));
        Assert.False(File.Exists(PathFor(2)));
        Assert.Equal(original, CompressedLogStore.Read(PathFor(2)));
        Assert.True(new FileInfo(PathFor(2) + ".gz").Length < Encoding.UTF8.GetByteCount(original) / 4);
        CompressedLogStore.Append(PathFor(2), "new\n");
        Assert.Equal(original + "new\n", CompressedLogStore.Read(PathFor(2)));
    }

    [Fact]
    public void MixedFilesAreBothReadWithoutOverwritingEither()
    {
        File.WriteAllText(PathFor(3), "old");
        CompressedLogStore.Append(PathFor(3), "new\n");
        CompressedLogStore.Migrate(PathFor(3));
        Assert.True(File.Exists(PathFor(3)));
        Assert.Equal("old\nnew\n", CompressedLogStore.Read(PathFor(3)));
        File.WriteAllText(Path.Combine(_dir, "day_004.jsonl.gz.partial"), "unfinished");
        Assert.Equal(new[] { 3 }, CompressedLogStore.Days(_dir));
        Assert.Equal("", CompressedLogStore.Read(PathFor(8)));
    }
}
