using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.UI.Tests
{
    public sealed class UiServiceTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject instance in _objects)
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            _objects.Clear();
        }

        [Test]
        public async Task TwentyOpenCloseCyclesReturnOwnershipToBaseline()
        {
            var service = new UiService();
            int created = 0;
            int released = 0;
            service.Register(
                "page",
                UiLayer.Page,
                _ =>
                {
                    created++;
                    return new ValueTask<UiView>(View("page", () => released++));
                }
            );
            for (int cycle = 0; cycle < 20; cycle++)
            {
                await service.OpenAsync("page");
                await service.OpenAsync("page");
                Assert.That(service.OpenCount, Is.EqualTo(1));
                await service.CloseAsync("page");
                await service.CloseAsync("page");
                Assert.That(service.OpenCount, Is.Zero);
                Assert.That(released, Is.EqualTo(cycle + 1));
            }
            await service.DisposeAsync();
            await service.DisposeAsync();
            Assert.That(created, Is.EqualTo(20));
            Assert.That(released, Is.EqualTo(20));
            Assert.That(service.BlocksGameplay, Is.False);
        }

        [Test]
        public async Task ModalBlocksUnderlyingPageAndBackRestoresIt()
        {
            var service = new UiService();
            UiView page = View("page");
            UiView modal = View("modal");
            service.Register("page", UiLayer.Page, _ => new ValueTask<UiView>(page));
            service.Register("modal", UiLayer.Modal, _ => new ValueTask<UiView>(modal));
            await service.OpenAsync("page");
            await service.OpenAsync("modal");
            Assert.That(service.BlocksGameplay, Is.True);
            Assert.That(page.Root.GetComponent<CanvasGroup>().interactable, Is.False);
            Assert.That(modal.Root.GetComponent<CanvasGroup>().interactable, Is.True);
            await service.BackAsync();
            Assert.That(service.OpenCount, Is.EqualTo(1));
            Assert.That(service.BlocksGameplay, Is.False);
            Assert.That(page.Root.GetComponent<CanvasGroup>().interactable, Is.True);
            await service.DisposeAsync();
        }

        [Test]
        public async Task PageOpenedAfterModalCannotBypassItsInputBlock()
        {
            var service = new UiService();
            UiView page = View("page");
            UiView modal = View("modal");
            service.Register("page", UiLayer.Page, _ => new ValueTask<UiView>(page));
            service.Register("modal", UiLayer.Modal, _ => new ValueTask<UiView>(modal));
            await service.OpenAsync("modal");
            await service.OpenAsync("page");
            Assert.That(service.BlocksGameplay, Is.True);
            Assert.That(page.Root.GetComponent<CanvasGroup>().interactable, Is.False);
            Assert.That(modal.Root.GetComponent<CanvasGroup>().interactable, Is.True);
            await service.DisposeAsync();
        }

        [Test]
        public async Task CancellationReleasesLateFactoryResult()
        {
            var service = new UiService();
            var completion = new TaskCompletionSource<UiView>();
            int released = 0;
            service.Register("delayed", UiLayer.Page, _ => new ValueTask<UiView>(completion.Task));
            using (var cancellation = new CancellationTokenSource())
            {
                Task pending = service.OpenAsync("delayed", cancellation.Token).AsTask();
                cancellation.Cancel();
                completion.SetResult(View("late", () => released++));
                Exception failure = await FailureOf(pending);
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                Assert.That(service.OpenCount, Is.Zero);
                Assert.That(released, Is.EqualTo(1));
            }
            await service.DisposeAsync();
        }

        [Test]
        public async Task DisposeWaitsForPendingFactoryAndReleasesEveryView()
        {
            var service = new UiService();
            var completion = new TaskCompletionSource<UiView>();
            int released = 0;
            service.Register(
                "existing",
                UiLayer.Page,
                _ => new ValueTask<UiView>(View("existing", () => released++))
            );
            service.Register("delayed", UiLayer.Modal, _ => new ValueTask<UiView>(completion.Task));
            await service.OpenAsync("existing");
            Task opening = service.OpenAsync("delayed").AsTask();
            Task disposing = service.DisposeAsync().AsTask();
            Assert.That(disposing.IsCompleted, Is.False);
            completion.SetResult(View("late", () => released++));
            Assert.That(await FailureOf(opening), Is.InstanceOf<OperationCanceledException>());
            await disposing;
            Assert.That(released, Is.EqualTo(2));
            Assert.That(service.OpenCount, Is.Zero);
        }

        [Test]
        public async Task CancelCallbackFailureCannotSkipExistingViewCleanup()
        {
            var service = new UiService();
            var completion = new TaskCompletionSource<UiView>();
            CancellationTokenRegistration callback = default;
            int released = 0;
            service.Register(
                "existing",
                UiLayer.Page,
                _ => new ValueTask<UiView>(View("existing", () => released++))
            );
            service.Register(
                "delayed",
                UiLayer.Modal,
                token =>
                {
                    callback = token.Register(() =>
                        throw new InvalidOperationException("cancel-callback")
                    );
                    return new ValueTask<UiView>(completion.Task);
                }
            );
            await service.OpenAsync("existing");
            Task opening = service.OpenAsync("delayed").AsTask();
            Task disposing = DisposeService(service);
            completion.SetResult(View("late", () => released++));
            await FailureOf(opening);
            Exception disposalFailure = await FailureOf(disposing);
            callback.Dispose();
            Assert.That(disposalFailure, Is.Not.Null);
            Assert.That(released, Is.EqualTo(2));
            Assert.That(service.OpenCount, Is.Zero);
        }

        [Test]
        public async Task ReleaseFailureRemainsIdempotentAndDoesNotSkipOtherViews()
        {
            var service = new UiService();
            int released = 0;
            var faulty = new UiView(
                Create("faulty"),
                () =>
                {
                    released++;
                    throw new InvalidOperationException("release-failed");
                }
            );
            service.Register(
                "page",
                UiLayer.Page,
                _ => new ValueTask<UiView>(View("page", () => released++))
            );
            service.Register("modal", UiLayer.Modal, _ => new ValueTask<UiView>(faulty));
            await service.OpenAsync("page");
            await service.OpenAsync("modal");
            Assert.That(
                await FailureOf(DisposeService(service)),
                Is.InstanceOf<AggregateException>()
            );
            Assert.That(service.OpenCount, Is.Zero);
            Assert.That(released, Is.EqualTo(2));
            Assert.That(await FailureOf(DisposeView(faulty)), Is.Not.Null);
            Assert.That(released, Is.EqualTo(2));
        }

        private GameObject Create(string name)
        {
            var instance = new GameObject(name);
            _objects.Add(instance);
            return instance;
        }

        private UiView View(string name, Action onRelease = null)
        {
            GameObject instance = Create(name);
            return new UiView(
                instance,
                () =>
                {
                    onRelease?.Invoke();
                    UnityEngine.Object.DestroyImmediate(instance);
                    return default;
                }
            );
        }

        private static async Task DisposeService(UiService service)
        {
            await service.DisposeAsync();
        }

        private static async Task DisposeView(UiView view)
        {
            await view.DisposeAsync();
        }

        private static async Task<Exception> FailureOf(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
