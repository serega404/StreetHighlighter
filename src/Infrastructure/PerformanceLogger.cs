using System.Collections.Concurrent;
using System.Diagnostics;

namespace StreetHighlighter.Infrastructure
{
    public class PerformanceLogger
    {
        private readonly ILogger<PerformanceLogger> _logger;
        private readonly ConcurrentDictionary<string, long> _timings = new();

        public PerformanceLogger(ILogger<PerformanceLogger> logger)
        {
            _logger = logger;
        }

        public IDisposable Measure(string operationName)
        {
            return new TimerScope(operationName, this);
        }

        public void LogSummary(string requestId)
        {
            var summary = string.Join(", ", _timings.Select(kv => $"{kv.Key}: {kv.Value}ms"));
            _logger.LogInformation("Request {RequestId} Performance: {Summary}", requestId, summary);
        }

        private void Record(string operationName, long elapsedMilliseconds)
        {
            _timings[operationName] = elapsedMilliseconds;
        }

        private sealed class TimerScope : IDisposable
        {
            private readonly string _name;
            private readonly PerformanceLogger _parent;
            private readonly Stopwatch _sw;

            public TimerScope(string name, PerformanceLogger parent)
            {
                _name = name;
                _parent = parent;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                _parent.Record(_name, _sw.ElapsedMilliseconds);
            }
        }
    }
}
