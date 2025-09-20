using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    /// <summary>
    /// Общий контракт для сервиса компиляции и запуска кода.
    /// </summary>
    public interface ICompilerService
    {
        /// <summary>
        /// Компилирует и выполняет код в заданном языке программирования.
        /// </summary>
        /// <param name="request">Запрос с языком, кодом и входными данными.</param>
        /// <returns>Результат выполнения: вывод или ошибка.</returns>
        Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto request);

        /// <summary>
        /// Запускает тесты для заданного кода и языка.
        /// </summary>
        /// <param name="request">Запрос с кодом и тестовыми случаями.</param>
        /// <returns>Список результатов тестов.</returns>
        Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto request);
    }

    /// <summary>
    /// Интерфейс для специализированного компилятора конкретного языка.
    /// </summary>
    public interface ILanguageCompiler
    {
        /// <summary>
        /// Имя или ключ языка (например, "csharp", "cpp", "python").
        /// </summary>
        string LanguageKey { get; }

        /// <summary>
        /// Компиляция и запуск кода.
        /// </summary>
        Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input);

        /// <summary>
        /// Выполнение тестов для кода.
        /// </summary>
        Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases);
    }
}
