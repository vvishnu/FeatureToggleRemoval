using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public class ConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SimpleConsoleLogger(categoryName);
        public void Dispose() { }
    }

    public class SimpleConsoleLogger : ILogger
    {
        private readonly string _categoryName;

        public SimpleConsoleLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => logLevel.ToString().ToLower()
            };

            Console.WriteLine($"[{level}] {_categoryName}: {formatter(state, exception)}");
            if (exception != null)
            {
                Console.WriteLine(exception.ToString());
            }
        }
    }
}
