// Data/Models/DTO/Solutions/JudgeResponseDto.cs
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO.Solutions
{
    public sealed class JudgeResponseDto
    {
        public string Status { get; set; } = "passed";
        public string? Message { get; set; }
        public string? Version { get; set; }

        public CompileInfo Compile { get; set; } = new();
        public RunInfo     Run     { get; set; } = new();

        public List<TestCaseResult> Tests { get; set; } = new();
        public ExecStats? Metrics { get; set; }

        public sealed class CompileInfo
        {
            public bool Ok { get; set; } = true;
            public string Stdout { get; set; } = "";
            public string Stderr { get; set; } = "";
            public List<Diagnostic> Diagnostics { get; set; } = new();
        }

        public sealed class RunInfo
        {
            public int ExitCode { get; set; } = 0;
            public string Stdout { get; set; } = "";
            public string Stderr { get; set; } = "";
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
            public int? TimeMs { get; set; }
            public bool Hidden { get; set; }
        }

        public sealed class ExecStats
        {
            public int TotalTimeMs { get; set; }
        }

        public sealed class Diagnostic
        {
            public string Level { get; set; } = "error"; // error | warning
            public string Message { get; set; } = "";
            public string? Code { get; set; }
            public string? File { get; set; }
            public int? Line { get; set; }
            public int? Column { get; set; }
        }
    }
}
