﻿using System;
using System.Collections.Generic;
using System.Linq;
using taskforge.Services.Interfaces;
using taskforge.Services.Remote;

namespace taskforge.Services
{
    /// <summary>
    /// Провайдер компиляторов с нормализацией названий языков и
    /// корректным сопоставлением alias → конкретный ICompiler.
    /// </summary>
    public class CompilerProvider : ICompilerProvider
    {
        // alias -> canonical key ("csharp" | "cpp" | "python")
        private static readonly Dictionary<string, string> _aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // C#
                ["c#"] = "csharp",
                ["cs"] = "csharp",
                ["csharp"] = "csharp",
                [".net"] = "csharp",
                ["dotnet"] = "csharp",

                // C++
                ["c++"] = "cpp",
                ["cplusplus"] = "cpp",
                ["cpp"] = "cpp",
                ["cc"] = "cpp",

                // Python
                ["py"] = "python",
                ["python"] = "python",
                ["py3"] = "python",
            };

        private readonly Dictionary<string, ICompiler> _byCanonical =
            new(StringComparer.OrdinalIgnoreCase);

        public CompilerProvider(IEnumerable<ICompiler> compilers)
        {
            // Сопоставляем известные нам типы в канонические ключи.
            // (HttpRunnerCompilerBase хранит langKey, но он protected — поэтому по типу.)
            var list = compilers?.ToList() ?? new List<ICompiler>();

            var csharp = list.OfType<CSharpHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            var cpp    = list.OfType<CppHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            var py     = list.OfType<PythonHttpCompiler>().Cast<ICompiler>().FirstOrDefault();

            if (csharp != null) _byCanonical["csharp"] = csharp;
            if (cpp    != null) _byCanonical["cpp"]    = cpp;
            if (py     != null) _byCanonical["python"]= py;
        }

        public ICompiler? GetCompiler(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return null;

            var norm = Normalize(language);

            // если сразу канонический ключ
            if (_byCanonical.TryGetValue(norm, out var compiler))
                return compiler;

            // пробуем через алиасы
            if (_aliases.TryGetValue(norm, out var canonical) &&
                _byCanonical.TryGetValue(canonical, out compiler))
            {
                return compiler;
            }

            return null;
        }

        private static string Normalize(string s)
        {
            var t = s.Trim().ToLowerInvariant();
            t = t.Replace(" ", "").Replace(".", "");
            // популярные варианты написания
            if (t == "c++") t = "cpp";
            if (t == "c#")  t = "csharp";
            if (t == "py3") t = "python";
            return t;
        }
    }
}
