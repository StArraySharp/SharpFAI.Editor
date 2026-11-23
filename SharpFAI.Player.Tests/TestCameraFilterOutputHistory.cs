using SharpFAI.Player.Engine.Filters;

namespace SharpFAI.Player.Tests;

public sealed class TestCameraFilterOutputHistory
{
    [Test]
    public void DifferentFiltersShareLastSuccessfulOutputAcrossInvalidAndOffFrames()
    {
        var history = new CameraFilterOutputHistory<string>();
        history.Observe(0, 640, 480);
        Assert.That(history.Fallback(new InvalidFilterTargetSizeException("0×0")), Is.Null);

        history.Remember("FlyVision");
        history.Observe(1, 640, 480);
        Assert.That(history.Fallback(new InvalidFilterTargetSizeException("8bits ONLY: 0×0")), Is.EqualTo("FlyVision"));

        history.Remember("8bits ONLY: 100");
        foreach (string invalid in new[] { "0", "-100" })
            Assert.That(history.Fallback(new InvalidFilterTargetSizeException(invalid)), Is.EqualTo("8bits ONLY: 100"));

        // OFF observes time and size without recording an unfiltered scene.
        history.Observe(2, 640, 480);
        history.Observe(3, 640, 480);
        Assert.That(history.Fallback(new DivideByZeroException()), Is.EqualTo("8bits ONLY: 100"));
        Assert.That(history.Fallback(new OverflowException()), Is.EqualTo("8bits ONLY: 100"));
    }

    [Test]
    public void ResourceErrorsAlwaysPassThroughWithoutReplacingHistory()
    {
        var history = new CameraFilterOutputHistory<object>();
        var output = new object();
        history.Observe(10, 640, 480);
        history.Remember(output);
        Assert.That(history.Fallback(new FilterResourceException("Missing shader")), Is.Null);
        Assert.That(history.Fallback(new FilterResourceException("Missing texture")), Is.Null);
        Assert.That(history.Output, Is.SameAs(output));
    }

    [TestCase(9, 640, 480)]
    [TestCase(11, 800, 480)]
    [TestCase(11, 640, 600)]
    [TestCase(11, 0, 0)]
    public void SeekAndResizeDiscardHistoryEvenOnOffFrames(double time, int width, int height)
    {
        var history = new CameraFilterOutputHistory<object>();
        var output = new object();
        history.Observe(10, 640, 480);
        history.Remember(output);
        Assert.That(history.Observe(time, width, height), Is.SameAs(output));
        Assert.That(history.Output, Is.Null);
        Assert.That(history.Observe(12, 640, 480), Is.Null);
        Assert.That(history.Fallback(new DivideByZeroException()), Is.Null);
    }

    [Test]
    public void EqualTimeKeepsHistoryAndClearReturnsOwnershipOnlyOnce()
    {
        var history = new CameraFilterOutputHistory<object>();
        var output = new object();
        history.Observe(10, 640, 480);
        history.Remember(output);
        Assert.That(history.Observe(10, 640, 480), Is.Null);
        Assert.That(history.Output, Is.SameAs(output));
        Assert.That(history.Clear(), Is.SameAs(output));
        Assert.That(history.Clear(), Is.Null);
    }
}
