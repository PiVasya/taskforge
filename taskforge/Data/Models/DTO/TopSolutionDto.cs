using System;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Represents a solution summary used for leaderboard/top results.
    /// Contains user identification, passed/failed counts, submission timestamp and code snippet.
    /// </summary>
    public class TopSolutionDto
    {
        /// <summary>
        /// Identifier of the user who submitted the solution.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Display name of the user (e.g. "FirstName LastName").
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Number of public and hidden test cases passed by this solution.
        /// </summary>
        public int PassedCount { get; set; }

        /// <summary>
        /// Number of test cases failed by this solution.
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// Timestamp when the solution was submitted.
        /// </summary>
        public DateTime SubmittedAt { get; set; }

        /// <summary>
        /// Programming language used for the solution (e.g. cpp, csharp, python).
        /// </summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>
        /// Source code of the solution. Returned to display on the leaderboard.
        /// </summary>
        public string Code { get; set; } = string.Empty;
    }
}
