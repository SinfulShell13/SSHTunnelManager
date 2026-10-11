using System.Diagnostics;
using SSHTunnelManager.Services;
using Xunit;

namespace SSHTunnelManager.Tests;

public sealed class ConfigWatcherTests
{
    [Fact]
    public async Task Watcher_ObservesCreationAtomicReplacementAndDeletionInInitiallyMissingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SSHTunnelManagerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sshDirectory = Path.Combine(directory, "ssh");
            var config = Path.Combine(sshDirectory, "config");
            var calls = 0;
            using var watcher = new ConfigWatcher(config, () => Interlocked.Increment(ref calls));
            Assert.False(Directory.Exists(sshDirectory));

            Directory.CreateDirectory(sshDirectory);
            File.WriteAllText(config, "Host demo\n  RemoteForward 29099 localhost:29097\n");
            await WaitUntilAsync(() => Volatile.Read(ref calls) >= 1);

            var previousCalls = Volatile.Read(ref calls);
            var replacement = Path.Combine(sshDirectory, "config.tmp");
            File.WriteAllText(replacement, "Host demo\n  #RemoteForward 29099 localhost:29097\n");
            File.Move(replacement, config, overwrite: true);
            await WaitUntilAsync(() => Volatile.Read(ref calls) > previousCalls);

            previousCalls = Volatile.Read(ref calls);
            File.Delete(config);
            await WaitUntilAsync(() => Volatile.Read(ref calls) > previousCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "等待文件监视事件超时。");
            await Task.Delay(25);
        }
    }
}


