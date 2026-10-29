using System;
using System.IO;
using System.Reflection;
using AiNative.Client.Editor;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Application.Tests
{
    public sealed class FrameworkBootstrapBuildTests
    {
        [Test]
        public void PlayerScenesKeepProductBytesAndExistingBootstrapUnchanged()
        {
            byte[] productBefore = File.ReadAllBytes(FrameworkBootstrap.ContentScenePath);
            string[] scenes = FrameworkBootstrapBuild.GetPlayerScenes();
            byte[] bootstrapBefore = File.ReadAllBytes(FrameworkBootstrapBuild.ScenePath);
            string[] repeated = FrameworkBootstrapBuild.GetPlayerScenes();

            Assert.That(
                scenes,
                Is.EqualTo(
                    new[] { FrameworkBootstrapBuild.ScenePath, FrameworkBootstrap.ContentScenePath }
                )
            );
            Assert.That(repeated, Is.EqualTo(scenes));
            Assert.That(
                File.ReadAllBytes(FrameworkBootstrap.ContentScenePath),
                Is.EqualTo(productBefore)
            );
            Assert.That(
                File.ReadAllBytes(FrameworkBootstrapBuild.ScenePath),
                Is.EqualTo(bootstrapBefore)
            );
        }

        [Test]
        public void GeneratorRejectsUnknownSceneContentWithoutDeletingIt()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var unknown = new GameObject("UserOwnedContent");
                SceneManager.MoveGameObjectToScene(unknown, scene);
                MethodInfo validate = typeof(FrameworkBootstrapBuild).GetMethod(
                    "ValidateOwnedScene",
                    BindingFlags.NonPublic | BindingFlags.Static
                );
                Assert.That(validate, Is.Not.Null);
                TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                    validate.Invoke(null, new object[] { scene })
                );
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(1));
                Assert.That(unknown.name, Is.EqualTo("UserOwnedContent"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
