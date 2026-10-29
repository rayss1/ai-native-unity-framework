using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Async;
using AiNative.Client.Composition;
using NUnit.Framework;
using VContainer;

namespace AiNative.Client.Runtime.Tests
{
    public sealed class ClientIntegrationContractTests
    {
        [Test]
        public void ChildScopeOwnsItsServicesWithoutDisposingParentServices()
        {
            var counter = new DisposalCounter();
            using (
                var application = new ClientServiceScope(builder =>
                {
                    builder.RegisterInstance(counter);
                    builder.Register<OwnedService>(Lifetime.Scoped);
                })
            )
            {
                OwnedService parentService = application.Resolve<OwnedService>();
                ClientServiceScope child = application.CreateChild(_ => { });
                OwnedService childService = child.Resolve<OwnedService>();
                Assert.That(childService, Is.Not.SameAs(parentService));
                Assert.That(child.Resolve<OwnedService>(), Is.SameAs(childService));
                child.Dispose();
                child.Dispose();
                Assert.That(counter.Disposed, Is.EqualTo(1));
                Assert.That(parentService.IsDisposed, Is.False);
                Assert.Throws<ObjectDisposedException>(() => child.Resolve<OwnedService>());
            }
            Assert.That(counter.Disposed, Is.EqualTo(2));
        }

        [Test]
        public void IndependentApplicationsNeverShareScopedInstances()
        {
            using (var first = CreateApplication())
            using (var second = CreateApplication())
            {
                Assert.That(
                    first.Resolve<OwnedService>(),
                    Is.Not.SameAs(second.Resolve<OwnedService>())
                );
                first.Dispose();
                Assert.That(second.Resolve<OwnedService>().IsDisposed, Is.False);
            }
        }

        [Test]
        public void ExplicitRegistrationResolvesConstructorDependencies()
        {
            using (var application = CreateApplication())
            {
                Assert.That(
                    application.Resolve<OwnedService>().Counter,
                    Is.SameAs(application.Resolve<DisposalCounter>())
                );
            }
        }

        [Test]
        public void UnityAwaitAdapterRejectsNullOperation()
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await UnityAsync.CompleteAsync(null)
            );
        }

        [Test]
        public void AlreadyCancelledUnityYieldDoesNotResumeNormally()
        {
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await UnityAsync.NextFrameAsync(new CancellationToken(true))
            );
        }

        [Test]
        public async Task SceneServiceRejectsUnknownLogicalSceneBeforeNativeLoad()
        {
            var service = new ClientSceneService(_ => string.Empty);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await service.SwitchAsync(new SceneId("unknown"))
            );
            await service.UnloadAsync();
        }

        [Test]
        public void SceneServiceHonorsCancellationBeforeResolvingPath()
        {
            int resolves = 0;
            var service = new ClientSceneService(_ =>
            {
                resolves++;
                return "unused";
            });
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await service.SwitchAsync(new SceneId("cancelled"), new CancellationToken(true))
            );
            Assert.That(resolves, Is.Zero);
        }

        private static ClientServiceScope CreateApplication()
        {
            return new ClientServiceScope(builder =>
            {
                builder.Register<DisposalCounter>(Lifetime.Singleton);
                builder.Register<OwnedService>(Lifetime.Scoped);
            });
        }

        public sealed class DisposalCounter
        {
            public int Disposed { get; set; }
        }

        public sealed class OwnedService : IDisposable
        {
            public DisposalCounter Counter { get; }
            public bool IsDisposed { get; private set; }

            public OwnedService(DisposalCounter counter)
            {
                Counter = counter;
            }

            public void Dispose()
            {
                Assert.That(
                    IsDisposed,
                    Is.False,
                    "Container must dispose owned instances exactly once."
                );
                IsDisposed = true;
                Counter.Disposed++;
            }
        }
    }
}
