using System;
using System.Threading;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Metadata
{
    /// <summary>
    /// The one place profiling commands are constructed, so the safety rules cannot be forgotten:
    /// CommandTimeout comes from options, and the CancellationToken is wired to
    /// SqlCommand.Cancel() for the command's lifetime.
    /// </summary>
    internal static class SqlCommandFactory
    {
        public static ProfilingCommand Create(
            SqlConnection connection,
            string sql,
            ProfileOptions options,
            CancellationToken cancellationToken)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = options.QueryTimeoutSeconds;

            CancellationTokenRegistration registration = default(CancellationTokenRegistration);
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(state =>
                {
                    // Cancel() throws if the command already finished or was disposed —
                    // both are benign races, and neither should surface to the caller.
                    try { ((SqlCommand)state).Cancel(); }
                    catch (InvalidOperationException) { }
                }, cmd);
            }

            return new ProfilingCommand(cmd, registration);
        }
    }

    /// <summary>A SqlCommand plus the cancellation registration that must die with it.</summary>
    internal sealed class ProfilingCommand : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private bool _disposed;

        public SqlCommand Cmd { get; private set; }

        public ProfilingCommand(SqlCommand cmd, CancellationTokenRegistration registration)
        {
            Cmd = cmd;
            _registration = registration;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _registration.Dispose();
            Cmd.Dispose();
        }
    }
}
