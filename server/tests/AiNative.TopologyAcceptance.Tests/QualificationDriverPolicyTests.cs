using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public sealed class QualificationDriverPolicyTests
{
    [TestCase(null, true, "windows-high-resolution", ThreadPriority.Normal)]
    [TestCase("", true, "windows-high-resolution", ThreadPriority.Normal)]
    [TestCase("normal", true, "windows-high-resolution", ThreadPriority.Normal)]
    [TestCase(null, false, "task-delay", ThreadPriority.Normal)]
    [TestCase("normal", true, "task-delay", ThreadPriority.Normal)]
    [TestCase("above-normal", true, "windows-high-resolution", ThreadPriority.AboveNormal)]
    public void InputPriorityExperimentIsExplicit(string? value, bool windows, string waitMode, ThreadPriority expected)
        => Assert.That(QualificationDriverExecution.SelectThreadPriority(value, windows, waitMode), Is.EqualTo(expected));

    [TestCase("highest", true, "windows-high-resolution")]
    [TestCase("realtime", true, "windows-high-resolution")]
    [TestCase(" ", true, "windows-high-resolution")]
    [TestCase("above-normal", false, "windows-high-resolution")]
    [TestCase("above-normal", true, "task-delay")]
    public void UnsupportedPriorityExperimentFailsClosed(string value, bool windows, string waitMode)
        => Assert.Throws<InvalidOperationException>(() => QualificationDriverExecution.SelectThreadPriority(value, windows, waitMode));

    [TestCase(null, true, "windows-high-resolution")]
    [TestCase("", true, "windows-high-resolution")]
    [TestCase(null, false, "task-delay")]
    [TestCase("", false, "task-delay")]
    [TestCase("task-delay", true, "task-delay")]
    [TestCase("task-delay", false, "task-delay")]
    [TestCase("windows-high-resolution", true, "windows-high-resolution")]
    [TestCase("windows-high-resolution", false, "windows-high-resolution")]
    public void PlatformDefaultAndExplicitRollbackAreRecordedExactly(string? value, bool windows, string expected)
        => Assert.That(QualificationDriverExecution.SelectWaitMode(value, windows), Is.EqualTo(expected));

    [TestCase("unknown")]
    [TestCase(" ")]
    public void InvalidConfigurationFailsClosed(string value)
        => Assert.Throws<InvalidOperationException>(() => QualificationDriverExecution.SelectWaitMode(value, true));
}
