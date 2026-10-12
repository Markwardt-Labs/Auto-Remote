namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class CertificateProviderTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly CertificateProvider provider = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task Load_CreatesASelfSignedCertificateInTheDataFolderAndReusesIt()
    {
        ServerConfig config = new() { Port = 1, WebPort = 2, Key = "k", DataFolder = temp.Combine("data") };

        using X509Certificate2 first = await provider.Load(config, CancellationToken.None);
        using X509Certificate2 second = await provider.Load(config, CancellationToken.None);

        Assert.True(first.HasPrivateKey);
        Assert.True(File.Exists(temp.Combine("data/certificate.pfx")));
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public async Task Load_UsesTheConfiguredCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Mine", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 mine = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllBytesAsync(temp.Combine("mine.pfx"), mine.Export(X509ContentType.Pfx, "pw"));

        using X509Certificate2 loaded = await provider.Load(new ServerConfig { Port = 1, WebPort = 2, Key = "k", CertificateFile = temp.Combine("mine.pfx"), CertificatePassword = "pw" }, CancellationToken.None);

        Assert.Equal(mine.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public async Task Load_FailsForAMissingConfiguredCertificate() => await Assert.ThrowsAsync<InvalidDataException>(() => provider.Load(new ServerConfig { Port = 1, WebPort = 2, Key = "k", CertificateFile = temp.Combine("none.pfx") }, CancellationToken.None));
}
