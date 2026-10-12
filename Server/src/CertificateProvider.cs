namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Supplies the certificate the server presents to every client.
/// </summary>
internal interface ICertificateProvider
{
    /// <summary>
    /// Loads the configured certificate, or the self-signed one kept in the data folder, creating it on first use.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The certificate, with its private key.</returns>
    /// <exception cref="InvalidDataException">The configured certificate file is missing or cannot be read.</exception>
    Task<X509Certificate2> Load(ServerConfig config, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class CertificateProvider : ICertificateProvider
{
    private readonly string generatedName = "certificate.pfx";

    /// <inheritdoc />
    public async Task<X509Certificate2> Load(ServerConfig config, CancellationToken cancellation)
    {
        if (config.CertificateFile is { Length: > 0 } configured)
        {
            return await Read(configured, config.CertificatePassword, cancellation);
        }

        string path = Path.Combine(config.DataFolder, generatedName);

        if (File.Exists(path))
        {
            return await Read(path, null, cancellation);
        }

        using X509Certificate2 created = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (FileStream file = new(path, FileMode.Create, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await file.WriteAsync(created.Export(X509ContentType.Pfx), cancellation);
        }

        return await Read(path, null, cancellation);
    }

    private async Task<X509Certificate2> Read(string path, string? password, CancellationToken cancellation)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(await File.ReadAllBytesAsync(path, cancellation), password);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The certificate '{path}' could not be loaded: {exception.Message}", exception);
        }
    }

    private X509Certificate2 Create()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Auto Remote", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        names.AddDnsName(Dns.GetHostName());
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (IPAddress address in NetworkInterface.GetAllNetworkInterfaces().SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(unicast => unicast.Address).Where(address => !address.IsIPv6LinkLocal))
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }
}
