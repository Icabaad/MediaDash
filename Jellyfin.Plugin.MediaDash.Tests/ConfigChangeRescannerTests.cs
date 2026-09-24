using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class ConfigChangeRescannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidChangesRetainAllInvalidatedTypes(bool firstIsFullScan)
    {
        var state = new RunState();
        var started = new TaskCompletionSource<IReadOnlySet<IssueType>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = DispatchProxy.Create<IScheduledTaskWorker, TaskProxy>();
        ((TaskProxy)(object)worker).Handler = method => method.Name switch
        {
            "get_ScheduledTask" => RuntimeHelpers.GetUninitializedObject(typeof(ScanTask)),
            "get_State" => TaskState.Idle,
            _ => null
        };
        var manager = DispatchProxy.Create<ITaskManager, TaskProxy>();
        ((TaskProxy)(object)manager).Handler = method =>
        {
            if (method.Name == "get_ScheduledTasks") return new[] { worker };
            if (method.Name == "Execute")
            {
                started.TrySetResult(state.ConsumeNextScanTypeFilter());
                return Task.CompletedTask;
            }
            return null;
        };
        using var rescanner = new ConfigChangeRescanner(manager, null!, state, NullLogger<ConfigChangeRescanner>.Instance);
        var schedule = typeof(ConfigChangeRescanner).GetMethod("ScheduleRescan", BindingFlags.Instance | BindingFlags.NonPublic)!;
        schedule.Invoke(rescanner, new object?[] { firstIsFullScan ? null : new HashSet<IssueType> { IssueType.AudioLanguage } });
        schedule.Invoke(rescanner, new object?[] { new HashSet<IssueType> { IssueType.SubtitleLanguage } });
        var filter = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rescanner.StopAsync(CancellationToken.None);
        if (firstIsFullScan) Assert.Null(filter);
        else
        {
            Assert.NotNull(filter);
            Assert.Contains(IssueType.AudioLanguage, filter);
            Assert.Contains(IssueType.SubtitleLanguage, filter);
        }
    }

    public class TaskProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler { get; set; } = _ => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!);
    }
}
