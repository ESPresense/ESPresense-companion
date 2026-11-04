using ESPresense.Utils;

namespace ESPresense.Companion.Tests;

public class LogSanitizerTests
{
    [TestCase("espresense/rooms/node-1/#", "espresense/rooms/node-1/#")]
    [TestCase("a\nb", "ab")]
    [TestCase("a\r\nb", "ab")]
    [TestCase("a\rb\n\nc", "abc")]
    [TestCase("", "")]
    public void Strip_RemovesLineBreaks(string input, string expected) =>
        Assert.That(LogSanitizer.Strip(input), Is.EqualTo(expected));

    [Test]
    public void StripOrNull_PassesNullThrough() =>
        Assert.That(LogSanitizer.StripOrNull(null), Is.Null);
}
