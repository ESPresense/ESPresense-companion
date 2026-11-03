using ESPresense.Utils;

namespace ESPresense.Companion.Tests.Utils;

public class SpaIndexTests
{
    // Trimmed from a real adapter-static build of src/ui.
    private const string Index = """
        <link rel="icon" href="./favicon.png" />
        <link href="./_app/immutable/entry/start.js" rel="modulepreload">
        <link href="./_app/immutable/assets/0.css" rel="stylesheet">
        <script>
            __sveltekit_x = { base: new URL(".", location).pathname.slice(0, -1) };
            import("./_app/immutable/entry/start.js").then(async (kit) => {
                const app = await import("./_app/immutable/entry/app.js");
            });
        </script>
        """;

    [TestCase(null, "./")]
    [TestCase("", "./")]
    [TestCase("/", "./")]
    [TestCase("/devices", "./")]
    [TestCase("/devices/", "../")]
    [TestCase("/devices/irk:70f7", "../")]
    [TestCase("/calibration/devices/irk:70f7", "../../")]
    [TestCase("/devices/a%2Fb", "../")]
    public void PrefixFor_ReturnsPathBackToRoot(string? path, string expected) =>
        Assert.That(SpaIndex.PrefixFor(path), Is.EqualTo(expected));

    [Test]
    public void Rebase_AtRoot_LeavesHtmlUnchanged() =>
        Assert.That(SpaIndex.Rebase(Index, "/"), Is.SameAs(Index));

    [Test]
    public void Rebase_DeepLink_RewritesAssetsImportsAndRuntimeBase()
    {
        var html = SpaIndex.Rebase(Index, "/calibration/devices/irk:70f7");

        Assert.That(html, Does.Not.Contain("\"./"));
        Assert.That(html, Does.Contain("href=\"../../favicon.png\""));
        Assert.That(html, Does.Contain("href=\"../../_app/immutable/entry/start.js\""));
        Assert.That(html, Does.Contain("import(\"../../_app/immutable/entry/start.js\")"));
        Assert.That(html, Does.Contain("import(\"../../_app/immutable/entry/app.js\")"));
        Assert.That(html, Does.Contain("new URL(\"../../\", location)"));
    }

    [Test]
    public void Rebase_RuntimeBase_ResolvesToAppRootUnderIngress()
    {
        // HA ingress strips its prefix before the request reaches us, but the browser resolves against the full URL.
        var html = SpaIndex.Rebase(Index, "/devices/irk:70f7");
        var rel = System.Text.RegularExpressions.Regex.Match(html, "new URL\\(\"([^\"]+)\", location\\)").Groups[1].Value;
        var location = new Uri("http://ha:8123/api/hassio_ingress/tok/devices/irk:70f7");

        Assert.That(new Uri(location, rel).AbsolutePath.TrimEnd('/'), Is.EqualTo("/api/hassio_ingress/tok"));
    }
}
