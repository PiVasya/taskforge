﻿using System;
using System.Collections.Generic;
using System.Linq;
using taskforge.Services.Interfaces;
using taskforge.Services.Remote;

namespace taskforge.Services
{
    public class CompilerProvider : ICompilerProvider
    {
        private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // C#
            ["c#"]      = "csharp",
            ["cs"]      = "csharp",
            ["csharp"]  = "csharp",
            [".net"]    = "csharp",
            ["dotnet"]  = "csharp",
            // C++
            ["c++"]     = "cpp",
            ["cplusplus"] = "cpp",
            ["cpp"]     = "cpp",
            ["cc"]      = "cpp",
            // Python            // Java
            ["java"]    = "java",
            // Pascal
            ["pascal"]    = "pascal",
            ["pas"]       = "pascal",
            ["pascalabc"] = "pascal",
            // JavaScript
            ["js"]       = "javascript",
            ["javascript"] = "javascript",
            ["node"]    = "javascript"
        };

        private readonly Dictionary<string, ICompiler> _byCanonical = new(StringComparer.OrdinalIgnoreCase);

        public CompilerProvider(IEnumerable<ICompiler> compilers)
        {
            var list = compilers?.ToList() ?? new List<ICompiler>();
            var csharp = list.OfType<CSharpHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            var cpp    = list.OfType<CppHttpCompiler>().Cast<ICompiler>().FirstOrDefault();            var js     = list.OfType<JavascriptHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            var pascal = list.OfType<PascalHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            var java   = list.OfType<JavaHttpCompiler>().Cast<ICompiler>().FirstOrDefault();
            if (csharp != null) _byCanonical["csharp"] = csharp;
            if (cpp    != null) _byCanonical["cpp"]    = cpp;            if (js     != null) _byCanonical["javascript"] = js;
            if (pascal != null) _byCanonical["pascal"] = pascal;
            if (java   != null) _byCanonical["java"]   = java;
        }
        public ICompiler? GetCompiler(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;
            var norm = Normalize(language);
            if (_byCanonical.TryGetValue(norm, out var compiler)) return compiler;
            if (_aliases.TryGetValue(norm, out var canonical) && _byCanonical.TryGetValue(canonical, out compiler)) return compiler;
            return null;
        }
        private static string Normalize(string s)
        {
            var t = s.Trim().ToLowerInvariant();
            t = t.Replace(" ", "").Replace(".", "");
            if (t == "c++") t = "cpp";
            if (t == "c#")  t = "csharp";
            return t;
        }
    }
}
