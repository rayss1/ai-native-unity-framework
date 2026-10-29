#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Core.Mvvm;
using NUnit.Framework;

namespace AiNative.Client.Core.Tests
{
    public sealed class MvvmTests
    {
        [Test]
        public void PropertyNotifiesOnlyOnActualChange()
        {
            var property = new BindableProperty<string>("before");
            int changes = 0;
            property.PropertyChanged += (_, arguments) =>
            {
                Assert.That(arguments.PropertyName, Is.EqualTo("Value"));
                Assert.That(property.Value, Is.EqualTo("after"));
                changes++;
            };
            property.Value = "before";
            property.Value = "after";
            property.Value = "after";
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void BindingScopeReleasesReverseOrderAndOnlyOnce()
        {
            var history = new List<int>();
            var scope = new BindingScope();
            IDisposable early = scope.Add(() => history.Add(1));
            scope.Add(() => history.Add(2));
            early.Dispose();
            scope.Dispose();
            scope.Dispose();
            scope.Add(() => history.Add(3));
            Assert.That(history, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void BindingFailureDoesNotLeakOtherSubscriptions()
        {
            bool released = false;
            var scope = new BindingScope();
            scope.Add(() => released = true);
            scope.Add(() => throw new InvalidOperationException("release"));
            Assert.Throws<AggregateException>(scope.Dispose);
            Assert.That(released, Is.True);
            Assert.DoesNotThrow(scope.Dispose);
        }

        [Test]
        public async Task CommandRejectsReentryAndReenablesOnCompletion()
        {
            var release = new TaskCompletionSource<bool>();
            int executions = 0;
            var command = new AsyncCommand(async _ =>
            {
                executions++;
                await release.Task;
            });
            Task pending = command.ExecuteAsync().AsTask();
            Assert.That(command.IsExecuting, Is.True);
            Assert.That(command.CanExecute, Is.False);
            await command.ExecuteAsync();
            Assert.That(executions, Is.EqualTo(1));
            release.SetResult(true);
            await pending;
            Assert.That(command.IsExecuting, Is.False);
            Assert.That(command.CanExecute, Is.True);
            command.Dispose();
        }

        [Test]
        public async Task BusinessFailureIsObservedAndCanRetry()
        {
            bool fail = true;
            var command = new AsyncCommand(_ =>
            {
                if (fail)
                {
                    throw new InvalidOperationException("business-error");
                }
                return default;
            });
            await command.ExecuteAsync();
            Assert.That(command.Error, Is.TypeOf<InvalidOperationException>());
            fail = false;
            await command.ExecuteAsync();
            Assert.That(command.Error, Is.Null);
            command.Dispose();
        }

        [Test]
        public async Task CloseSuppressesLateErrorsAndAllNotifications()
        {
            var release = new TaskCompletionSource<bool>();
            CancellationToken received = default;
            var command = new AsyncCommand(async token =>
            {
                received = token;
                await release.Task;
                throw new InvalidOperationException("late-error");
            });
            int changes = 0;
            command.PropertyChanged += (_, __) => changes++;
            Task pending = command.ExecuteAsync().AsTask();
            command.Dispose();
            int beforeCompletion = changes;
            Assert.That(received.IsCancellationRequested, Is.True);
            release.SetResult(true);
            await pending;
            Assert.That(changes, Is.EqualTo(beforeCompletion));
            Assert.That(command.Error, Is.Null);
            Assert.That(command.CanExecute, Is.False);
            Assert.That(command.IsExecuting, Is.False);
        }

        [Test]
        public async Task CancellationIsNotABusinessFailure()
        {
            var command = new AsyncCommand(async token =>
                await Task.Delay(Timeout.Infinite, token)
            );
            Task pending = command.ExecuteAsync().AsTask();
            command.Cancel();
            await pending;
            Assert.That(command.Error, Is.Null);
            Assert.That(command.CanExecute, Is.True);
            command.Dispose();
        }

        [Test]
        public async Task ExplicitPredicateControlsCommandAdmission()
        {
            bool allowed = false;
            int calls = 0;
            var command = new AsyncCommand(
                _ =>
                {
                    calls++;
                    return default;
                },
                () => allowed
            );
            await command.ExecuteAsync();
            Assert.That(calls, Is.Zero);
            allowed = true;
            command.RefreshCanExecute();
            await command.ExecuteAsync();
            Assert.That(calls, Is.EqualTo(1));
            command.Dispose();
        }

        [Test]
        public async Task ClosingFromInitialNotificationPreventsBusinessExecution()
        {
            int calls = 0;
            var command = new AsyncCommand(_ =>
            {
                calls++;
                return default;
            });
            command.PropertyChanged += (_, __) => command.Dispose();
            await command.ExecuteAsync();
            Assert.That(calls, Is.Zero);
            Assert.That(command.CanExecute, Is.False);
        }
    }
}
