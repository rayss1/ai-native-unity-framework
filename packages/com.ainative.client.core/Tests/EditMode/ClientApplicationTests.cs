#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AiNative.Client.Core.Tests
{
    public sealed class ClientApplicationTests
    {
        [Test]
        public async Task StartStopAndRestartKeepExplicitOrder()
        {
            var history = new List<string>();
            var application = new ClientApplication(
                new[] { Module.Recording("a", history), Module.Recording("b", history) }
            );
            await application.StartAsync();
            await application.StartAsync();
            Assert.That(application.IsRunning, Is.True);
            await application.StopAsync();
            await application.StopAsync();
            await application.StartAsync();
            await application.StopAsync();
            Assert.That(
                history,
                Is.EqualTo(
                    new[]
                    {
                        "start-a",
                        "start-b",
                        "stop-b",
                        "stop-a",
                        "start-a",
                        "start-b",
                        "stop-b",
                        "stop-a",
                    }
                )
            );
            Assert.That(application.IsRunning, Is.False);
        }

        [Test]
        public void FailedPartialModuleIsStoppedBeforeItsDependencies()
        {
            var history = new List<string>();
            Module failing = Module.Recording("b", history);
            failing.Start = _ => throw new InvalidOperationException("start-failed");
            var application = new ClientApplication(
                new[] { Module.Recording("a", history), failing, Module.Recording("c", history) }
            );
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await application.StartAsync()
            );
            Assert.That(history, Is.EqualTo(new[] { "start-a", "stop-b", "stop-a" }));
            Assert.That(application.IsRunning, Is.False);
        }

        [Test]
        public async Task StopDuringStartCancelsAndWaitsForCleanup()
        {
            var entered = new TaskCompletionSource<bool>();
            var releaseCleanup = new TaskCompletionSource<bool>();
            bool cancelled = false;
            var module = new Module
            {
                Start = async token =>
                {
                    entered.SetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        throw;
                    }
                },
                Stop = async token =>
                {
                    Assert.That(token.CanBeCanceled, Is.False);
                    await releaseCleanup.Task;
                },
            };
            var application = new ClientApplication(new[] { module });
            Task start = application.StartAsync().AsTask();
            await entered.Task;
            Task stop = application.StopAsync().AsTask();
            Assert.That(stop.IsCompleted, Is.False);
            Assert.Throws<InvalidOperationException>(() => application.StartAsync());
            releaseCleanup.SetResult(true);
            await stop;
            Assert.That(cancelled, Is.True);
            Assert.ThrowsAsync<TaskCanceledException>(async () => await start);
            Assert.That(application.IsRunning, Is.False);
        }

        [Test]
        public async Task ReentrantStartsShareOneOperation()
        {
            var release = new TaskCompletionSource<bool>();
            int starts = 0;
            var application = new ClientApplication(
                new[]
                {
                    new Module
                    {
                        Start = async _ =>
                        {
                            starts++;
                            await release.Task;
                        },
                    },
                }
            );
            Task first = application.StartAsync().AsTask();
            Task second = application.StartAsync().AsTask();
            Assert.That(first, Is.SameAs(second));
            release.SetResult(true);
            await first;
            await application.StopAsync();
            Assert.That(starts, Is.EqualTo(1));
        }

        [Test]
        public async Task CleanupFailureDoesNotSkipRemainingModules()
        {
            var history = new List<string>();
            var failing = new Module
            {
                Stop = _ => throw new InvalidOperationException("stop-failed"),
            };
            var application = new ClientApplication(
                new[] { Module.Recording("a", history), failing }
            );
            await application.StartAsync();
            Assert.ThrowsAsync<AggregateException>(async () => await application.StopAsync());
            Assert.That(history, Is.EqualTo(new[] { "start-a", "stop-a" }));
            Assert.That(application.IsRunning, Is.False);
            Assert.ThrowsAsync<AggregateException>(async () => await application.StopAsync());
            Assert.That(history.Count, Is.EqualTo(2));
        }

        [Test]
        public void StartupAndCleanupFailuresAreBothPreserved()
        {
            var application = new ClientApplication(
                new[]
                {
                    new Module
                    {
                        Start = _ => throw new InvalidOperationException("start-failed"),
                        Stop = _ => throw new ArgumentException("stop-failed"),
                    },
                }
            );
            AggregateException? failure = Assert.ThrowsAsync<AggregateException>(async () =>
                await application.StartAsync()
            );
            Assert.That(failure!.Flatten().InnerExceptions.Count, Is.EqualTo(2));
        }

        [Test]
        public void DuplicateInstancesAreRejected()
        {
            var module = new Module();
            Assert.Throws<ArgumentException>(() => new ClientApplication(new[] { module, module }));
        }

        [Test]
        public void StopDuringStartupReportsFailedCleanup()
        {
            var module = new Module
            {
                Start = async token => await Task.Delay(Timeout.Infinite, token),
                Stop = _ => throw new InvalidOperationException("cleanup-failed"),
            };
            var application = new ClientApplication(new[] { module });
            Task start = application.StartAsync().AsTask();
            Assert.ThrowsAsync<AggregateException>(async () => await application.StopAsync());
            Assert.ThrowsAsync<AggregateException>(async () => await start);
        }

        [Test]
        public void AlreadyCancelledStartDoesNotTouchModules()
        {
            int starts = 0;
            var application = new ClientApplication(
                new[]
                {
                    new Module
                    {
                        Start = _ =>
                        {
                            starts++;
                            return default;
                        },
                    },
                }
            );
            Assert.Throws<OperationCanceledException>(() =>
                application.StartAsync(new CancellationToken(true))
            );
            Assert.That(starts, Is.Zero);
        }

        [Test]
        public async Task ApplicationsDoNotShareLifetime()
        {
            var first = new ClientApplication(new[] { new Module() });
            var second = new ClientApplication(new[] { new Module() });
            await first.StartAsync();
            await second.StartAsync();
            await first.StopAsync();
            Assert.That(second.IsRunning, Is.True);
            await second.StopAsync();
        }

        private sealed class Module : IClientModule
        {
            internal Func<CancellationToken, ValueTask> Start = _ => default;
            internal Func<CancellationToken, ValueTask> Stop = _ => default;

            public ValueTask StartAsync(CancellationToken cancellationToken) =>
                Start(cancellationToken);

            public ValueTask StopAsync(CancellationToken cancellationToken) =>
                Stop(cancellationToken);

            internal static Module Recording(string name, List<string> history)
            {
                return new Module
                {
                    Start = _ =>
                    {
                        history.Add("start-" + name);
                        return default;
                    },
                    Stop = _ =>
                    {
                        history.Add("stop-" + name);
                        return default;
                    },
                };
            }
        }
    }
}
