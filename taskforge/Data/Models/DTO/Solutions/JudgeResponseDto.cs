using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO.Solutions
{
    public sealed class JudgeResponseDto
    {
        public string Status { get; set; } = "passed";
        public string Message { get; set; } = string.Empty;

        public CompileInfo? Compile { get; set; }
        public RunInfo? Run { get; set; }
        public List<TestCaseResult> Tests { get; set; } = new();

        public ExecStats? Metrics { get; set; }
        public string? Version { get; set; }

        public sealed class CompileInfo
        {
            public bool Ok { get; set; }
            public List<Diagnostic> Diagnostics { get; set; } = new();
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
        }

        public sealed class Diagnostic
        {
            public string Level { get; set; } = "error";
            public string Message { get; set; } = string.Empty;
            public string? Code { get; set; }
            public string? File { get; set; }
            public int? Line { get; set; }
            public int? Column { get; set; }
        }

        public sealed class RunInfo
        {
            public int ExitCode { get; set; }
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
            public ExceptionInfo? Exception { get; set; }
        }

        public sealed class ExceptionInfo
        {
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
            public string? StackTrace { get; set; }
            public int? Line { get; set; }
            public int? Column { get; set; }
        }

        public sealed class TestCaseResult
        {
            public string Name { get; set; } = "";
            public string Input { get; set; } = "";
            public string Expected { get; set; } = "";
            public string Actual { get; set; } = "";
            public bool Passed { get; set; }
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
            public int? TimeMs { get; set; }   // <— сделал nullable
        }

        public sealed class ExecStats
        {
            public int TotalTimeMs { get; set; }
            public long? MaxMemoryBytes { get; set; }
        }
    }
}
