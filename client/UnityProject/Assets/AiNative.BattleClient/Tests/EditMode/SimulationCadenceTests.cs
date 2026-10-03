using AiNative.Gameplay;
using NUnit.Framework;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class SimulationCadenceTests
    {
        [Test]
        public void PersistedUnityClockMatchesTheArenaStep()
        {
            string path = Path.GetFullPath(Path.Combine(global::UnityEngine.Application.dataPath, "../ProjectSettings/TimeManager.asset"));
            string text = File.ReadAllText(path);
            long persistedCount = long.Parse(Regex.Match(text, @"m_Count:\s*(\d+)").Groups[1].Value);
            long denominator = long.Parse(Regex.Match(text, @"m_Denominator:\s*(\d+)").Groups[1].Value);
            long numerator = long.Parse(Regex.Match(text, @"m_Numerator:\s*(\d+)").Groups[1].Value);
            Assert.That((double)persistedCount * denominator / numerator,
                Is.EqualTo(1d / ArenaMovement.TickRate).Within(0.0000001d));
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TimeManager.asset")[0]);
            var fixedStep = settings.FindProperty("Fixed Timestep");
            long count = fixedStep.FindPropertyRelative("m_Count").longValue;
            var rate = fixedStep.FindPropertyRelative("m_Rate");
            double seconds = (double)count * rate.FindPropertyRelative("m_Denominator").intValue /
                rate.FindPropertyRelative("m_Numerator").intValue;
            Assert.That(seconds, Is.EqualTo(1d / ArenaMovement.TickRate).Within(0.0000001d));
        }

        [Test]
        public void UnityFixedUpdatesAdvanceExactlyOneSecondOfArenaSimulation()
        {
            float step = Time.fixedDeltaTime;
            Assert.That(step, Is.EqualTo(1f / ArenaMovement.TickRate).Within(0.0000001f),
                "Each FixedUpdate predicts one 60 Hz rule step; the persisted Unity clock must match.");
            Assert.That(step * ArenaMovement.TickRate, Is.EqualTo(1f).Within(0.00001f));
        }
    }
}
