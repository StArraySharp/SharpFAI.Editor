using NUnit.Framework;
using SharpFAI.Editor;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class CanvasInputRegressionTests
{
    [Test]
    public void OverlappingFloorsCycleByOrderedHitsAndWrap()
    {
        FloorHitCycle cycle = new();
        Assert.That(cycle.Pick([2, 6, 9]), Is.EqualTo(2));
        Assert.That(cycle.Pick([2, 6, 9]), Is.EqualTo(6));
        Assert.That(cycle.Pick([2, 6, 9]), Is.EqualTo(9));
        Assert.That(cycle.Pick([2, 6, 9]), Is.EqualTo(2));
        Assert.That(cycle.Pick([2, 9]), Is.EqualTo(2));
        Assert.That(cycle.Pick([9, 2]), Is.EqualTo(9));
        Assert.That(cycle.Pick([9, 2]), Is.EqualTo(2));
        Assert.That(cycle.Pick([]), Is.EqualTo(-1));
        Assert.That(cycle.Pick([9, 2]), Is.EqualTo(9));
    }

    [TestCase(false, false, 8)]
    [TestCase(false, true, 8)]
    [TestCase(true, false, 10)]
    [TestCase(true, true, 8)]
    public void FourLayoutsHaveExpectedButtons(bool shift, bool backQuote, int count)
    {
        var visible = EditorCanvasInput.Directions.Where(binding =>
            EditorCanvasInput.VisibleDirection(binding.Extra, binding.Angle is 0 or 180, shift, backQuote)).ToArray();
        Assert.That(visible, Has.Length.EqualTo(count));
        if (shift && backQuote)
            Assert.That(visible.Select(binding => binding.AngleFor(true)), Is.EquivalentTo(new[] { 15, 75, 105, 165, 195, 255, 285, 345 }));
        if (!shift)
            Assert.That(visible.Select(binding => binding.AngleFor(backQuote)), Is.EquivalentTo(new[] { 0, 45, 90, 135, 180, 225, 270, 315 }));
    }

    [Test]
    public void FineKeysMatchOriginalEditor()
    {
        string[] keys = ["J", "Y", "T", "H", "N", "V", "B", "M"];
        Assert.That(keys.Select(key => EditorCanvasInput.Directions.Single(binding => binding.Key == key).AngleFor(true)),
            Is.EqualTo(new[] { 15, 75, 105, 165, 195, 255, 285, 345 }));
    }

    [TestCase(1, 0, false, 0)]
    [TestCase(0, 1, false, 90)]
    [TestCase(-1, 0, false, 180)]
    [TestCase(0, -1, false, 270)]
    [TestCase(1, -0.01, false, 0)]
    public void RotationUsesWorldAxesAndWraps(double x, double y, bool fine, double expected) =>
        Assert.That(EditorCanvasInput.Rotation(x, y, fine), Is.EqualTo(expected).Within(1e-8));

    [Test]
    public void ShiftKeepsFineRotationAndFreePosition()
    {
        Assert.That(EditorCanvasInput.Rotation(1, .1, true), Is.EqualTo(Math.Atan(.1) * 180 / Math.PI).Within(1e-8));
        Assert.That(EditorCanvasInput.SnapPosition(.31, true, false), Is.EqualTo(.31));
        Assert.That(EditorCanvasInput.SnapPosition(.31, false, false), Is.EqualTo(.5));
        Assert.That(EditorCanvasInput.SnapPosition(.8, false, true), Is.EqualTo(.707));
    }

    [TestCase(true, 1)]
    [TestCase(false, 10)]
    public void WheelAccumulatesFractionalDeltasAndDoesNotStickAtLimits(bool fine, int step)
    {
        double remainder = 0;
        int percent = 100;
        for (int i = 0; i < 4; i++) percent = EditorCanvasInput.WheelSpeed(percent, .25f, fine, ref remainder);
        Assert.That(percent, Is.EqualTo(100 + step));
        percent = EditorCanvasInput.WheelSpeed(1000, 15, fine, ref remainder);
        Assert.That(percent, Is.EqualTo(1000));
        Assert.That(EditorCanvasInput.WheelSpeed(percent, -1, fine, ref remainder), Is.EqualTo(1000 - step));
        Assert.That(EditorCanvasInput.WheelSpeed(1, -100, fine, ref remainder), Is.EqualTo(1));
        Assert.That(EditorCanvasInput.WheelSpeed(1, .5f, fine, ref remainder), Is.EqualTo(1));
        Assert.That(EditorCanvasInput.WheelSpeed(1, .5f, fine, ref remainder), Is.EqualTo(1 + step));
    }

    [TestCase(200, 10, true)]
    [TestCase(100, .005, true)]
    [TestCase(0, 1, false)]
    [TestCase(-100, 1, false)]
    [TestCase(double.NaN, 1, false)]
    [TestCase(double.PositiveInfinity, 1, false)]
    [TestCase(1e20, 10, false)]
    [TestCase(.00001, .01, false)]
    public void CombinedPitchChecksBackendRangeInsteadOfFallingBack(double pitch, double multiplier, bool expected) =>
        Assert.That(EditorCanvasInput.TryPlaybackPitch(pitch, multiplier, out _), Is.EqualTo(expected));

    [TestCase(75, .75, .375)]
    [TestCase(100, 1, .5)]
    [TestCase(150, 1.5, .75)]
    [TestCase(1, .01, .005)]
    [TestCase(1000, 10, 5)]
    public void EachPlaybackModeUsesItsOwnMultiplier(int percent, double configured, double half)
    {
        Assert.That(EditorCanvasInput.PlaybackMultiplier(percent, PlaybackRate.Normal), Is.EqualTo(1));
        Assert.That(EditorCanvasInput.PlaybackMultiplier(percent, PlaybackRate.Configured), Is.EqualTo(configured).Within(1e-9));
        Assert.That(EditorCanvasInput.PlaybackMultiplier(percent, PlaybackRate.Half), Is.EqualTo(half).Within(1e-9));
    }
}
