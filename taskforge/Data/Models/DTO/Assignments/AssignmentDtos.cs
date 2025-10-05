using System;
using System.Collections.Generic;

namespace taskforge.Data.Models.DTO
{

    public sealed class AssignmentTestCaseDto
    {
        public Guid Id { get; set; }
        public string Input { get; set; } = "";
        public string ExpectedOutput { get; set; } = "";
        public bool IsHidden { get; set; }
    }
}
