namespace Coordinator.Editor
{
    public static class CoordinatorSeverities
    {
        public const string Error = "error";
        public const string Warning = "warning";
    }

    public sealed class CoordinatorDiagnostic
    {
        public CoordinatorDiagnostic(string code, string severity, string file, string subject, string message)
        {
            Code = code;
            Severity = severity;
            File = file ?? string.Empty;
            Subject = subject ?? string.Empty;
            Message = message;
        }

        public string Code { get; }
        public string Severity { get; }
        public string File { get; }
        public string Subject { get; }
        public string Message { get; }

        public bool IsError => Severity == CoordinatorSeverities.Error;

        public override string ToString() => $"{Severity} {Code} {File} {Subject}: {Message}";
    }
}
