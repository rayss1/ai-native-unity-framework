using System.Text.Json;
using NUnit.Framework;

public class QualificationTerminalDeliveryGateTests
{
    static JsonElement Report(long failures, long timeouts) => JsonSerializer.SerializeToElement(new { nodes = new[] {
        new { node = "battle-1", terminalDeliveryFailures = 0L, terminalDeliveryTimeouts = 0L },
        new { node = "battle-2", terminalDeliveryFailures = failures, terminalDeliveryTimeouts = timeouts } } });
    [Test]
    public void CleanMeasurementCannotHideFailureOfTheLastRetiringRoom()
    {
        Assert.That(QualificationTerminalDeliveryGate.Passes(Report(0, 0)), Is.True);
        Assert.That(QualificationTerminalDeliveryGate.Passes(Report(1, 1)), Is.False);
    }
    [TestCase(1, 0)]
    [TestCase(0, 1)]
    public void EitherFailureCounterRejectsQualification(long failures, long timeouts)
        => Assert.That(QualificationTerminalDeliveryGate.Passes(Report(failures, timeouts)), Is.False);
    [Test]
    public void MissingNodeCannotBeAZeroFailurePass()
        => Assert.That(QualificationTerminalDeliveryGate.Passes(JsonSerializer.SerializeToElement(new { nodes = Array.Empty<object>() })), Is.False);
}
