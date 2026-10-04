using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AiNative.Client.Fantasy;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class AndroidClientLaunchConfigurationTests
    {
        [Test]
        public void PublicIntent_UsesTlsAndConfinesEvidenceToPersistentStorage()
        {
            var extras = new Dictionary<string, string> {
                ["ainative.endpoint"] = "https://192.0.2.10:23001", ["ainative.pin"] = new string('a', 64),
                ["ainative.runId"] = "device_01", ["ainative.timeout"] = "240", ["ainative.automated"] = "true",
                ["username"] = "never-persist", ["password"] = "never-persist", ["ainative.result"] = "D:/outside.json"
            };
            object config = Parse(extras);
            var options = (GateConnectionOptions)Value(config, "GateOptions");
            Assert.That((options.Host, options.Port, options.UseTls, options.CertificateSha256), Is.EqualTo(("192.0.2.10", 23001, true, new string('A', 64))));
            Assert.That(Value(config, "Automated"), Is.True);
            Assert.That(Value(config, "TimeoutSeconds"), Is.EqualTo(240f));
            Assert.That(Value(config, "ResultPath"), Is.EqualTo(Path.Combine(Path.GetTempPath(), "ainative-android-device_01.json")));
            Assert.That(Value(config, "RunId"), Is.EqualTo("device_01"));
        }

        [Test]
        public void OrdinaryLaunch_RemainsInteractiveAndGetsPersistentEvidencePath()
        {
            object config = Parse(new Dictionary<string, string>());
            Assert.That(Value(config, "Automated"), Is.False);
            Assert.That(Path.GetDirectoryName((string)Value(config, "ResultPath")), Is.EqualTo(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
            Assert.That(Value(config, "TimeoutSeconds"), Is.EqualTo(120f));
        }

        [TestCase("ainative.runId", "../escape")]
        [TestCase("ainative.runId", "")]
        [TestCase("ainative.timeout", "29")]
        [TestCase("ainative.timeout", "3601")]
        [TestCase("ainative.endpoint", "http://example.test:23001")]
        [TestCase("ainative.endpoint", "https://user:secret@example.test:23001")]
        [TestCase("ainative.endpoint", "https://example.test:23001/path")]
        [TestCase("ainative.pin", "not-a-sha256")]
        [TestCase("ainative.automated", "yes")]
        public void InvalidPublicConfiguration_IsRejected(string key, string value)
        {
            Exception rejection = null;
            try { Parse(new Dictionary<string, string> { [key] = value }); }
            catch (TargetInvocationException exception) { rejection = exception.InnerException; }
            Assert.That(rejection, Is.InstanceOf<ArgumentException>());
        }

        private static object Parse(IReadOnlyDictionary<string, string> extras)
        {
            Type type = typeof(TopologyClientFlow).Assembly.GetType("AiNative.Client.Application.AndroidClientLaunchConfiguration");
            Assert.That(type, Is.Not.Null, "Android public intent configuration is not implemented.");
            return type.GetMethod("Parse").Invoke(null, new object[] { extras, Path.GetTempPath() });
        }
        private static object Value(object config, string property) => config.GetType().GetProperty(property).GetValue(config);
    }
}
