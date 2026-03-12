using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

public class AnchorLocatorTests
{
    [Test]
    public void Locate_SetsCorrectAnchorLocation()
    {
        // Arrange
        var anchorLocation = new Point3D(1.5, 2.5, 0.5);
        var locator = new AnchorLocator(anchorLocation);
        var scenario = new Scenario(null, locator, "Test");

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(scenario.Location, Is.EqualTo(anchorLocation));
        Assert.That(scenario.Confidence, Is.EqualTo(100));
        Assert.That(scenario.Scale, Is.EqualTo(1.0));
        Assert.That(scenario.Error, Is.EqualTo(0.0));
        Assert.That(scenario.Iterations, Is.EqualTo(0));
    }

    [Test]
    public void Locate_ReturnsFalseWhenLocationNotChanged()
    {
        // Arrange
        var anchorLocation = new Point3D(1.0, 1.0, 1.0);
        var locator = new AnchorLocator(anchorLocation);
        var scenario = new Scenario(null, locator, "Test");

        // Pre-set the location to the same as anchor
        scenario.UpdateLocation(anchorLocation);

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(moved, Is.False);
    }

    [Test]
    public void Locate_ReturnsTrueWhenLocationChanged()
    {
        // Arrange
        var anchorLocation = new Point3D(2.0, 2.0, 1.0);
        var locator = new AnchorLocator(anchorLocation);
        var scenario = new Scenario(null, locator, "Test");

        // Pre-set the location to something different
        scenario.UpdateLocation(new Point3D(0.0, 0.0, 0.0));

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(moved, Is.True);
        Assert.That(scenario.Location.X, Is.EqualTo(anchorLocation.X).Within(0.001));
        Assert.That(scenario.Location.Y, Is.EqualTo(anchorLocation.Y).Within(0.001));
        Assert.That(scenario.Location.Z, Is.EqualTo(anchorLocation.Z).Within(0.001));
    }

    [Test]
    public void Locate_ReturnsTrueWhenLocationIsNull()
    {
        // Arrange
        var anchorLocation = new Point3D(3.0, 4.0, 0.0);
        var locator = new AnchorLocator(anchorLocation);
        var scenario = new Scenario(null, locator, "Test");

        // Leave location as null (default)

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(moved, Is.True);
        Assert.That(scenario.Location.X, Is.EqualTo(anchorLocation.X).Within(0.001));
        Assert.That(scenario.Location.Y, Is.EqualTo(anchorLocation.Y).Within(0.001));
        Assert.That(scenario.Location.Z, Is.EqualTo(anchorLocation.Z).Within(0.001));
    }
}