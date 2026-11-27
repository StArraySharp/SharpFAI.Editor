using System.Security.Cryptography;
using System.Text.Json;

namespace SharpFAI.Player.Tests;

public sealed class TestApplicationUpdater
{
    private static JsonDocument Release(string asset, string digest, bool prerelease = false) => JsonDocument.Parse(
        JsonSerializer.Serialize(new
        {
            draft = false, prerelease, tag_name = "v1.0.5",
            assets = new[] { new { name = asset, size = 123, digest,
                browser_download_url = "https://github.com/adofaiex/SharpFAI.Editor/releases/download/v1.0.5/" + asset } },
        }));

    [Test]
    public void PicksOnlyACompatibleNewerStableExecutable()
    {
        using var json = Release("SharpFAI.Editor-win-x64-1.0.5.exe", "sha256:" + new string('a', 64));
        var update = ApplicationUpdater.SelectRelease(json.RootElement, new(1, 0, 4), "win-x64");
        Assert.That(update?.Version, Is.EqualTo(new Version(1, 0, 5)));
        Assert.That(ApplicationUpdater.SelectRelease(json.RootElement, new(1, 0, 5), "win-x64"), Is.Null);
        Assert.Throws<InvalidDataException>(() => ApplicationUpdater.SelectRelease(json.RootElement, new(1, 0, 4), "osx-arm64"));

        using var preview = Release("SharpFAI.Editor-win-x64-1.0.5.exe", "sha256:" + new string('a', 64), prerelease: true);
        Assert.That(ApplicationUpdater.SelectRelease(preview.RootElement, new(1, 0, 4), "win-x64"), Is.Null);
    }

    [Test]
    public async Task DownloadRequiresExpectedSizeAndHash()
    {
        byte[] payload = [42, 58, 91];
        var update = new ApplicationUpdater.Release(new(1, 0, 5), new("https://github.com"), payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)));
        using var output = new MemoryStream();
        await ApplicationUpdater.CopyVerifiedAsync(new MemoryStream(payload), output, update, _ => { }, default);
        Assert.That(output.ToArray(), Is.EqualTo(payload));

        Assert.ThrowsAsync<InvalidDataException>(() => ApplicationUpdater.CopyVerifiedAsync(
            new MemoryStream([42, 58, 90]), Stream.Null, update, _ => { }, default));
        Assert.ThrowsAsync<InvalidDataException>(() => ApplicationUpdater.CopyVerifiedAsync(
            new MemoryStream([42]), Stream.Null, update, _ => { }, default));
    }
}
