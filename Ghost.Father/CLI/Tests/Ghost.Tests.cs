using Ghost.Core;
using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Exceptions;
using Ghost.Core.Logging;
using Ghost.Core.Modules;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Ghost.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghost.Tests
{
    // These tests cover core ghost components and their interactions

    #region Core Tests

    [TestClass]
    public class GhostExceptionTests
    {
        [TestMethod]
        public void Constructor_WithMessage_SetsMessageAndDefaultErrorCode()
        {
            // Arrange & Act
            var exception = new GhostException("Test message");

            // Assert
            Assert.AreEqual("Test message", exception.Message);
            Assert.AreEqual(ErrorCode.Unknown, exception.Code);
        }

        [TestMethod]
        public void Constructor_WithMessageAndErrorCode_SetsMessageAndErrorCode()
        {
            // Arrange & Act
            var exception = new GhostException("Test message", ErrorCode.ConfigurationError);

            // Assert
            Assert.AreEqual("Test message", exception.Message);
            Assert.AreEqual(ErrorCode.ConfigurationError, exception.Code);
        }

        [TestMethod]
        public void Constructor_WithMessageAndInnerException_SetsMessageAndInnerException()
        {
            // Arrange
            var innerException = new InvalidOperationException("Inner exception");

            // Act
            var exception = new GhostException("Test message", innerException);

            // Assert
            Assert.AreEqual("Test message", exception.Message);
            Assert.AreEqual(innerException, exception.InnerException);
            Assert.AreEqual(ErrorCode.Unknown, exception.Code);
        }

        [TestMethod]
        public void Constructor_WithMessageAndInnerExceptionAndErrorCode_SetsAllProperties()
        {
            // Arrange
            var innerException = new InvalidOperationException("Inner exception");

            // Act
            var exception = new GhostException("Test message", innerException, ErrorCode.ProcessError);

            // Assert
            Assert.AreEqual("Test message", exception.Message);
            Assert.AreEqual(innerException, exception.InnerException);
            Assert.AreEqual(ErrorCode.ProcessError, exception.Code);
        }
    }

    [TestClass]
    public class GhostConfigTests
    {
        [TestMethod]
        public void Constructor_CreatesDefaultConfig()
        {
            // Arrange & Act
            var config = new GhostConfig();

            // Assert
            Assert.IsNotNull(config.App);
            Assert.IsNotNull(config.Core);
            Assert.IsNotNull(config.Modules);
        }

        [TestMethod]
        public void HasModule_WithExistingEnabledModule_ReturnsTrue()
        {
            // Arrange
            var config = new GhostConfig
            {
                Modules = new Dictionary<string, ModuleConfig>
                {
                    ["test"] = new LoggingConfig { Enabled = true }
                }
            };

            // Act
            var result = config.HasModule("test");

            // Assert
            Assert.IsTrue(result);
        }

        [TestMethod]
        public void HasModule_WithExistingDisabledModule_ReturnsFalse()
        {
            // Arrange
            var config = new GhostConfig
            {
                Modules = new Dictionary<string, ModuleConfig>
                {
                    ["test"] = new LoggingConfig { Enabled = false }
                }
            };

            // Act
            var result = config.HasModule("test");

            // Assert
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void HasModule_WithNonexistentModule_ReturnsFalse()
        {
            // Arrange
            var config = new GhostConfig();

            // Act
            var result = config.HasModule("nonexistent");

            // Assert
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void GetModuleConfig_WithExistingEnabledModule_ReturnsConfig()
        {
            // Arrange
            var loggingConfig = new LoggingConfig { Enabled = true };
            var config = new GhostConfig
            {
                Modules = new Dictionary<string, ModuleConfig>
                {
                    ["logging"] = loggingConfig
                }
            };

            // Act
            var result = config.GetModuleConfig<LoggingConfig>("logging");

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(loggingConfig, result);
        }

        [TestMethod]
        public void GetModuleConfig_WithNonexistentModule_ReturnsNull()
        {
            // Arrange
            var config = new GhostConfig();

            // Act
            var result = config.GetModuleConfig<LoggingConfig>("nonexistent");

            // Assert
            Assert.IsNull(result);
        }
    }

    [TestClass]
    public class GLoggingTests
    {
        private Mock<IGhostLogger> _mockLogger;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<IGhostLogger>();
            G.Initialize(_mockLogger.Object);
        }

        [TestMethod]
        public void LogInfo_WithMessage_CallsLoggerWithInfoLevel()
        {
            // Arrange
            string message = "Test info message";

            // Act
            G.LogInfo(message);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                message,
                Microsoft.Extensions.Logging.LogLevel.Information,
                null,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }

        [TestMethod]
        public void LogDebug_WithMessage_CallsLoggerWithDebugLevel()
        {
            // Arrange
            string message = "Test debug message";

            // Act
            G.LogDebug(message);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                message,
                Microsoft.Extensions.Logging.LogLevel.Debug,
                null,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }

        [TestMethod]
        public void LogWarn_WithMessage_CallsLoggerWithWarningLevel()
        {
            // Arrange
            string message = "Test warning message";

            // Act
            G.LogWarn(message);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                message,
                Microsoft.Extensions.Logging.LogLevel.Warning,
                null,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }

        [TestMethod]
        public void LogError_WithMessage_CallsLoggerWithErrorLevel()
        {
            // Arrange
            string message = "Test error message";

            // Act
            G.LogError(message);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                message,
                Microsoft.Extensions.Logging.LogLevel.Error,
                null,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }

        [TestMethod]
        public void LogError_WithExceptionAndMessage_CallsLoggerWithException()
        {
            // Arrange
            string message = "Test error with exception";
            var exception = new Exception("Test exception");

            // Act
            G.LogError(message, exception);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                message,
                Microsoft.Extensions.Logging.LogLevel.Error,
                exception,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }

        [TestMethod]
        public void LogInfo_WithFormatArguments_FormatsMessageCorrectly()
        {
            // Arrange
            string format = "Test {0} with {1}";
            string arg1 = "message";
            string arg2 = "arguments";
            string expectedMessage = "Test message with arguments";

            // Act
            G.LogInfo(format, arg1, arg2);

            // Assert
            _mockLogger.Verify(l => l.LogWithSource(
                expectedMessage,
                Microsoft.Extensions.Logging.LogLevel.Information,
                null,
                It.IsAny<string>(),
                It.IsAny<int>()
            ), Times.Once);
        }
    }

    #endregion

    #region Storage Tests

    [TestClass]
    public class LocalCacheTests
    {
        private string _tempPath;
        private LocalCache _cache;

        [TestInitialize]
        public void Setup()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "ghost-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempPath);
            _cache = new LocalCache(_tempPath);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _cache.DisposeAsync().AsTask().Wait();
            if (Directory.Exists(_tempPath))
            {
                try
                {
                    Directory.Delete(_tempPath, true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        [TestMethod]
        public async Task SetAndGetAsync_WithValidData_StoresAndRetrievesData()
        {
            // Arrange
            string key = "test_key";
            string value = "test_value";

            // Act
            await _cache.SetAsync(key, value);
            var result = await _cache.GetAsync<string>(key);

            // Assert
            Assert.AreEqual(value, result);
        }

        [TestMethod]
        public async Task ExistsAsync_WithExistingKey_ReturnsTrue()
        {
            // Arrange
            string key = "test_key";
            string value = "test_value";
            await _cache.SetAsync(key, value);

            // Act
            var result = await _cache.ExistsAsync(key);

            // Assert
            Assert.IsTrue(result);
        }

        [TestMethod]
        public async Task ExistsAsync_WithNonexistentKey_ReturnsFalse()
        {
            // Arrange
            string key = "nonexistent_key";

            // Act
            var result = await _cache.ExistsAsync(key);

            // Assert
            Assert.IsFalse(result);
        }

        [TestMethod]
        public async Task DeleteAsync_WithExistingKey_RemovesKey()
        {
            // Arrange
            string key = "test_key";
            string value = "test_value";
            await _cache.SetAsync(key, value);

            // Act
            await _cache.DeleteAsync(key);
            var exists = await _cache.ExistsAsync(key);

            // Assert
            Assert.IsFalse(exists);
        }

        [TestMethod]
        public async Task ClearAsync_RemovesAllKeys()
        {
            // Arrange
            await _cache.SetAsync("key1", "value1");
            await _cache.SetAsync("key2", "value2");

            // Act
            await _cache.ClearAsync();
            var key1Exists = await _cache.ExistsAsync("key1");
            var key2Exists = await _cache.ExistsAsync("key2");

            // Assert
            Assert.IsFalse(key1Exists);
            Assert.IsFalse(key2Exists);
        }

        [TestMethod]
        public async Task ExpireAsync_SetsExpiryOnKey()
        {
            // Arrange
            string key = "test_key";
            string value = "test_value";
            await _cache.SetAsync(key, value);

            // Act
            await _cache.ExpireAsync(key, TimeSpan.FromMilliseconds(50));
            var beforeExpiry = await _cache.ExistsAsync(key);
            await Task.Delay(100); // Wait for expiry
            var afterExpiry = await _cache.ExistsAsync(key);

            // Assert
            Assert.IsTrue(beforeExpiry);
            Assert.IsFalse(afterExpiry);
        }
    }

    [TestClass]
    public class GhostBusTests
    {
        private LocalCache _cache;
        private GhostBus _bus;

        [TestInitialize]
        public void Setup()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "ghost-bus-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(tempPath);
            _cache = new LocalCache(tempPath);
            _bus = new GhostBus(_cache);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _bus.DisposeAsync().AsTask().Wait();
            _cache.DisposeAsync().AsTask().Wait();
        }

        [TestMethod]
        public async Task PublishAndSubscribeAsync_WithValidMessage_DeliversMessageToSubscriber()
        {
            // Arrange
            string channel = "test_channel";
            string message = "test_message";
            string receivedMessage = null;
            var messageSemaphore = new SemaphoreSlim(0, 1);

            // Start subscription in background
            var subscriptionTask = Task.Run(async () =>
            {
                await foreach (var msg in _bus.SubscribeAsync<string>(channel, CancellationToken.None))
                {
                    receivedMessage = msg;
                    messageSemaphore.Release();
                    break;
                }
            });

            // Wait a bit for subscription to initialize
            await Task.Delay(100);

            // Act
            await _bus.PublishAsync(channel, message);

            // Wait for message to be received with timeout
            var received = await messageSemaphore.WaitAsync(1000);

            // Assert
            Assert.IsTrue(received, "Message was not received within timeout");
            Assert.AreEqual(message, receivedMessage);

            // Cleanup
            await _bus.UnsubscribeAsync(channel);
        }

        [TestMethod]
        public async Task UnsubscribeAsync_CancelsSubscription()
        {
            // Arrange
            string channel = "test_channel";
            string message = "test_message";
            int messageCount = 0;
            var cts = new CancellationTokenSource();

            // Start subscription in background
            var subscriptionTask = Task.Run(async () =>
            {
                await foreach (var msg in _bus.SubscribeAsync<string>(channel, cts.Token))
                {
                    messageCount++;
                }
            });

            // Wait a bit for subscription to initialize
            await Task.Delay(100);

            // Act - send a message, unsubscribe, then send another message
            await _bus.PublishAsync(channel, message);
            await Task.Delay(100); // Wait for message processing
            await _bus.UnsubscribeAsync(channel);
            cts.Cancel(); // Ensure the subscription loop exits
            await _bus.PublishAsync(channel, "another_message");
            await Task.Delay(100); // Wait for potential message processing

            // Assert
            Assert.AreEqual(1, messageCount, "Only one message should have been received before unsubscribing");
        }

        [TestMethod]
        public async Task PublishAsync_WithExpiry_MessagesExpireAfterSpecifiedTime()
        {
            // Arrange
            string channel = "expiry_channel";
            string message = "expiring_message";

            // Act
            await _bus.PublishAsync(channel, message, TimeSpan.FromMilliseconds(100));

            // Check if message exists in cache
            var exists1 = await _cache.ExistsAsync($"message:{channel}:{message}");

            // Wait for expiry
            await Task.Delay(150);

            // Check if message still exists
            var exists2 = await _cache.ExistsAsync($"message:{channel}:{message}");

            // Assert
            Assert.IsFalse(exists2, "Message should have expired");
        }
    }

    #endregion

    #region Monitoring Tests

    [TestClass]
    public class ProcessMetricsTests
    {
        [TestMethod]
        public void CreateSnapshot_ReturnsValidSnapshot()
        {
            // Arrange
            string processId = "test-process";

            // Act
            var metrics = ProcessMetrics.CreateSnapshot(processId);

            // Assert
            Assert.AreEqual(processId, metrics.ProcessId);
            Assert.IsTrue(metrics.MemoryBytes > 0);
            Assert.IsTrue(metrics.ThreadCount > 0);
            Assert.IsTrue(metrics.GCTotalMemory > 0);
            Assert.IsTrue(metrics.HandleCount > 0);
            Assert.IsTrue((DateTime.UtcNow - metrics.Timestamp).TotalSeconds < 1);
        }
    }

    [TestClass]
    public class HealthMonitorTests
    {
        [TestMethod]
        public async Task StartAndStopAsync_ControlsMonitoring()
        {
            // Arrange
            var monitor = new HealthMonitor(TimeSpan.FromMilliseconds(100));

            // Act & Assert - no exceptions
            await monitor.StartAsync();
            await Task.Delay(200); // Let it run for a bit
            await monitor.StopAsync();
        }

        [TestMethod]
        public async Task ReportHealthAsync_UpdatesCurrentStatus()
        {
            // Arrange
            var monitor = new HealthMonitor(TimeSpan.FromSeconds(1));
            var initialStatus = monitor.CurrentStatus;
            var newStatus = HealthStatus.Degraded;
            bool eventFired = false;

            monitor.HealthChanged += (sender, report) =>
            {
                eventFired = true;
                Assert.AreEqual(newStatus, report.Status);
            };

            // Act
            await monitor.ReportHealthAsync(new HealthReport(
                Status: newStatus,
                Message: "Test health report",
                Metrics: new Dictionary<string, object>(),
                Timestamp: DateTime.UtcNow
            ));

            // Assert
            Assert.AreEqual(newStatus, monitor.CurrentStatus);
            Assert.IsTrue(eventFired);
        }
    }

    [TestClass]
    public class MetricsCollectorTests
    {
        [TestMethod]
        public async Task TrackMetricAsync_StoresMetric()
        {
            // Arrange
            var collector = new MetricsCollector(TimeSpan.FromSeconds(1));
            await collector.StartAsync();

            var metric = new MetricValue(
                "test.metric",
                42.5,
                new Dictionary<string, string> { ["tag"] = "value" },
                DateTime.UtcNow
            );

            // Act
            await collector.TrackMetricAsync(metric);
            var start = DateTime.UtcNow.AddMinutes(-1);
            var end = DateTime.UtcNow.AddMinutes(1);
            var metrics = await collector.GetMetricsAsync("test.metric", start, end);
            await collector.StopAsync();

            // Assert
            Assert.IsTrue(metrics.Any());
            var retrievedMetric = metrics.FirstOrDefault();
            Assert.IsNotNull(retrievedMetric);
            Assert.AreEqual(metric.Name, retrievedMetric.Name);
            Assert.AreEqual(metric.Value, retrievedMetric.Value);
            Assert.AreEqual(metric.Tags["tag"], retrievedMetric.Tags["tag"]);
        }
    }

    #endregion

    #region Logging Tests

    [TestClass]
    public class DefaultGhostLoggerTests
    {
        private string _tempPath;
        private GhostLoggerConfiguration _config;
        private DefaultGhostLogger _logger;

        [TestInitialize]
        public void Setup()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "ghost-logger-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempPath);
            _config = new GhostLoggerConfiguration
            {
                LogsPath = Path.Combine(_tempPath, "logs"),
                OutputsPath = Path.Combine(_tempPath, "outputs"),
                LogLevel = LogLevel.Debug
            };
            _logger = new DefaultGhostLogger(null, _config);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempPath))
            {
                try
                {
                    Directory.Delete(_tempPath, true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        [TestMethod]
        public void LogWithSource_CreatesLogFiles()
        {
            // Arrange
            string infoMessage = "Test info message";
            string errorMessage = "Test error message";

            // Create outputs directory
            Directory.CreateDirectory(Path.Combine(_tempPath, "outputs"));
            Directory.CreateDirectory(Path.Combine(_tempPath, "logs"));

            // Act
            _logger.LogWithSource(infoMessage, LogLevel.Information);
            _logger.LogWithSource(errorMessage, LogLevel.Error);

            // Assert
            var outputFiles = Directory.GetFiles(Path.Combine(_tempPath, "outputs"));
            var logFiles = Directory.GetFiles(Path.Combine(_tempPath, "logs"));

            Assert.IsTrue(outputFiles.Length > 0, "No output files were created");
            Assert.IsTrue(logFiles.Length > 0, "No log files were created");

            // Verify log content
            string outputContent = File.ReadAllText(outputFiles[0]);
            string errorContent = File.ReadAllText(logFiles[0]);

            Assert.IsTrue(outputContent.Contains(infoMessage), "Info message not found in output log");
            Assert.IsTrue(errorContent.Contains(errorMessage), "Error message not found in error log");
        }

        [TestMethod]
        public void IsEnabled_RespectsLogLevel()
        {
            // Arrange
            _config.LogLevel = LogLevel.Warning;

            // Act & Assert
            Assert.IsFalse(_logger.IsEnabled(LogLevel.Debug));
            Assert.IsFalse(_logger.IsEnabled(LogLevel.Information));
            Assert.IsTrue(_logger.IsEnabled(LogLevel.Warning));
            Assert.IsTrue(_logger.IsEnabled(LogLevel.Error));
            Assert.IsTrue(_logger.IsEnabled(LogLevel.Critical));
        }
    }

    [TestClass]
    public class NullGhostLoggerTests
    {
        [TestMethod]
        public void LogWithSource_DoesNotThrowException()
        {
            // Arrange
            var logger = NullGhostLogger.Instance;

            // Act & Assert - no exception
            logger.LogWithSource("Test message", LogLevel.Information);
            logger.LogWithSource("Test error", LogLevel.Error, new Exception("Test exception"));
        }

        [TestMethod]
        public void IsEnabled_AlwaysReturnsFalse()
        {
            // Arrange
            var logger = NullGhostLogger.Instance;

            // Act & Assert
            Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
            Assert.IsFalse(logger.IsEnabled(LogLevel.Error));
            Assert.IsFalse(logger.IsEnabled(LogLevel.Critical));
        }
    }

    #endregion

    #region SDK Tests

    [TestClass]
    public class GhostAppTests
    {
        private class TestGhostApp : GhostApp
        {
            public bool RunCalled { get; private set; }
            public bool BeforeRunCalled { get; private set; }
            public bool AfterRunCalled { get; private set; }
            public bool ErrorHandlerCalled { get; private set; }
            public bool ShouldThrowInRun { get; set; }

            public override async Task RunAsync(IEnumerable<string> args)
            {
                RunCalled = true;
                if (ShouldThrowInRun)
                {
                    throw new Exception("Test exception");
                }
                await Task.CompletedTask;
            }

            protected override Task OnBeforeRunAsync()
            {
                BeforeRunCalled = true;
                return Task.CompletedTask;
            }

            protected override Task OnAfterRunAsync()
            {
                AfterRunCalled = true;
                return Task.CompletedTask;
            }

            protected override Task OnErrorAsync(Exception ex)
            {
                ErrorHandlerCalled = true;
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_CallsLifecycleMethods()
        {
            // Arrange
            var app = new TestGhostApp();

            // Act
            await app.ExecuteAsync(Array.Empty<string>());

            // Assert
            Assert.IsTrue(app.BeforeRunCalled, "OnBeforeRunAsync should be called");
            Assert.IsTrue(app.RunCalled, "RunAsync should be called");
            Assert.IsTrue(app.AfterRunCalled, "OnAfterRunAsync should be called");
            Assert.IsFalse(app.ErrorHandlerCalled, "OnErrorAsync should not be called without errors");
        }

        [TestMethod]
        public async Task ExecuteAsync_WithException_CallsErrorHandler()
        {
            // Arrange
            var app = new TestGhostApp { ShouldThrowInRun = true };

            // Act & Assert
            try
            {
                await app.ExecuteAsync(Array.Empty<string>());
                Assert.Fail("Should have thrown an exception");
            }
            catch
            {
                // Expected
            }

            // Assert
            Assert.IsTrue(app.BeforeRunCalled, "OnBeforeRunAsync should be called");
            Assert.IsTrue(app.RunCalled, "RunAsync should be called");
            Assert.IsTrue(app.ErrorHandlerCalled, "OnErrorAsync should be called when an error occurs");
        }
    }

    [TestClass]
    public class GhostServiceAppTests
    {
        private class TestServiceApp : GhostApp
        {
            public bool TickCalled { get; private set; }
            public int TickCount { get; private set; }
            private readonly EventWaitHandle _tickEvent = new ManualResetEvent(false);

            public TestServiceApp()
            {
                TickInterval = TimeSpan.FromMilliseconds(100);
            }

            public override Task RunAsync(IEnumerable<string> args)
            {
                return Task.CompletedTask;
            }

            protected override Task OnTickAsync()
            {
                TickCalled = true;
                TickCount++;
                _tickEvent.Set();
                return Task.CompletedTask;
            }

            public bool WaitForTick(int timeoutMs)
            {
                return _tickEvent.WaitOne(timeoutMs);
            }
        }

        [TestMethod]
        public async Task ServiceApp_ExecutesTickCallback()
        {
            // Arrange
            var app = new TestServiceApp();

            // Act
            var executeTask = app.ExecuteAsync(Array.Empty<string>());

            // Wait for at least one tick
            var tickOccurred = app.WaitForTick(1000);

            // Stop the service
            await app.StopAsync();

            // Wait for execute to complete
            await executeTask;

            // Assert
            Assert.IsTrue(tickOccurred, "Service tick should have occurred");
            Assert.IsTrue(app.TickCalled, "OnTickAsync should be called");
            Assert.IsTrue(app.TickCount > 0, "Tick count should be greater than 0");
        }
    }

    #endregion

    #region Process Management Tests

    [TestClass]
    public class ProcessInfoTests
    {
        [TestMethod]
        public async Task StartAndStopAsync_ControlsProcess()
        {
            // Arrange - create a process that sleeps for a short time
            var processId = "test-process";
            var metadata = new ProcessMetadata(
                Name: "TestProcess",
                Type: "test",
                Version: "1.0.0",
                Environment: new Dictionary<string, string>(),
                Configuration: new Dictionary<string, string>()
            );

            string executablePath;
            string arguments;

            if (OperatingSystem.IsWindows())
            {
                executablePath = "cmd.exe";
                arguments = "/c timeout /t 2";
            }
            else
            {
                executablePath = "sleep";
                arguments = "2";
            }

            var processInfo = new ProcessInfo(
                processId,
                metadata,
                executablePath,
                arguments,
                Directory.GetCurrentDirectory(),
                new Dictionary<string, string>()
            );

            // Act
            await processInfo.StartAsync();
            var isRunning = processInfo.IsRunning;
            var status = processInfo.Status;

            // Verify process is running
            Assert.IsTrue(isRunning, "Process should be running after start");
            Assert.AreEqual(ProcessStatus.Running, status, "Process status should be Running");

            // Stop the process
            await processInfo.StopAsync(TimeSpan.FromSeconds(5));
            var isRunningAfterStop = processInfo.IsRunning;
            var statusAfterStop = processInfo.Status;

            // Assert process is stopped
            Assert.IsFalse(isRunningAfterStop, "Process should not be running after stop");
            Assert.AreEqual(ProcessStatus.Stopped, statusAfterStop, "Process status should be Stopped");

            // Clean up
            await processInfo.DisposeAsync();
        }

        [TestMethod]
        public async Task ProcessEvents_AreFiredCorrectly()
        {
            // Arrange - create a process that outputs something
            var processId = "test-events";
            var metadata = new ProcessMetadata(
                Name: "TestEventsProcess",
                Type: "test",
                Version: "1.0.0",
                Environment: new Dictionary<string, string>(),
                Configuration: new Dictionary<string, string>()
            );

            string executablePath;
            string arguments;

            if (OperatingSystem.IsWindows())
            {
                executablePath = "cmd.exe";
                arguments = "/c echo Hello World";
            }
            else
            {
                executablePath = "bash";
                arguments = "-c \"echo Hello World\"";
            }

            var processInfo = new ProcessInfo(
                processId,
                metadata,
                executablePath,
                arguments,
                Directory.GetCurrentDirectory(),
                new Dictionary<string, string>()
            );

            bool statusChangedFired = false;
            bool outputReceivedFired = false;
            string outputContent = null;

            // Set up event handlers
            processInfo.StatusChanged += (sender, e) => {
                statusChangedFired = true;
            };

            processInfo.OutputReceived += (sender, e) => {
                outputReceivedFired = true;
                outputContent = e.Data;
            };

            // Act
            await processInfo.StartAsync();

            // Wait for process to complete and events to fire
            await Task.Delay(1000);

            // Clean up
            await processInfo.DisposeAsync();

            // Assert
            Assert.IsTrue(statusChangedFired, "StatusChanged event should have fired");
            Assert.IsTrue(outputReceivedFired, "OutputReceived event should have fired");
            Assert.IsTrue(outputContent?.Contains("Hello World") == true, "Output should contain expected content");
        }

        [TestMethod]
        public async Task RestartAsync_RestartsCrashedProcess()
        {
            // Arrange - create a process that exits quickly
            var processId = "test-restart";
            var metadata = new ProcessMetadata(
                Name: "TestRestartProcess",
                Type: "test",
                Version: "1.0.0",
                Environment: new Dictionary<string, string>(),
                Configuration: new Dictionary<string, string>()
            );

            string executablePath;
            string arguments;

            if (OperatingSystem.IsWindows())
            {
                executablePath = "cmd.exe";
                arguments = "/c exit";
            }
            else
            {
                executablePath = "bash";
                arguments = "-c \"exit\"";
            }

            var processInfo = new ProcessInfo(
                processId,
                metadata,
                executablePath,
                arguments,
                Directory.GetCurrentDirectory(),
                new Dictionary<string, string>()
            );

            // Act
            await processInfo.StartAsync();

            // Wait for process to exit
            await Task.Delay(1000);

            // Initial restart count
            var initialRestartCount = processInfo.RestartCount;

            // Restart the process
            await processInfo.RestartAsync(TimeSpan.FromSeconds(1));

            // New restart count
            var newRestartCount = processInfo.RestartCount;

            // Clean up
            await processInfo.DisposeAsync();

            // Assert
            Assert.AreEqual(initialRestartCount + 1, newRestartCount, "Restart count should be incremented");
        }
    }

    #endregion

    #region Integration Tests

    [TestClass]
    public class GhostSdkIntegrationTests
    {
        private IGhostBus _bus;
        private IGhostData _data;
        private ICache _cache;
        private string _tempPath;

        [TestInitialize]
        public void Setup()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "ghost-sdk-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempPath);
            _cache = new LocalCache(_tempPath);
            _bus = new GhostBus(_cache);

            // Set up mock data
            var mockData = new Mock<IGhostData>();
            _data = mockData.Object;
        }

        [TestCleanup]
        public void Cleanup()
        {
            (_bus as IAsyncDisposable)?.DisposeAsync().AsTask().Wait();
            (_cache as IAsyncDisposable)?.DisposeAsync().AsTask().Wait();

            if (Directory.Exists(_tempPath))
            {
                try
                {
                    Directory.Delete(_tempPath, true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        [TestMethod]
        public async Task GhostFatherConnection_InitializesAndReportsHealth()
        {
            // Arrange
            var metadata = new ProcessMetadata(
                Name: "TestApp",
                Type: "test",
                Version: "1.0.0",
                Environment: new Dictionary<string, string>(),
                Configuration: new Dictionary<string, string>()
            );

            var logger = new NullGhostLogger();

            // Act
            await using var connection = new GhostFatherConnection(_bus, logger, metadata);

            // Report health and metrics
            await connection.ReportHealthAsync("Healthy", "Test health report");

            // Assert
            // No exceptions means success
            Assert.IsNotNull(connection.Id);
            Assert.AreEqual(metadata, connection.Metadata);
        }

        [TestMethod]
        public async Task GhostClient_RegistersAndHandlesCommands()
        {
            // Arrange
            var client = new GhostClient("TestClient", "1.0.0", "test");
            bool commandHandled = false;

            // Register a custom command handler
            client.RegisterCommand("test_command", () => {
                commandHandled = true;
                return Task.CompletedTask;
            });

            // Create a command
            var command = new GhostFatherCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "test_command",
                Parameters = new Dictionary<string, string>()
            };

            // Get the command handler through reflection
            var handleCommandMethod = typeof(GhostClient)
                .GetMethod("HandleCommandAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            await (Task)handleCommandMethod.Invoke(client, new object[] { command });

            // Assert
            Assert.IsTrue(commandHandled, "Command handler should have been called");

            // Clean up
            await client.DisposeAsync();
        }
    }

    #endregion
}