namespace taskforge.Constants
{
    /// <summary>
    /// Supported task assignment types.
    /// NOTE: stored in DB as string.
    /// </summary>
    public static class TaskAssignmentTypes
    {
        public const string CodeTest = "code-test";
        public const string Test = "test";
        public const string ImageTest = "image-test";

        public static string Normalize(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return CodeTest;

            return type.Trim().ToLowerInvariant();
        }

        public static bool IsSupported(string? type)
        {
            var t = Normalize(type);
            return t == CodeTest || t == Test || t == ImageTest;
        }
    }
}
