using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ESPresense.Extensions;
using ESPresense.Models;
using MQTTnet;

namespace ESPresense.Companion.Tests;

public class MqttTlsValidationTests
{
    private X509Certificate2 _ca = null!;
    private X509Certificate2 _leaf = null!;
    private X509Certificate2 _otherLeaf = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _ca = CreateCa("CN=Test CA");
        _leaf = CreateLeaf(_ca, "CN=broker");
        using var otherCa = CreateCa("CN=Other CA");
        _otherLeaf = CreateLeaf(otherCa, "CN=broker");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _ca?.Dispose();
        _leaf?.Dispose();
        _otherLeaf?.Dispose();
    }

    [Test]
    public void LeafSignedByCa_IsAccepted() =>
        Assert.That(MqttClientOptionsBuilderExtensions.ValidateWithCa(_leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, _ca), Is.True);

    [Test]
    public void LeafSignedByOtherCa_IsRejected() =>
        Assert.That(MqttClientOptionsBuilderExtensions.ValidateWithCa(_otherLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, _ca), Is.False);

    [Test]
    public void NameMismatch_IsRejected() =>
        Assert.That(MqttClientOptionsBuilderExtensions.ValidateWithCa(_leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, _ca), Is.False);

    [Test]
    public void MissingCaFile_Throws()
    {
        var cfg = new ConfigMqtt { Ssl = true, CaCertPath = "/nonexistent/ca.crt" };
        Assert.Throws<FileNotFoundException>(() => new MqttClientOptionsBuilder().WithConfig(cfg));
    }

    [Test]
    public void ValidCaFile_BuildsOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".crt");
        File.WriteAllBytes(path, _ca.Export(X509ContentType.Cert));
        try
        {
            var cfg = new ConfigMqtt { Host = "localhost", Ssl = true, Insecure = false, CaCertPath = path };
            var options = new MqttClientOptionsBuilder().WithConfig(cfg).Build();
            Assert.That(options.ChannelOptions.TlsOptions.UseTls, Is.True);
            Assert.That(options.ChannelOptions.TlsOptions.CertificateValidationHandler, Is.Not.Null);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void LegacySsl_AllowsUntrusted()
    {
        var options = new MqttClientOptionsBuilder().WithConfig(new ConfigMqtt { Host = "localhost", Ssl = true }).Build();
        Assert.That(options.ChannelOptions.TlsOptions.AllowUntrustedCertificates, Is.True);
    }

    [Test]
    public void ExplicitInsecureFalse_IsStrict()
    {
        var options = new MqttClientOptionsBuilder().WithConfig(new ConfigMqtt { Host = "localhost", Ssl = true, Insecure = false }).Build();
        Assert.That(options.ChannelOptions.TlsOptions.AllowUntrustedCertificates, Is.False);
    }

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca, string subject)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.Create(ca, new DateTimeOffset(ca.NotBefore).AddMinutes(1), new DateTimeOffset(ca.NotAfter).AddMinutes(-1), RandomNumberGenerator.GetBytes(8));
        return new X509Certificate2(cert.Export(X509ContentType.Cert));
    }
}
