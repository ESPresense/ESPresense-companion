using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ESPresense.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;

namespace ESPresense.Extensions;

public static class MqttClientOptionsBuilderExtensions
{
    public static MqttClientOptionsBuilder WithConfig(this MqttClientOptionsBuilder mcob, ConfigMqtt mqtt, ILogger? logger = null)
    {
        mcob
            .WithTcpServer(mqtt.Host ?? "localhost", mqtt.Port)
            .WithCredentials(mqtt.Username, mqtt.Password)
            .WithProtocolVersion(MqttProtocolVersion.V311);

        if (mqtt.Ssl == null)
            return mcob;

        var useTls = mqtt.Ssl == true;
        X509Certificate2? ca = useTls && mqtt.Insecure != true ? LoadCaCertificate(mqtt.CaCertPath, logger) : null;

        mcob.WithTlsOptions(o =>
        {
            o.UseTls(useTls);
            if (!useTls)
                return;

            if (mqtt.Insecure == true)
            {
                logger?.LogWarning("MQTT mqtt.insecure is true: TLS certificate validation is disabled");
                o.WithAllowUntrustedCertificates();
                o.WithCertificateValidationHandler(_ => true);
            }
            else if (ca != null)
            {
                o.WithCertificateValidationHandler(args => ValidateWithCa(args.Certificate, args.Chain, args.SslPolicyErrors, ca));
            }
            else if (mqtt.Insecure == null)
            {
                // Legacy behavior: ssl without explicit insecure setting accepted untrusted certificates.
                o.WithAllowUntrustedCertificates();
            }
        });
        return mcob;
    }

    private static X509Certificate2? LoadCaCertificate(string? path, ILogger? logger)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (!File.Exists(path))
            throw new FileNotFoundException($"mqtt.ca_cert_path '{path}' does not exist", path);

        try
        {
            return new X509Certificate2(path);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unable to load MQTT CA certificate from {Path}", path);
            throw;
        }
    }

    internal static bool ValidateWithCa(X509Certificate certificate, X509Chain? presentedChain, SslPolicyErrors errors, X509Certificate2 ca)
    {
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        if (presentedChain != null)
            foreach (var element in presentedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);

        var leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}
