using System.Reflection;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;
using Moq;

namespace LogFlow.Tests;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public class ExLoggerTests
{
    private readonly Mock<ILogger> _mockLogger;

    public ExLoggerTests()
    {
        _mockLogger = new Mock<ILogger>();
        _ = _mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    #region Core Logging Tests

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void Log_NoArgs_UsesDelegate(LogLevel level)
    {
        ExLogger.Log(_mockLogger.Object, level, "Test message");

        _mockLogger.Verify(l => l.IsEnabled(level), Times.AtLeastOnce);

        _mockLogger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void Log_WithArgs_UsesStructuredLogging()
    {
        ExLogger.Log(_mockLogger.Object, LogLevel.Information, "User {UserId} logged in", 42);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);

        _mockLogger.Verify(l => l.IsEnabled(LogLevel.Information), Times.AtLeastOnce);
    }

    [Fact]
    public void Log_WithExceptionAndArgs_Works()
    {
        var ex = new InvalidOperationException("Oops");

        ExLogger.Log(_mockLogger.Object, LogLevel.Error, ex, "Error {Code}", 500);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            ex,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void Log_NoArgs_ExceptionVariant_Works()
    {
        var ex = new Exception("boom");

        ExLogger.Log(_mockLogger.Object, LogLevel.Error, "Simple message", ex);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            ex,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void Log_GenericOverloads_Work()
    {
        ExLogger.Log(_mockLogger.Object, LogLevel.Debug, "Test {X}", 1);
        ExLogger.Log(_mockLogger.Object, LogLevel.Debug, "Test {X} {Y}", 1, 2);
        _mockLogger.Verify(l => l.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Exactly(2));
    }

    #endregion Core Logging Tests

    #region Convenience Wrappers

    [Fact]
    public void ExLogInformation_Calls_LogNoArgs_WhenNoArgs()
    {
        // Act
        _mockLogger.Object.ExLogInformation("Simple info");

        // Assert
        _mockLogger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void ExLogError_WithException_Works()
    {
        var ex = new InvalidOperationException("error");

        _mockLogger.Object.ExLogError("error occurred", ex);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            ex,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void ExLogCritical_WithoutException_Works()
    {
        _mockLogger.Object.ExLogCritical("critical issue");

        _mockLogger.Verify(l => l.Log(
            LogLevel.Critical,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    #endregion Convenience Wrappers

    #region Exception Formatting

    [Fact]
    public void ExLogErrorException_FormatsProperly()
    {
        var ex = new Exception("outer", new InvalidOperationException("inner"));

        _mockLogger.Object.ExLogErrorException(ex, "Custom Title", moreDetailsEnabled: true);

        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            ex,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.Once);
    }

    [Fact]
    public void ExceptionFormatter_CanBeReplaced()
    {
        var called = false;
        ExLogger.ExceptionFormatter = (_, __, ___) => { called = true; return "custom"; };

        var ex = new Exception("boom");
        _mockLogger.Object.ExLogErrorException(ex);

        Assert.True(called);
    }

    [Fact]
    public void GetEventId_ReturnsExpectedValues()
    {
        var method = typeof(ExLogger).GetMethod("GetEventId", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
        {
            var id = (EventId)method!.Invoke(null, [level])!;
            Assert.NotEqual(default, id);
            Assert.False(string.IsNullOrEmpty(id.Name));
        }
    }

    #endregion Exception Formatting

    #region UTC Cache Timer

    [Fact]
    public async Task CachedUtc_IsFormattedCorrectly()
    {
        await Task.Delay(5);

        var value = typeof(ExLogger)
            .GetField("_cachedUtc", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.NotNull(value);
        Assert.Contains("T", value);
        Assert.EndsWith("Z", value);
    }

    [Fact]
    public void ShutdownUtcTimerIsSafeToCallMultipleTimes()
    {
        ExLogger.ShutdownUtcTimer();
        ExLogger.ShutdownUtcTimer();

        var cachedUtc = typeof(ExLogger)
            .GetField("_cachedUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.False(string.IsNullOrWhiteSpace(cachedUtc));
        Assert.Contains("T", cachedUtc);
        Assert.EndsWith("Z", cachedUtc);
    }

    #endregion UTC Cache Timer

    #region Scope Handling

    [Fact]
    public void ExBeginScope_SingleKeyValue_Works()
    {
        using var scope = _mockLogger.Object.ExBeginScope("UserId", 123);
        Assert.NotNull(scope);
    }

    [Fact]
    public void ExBeginScope_Dictionary_Works()
    {
        var dict = new Dictionary<string, object> { ["User"] = "Alice", ["Action"] = "Login" };
        using var scope = _mockLogger.Object.ExBeginScope(dict);
        Assert.NotNull(scope);
    }

    [Fact]
    public void ExBeginScope_EmptyDict_ReturnsNullScope()
    {
        var dict = new Dictionary<string, object>();
        using var scope = _mockLogger.Object.ExBeginScope(dict);
        Assert.NotNull(scope);
    }

    [Fact]
    public void Scope_ToString_ProducesExpectedOutput()
    {
        var dict = new Dictionary<string, object> { ["Key1"] = "A", ["Key2"] = "B" };
        using var scope = _mockLogger.Object.ExBeginScope(dict);
        Assert.Contains("Key1=A", scope.ToString());
    }

    #endregion Scope Handling

    #region Async Flush and Hooks

    [Fact]
    public async Task FlushAsync_Works()
    {
        var task = ExLogger.FlushAsync();

        await task;
        Assert.True(task.IsCompletedSuccessfully, "FlushAsync should complete successfully without throwing.");
    }

    [Fact]
    public void UseAsyncSinkProvider_ChangesFilter()
    {
        var called = false;
        ExLogger.UseAsyncSinkProvider((_, __, ___) => { called = true; return true; });

        var filter = typeof(ExLogger)
            .GetProperty("AsyncSinkFilter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as Func<LogLevel, string, Exception, bool>;

        _ = filter!(LogLevel.Information, "msg", null);
        Assert.True(called);
    }

    #endregion Async Flush and Hooks
}